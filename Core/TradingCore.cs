using System;
using System.Collections.Generic;
using System.Linq;
using DerivSmartBotDesktop.Deriv;

namespace DerivSmartBotDesktop.Core
{
    #region Basic Models

    


    public enum TradeSignal
    {
        None = 0,
        Buy = 1,
        Sell = -1
    }

    public class Tick
    {
        public string Symbol { get; set; }
        public double Quote { get; set; }
        public DateTime Time { get; set; }
    }

    public class TradeRecord
    {
        public DateTime Time { get; set; }
        public string Symbol { get; set; }
        public string StrategyName { get; set; }
        public string Direction { get; set; }   // "Buy" or "Sell"
        public double Stake { get; set; }
        public double Profit { get; set; }

        // Entry-time ML/diagnostic snapshot (used for logging without leakage)
        public FeatureVector? EntryFeatures { get; set; }
        public StrategyDecision? Decision { get; set; }
    }

    public class StrategyStats
    {
        public int Wins { get; set; }
        public int Losses { get; set; }

        public int TotalTrades => Wins + Losses;

        public double NetPL { get; set; }

        public double WinRate => TotalTrades > 0
            ? (double)Wins / TotalTrades * 100.0
            : 0.0;
    }

    public class SymbolPerformanceStats
    {
        public string Symbol { get; set; } = string.Empty;

        public double LastHeat { get; set; }
        public MarketRegime LastRegime { get; set; } = MarketRegime.Unknown;
        public double LastRegimeScore { get; set; }

        public int Wins { get; set; }
        public int Losses { get; set; }
        public double NetPL { get; set; }

        public int TotalTrades => Wins + Losses;

        public double WinRate => TotalTrades > 0
            ? (double)Wins / TotalTrades * 100.0
            : 0.0;

        public DateTime LastUpdated { get; set; }

        // When true, this symbol has been marked as not tradable for the current session
        // (for example, after an InvalidOfferings error from Deriv).
        public bool IsDisabledForSession { get; set; }
    }

    #endregion

    #region Market Regime & Diagnostics

    public enum MarketRegime
    {
        Unknown = 0,
        TrendingUp = 1,
        TrendingDown = 2,
        RangingLowVol = 3,
        RangingHighVol = 4,
        VolatileChoppy = 5
    }

    public class MarketDiagnostics
    {
        public MarketRegime Regime { get; set; } = MarketRegime.Unknown;
        public double? Volatility { get; set; }      // std dev
        public double? TrendSlope { get; set; }      // simple LR slope
        public double? RegimeScore { get; set; }     // 0..1, optional AI confidence
        public DateTime Time { get; set; }
    }

    #endregion

    #region AI Market Regime Classifier

    public interface IMarketRegimeClassifier
    {
        MarketRegime Classify(
            IReadOnlyList<double> prices,
            double? volatility,
            double? trendSlope,
            out double score);
    }

    public class AiMarketRegimeClassifier : IMarketRegimeClassifier
    {
        public MarketRegime Classify(
            IReadOnlyList<double> prices,
            double? volatility,
            double? trendSlope,
            out double score)
        {
            score = 0.0;

            if (prices == null || prices.Count < 10)
                return MarketRegime.Unknown;

            double first = prices.First();
            double last = prices.Last();
            double range = prices.Max() - prices.Min();

            double netMove = last - first;
            double directionalBias = range > 0 ? netMove / range : 0.0;

            double vol = volatility ?? 0.0;
            double slope = trendSlope ?? 0.0;
            double absSlope = Math.Abs(slope);

            int flips = 0;
            for (int i = 2; i < prices.Count; i++)
            {
                double d1 = prices[i - 1] - prices[i - 2];
                double d2 = prices[i] - prices[i - 1];
                if (Math.Sign(d1) != 0 && Math.Sign(d2) != 0 && Math.Sign(d1) != Math.Sign(d2))
                    flips++;
            }
            double flipRatio = (double)flips / prices.Count;

            // Very noisy: many flips + relatively high vol
            if (flipRatio > 0.4 && vol > 0.15)
            {
                score = 0.75 + 0.25 * Math.Min(1.0, flipRatio);
                return MarketRegime.VolatileChoppy;
            }

            // Strong trend
            if (Math.Abs(directionalBias) > 0.5 && absSlope > 0.0004 && vol > 0.03)
            {
                score = 0.8 + 0.2 * Math.Min(1.0, absSlope / 0.001);

                if (netMove > 0)
                    return MarketRegime.TrendingUp;
                else
                    return MarketRegime.TrendingDown;
            }

            // Breakout / expansion
            if (range > 0 && vol > 0.08 && absSlope > 0.0005)
            {
                score = 0.85 + 0.15 * Math.Min(1.0, vol / 0.2);

                if (Math.Abs(directionalBias) > 0.3)
                    return netMove > 0 ? MarketRegime.TrendingUp : MarketRegime.TrendingDown;

                return MarketRegime.RangingHighVol;
            }

            // Ranging / mean reversion
            if (Math.Abs(directionalBias) < 0.2)
            {
                if (vol < 0.05)
                {
                    score = 0.6;
                    return MarketRegime.RangingLowVol;
                }
                else if (vol < 0.2)
                {
                    score = 0.7;
                    return MarketRegime.RangingHighVol;
                }
            }

            // Fallback
            if (volatility.HasValue || trendSlope.HasValue)
            {
                if (absSlope > 0.0004)
                {
                    score = 0.6;
                    return netMove >= 0 ? MarketRegime.TrendingUp : MarketRegime.TrendingDown;
                }

                if (vol < 0.05)
                {
                    score = 0.5;
                    return MarketRegime.RangingLowVol;
                }

                if (vol > 0.2)
                {
                    score = 0.5;
                    return MarketRegime.RangingHighVol;
                }
            }

            score = 0.3;
            return MarketRegime.Unknown;
        }
    }

    #endregion

    #region Strategy Context

    public class StrategyContext
    {
        public List<Tick> TickWindow { get; } = new List<Tick>();

        public int MaxTickWindowSize { get; set; } = 200;

        public void AddTick(Tick tick)
        {
            if (tick == null) return;

            TickWindow.Add(tick);
            if (TickWindow.Count > MaxTickWindowSize)
                TickWindow.RemoveAt(0);
        }

        public IReadOnlyList<double> GetLastQuotes(int n)
        {
            if (TickWindow.Count == 0) return Array.Empty<double>();
            if (n >= TickWindow.Count)
                return TickWindow.Select(t => t.Quote).ToArray();
            return TickWindow.Skip(TickWindow.Count - n).Select(t => t.Quote).ToArray();
        }

        public double? GetSimpleMovingAverage(int n)
        {
            var list = GetLastQuotes(n);
            if (list.Count == 0) return null;
            return list.Average();
        }

        public double? GetStdDev(int n)
        {
            var list = GetLastQuotes(n);
            if (list.Count == 0) return null;
            double mean = list.Average();
            double variance = list.Average(v => (v - mean) * (v - mean));
            return Math.Sqrt(variance);
        }

        public double? GetTrendSlope(int n)
        {
            var list = GetLastQuotes(n);
            if (list.Count < 2) return null;

            int len = list.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < len; i++)
            {
                double x = i;
                double y = list[i];
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double denom = len * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-9) return 0.0;

            double slope = (len * sumXY - sumX * sumY) / denom;
            return slope;
        }

        public MarketDiagnostics AnalyzeRegime(
            int volLookback = 50,
            int trendLookback = 50,
            double trendThreshold = 0.0003,
            double lowVolThreshold = 0.2,
            double highVolThreshold = 0.8)
        {
            var sd = GetStdDev(volLookback);
            var slope = GetTrendSlope(trendLookback);

            var diag = new MarketDiagnostics
            {
                Time = TickWindow.LastOrDefault()?.Time ?? DateTime.Now,
                Volatility = sd,
                TrendSlope = slope,
                Regime = MarketRegime.Unknown
            };

            if (sd == null || slope == null)
            {
                diag.Regime = MarketRegime.Unknown;
                return diag;
            }

            double v = sd.Value;
            double s = slope.Value;

            if (Math.Abs(s) > trendThreshold)
            {
                diag.Regime = s > 0 ? MarketRegime.TrendingUp : MarketRegime.TrendingDown;
            }
            else
            {
                if (v < lowVolThreshold)
                    diag.Regime = MarketRegime.RangingLowVol;
                else if (v > highVolThreshold)
                    diag.Regime = MarketRegime.VolatileChoppy;
                else
                    diag.Regime = MarketRegime.RangingHighVol;
            }

            return diag;
        }
    }

    #endregion

    #region Strategy Interfaces + AI

    public class StrategyDecision
    {
        public string StrategyName { get; set; }
        public TradeSignal Signal { get; set; }
        public double Confidence { get; set; }  // 0.0 - 1.0
        public int Duration { get; set; } = 1;
        public string DurationUnit { get; set; } = "t";

        /// <summary>
        /// ML-estimated probability that this decision will result in a profitable trade.
        /// Populated by strategy selectors when an edge model is available.
        /// </summary>
        public double? EdgeProbability { get; set; }

        /// <summary>
        /// Composite quality score used by the selector to rank candidates.
        /// Higher is better; typically anchored around 0..100.
        /// </summary>
        public double QualityScore { get; set; }
    }

    public interface ITradingStrategy
    {
        string Name { get; }
        TradeSignal OnNewTick(Tick tick, StrategyContext context);
    }

    public interface ITradeDurationProvider
    {
        int DefaultDuration { get; }
        string DefaultDurationUnit { get; }
    }

    public interface IRegimeAwareStrategy
    {
        bool ShouldTradeIn(MarketDiagnostics diagnostics);
    }

    public interface IAITradingStrategy : ITradingStrategy
    {
        StrategyDecision Decide(Tick tick, StrategyContext context, MarketDiagnostics diag);
    }

    #endregion

    
#region Strategies

    internal static class StrategyMath
    {
        public static double? ComputeRsi(IReadOnlyList<double> prices, int period)
        {
            if (prices == null) return null;
            if (period <= 0) return null;
            if (prices.Count < period + 1) return null;

            int start = prices.Count - period;
            double gain = 0.0;
            double loss = 0.0;

            for (int i = start; i < prices.Count; i++)
            {
                double change = prices[i] - prices[i - 1];
                if (change > 0)
                    gain += change;
                else if (change < 0)
                    loss -= change;
            }

            if (gain == 0.0 && loss == 0.0)
                return null;

            double rs = loss == 0.0 ? double.PositiveInfinity : gain / loss;
            double rsi = 100.0 - (100.0 / (1.0 + rs));
            return rsi;
        }

        public static (double min, double max)? GetRange(IReadOnlyList<double> prices, int lookback)
        {
            if (prices == null || prices.Count == 0) return null;
            if (lookback <= 0 || lookback > prices.Count)
                lookback = prices.Count;

            int start = prices.Count - lookback;
            double min = double.MaxValue;
            double max = double.MinValue;

            for (int i = start; i < prices.Count; i++)
            {
                double p = prices[i];
                if (p < min) min = p;
                if (p > max) max = p;
            }

            if (min == double.MaxValue || max == double.MinValue)
                return null;

            return (min, max);
        }
    }

    public class ScalpingStrategy : ITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "Scalping EMA 9/19 Retest";
        public int DefaultDuration => 3;
        public string DefaultDurationUnit => "t";

        private sealed class Ema
        {
            private readonly double _alpha;
            private double? _value;
            private int _count;

            public Ema(int period)
            {
                if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
                Period = period;
                _alpha = 2.0 / (period + 1.0);
            }

            public int Period { get; }
            public double? Value => _value;
            public bool IsReady => _count >= Period;

            public double? Update(double price)
            {
                if (!_value.HasValue)
                {
                    _value = price;
                }
                else
                {
                    _value = _alpha * price + (1.0 - _alpha) * _value.Value;
                }

                _count++;
                return _value;
            }

            public void Reset()
            {
                _value = null;
                _count = 0;
            }
        }

        private sealed class MinuteBar
        {
            public DateTime Start { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
        }

        // 1-minute bar series built from ticks
        private readonly List<MinuteBar> _bars = new List<MinuteBar>();
        private MinuteBar? _currentBar;
        private readonly Ema _ema9 = new Ema(9);
        private readonly Ema _ema19 = new Ema(19);

        // Tunable parameters
        private readonly int _minBars;
        private readonly int _atrPeriod;
        private readonly double _maxStopAtr;
        private readonly double _minImpulseAtr;
        private readonly double _retestToleranceAtr;
        private readonly int _minBarsBetweenTrades;
        private readonly int _swingLookback;
        private readonly double _minRewardRisk;

        private DateTime _lastTradeBarStart = DateTime.MinValue;

        private enum SetupState
        {
            None,
            WaitingForLongRetest,
            WaitingForShortRetest
        }

        private SetupState _state = SetupState.None;

        public ScalpingStrategy(
            int atrPeriod = 14,
            int minBars = 40,
            double maxStopAtr = 0.8,
            double minImpulseAtr = 0.7,
            double retestToleranceAtr = 0.25,
            int minBarsBetweenTrades = 3,
            int swingLookback = 20,
            double minRewardRisk = 2.0)
        {
            _atrPeriod = atrPeriod;
            _minBars = minBars;
            _maxStopAtr = maxStopAtr;
            _minImpulseAtr = minImpulseAtr;
            _retestToleranceAtr = retestToleranceAtr;
            _minBarsBetweenTrades = minBarsBetweenTrades;
            _swingLookback = swingLookback;
            _minRewardRisk = minRewardRisk;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            if (tick == null) throw new ArgumentNullException(nameof(tick));

            // Build/extend 1-minute candle and update EMAs on bar close
            UpdateBars(tick);

            if (_bars.Count < _minBars || !_ema9.IsReady || !_ema19.IsReady)
                return TradeSignal.None;

            var lastClosedBar = _bars[_bars.Count - 1];

            // Simple trade-spacing: enforce a minimum number of 1-minute bars between trades
            if (_lastTradeBarStart != DateTime.MinValue)
            {
                var barsSinceLastTrade = (int)((lastClosedBar.Start - _lastTradeBarStart).TotalMinutes);
                if (barsSinceLastTrade >= 0 && barsSinceLastTrade < _minBarsBetweenTrades)
                    return TradeSignal.None;
            }

            var ema9 = _ema9.Value!.Value;
            var ema19 = _ema19.Value!.Value;
            double price = tick.Quote;

            double atr = ComputeAtr(_atrPeriod);
            if (atr <= 0)
                return TradeSignal.None;

            // Trend filter (1-minute)
            bool upTrend = ema9 > ema19 && price > ema9 && price > ema19;
            bool downTrend = ema9 < ema19 && price < ema9 && price < ema19;

            if (!upTrend && !downTrend)
            {
                _state = SetupState.None;
                return TradeSignal.None;
            }

            // Implied stop width: distance between entry (near EMA9) and EMA19
            double emaDistance = Math.Abs(ema9 - ema19);
            double maxStop = _maxStopAtr * atr;
            if (emaDistance > maxStop)
            {
                // The stop would be too wide relative to volatility -> poor scalping setup
                _state = SetupState.None;
                return TradeSignal.None;
            }

            // Impulse magnitude from EMA9
            double impulse = Math.Abs(price - ema9);

            switch (_state)
            {
                case SetupState.None:
                    // Look for initial impulse away from EMA9 in trend direction
                    if (upTrend && price > ema9 && impulse >= _minImpulseAtr * atr)
                    {
                        _state = SetupState.WaitingForLongRetest;
                    }
                    else if (downTrend && price < ema9 && impulse >= _minImpulseAtr * atr)
                    {
                        _state = SetupState.WaitingForShortRetest;
                    }

                    return TradeSignal.None;

                case SetupState.WaitingForLongRetest:
                    // If trend breaks, abandon the setup
                    if (!upTrend)
                    {
                        _state = SetupState.None;
                        return TradeSignal.None;
                    }

                    // Retest: price touches or slightly pierces EMA9
                    if (IsRetest(price, ema9, atr))
                    {
                        double entry = price;
                        double stop = ema19;
                        double rr = ComputeRewardRisk(true, entry, stop);

                        // Enforce both R:R and shallow pullback (lows above EMA19)
                        if (rr >= _minRewardRisk && lastClosedBar.Low > ema19)
                        {
                            _state = SetupState.None;
                            _lastTradeBarStart = lastClosedBar.Start;
                            return TradeSignal.Buy;
                        }

                        // Retest happened but conditions not good enough – reset setup
                        _state = SetupState.None;
                    }

                    return TradeSignal.None;

                case SetupState.WaitingForShortRetest:
                    if (!downTrend)
                    {
                        _state = SetupState.None;
                        return TradeSignal.None;
                    }

                    if (IsRetest(price, ema9, atr))
                    {
                        double entry = price;
                        double stop = ema19;
                        double rr = ComputeRewardRisk(false, entry, stop);

                        if (rr >= _minRewardRisk && lastClosedBar.High < ema19)
                        {
                            _state = SetupState.None;
                            _lastTradeBarStart = lastClosedBar.Start;
                            return TradeSignal.Sell;
                        }

                        _state = SetupState.None;
                    }

                    return TradeSignal.None;

                default:
                    _state = SetupState.None;
                    return TradeSignal.None;
            }
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            // This is explicitly a trend-continuation scalper.
            if (diagnostics == null)
                return false;

            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown;
        }

        // --- Helpers ---

        private void UpdateBars(Tick tick)
        {
            var minute = new DateTime(
                tick.Time.Year,
                tick.Time.Month,
                tick.Time.Day,
                tick.Time.Hour,
                tick.Time.Minute,
                0,
                tick.Time.Kind);

            // New 1-minute bar
            if (_currentBar == null || minute > _currentBar.Start)
            {
                if (_currentBar != null)
                {
                    // When a bar completes, feed its close into the EMAs
                    _ema9.Update(_currentBar.Close);
                    _ema19.Update(_currentBar.Close);

                    _bars.Add(_currentBar);
                    if (_bars.Count > 500)
                        _bars.RemoveAt(0);
                }

                _currentBar = new MinuteBar
                {
                    Start = minute,
                    Open = tick.Quote,
                    High = tick.Quote,
                    Low = tick.Quote,
                    Close = tick.Quote
                };
            }
            else
            {
                // Update current bar
                if (tick.Quote > _currentBar.High) _currentBar.High = tick.Quote;
                if (tick.Quote < _currentBar.Low) _currentBar.Low = tick.Quote;
                _currentBar.Close = tick.Quote;
            }
        }

        private double ComputeAtr(int period)
        {
            if (_bars.Count < period + 1)
                return 0.0;

            int start = _bars.Count - period;
            double sum = 0.0;

            for (int i = start; i < _bars.Count; i++)
            {
                var b = _bars[i];
                sum += b.High - b.Low;
            }

            return sum / period;
        }

        private bool IsRetest(double price, double ema9, double atr)
        {
            double tolerance = _retestToleranceAtr * atr;
            return price >= ema9 - tolerance && price <= ema9 + tolerance;
        }

        private double ComputeRewardRisk(bool isLong, double entry, double stop)
        {
            double stopDistance = Math.Abs(entry - stop);
            if (stopDistance <= 0.0)
                return 0.0;

            if (_bars.Count == 0)
                return 0.0;

            int startIndex = Math.Max(0, _bars.Count - _swingLookback);
            double target;

            if (isLong)
            {
                double swingHigh = _bars[startIndex].High;
                for (int i = startIndex + 1; i < _bars.Count; i++)
                {
                    if (_bars[i].High > swingHigh)
                        swingHigh = _bars[i].High;
                }

                target = swingHigh;
            }
            else
            {
                double swingLow = _bars[startIndex].Low;
                for (int i = startIndex + 1; i < _bars.Count; i++)
                {
                    if (_bars[i].Low < swingLow)
                        swingLow = _bars[i].Low;
                }

                target = swingLow;
            }

            double reward = Math.Abs(target - entry);
            return reward / stopDistance;
        }
    }


    public class MomentumStrategy : ITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "Momentum Trend";
        public int DefaultDuration => 5;
        public string DefaultDurationUnit => "t";

        private readonly int _trendLookback;
        private readonly double _distanceFraction;

        public MomentumStrategy(int trendLookback = 50, double distanceFraction = 0.001)
        {
            _trendLookback = trendLookback;
            _distanceFraction = distanceFraction;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            if (context == null) return TradeSignal.None;

            var slope = context.GetTrendSlope(_trendLookback);
            var ma = context.GetSimpleMovingAverage(_trendLookback);

            if (slope == null || ma == null) return TradeSignal.None;

            double price = tick.Quote;
            double baseline = ma.Value;
            if (Math.Abs(baseline) < 1e-9) return TradeSignal.None;

            double distFraction = (price - baseline) / baseline;

            if (slope.Value > 0 && distFraction > _distanceFraction)
                return TradeSignal.Buy;

            if (slope.Value < 0 && distFraction < -_distanceFraction)
                return TradeSignal.Sell;

            return TradeSignal.None;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;

            // Prefer trending regimes but still allow high-volatility ranging.
            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown ||
                   diagnostics.Regime == MarketRegime.RangingHighVol;
        }
    }

    public class RangeTradingStrategy : ITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "Range / Mean Reversion";
        public int DefaultDuration => 2;
        public string DefaultDurationUnit => "m";

        private readonly int _lookback;
        private readonly double _bandWidth;

        public RangeTradingStrategy(int lookback = 30, double bandWidth = 2.0)
        {
            _lookback = lookback;
            _bandWidth = bandWidth;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            if (context == null) return TradeSignal.None;

            var ma = context.GetSimpleMovingAverage(_lookback);
            var sd = context.GetStdDev(_lookback);

            if (ma == null || sd == null) return TradeSignal.None;

            double mid = ma.Value;
            double dev = sd.Value;
            if (dev <= 0.0) return TradeSignal.None;

            double upper = mid + _bandWidth * dev;
            double lower = mid - _bandWidth * dev;
            double price = tick.Quote;

            if (price <= lower)
                return TradeSignal.Buy;

            if (price >= upper)
                return TradeSignal.Sell;

            return TradeSignal.None;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;

            return diagnostics.Regime == MarketRegime.RangingLowVol ||
                   diagnostics.Regime == MarketRegime.RangingHighVol ||
                   diagnostics.Regime == MarketRegime.Unknown;
        }
    }

    public class BreakoutStrategy : ITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "Breakout";
        public int DefaultDuration => 3;
        public string DefaultDurationUnit => "m";

        private readonly int _lookback;
        private readonly double _bufferFraction;

        public BreakoutStrategy(int lookback = 30, double bufferFraction = 0.002)
        {
            _lookback = lookback;
            _bufferFraction = bufferFraction;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            if (context == null) return TradeSignal.None;

            var quotes = context.GetLastQuotes(_lookback);
            if (quotes == null || quotes.Count < _lookback) return TradeSignal.None;

            var range = StrategyMath.GetRange(quotes, _lookback);
            if (range == null) return TradeSignal.None;

            double min = range.Value.min;
            double max = range.Value.max;
            double mid = 0.5 * (min + max);
            double buffer = Math.Max(Math.Abs(mid) * _bufferFraction, 1e-6);

            double price = tick.Quote;

            if (price > max + buffer)
                return TradeSignal.Buy;

            if (price < min - buffer)
                return TradeSignal.Sell;

            return TradeSignal.None;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;

            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown ||
                   diagnostics.Regime == MarketRegime.RangingHighVol ||
                   diagnostics.Regime == MarketRegime.Unknown;
        }
    }

    public class SmartMoneyConceptStrategy : IAITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "SMC Liquidity Sweep";
        public int DefaultDuration => 5;
        public string DefaultDurationUnit => "m";

        private readonly int _higherTfLookback;
        private readonly int _liquidityLookback;
        private readonly int _chochLookback;
        private readonly double _toleranceFraction;

        private enum SmcState
        {
            Searching = 0,
            LiquidityTaken = 1,
            WaitingEntry = 2
        }

        private SmcState _state = SmcState.Searching;
        private double? _liquidityLevel;
        private double? _sweepExtreme;
        private double? _structureLevel;
        private int _lastSweepIndex = -1;

        public SmartMoneyConceptStrategy(
            int higherTfLookback = 120,
            int liquidityLookback = 60,
            int chochLookback = 40,
            double toleranceFraction = 0.0005)
        {
            _higherTfLookback = higherTfLookback;
            _liquidityLookback = liquidityLookback;
            _chochLookback = chochLookback;
            _toleranceFraction = toleranceFraction;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var decision = Decide(tick, context, context?.AnalyzeRegime());
            return decision.Signal;
        }

        public StrategyDecision Decide(Tick tick, StrategyContext context, MarketDiagnostics diag)
        {
            var result = new StrategyDecision
            {
                StrategyName = Name,
                Signal = TradeSignal.None,
                Confidence = 0.0,
                Duration = DefaultDuration,
                DurationUnit = DefaultDurationUnit
            };

            if (context == null || context.TickWindow.Count < _higherTfLookback)
            {
                Reset();
                return result;
            }

            var diagnostics = diag ?? context.AnalyzeRegime();
            if (diagnostics == null || diagnostics.TrendSlope == null)
            {
                Reset();
                return result;
            }

            double slope = diagnostics.TrendSlope.Value;
            int bias = Math.Sign(slope);
            if (bias == 0)
            {
                Reset();
                return result;
            }

            var prices = context.TickWindow.Select(t => t.Quote).ToList();
            int n = prices.Count;
            double price = tick.Quote;

            double tolBase = Math.Max(Math.Abs(price) * _toleranceFraction, 1e-6);

            // Step 2: identify liquidity pools (equal highs/lows)
            if (_state == SmcState.Searching)
            {
                int start = Math.Max(0, n - _liquidityLookback);

                double max1 = double.MinValue, max2 = double.MinValue;
                int max1Idx = -1, max2Idx = -1;
                double min1 = double.MaxValue, min2 = double.MaxValue;
                int min1Idx = -1, min2Idx = -1;

                for (int i = start; i < n; i++)
                {
                    double p = prices[i];

                    if (p > max1)
                    {
                        max2 = max1;
                        max2Idx = max1Idx;
                        max1 = p;
                        max1Idx = i;
                    }
                    else if (p > max2)
                    {
                        max2 = p;
                        max2Idx = i;
                    }

                    if (p < min1)
                    {
                        min2 = min1;
                        min2Idx = min1Idx;
                        min1 = p;
                        min1Idx = i;
                    }
                    else if (p < min2)
                    {
                        min2 = p;
                        min2Idx = i;
                    }
                }

                if (bias < 0 && max1Idx >= 0 && max2Idx >= 0 && Math.Abs(max1 - max2) <= tolBase)
                {
                    _liquidityLevel = (max1 + max2) / 2.0;
                }
                else if (bias > 0 && min1Idx >= 0 && min2Idx >= 0 && Math.Abs(min1 - min2) <= tolBase)
                {
                    _liquidityLevel = (min1 + min2) / 2.0;
                }
            }

            // Step 3: wait for liquidity sweep
            if (_state == SmcState.Searching && _liquidityLevel != null)
            {
                if (bias < 0 && price > _liquidityLevel.Value + tolBase)
                {
                    _state = SmcState.LiquidityTaken;
                    _sweepExtreme = price;
                    _lastSweepIndex = n - 1;
                }
                else if (bias > 0 && price < _liquidityLevel.Value - tolBase)
                {
                    _state = SmcState.LiquidityTaken;
                    _sweepExtreme = price;
                    _lastSweepIndex = n - 1;
                }
            }
            else if (_state == SmcState.LiquidityTaken)
            {
                // Step 4: Change of Character (ChoCH)
                int start = Math.Max(0, _lastSweepIndex - _chochLookback);
                double swingHigh = double.MinValue;
                double swingLow = double.MaxValue;

                for (int i = start; i <= _lastSweepIndex && i < n; i++)
                {
                    double p = prices[i];
                    if (p > swingHigh) swingHigh = p;
                    if (p < swingLow) swingLow = p;
                }

                if (bias < 0 && price < swingLow - tolBase)
                {
                    _state = SmcState.WaitingEntry;
                    _structureLevel = swingLow;
                }
                else if (bias > 0 && price > swingHigh + tolBase)
                {
                    _state = SmcState.WaitingEntry;
                    _structureLevel = swingHigh;
                }
            }
            else if (_state == SmcState.WaitingEntry && _structureLevel != null && _sweepExtreme != null)
            {
                double structure = _structureLevel.Value;
                double sweep = _sweepExtreme.Value;
                double mid = 0.5 * (structure + sweep);

                if (bias < 0)
                {
                    double lower = Math.Min(structure, mid) - tolBase;
                    double upper = sweep + tolBase;

                    if (price >= lower && price <= upper)
                    {
                        result.Signal = TradeSignal.Sell;
                        result.Confidence = 0.7;
                        Reset();
                        return result;
                    }
                }
                else if (bias > 0)
                {
                    double lower = sweep - tolBase;
                    double upper = Math.Max(structure, mid) + tolBase;

                    if (price >= lower && price <= upper)
                    {
                        result.Signal = TradeSignal.Buy;
                        result.Confidence = 0.7;
                        Reset();
                        return result;
                    }
                }
            }

            return result;
        }

        private void Reset()
        {
            _state = SmcState.Searching;
            _liquidityLevel = null;
            _sweepExtreme = null;
            _structureLevel = null;
            _lastSweepIndex = -1;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;

            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown ||
                   diagnostics.Regime == MarketRegime.RangingHighVol;
        }
    }



    public class AdvancedPriceActionStrategy : IAITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "Advanced Price Action";
        public int DefaultDuration => 3;
        public string DefaultDurationUnit => "m";

        private readonly int _swingLookback;
        private readonly int _minTicks;

        public AdvancedPriceActionStrategy(int swingLookback = 40, int minTicks = 25)
        {
            _swingLookback = swingLookback;
            _minTicks = minTicks;
        }

        private class SwingPoint
        {
            public int Index { get; set; }
            public double Price { get; set; }
            public bool IsHigh { get; set; }
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var decision = Decide(tick, context, context?.AnalyzeRegime());
            return decision.Signal;
        }

        
        public StrategyDecision Decide(Tick tick, StrategyContext context, MarketDiagnostics diag)
        {
            var result = new StrategyDecision
            {
                StrategyName = Name,
                Signal = TradeSignal.None,
                Confidence = 0.0,
                Duration = DefaultDuration,
                DurationUnit = DefaultDurationUnit
            };

            if (context == null || context.TickWindow.Count < _minTicks)
                return result;

            var diagnostics = diag ?? context.AnalyzeRegime();
            var ticks = context.TickWindow;
            int n = ticks.Count;
            int start = Math.Max(0, n - _swingLookback);

            var swings = new List<SwingPoint>();

            // Detect simple swing highs/lows (price action structure)
            for (int i = start + 2; i < n - 2; i++)
            {
                double p = ticks[i].Quote;
                double p1 = ticks[i - 1].Quote;
                double p2 = ticks[i - 2].Quote;
                double n1 = ticks[i + 1].Quote;
                double n2 = ticks[i + 2].Quote;

                bool isHigh = p > p1 && p > p2 && p > n1 && p > n2;
                bool isLow = p < p1 && p < p2 && p < n1 && p < n2;

                if (isHigh)
                {
                    swings.Add(new SwingPoint { Index = i, Price = p, IsHigh = true });
                }
                else if (isLow)
                {
                    swings.Add(new SwingPoint { Index = i, Price = p, IsHigh = false });
                }
            }

            if (swings.Count < 4)
                return result;

            // Keep the last few swings
            var recentSwings = swings
                .OrderBy(s => s.Index)
                .Skip(Math.Max(0, swings.Count - 6))
                .ToList();

            // Separate highs and lows
            var highs = recentSwings.Where(s => s.IsHigh).OrderBy(s => s.Index).ToList();
            var lows = recentSwings.Where(s => !s.IsHigh).OrderBy(s => s.Index).ToList();

            if (highs.Count < 2 || lows.Count < 2)
                return result;

            var lastHigh1 = highs[highs.Count - 1];
            var lastHigh2 = highs[highs.Count - 2];
            var lastLow1 = lows[lows.Count - 1];
            var lastLow2 = lows[lows.Count - 2];

            // Determine structural bias (HH/HL or LH/LL)
            int bias = 0; // 1 = bullish, -1 = bearish
            if (lastHigh1.Price > lastHigh2.Price && lastLow1.Price > lastLow2.Price)
                bias = 1; // higher highs, higher lows
            else if (lastHigh1.Price < lastHigh2.Price && lastLow1.Price < lastLow2.Price)
                bias = -1; // lower highs, lower lows

            if (bias == 0)
                return result;

            double price = tick.Quote;

            // For confluence, use a medium moving average and trend slope
            var ma = context.GetSimpleMovingAverage(30);
            var slope = context.GetTrendSlope(40);

            // Safety checks
            if (ma == null || slope == null)
                return result;

            double baseline = ma.Value;
            double slopeVal = slope.Value;

            // Distance tolerance around structure levels
            double tol = Math.Max(Math.Abs(price) * 0.0008, 1e-6);

            // --- Candlestick / price-action pattern detection on closes ---
            var closes = ticks.Select(t => t.Quote).ToList();
            bool bullishEngulfing = false;
            bool bearishEngulfing = false;
            bool insideBar = false;
            bool threeBarBull = false;
            bool threeBarBear = false;
            bool doubleTop = false;
            bool doubleBottom = false;

            if (closes.Count >= 5)
            {
                int last = closes.Count - 1;
                double c0 = closes[last];
                double c1 = closes[last - 1];
                double c2 = closes[last - 2];
                double c3 = closes[last - 3];
                double c4 = closes[last - 4];

                double body0 = Math.Abs(c0 - c1);
                double body1 = Math.Abs(c1 - c2);
                double body2 = Math.Abs(c2 - c3);

                // Approximate bullish engulfing: prior down move, current strong up move larger than prior body
                if (c1 < c2 && c0 > c1 && body0 > body1 * 1.1)
                    bullishEngulfing = true;

                // Approximate bearish engulfing: prior up move, current strong down move larger than prior body
                if (c1 > c2 && c0 < c1 && body0 > body1 * 1.1)
                    bearishEngulfing = true;

                // Inside bar (range contraction) approximation with closes
                if (body0 < body1 * 0.7)
                    insideBar = true;

                // Three-bar reversal (bullish): strong down, small pause, strong up
                if (c2 < c3 && c1 <= c2 && c0 > c1 && body0 > body2 * 0.8)
                    threeBarBull = true;

                // Three-bar reversal (bearish): strong up, small pause, strong down
                if (c2 > c3 && c1 >= c2 && c0 < c1 && body0 > body2 * 0.8)
                    threeBarBear = true;
            }

            // Structural double-top/double-bottom using last swings
            double swingTol = Math.Max(Math.Abs(price) * 0.0008, 1e-6);
            if (Math.Abs(lastHigh1.Price - lastHigh2.Price) <= swingTol)
                doubleTop = true;
            if (Math.Abs(lastLow1.Price - lastLow2.Price) <= swingTol)
                doubleBottom = true;

            TradeSignal signal = TradeSignal.None;
            double conf = 0.0;

            // --- Core structure-based entries ---
            if (bias > 0)
            {
                // Bullish structure: look to buy on a pullback near last swing low with MA support
                bool nearSupport = Math.Abs(price - lastLow1.Price) <= tol ||
                                   Math.Abs(price - lastLow2.Price) <= tol;
                bool aboveMa = price >= baseline - tol;

                if (nearSupport && aboveMa && slopeVal >= 0)
                {
                    signal = TradeSignal.Buy;
                    conf = 0.65;
                }
            }
            else if (bias < 0)
            {
                // Bearish structure: look to sell on a pullback near last swing high with MA resistance
                bool nearResistance = Math.Abs(price - lastHigh1.Price) <= tol ||
                                      Math.Abs(price - lastHigh2.Price) <= tol;
                bool belowMa = price <= baseline + tol;

                if (nearResistance && belowMa && slopeVal <= 0)
                {
                    signal = TradeSignal.Sell;
                    conf = 0.65;
                }
            }

            // --- Pattern-based confluence and fallback entries ---
            if (signal == TradeSignal.Buy)
            {
                // Boost confidence when bullish patterns align with structure
                if (bullishEngulfing) conf += 0.05;
                if (threeBarBull) conf += 0.05;
                if (doubleBottom) conf += 0.05;
                if (insideBar) conf += 0.02;
            }
            else if (signal == TradeSignal.Sell)
            {
                if (bearishEngulfing) conf += 0.05;
                if (threeBarBear) conf += 0.05;
                if (doubleTop) conf += 0.05;
                if (insideBar) conf += 0.02;
            }
            else
            {
                // No clean structure entry: allow strong pattern-based entries in direction of bias
                if (bias > 0 && (bullishEngulfing || threeBarBull || doubleBottom) && slopeVal >= 0)
                {
                    signal = TradeSignal.Buy;
                    conf = 0.6;
                }
                else if (bias < 0 && (bearishEngulfing || threeBarBear || doubleTop) && slopeVal <= 0)
                {
                    signal = TradeSignal.Sell;
                    conf = 0.6;
                }
            }

            if (signal == TradeSignal.None)
                return result;

            // Adjust confidence with regime and slope strength
            if (diagnostics != null)
            {
                if (diagnostics.Regime == MarketRegime.TrendingUp && signal == TradeSignal.Buy)
                    conf += 0.05;
                else if (diagnostics.Regime == MarketRegime.TrendingDown && signal == TradeSignal.Sell)
                    conf += 0.05;
                else if (diagnostics.Regime == MarketRegime.VolatileChoppy)
                    conf -= 0.05;
            }

            // Clamp confidence
            if (conf < 0.5) conf = 0.5;
            if (conf > 0.95) conf = 0.95;

            result.Signal = signal;
            result.Confidence = conf;
            return result;
        }
public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;

            // Prefer clean structure: trending or higher-volatility ranges.
            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown ||
                   diagnostics.Regime == MarketRegime.RangingHighVol ||
                   diagnostics.Regime == MarketRegime.RangingLowVol;
        }
    }
public class VolatilityFilteredStrategy : ITradingStrategy, ITradeDurationProvider
    {
        private readonly ITradingStrategy _inner;
        private readonly string _name;

        private readonly double? _minVol;
        private readonly double? _maxVol;
        private readonly double? _trendThreshold;

        public string Name => _name;
        public int DefaultDuration => (_inner as ITradeDurationProvider)?.DefaultDuration ?? 1;
        public string DefaultDurationUnit => (_inner as ITradeDurationProvider)?.DefaultDurationUnit ?? "t";

        public VolatilityFilteredStrategy(
            ITradingStrategy inner,
            string label,
            double? minVol,
            double? maxVol,
            double? trendThreshold)
        {
            _inner = inner;
            _name = $"{inner.Name} [{label}]";
            _minVol = minVol;
            _maxVol = maxVol;
            _trendThreshold = trendThreshold;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var sd = context.GetStdDev(50);
            var slope = context.GetTrendSlope(50);

            if (sd != null)
            {
                if (_minVol != null && sd.Value < _minVol.Value)
                    return TradeSignal.None;

                if (_maxVol != null && sd.Value > _maxVol.Value)
                    return TradeSignal.None;
            }

            if (_trendThreshold != null && slope != null)
            {
                if (Math.Abs(slope.Value) < _trendThreshold.Value)
                    return TradeSignal.None;
            }

            return _inner.OnNewTick(tick, context);
        }
    }

    public class DebugPingPongStrategy : ITradingStrategy
    {
        private int _counter = 0;

        public string Name => "Debug PingPong";

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            if (context == null || context.TickWindow.Count < 5)
                return TradeSignal.None;

            _counter++;

            if (_counter % 5 == 0)
                return TradeSignal.Buy;

            return TradeSignal.None;
        }
    }

    #endregion

    #region Risk / Rules / Profiles

    public class RiskSettings
    {
        // Core risk parameters
        private double _riskPerTradeFraction = 0.01;       // 1% per trade
        private double _minStake = 0.35;                   // platform minimum
        private double _maxStake = 100.0;                  // safety cap

        // Daily risk / profit limits (fractions of balance)
        private double _maxDailyDrawdownFraction = 0.20;   // 20% of balance
        private double _maxDailyProfitFraction = 0.0;      // 0 = no profit cap as fraction

        // Absolute daily P/L caps (amounts)
        private double _maxDailyLossAmount = 00.0;         // e.g. hard $ loss limit (still active)
        private double _maxDailyProfitAmount = 0.0;        // 0 = no absolute daily profit cap

        // Streak / quality controls
        private int _maxConsecutiveLosses = 25;
        private int _minTradesBeforeWinRateCheck = 30;
        private double _minWinRatePercentToContinue = 55.0; // e.g. 55%

        // Dynamic stake controls
        private bool _enableDynamicStakeScaling = true;
        private double _maxStakeAsBalanceFraction = 0.05;   // e.g. 5% of balance
        private double _minConfidenceForDynamicStake = 0.60;
        private double _minRegimeScoreForDynamicStake = 0.60;
        private double _minHeatForDynamicStake = 45.0;

        // Percent-based daily controls (UI-friendly)
        private double _maxDailyDrawdownPercent = 2.0;      // e.g. 2% max drawdown per day
        private double _dailyProfitTarget = 0.0;            // 0 = NO daily profit cap

        // ========= CORE FRACTION-BASED PROPERTIES =========

        public double RiskPerTradeFraction
        {
            get => _riskPerTradeFraction;
            set => _riskPerTradeFraction = value;
        }

        public double MaxDailyDrawdownFraction
        {
            get => _maxDailyDrawdownFraction;
            set => _maxDailyDrawdownFraction = value;
        }

        public double MaxDailyProfitFraction
        {
            get => _maxDailyProfitFraction;
            set => _maxDailyProfitFraction = value;
        }

        // ========= BACKWARDS-COMPATIBLE ALIASES =========

        // Old name: RiskPerTrade
        public double RiskPerTrade
        {
            get => _riskPerTradeFraction;
            set => _riskPerTradeFraction = value;
        }

        // Old name: MaxDailyDrawdown
        public double MaxDailyDrawdown
        {
            get => _maxDailyDrawdownFraction;
            set => _maxDailyDrawdownFraction = value;
        }

        // Old name: MaxDailyProfit
        public double MaxDailyProfit
        {
            get => _maxDailyProfitFraction;
            set => _maxDailyProfitFraction = value;
        }

        // Old name: MaxDailyLoss
        public double MaxDailyLoss
        {
            get => _maxDailyLossAmount;
            set => _maxDailyLossAmount = value;
        }

        // ========= STAKE LIMITS =========

        public double MinStake
        {
            get => _minStake;
            set => _minStake = value;
        }

        public double MaxStake
        {
            get => _maxStake;
            set => _maxStake = value;
        }

        // ========= ABSOLUTE DAILY AMOUNTS =========

        public double MaxDailyLossAmount
        {
            get => _maxDailyLossAmount;
            set => _maxDailyLossAmount = value;
        }

        public double MaxDailyProfitAmount
        {
            get => _maxDailyProfitAmount;
            set => _maxDailyProfitAmount = value;
        }

        // ========= STREAK / QUALITY CONTROL =========

        public int MaxConsecutiveLosses
        {
            get => _maxConsecutiveLosses;
            set => _maxConsecutiveLosses = value;
        }

        public int MinTradesBeforeWinRateCheck
        {
            get => _minTradesBeforeWinRateCheck;
            set => _minTradesBeforeWinRateCheck = value;
        }

        public double MinWinRatePercentToContinue
        {
            get => _minWinRatePercentToContinue;
            set => _minWinRatePercentToContinue = value;
        }

        // ========= DYNAMIC STAKE SCALING =========

        public bool EnableDynamicStakeScaling
        {
            get => _enableDynamicStakeScaling;
            set => _enableDynamicStakeScaling = value;
        }

        public double MaxStakeAsBalanceFraction
        {
            get => _maxStakeAsBalanceFraction;
            set => _maxStakeAsBalanceFraction = value;
        }

        public double MinConfidenceForDynamicStake
        {
            get => _minConfidenceForDynamicStake;
            set => _minConfidenceForDynamicStake = value;
        }

        public double MinRegimeScoreForDynamicStake
        {
            get => _minRegimeScoreForDynamicStake;
            set => _minRegimeScoreForDynamicStake = value;
        }

        public double MinHeatForDynamicStake
        {
            get => _minHeatForDynamicStake;
            set => _minHeatForDynamicStake = value;
        }

        // ========= PERCENT-BASED DAILY CONTROLS =========

        public double MaxDailyDrawdownPercent
        {
            get => _maxDailyDrawdownPercent;
            set => _maxDailyDrawdownPercent = value;
        }

        /// <summary>
        /// Daily profit target in PERCENT of starting balance.
        /// 0 (or less) = disable daily profit cap completely.
        /// </summary>
        public double DailyProfitTarget
        {
            get => _dailyProfitTarget;
            set => _dailyProfitTarget = value;
        }
    }


    public class BotRules
    {
        public TimeSpan TradeCooldown { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxTradesPerHour { get; set; } = 5000;
        public int MaxOpenTrades { get; set; } = 1;
        public double MinMarketHeatToTrade { get; set; } = 35.0;
        public double MaxMarketHeatToTrade { get; set; } = 90.0;
        public double MinRegimeScoreToTrade { get; set; } = 0.55;
        public double MinEnsembleConfidence { get; set; } = 0.55;
        public double ExpectedProfitBlockThreshold { get; set; } = -0.05;
        public double ExpectedProfitWarnThreshold { get; set; } = 0.0;
        public int MinTradesBeforeMl { get; set; } = 50;
        public double MinVolatilityToTrade { get; set; } = 0.02;
        public double MaxVolatilityToTrade { get; set; } = 2.0;
        public int LossCooldownMultiplierSeconds { get; set; } = 3;
        public int MaxLossCooldownSeconds { get; set; } = 60;
        public int StrategyProbationMinTrades { get; set; } = 20;
        public double StrategyProbationWinRate { get; set; } = 45.0;
        public int StrategyProbationBlockMinutes { get; set; } = 30;
        public int StrategyProbationLossBlockMinutes { get; set; } = 15;
        public double HighHeatRotationThreshold { get; set; } = 60.0;
        public int HighHeatRotationIntervalSeconds { get; set; } = 60;
        public double RotationScoreDelta { get; set; } = 8.0;
        public double RotationScoreDeltaHighHeat { get; set; } = 3.0;


        public TimeSpan? SessionStartLocal { get; set; } = null;
        public TimeSpan? SessionEndLocal { get; set; } = null;
    }

    public class RiskManager
    {
        private RiskSettings _settings;
        private double _dailyStartBalance;
        private double _maxBalanceSeenToday;

        /// <summary>
        /// Current settings snapshot (used by TradingCore and DynamicRiskHelper).
        /// </summary>
        public RiskSettings Settings => _settings;

        public RiskManager(RiskSettings settings)
        {
            _settings = settings ?? new RiskSettings();
        }

        /// <summary>
        /// Called whenever the user changes profile/risk, or when a new session starts.
        /// </summary>
        public void UpdateSettings(RiskSettings newSettings)
        {
            if (newSettings == null)
                return;

            _settings = newSettings;
        }

        /// <summary>
        /// Reset daily state at the start of a new session/day.
        /// </summary>
        public void ResetForNewSession(double currentBalance)
        {
            _dailyStartBalance = currentBalance;
            _maxBalanceSeenToday = currentBalance;
        }

        /// <summary>
        /// Compute a "base stake" from current balance and risk fraction.
        /// TradingCore can then feed this into DynamicRiskHelper for additional refinements.
        /// </summary>
        public double ComputeStake(double balance)
        {
            if (balance <= 0)
                return _settings.MinStake;

            // basic risk per trade
            var stake = balance * _settings.RiskPerTradeFraction;

            return ClampStake(stake);
        }

        /// <summary>
        /// Ensure the stake remains within both absolute and balance-relative bounds.
        /// </summary>
        public double ClampStake(double stake)
        {
            if (stake < _settings.MinStake)
                stake = _settings.MinStake;

            if (_settings.EnableDynamicStakeScaling && _settings.MaxStakeAsBalanceFraction > 0)
            {
                // The actual balance will be supplied by caller and stake will be pre-computed,
                // so this method is mainly enforcing hard caps.
                // You can extend this to receive balance if you want tighter coupling.
            }

            if (stake > _settings.MaxStake)
                stake = _settings.MaxStake;

            return stake;
        }

        /// <summary>
        /// Should be called after each completed trade so the risk manager can track
        /// today's equity high and enforce drawdown limits.
        /// </summary>
        public void RegisterTradeResult(double currentBalance)
        {
            if (currentBalance > _maxBalanceSeenToday)
                _maxBalanceSeenToday = currentBalance;
        }

        /// <summary>
        /// Check if the daily % drawdown from the highest equity point today has been exceeded.
        /// </summary>
        public bool IsDailyDrawdownExceeded(double dailyStartBalance, double currentBalance)
        {
            // Ensure we have a baseline
            if (_dailyStartBalance <= 0)
                _dailyStartBalance = dailyStartBalance;

            if (currentBalance > _maxBalanceSeenToday)
                _maxBalanceSeenToday = currentBalance;

            var peakEquity = Math.Max(_dailyStartBalance, _maxBalanceSeenToday);

            if (peakEquity <= 0 || _settings.MaxDailyDrawdownPercent <= 0)
                return false;

            var drawdown = (peakEquity - currentBalance) / peakEquity * 100.0;
            return drawdown >= _settings.MaxDailyDrawdownPercent;
        }

        /// <summary>
        /// Check if the absolute daily loss amount limit has been exceeded.
        /// </summary>
        public bool IsDailyLossAmountExceeded(double dailyStartBalance, double currentBalance)
        {
            if (_settings.MaxDailyLossAmount <= 0)
                return false;

            var loss = dailyStartBalance - currentBalance;
            return loss >= _settings.MaxDailyLossAmount;
        }

        /// <summary>
        /// Check if the daily profit target has been reached.
        /// </summary>
        public bool IsDailyProfitLimitReached(double dailyStartBalance, double currentBalance)
        {
            if (_settings.DailyProfitTarget <= 0)
                return false;

            var profit = currentBalance - dailyStartBalance;
            return profit >= _settings.DailyProfitTarget;
        }
    }


    public enum BotProfile
    {
        Conservative,
        Balanced,
        Aggressive
    }

    public class BotProfileConfig
    {
        public RiskSettings Risk { get; set; }
        public BotRules Rules { get; set; }

        public static BotProfileConfig ForProfile(BotProfile profile)
        {
            switch (profile)
            {
                case BotProfile.Conservative:
                    return new BotProfileConfig
                    {
                        Risk = new RiskSettings
                        {
                            RiskPerTradeFraction = 0.005,
                            MinStake = 0.35,
                            MaxStake = 5.0,
                            MaxDailyDrawdownFraction = 0.1,
                            MaxDailyProfitFraction = 0.15,
                            MaxConsecutiveLosses = 5,
                            MinWinRatePercentToContinue = 50,
                            MinTradesBeforeWinRateCheck = 40,
                            MinConfidenceForDynamicStake = 0.65,
                            MinRegimeScoreForDynamicStake = 0.65,
                            MinHeatForDynamicStake = 50.0
                        },
                        Rules = new BotRules
                        {
                            TradeCooldown = TimeSpan.FromSeconds(20),
                            MaxTradesPerHour = 5000,
                            MinMarketHeatToTrade = 40.0,
                            MaxMarketHeatToTrade = 85.0,
                            MinRegimeScoreToTrade = 0.60,
                            MinEnsembleConfidence = 0.60,
                            ExpectedProfitBlockThreshold = -0.05,
                            ExpectedProfitWarnThreshold = 0.0,
                            MinTradesBeforeMl = 80,
                            LossCooldownMultiplierSeconds = 4,
                            MaxLossCooldownSeconds = 90,
                            StrategyProbationMinTrades = 25,
                            StrategyProbationWinRate = 50.0,
                            StrategyProbationBlockMinutes = 45,
                            StrategyProbationLossBlockMinutes = 20,
                            HighHeatRotationThreshold = 65.0,
                            HighHeatRotationIntervalSeconds = 60,
                            RotationScoreDelta = 10.0,
                            RotationScoreDeltaHighHeat = 3.0
                        }
                    };
                case BotProfile.Aggressive:
                    return new BotProfileConfig
                    {
                        Risk = new RiskSettings
                        {
                            RiskPerTradeFraction = 0.03,
                            MinStake = 0.35,
                            MaxStake = 100.0,
                            MaxDailyDrawdownFraction = 0.4,
                            MaxDailyProfitFraction = 0.6,
                            MaxConsecutiveLosses = 12,
                            MinWinRatePercentToContinue = 0,
                            MinConfidenceForDynamicStake = 0.55,
                            MinRegimeScoreForDynamicStake = 0.55,
                            MinHeatForDynamicStake = 40.0
                        },
                        Rules = new BotRules
                        {
                            TradeCooldown = TimeSpan.FromSeconds(3),
                            MaxTradesPerHour = 60,
                            MinMarketHeatToTrade = 30.0,
                            MaxMarketHeatToTrade = 95.0,
                            MinRegimeScoreToTrade = 0.50,
                            MinEnsembleConfidence = 0.50,
                            ExpectedProfitBlockThreshold = -0.10,
                            ExpectedProfitWarnThreshold = -0.02,
                            MinTradesBeforeMl = 30,
                            LossCooldownMultiplierSeconds = 2,
                            MaxLossCooldownSeconds = 45,
                            StrategyProbationMinTrades = 15,
                            StrategyProbationWinRate = 42.0,
                            StrategyProbationBlockMinutes = 20,
                            StrategyProbationLossBlockMinutes = 12,
                            HighHeatRotationThreshold = 55.0,
                            HighHeatRotationIntervalSeconds = 45,
                            RotationScoreDelta = 6.0,
                            RotationScoreDeltaHighHeat = 2.0
                        }
                    };
                case BotProfile.Balanced:
                default:
                    return new BotProfileConfig
                    {
                        Risk = new RiskSettings
                        {
                            RiskPerTradeFraction = 0.01,
                            MinStake = 0.35,
                            MaxStake = 25.0,
                            MaxDailyDrawdownFraction = 0.25,
                            MaxDailyProfitFraction = 0.4,
                            MaxConsecutiveLosses = 8,
                            MinWinRatePercentToContinue = 45,
                            MinTradesBeforeWinRateCheck = 40,
                            MinConfidenceForDynamicStake = 0.60,
                            MinRegimeScoreForDynamicStake = 0.60,
                            MinHeatForDynamicStake = 45.0
                        },
                        Rules = new BotRules
                        {
                            TradeCooldown = TimeSpan.FromSeconds(8),
                            MaxTradesPerHour = 5000,
                            MinMarketHeatToTrade = 35.0,
                            MaxMarketHeatToTrade = 90.0,
                            MinRegimeScoreToTrade = 0.55,
                            MinEnsembleConfidence = 0.55,
                            ExpectedProfitBlockThreshold = -0.05,
                            ExpectedProfitWarnThreshold = 0.0,
                            MinTradesBeforeMl = 50,
                            LossCooldownMultiplierSeconds = 3,
                            MaxLossCooldownSeconds = 60,
                            StrategyProbationMinTrades = 20,
                            StrategyProbationWinRate = 45.0,
                            StrategyProbationBlockMinutes = 30,
                            StrategyProbationLossBlockMinutes = 15,
                            HighHeatRotationThreshold = 60.0,
                            HighHeatRotationIntervalSeconds = 60,
                            RotationScoreDelta = 8.0,
                            RotationScoreDeltaHighHeat = 3.0
                        }
                    };
            }
        }
    }

    #endregion

    #region AI Helpers (Heat Index, Ensemble, Expected Profit)

    public static class AIHelpers
    {
        public static double ComputeMarketHeatIndex(StrategyContext ctx, MarketDiagnostics diag)
        {
            if (ctx == null || diag == null || ctx.TickWindow.Count < 20)
                return 0.0;

            double score = 50.0;

            // Volatility
            if (diag.Volatility is double vol)
            {
                if (vol < 0.05) score -= 15;
                else if (vol < 0.15) score += 5;
                else if (vol < 0.5) score += 10;
                else if (vol < 1.5) score -= 5;
                else score -= 15;
            }

            // Trend slope
            if (diag.TrendSlope is double slope)
            {
                double abs = Math.Abs(slope);
                if (abs < 0.0001) score -= 5;
                else if (abs < 0.0005) score += 5;
                else if (abs < 0.002) score += 10;
                else score -= 10;
            }

            // Regime modifier
            switch (diag.Regime)
            {
                case MarketRegime.TrendingUp:
                case MarketRegime.TrendingDown:
                    score += 10; break;
                case MarketRegime.RangingHighVol:
                    score += 5; break;
                case MarketRegime.RangingLowVol:
                    score += 0; break;
                case MarketRegime.VolatileChoppy:
                    score -= 15; break;
                case MarketRegime.Unknown:
                    score -= 10; break;
            }

            // Smoothness / spikes
            var quotes = ctx.GetLastQuotes(25);
            if (quotes.Count >= 5)
            {
                double mean = quotes.Average();
                double sd = Math.Sqrt(quotes.Average(q => (q - mean) * (q - mean)));
                if (sd > 0)
                {
                    int spikeCount = 0;
                    for (int i = 1; i < quotes.Count; i++)
                    {
                        double step = Math.Abs(quotes[i] - quotes[i - 1]);
                        if (step > 3.0 * sd)
                            spikeCount++;
                    }
                    if (spikeCount >= 1) score -= 10;
                    if (spikeCount >= 3) score -= 10;
                }
            }

            if (score < 0) score = 0;
            if (score > 100) score = 100;

            return score;
        }

        public static StrategyDecision EnsembleVote(IEnumerable<StrategyDecision> decisions)
        {
            var list = decisions.Where(d => d.Signal != TradeSignal.None && d.Confidence > 0.0).ToList();
            if (list.Count == 0)
            {
                return new StrategyDecision
                {
                    StrategyName = "Ensemble",
                    Signal = TradeSignal.None,
                    Confidence = 0.0
                };
            }

            int buyVotes = list.Count(d => d.Signal == TradeSignal.Buy);
            int sellVotes = list.Count(d => d.Signal == TradeSignal.Sell);

            double buyScore = list.Where(d => d.Signal == TradeSignal.Buy).Sum(d => d.Confidence);
            double sellScore = list.Where(d => d.Signal == TradeSignal.Sell).Sum(d => d.Confidence);

            if (buyScore == 0 && sellScore == 0)
            {
                return new StrategyDecision
                {
                    StrategyName = "Ensemble",
                    Signal = TradeSignal.None,
                    Confidence = 0.0
                };
            }

            TradeSignal finalSignal;
            double finalScore;
            int finalVotes;
            StrategyDecision? durationSource;

            if (buyScore > sellScore)
            {
                finalSignal = TradeSignal.Buy;
                finalScore = buyScore;
                finalVotes = buyVotes;
                durationSource = list.Where(d => d.Signal == TradeSignal.Buy)
                                     .OrderByDescending(d => d.Confidence)
                                     .FirstOrDefault();
            }
            else
            {
                finalSignal = TradeSignal.Sell;
                finalScore = sellScore;
                finalVotes = sellVotes;
                durationSource = list.Where(d => d.Signal == TradeSignal.Sell)
                                     .OrderByDescending(d => d.Confidence)
                                     .FirstOrDefault();
            }

            if (finalVotes < 2 && finalScore < 1.0)
            {
                return new StrategyDecision
                {
                    StrategyName = "Ensemble",
                    Signal = TradeSignal.None,
                    Confidence = 0.0
                };
            }

            double combinedConfidence = finalScore / (buyScore + sellScore + 1e-6);
            if (combinedConfidence > 0.99) combinedConfidence = 0.99;

            return new StrategyDecision
            {
                StrategyName = "Ensemble",
                Signal = finalSignal,
                Confidence = combinedConfidence,
                Duration = durationSource?.Duration ?? 1,
                DurationUnit = durationSource?.DurationUnit ?? "t"
            };
        }

        public static double EstimateExpectedProfitScore(
            StrategyDecision decision,
            MarketDiagnostics diag,
            IEnumerable<TradeRecord> recentTrades)
        {
            if (decision == null || decision.Signal == TradeSignal.None)
                return 0.0;

            double score = 0.0;

            var sameStrategy = recentTrades
                .Where(t => t.StrategyName == decision.StrategyName)
                .OrderByDescending(t => t.Time)
                .Take(30)
                .ToList();

            if (sameStrategy.Count >= 10)
            {
                int wins = sameStrategy.Count(t => t.Profit >= 0);
                double winRate = (double)wins / sameStrategy.Count;
                score += (winRate - 0.5) * 1.0;
            }

            var lastGlobal = recentTrades.OrderByDescending(t => t.Time).Take(20).ToList();
            if (lastGlobal.Count >= 5)
            {
                double sumPL = lastGlobal.Sum(t => t.Profit);
                score += Math.Tanh(sumPL / 10.0) * 0.5;
            }

            if (diag != null && diag.TrendSlope is double slope)
            {
                if (diag.Regime == MarketRegime.TrendingUp && decision.Signal == TradeSignal.Buy)
                    score += 0.3;
                else if (diag.Regime == MarketRegime.TrendingDown && decision.Signal == TradeSignal.Sell)
                    score += 0.3;
                else if (diag.Regime == MarketRegime.VolatileChoppy)
                    score -= 0.4;
            }

            if (score > 1.0) score = 1.0;
            if (score < -1.0) score = -1.0;
            return score;
        }
    }

    #endregion

    #region SmartBotController

    public enum AutoSymbolMode
    {
        Manual = 0,
        Auto = 1
    }

    public enum AutoPauseReason
    {
        None = 0,
        DailyDrawdownLimit,
        DailyLossAmountLimit,
        DailyProfitLimit,
        ConsecutiveLossLimit,
        WinRateBelowThreshold
    }

    public class SmartBotController
    {
        // Toggle for environment relaxation when testing on DEMO / ML.
        // This can be flipped at runtime from the UI/profile.
        // Set to false again for live / conservative use.
        private bool _relaxEnvironmentForTesting = false;
        public bool DisableGlobalRiskGatesForTesting { get; set; } = false;

        private DateTime _sessionStartTime = DateTime.MinValue;



        /// <summary>
        /// When true, environment filters (regime + volatility band) are relaxed for testing.
        /// Hard safety gates (unknown regime, extreme slope, 3-sigma spikes) remain enforced.
        /// </summary>
        public bool RelaxEnvironmentForTesting
        {
            get => _relaxEnvironmentForTesting;
            set => _relaxEnvironmentForTesting = value;
        }

        public bool ForwardTestEnabled { get; set; } = false;

        private readonly RiskManager _riskManager;
        private readonly List<ITradingStrategy> _strategies;
        private BotRules _rules;
        private readonly DerivWebSocketClient _deriv;
        private readonly StrategyContext _context = new StrategyContext();
        private readonly object _stateLock = new();

        // NEW: optional AI/ML helpers
        private readonly IFeatureExtractor? _featureExtractor;
        private readonly ITradeDataLogger? _tradeLogger;
        private IStrategySelector? _strategySelector;
        private readonly IStrategySelector _fallbackSelector = new RuleBasedStrategySelector();

        private readonly Dictionary<string, StrategyStats> _strategyStats = new();
        private readonly List<TradeRecord> _tradeHistory = new();
        private readonly Dictionary<Guid, TradeRecord> _openTrades = new();
        private readonly Dictionary<Guid, ForwardTestTrade> _forwardTrades = new();

        private sealed class ForwardTestTrade
        {
            public Guid TradeId { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public TradeSignal Direction { get; set; }
            public double EntryPrice { get; set; }
            public DateTime EntryTime { get; set; }
            public int RemainingTicks { get; set; }
            public DateTime? ExitTime { get; set; }
            public double Stake { get; set; }
            public double PayoutMultiplier { get; set; }
        }

        private static (int Duration, string Unit) GetDefaultDuration(ITradingStrategy strategy)
        {
            if (strategy is ITradeDurationProvider provider &&
                provider.DefaultDuration > 0 &&
                !string.IsNullOrWhiteSpace(provider.DefaultDurationUnit))
            {
                return (provider.DefaultDuration, provider.DefaultDurationUnit);
            }

            return (1, "t");
        }

        private static void EnsureDecisionDuration(StrategyDecision decision, ITradingStrategy strategy)
        {
            if (decision == null || strategy == null)
                return;

            var (duration, unit) = GetDefaultDuration(strategy);

            if (decision.Duration <= 0)
                decision.Duration = duration;

            if (string.IsNullOrWhiteSpace(decision.DurationUnit))
                decision.DurationUnit = unit;
        }

        private readonly Dictionary<string, DateTime> _strategyBlockedUntil = new();
        private bool _shortTermPLBlockActive;
        private DateTime _shortTermPLBlockUntil;

        private double _balance;
        private double _sessionStartBalance;
        private bool _running;
        private bool _autoPaused;
        private bool _userStopped;
        private AutoPauseReason _autoPauseReason = AutoPauseReason.None;

        private DateTime _lastTradeTime = DateTime.MinValue;
        private readonly List<DateTime> _tradeTimes = new();

        private int _consecutiveLosses = 0;

        // Multi-symbol
        private readonly List<string> _symbolsToWatch = new();
        private AutoSymbolMode _autoSymbolMode = AutoSymbolMode.Manual;
        private string _activeSymbol;

        // Per-symbol analytics for auto-rotation
        private readonly Dictionary<string, SymbolPerformanceStats> _symbolStats = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StrategyContext> _symbolContexts = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastSymbolRotationTime = DateTime.MinValue;
        private readonly TimeSpan _minSymbolRotationInterval = TimeSpan.FromMinutes(3);

        // Skip-reason tracking (for UI + future AI/ML)
        private string _lastSkipReason;
        private string _lastSkipCode;
        private string _lastSkipMessage;
        private DateTime _lastSkipTimestamp = DateTime.MinValue;
        private int _lastSkipRepeatCount = 0;
        private const int MaxSkipReasonLogEntries = 500;
        private readonly List<string> _skipReasonLog = new();

        public MarketDiagnostics CurrentDiagnostics { get; private set; } = new MarketDiagnostics();
        public double MarketHeatScore { get; private set; } = 0.0;

        public event Action<string> BotEvent;

        public bool AutoStartEnabled { get; set; } = true;
        private readonly int _minTicksBeforeAutoStart = 80;
        private readonly double _minHeatForAutoStart = 55.0;

        private IMarketRegimeClassifier _regimeClassifier;

        public SmartBotController(
            RiskManager riskManager,
            IEnumerable<ITradingStrategy> strategies,
            BotRules rules,
            DerivWebSocketClient deriv,
            IMarketRegimeClassifier? regimeClassifier = null,
            IFeatureExtractor? featureExtractor = null,
            ITradeDataLogger? tradeLogger = null,
            IStrategySelector? strategySelector = null)
        {
            _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
            _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _deriv = deriv ?? throw new ArgumentNullException(nameof(deriv));

            _regimeClassifier = regimeClassifier ?? new AiMarketRegimeClassifier();
            _featureExtractor = featureExtractor;
            _tradeLogger = tradeLogger ?? new CsvTradeDataLogger();
            _strategySelector = strategySelector;

            _deriv.TickReceived += OnTickReceived;
            _deriv.BalanceUpdated += OnBalanceUpdated;
            _deriv.ContractFinished += OnContractFinished;
            _deriv.BuyError += OnBuyError;

            foreach (var s in _strategies)
            {
                if (!_strategyStats.ContainsKey(s.Name))
                    _strategyStats[s.Name] = new StrategyStats();
            }
        }


        public bool IsRunning => _running && !_autoPaused;
        public bool IsConnected => _deriv?.IsConnected ?? false;

        public double Balance
        {
            get
            {
                lock (_stateLock)
                {
                    return _balance;
                }
            }
        }

        public double TodaysPL
        {
            get
            {
                lock (_stateLock)
                {
                    return _balance - _sessionStartBalance;
                }
            }
        }

        public string ActiveStrategyName { get; private set; }

        public AutoPauseReason LastAutoPauseReason
        {
            get
            {
                lock (_stateLock)
                {
                    return _autoPauseReason;
                }
            }
        }

        public int ConsecutiveLosses
        {
            get
            {
                lock (_stateLock)
                {
                    return _consecutiveLosses;
                }
            }
        }

        public IReadOnlyDictionary<string, StrategyStats> StrategyStats
        {
            get
            {
                lock (_stateLock)
                {
                    return _strategyStats.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new StrategyStats
                        {
                            Wins = kvp.Value.Wins,
                            Losses = kvp.Value.Losses,
                            NetPL = kvp.Value.NetPL
                        });
                }
            }
        }

        public IReadOnlyList<TradeRecord> TradeHistory
        {
            get
            {
                lock (_stateLock)
                {
                    return _tradeHistory
                        .Select(t => new TradeRecord
                        {
                            Time = t.Time,
                            Symbol = t.Symbol,
                            StrategyName = t.StrategyName,
                            Direction = t.Direction,
                            Stake = t.Stake,
                            Profit = t.Profit
                        })
                        .ToList();
                }
            }
        }

        public IReadOnlyDictionary<string, SymbolPerformanceStats> SymbolStats
        {
            get
            {
                lock (_stateLock)
                {
                    return _symbolStats.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new SymbolPerformanceStats
                        {
                            Symbol = kvp.Value.Symbol,
                            LastHeat = kvp.Value.LastHeat,
                            LastRegime = kvp.Value.LastRegime,
                            LastRegimeScore = kvp.Value.LastRegimeScore,
                            Wins = kvp.Value.Wins,
                            Losses = kvp.Value.Losses,
                            NetPL = kvp.Value.NetPL,
                            LastUpdated = kvp.Value.LastUpdated,
                            IsDisabledForSession = kvp.Value.IsDisabledForSession
                        });
                }
            }
        }

        public IReadOnlyList<string> SymbolsToWatch
        {
            get
            {
                lock (_stateLock)
                {
                    return _symbolsToWatch.ToList();
                }
            }
        }

        public AutoSymbolMode AutoSymbolMode
        {
            get
            {
                lock (_stateLock)
                {
                    return _autoSymbolMode;
                }
            }
        }

        public string ActiveSymbol
        {
            get
            {
                lock (_stateLock)
                {
                    return _activeSymbol;
                }
            }
        }


        public string LastSkipReason
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastSkipReason;
                }
            }
        }

        public IReadOnlyList<string> SkipReasonLog
        {
            get
            {
                lock (_stateLock)
                {
                    return _skipReasonLog.ToList();
                }
            }
        }


        public void SetSymbolsToWatch(IEnumerable<string> symbols)
        {
            lock (_stateLock)
            {
                _symbolsToWatch.Clear();

                if (symbols != null)
                {
                    foreach (var s in symbols)
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            var trimmed = s.Trim();
                            if (!_symbolsToWatch.Contains(trimmed))
                                _symbolsToWatch.Add(trimmed);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(_activeSymbol) && _symbolsToWatch.Count > 0)
                    _activeSymbol = _symbolsToWatch[0];

                RaiseBotEvent($"Symbols to watch: {string.Join(", ", _symbolsToWatch)}");
            }
        }

        public void SetAutoSymbolMode(AutoSymbolMode mode)
        {
            _autoSymbolMode = mode;
            RaiseBotEvent($"Auto symbol mode set to {mode}.");
        }

        public void UpdateStrategySelector(IStrategySelector? selector)
        {
            lock (_stateLock)
            {
                _strategySelector = selector;
            }
            RaiseBotEvent("[ML] Strategy selector updated.");
        }

        public void UpdateRegimeClassifier(IMarketRegimeClassifier classifier)
        {
            if (classifier == null)
                return;

            lock (_stateLock)
            {
                _regimeClassifier = classifier;
            }
            RaiseBotEvent("[ML] Regime classifier updated.");
        }

        public void SetActiveSymbol(string symbol)
        {
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                _activeSymbol = symbol.Trim();
                _context.TickWindow.Clear();
                RaiseBotEvent($"Active symbol set to {_activeSymbol}.");
            }
        }

        public void Start()
        {
            bool blockedByAutoPause = false;
            AutoPauseReason reasonSnapshot = AutoPauseReason.None;

            lock (_stateLock)
            {
                // Already running (and not paused) => ignore duplicate start calls.
                if (_running && !_autoPaused)
                    return;

                // If auto-paused by a risk gate, do not allow Start() to immediately resume
                // unless the user changes settings or you explicitly clear the pause.
                if (_autoPaused && _autoPauseReason != AutoPauseReason.None)
                {
                    blockedByAutoPause = true;
                    reasonSnapshot = _autoPauseReason;
                }
                else
                {
                    _autoPaused = false;
                    _userStopped = false;
                    _autoPauseReason = AutoPauseReason.None;
                    _running = true;

                    _sessionStartBalance = _balance;
                    _sessionStartTime = DateTime.Now;

                    _consecutiveLosses = 0;
                }
            }

            if (blockedByAutoPause)
            {
                SetSkipReason("AUTO_PAUSED",
                    $"Bot is auto-paused due to {reasonSnapshot}. Adjust risk settings (win-rate gate) or clear pause before starting.");
                RaiseBotEvent($"Start blocked: bot is auto-paused ({reasonSnapshot}).");
                return;
            }

            SetSkipReason(null, "Bot actively trading.");
            RaiseBotEvent("Bot started.");
        }

        public void ClearAutoPause()
        {
            lock (_stateLock)
            {
                _autoPaused = false;
                _autoPauseReason = AutoPauseReason.None;
                _consecutiveLosses = 0;
                if (_balance > 0)
                {
                    _sessionStartBalance = _balance;
                    _sessionStartTime = DateTime.Now;
                    _riskManager.ResetForNewSession(_balance);
                }
            }

            SetSkipReason("AUTO_PAUSE_CLEARED", "Auto-pause cleared by user; baseline reset.");
            RaiseBotEvent("Auto-pause cleared by user; baseline reset.");
        }


        public void Stop()
        {
            _running = false;
            _userStopped = true;
            SetSkipReason("MANUAL_STOP", "User manually stopped the bot.");
            RaiseBotEvent("Bot stopped.");
        }

        public void UpdateConfigs(RiskSettings risk, BotRules rules)
        {
            _riskManager.UpdateSettings(risk);
            _rules = rules;
            RaiseBotEvent("Risk and rules updated.");
        }

        private void OnBalanceUpdated(double bal)
        {
            if (_sessionStartBalance <= 0)
                _sessionStartBalance = bal;

            _balance = bal;

            _riskManager.RegisterTradeResult(bal);
        }

        private StrategyContext GetOrCreateSymbolContext(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "UNKNOWN";

            if (!_symbolContexts.TryGetValue(symbol, out var ctx))
            {
                ctx = new StrategyContext();
                _symbolContexts[symbol] = ctx;
            }

            return ctx;
        }

        private SymbolPerformanceStats GetOrCreateSymbolStats(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "UNKNOWN";

            lock (_stateLock)
            {
                if (!_symbolStats.TryGetValue(symbol, out var stats))
                {
                    stats = new SymbolPerformanceStats { Symbol = symbol };
                    _symbolStats[symbol] = stats;
                }

                return stats;
            }
        }


        private void UpdatePerSymbolState(Tick tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Symbol))
                return;

            var symbol = tick.Symbol.Trim();
            var ctx = GetOrCreateSymbolContext(symbol);
            ctx.AddTick(tick);

            var diag = ctx.AnalyzeRegime() ?? new MarketDiagnostics();

            var quotes = ctx.GetLastQuotes(120);
            if (_regimeClassifier != null && quotes.Count >= 20)
            {
                double aiScore;
                var aiRegime = _regimeClassifier.Classify(
                    quotes,
                    diag.Volatility,
                    diag.TrendSlope,
                    out aiScore);

                diag.Regime = aiRegime;
                diag.RegimeScore = aiScore;
            }

            var stats = GetOrCreateSymbolStats(symbol);
            stats.Symbol = symbol;
            stats.LastRegime = diag.Regime;
            if (diag.RegimeScore.HasValue)
                stats.LastRegimeScore = diag.RegimeScore.Value;
            stats.LastHeat = AIHelpers.ComputeMarketHeatIndex(ctx, diag);
            stats.LastUpdated = diag.Time;
        }

        private double ComputeSymbolCompositeScore(string symbol, SymbolPerformanceStats stats)
        {
            if (stats == null)
                return 0.0;

            double heat = stats.LastHeat;
            double perfComponent = stats.TotalTrades >= 5 ? stats.WinRate : 50.0;

            double regimeBase = stats.LastRegime switch
            {
                MarketRegime.TrendingUp or MarketRegime.TrendingDown => 70.0,
                MarketRegime.RangingHighVol => 65.0,
                MarketRegime.RangingLowVol => 55.0,
                MarketRegime.VolatileChoppy => 60.0,
                _ => 50.0
            };

            double regimeComponent = regimeBase;
            if (stats.LastRegimeScore > 0)
            {
                var clamped = Math.Clamp(stats.LastRegimeScore, 0.0, 1.0);
                regimeComponent += 20.0 * clamped;
            }

            // Symbol-specific tuning for synthetic indices
            double symbolBias = 1.0;
            var sym = symbol?.ToUpperInvariant() ?? string.Empty;

            if (sym.Contains("BOOM") || sym.Contains("CRASH"))
            {
                symbolBias = 1.15;
            }
            else if (sym.Contains("V") || sym.Contains("VOL"))
            {
                symbolBias = 1.10;
            }
            else if (sym.Contains("R_10") || sym.Contains("R10"))
            {
                symbolBias = 0.95;
            }

            double heatWeight = 0.45;
            double perfWeight = 0.35;
            double regimeWeight = 0.20;

            if (heat >= _rules.HighHeatRotationThreshold)
            {
                heatWeight = 0.60;
                perfWeight = 0.25;
                regimeWeight = 0.15;
            }

            double composite = heatWeight * heat + perfWeight * perfComponent + regimeWeight * regimeComponent;
            composite *= symbolBias;

            if (heat >= _rules.HighHeatRotationThreshold && stats.LastRegimeScore < _rules.MinRegimeScoreToTrade)
                composite *= 0.85;

            if (stats.LastUpdated != default)
            {
                var ageMinutes = (DateTime.Now - stats.LastUpdated).TotalMinutes;
                if (ageMinutes > 5)
                    composite *= 0.6;
                else if (ageMinutes > 2)
                    composite *= 0.85;
            }

            return composite;
        }

        private void TryRotateActiveSymbol(Tick latestTick)
        {
            if (_autoSymbolMode != AutoSymbolMode.Auto)
                return;

            if (_symbolsToWatch == null || _symbolsToWatch.Count == 0)
                return;

            var now = DateTime.Now;

            var candidates = new List<(string Symbol, SymbolPerformanceStats Stats, double Score)>();

            foreach (var symbol in _symbolsToWatch)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                if (!_symbolStats.TryGetValue(symbol, out var stats))
                    continue;

                // Skip symbols that have been disabled for this session (e.g. InvalidOfferings from Deriv)
                if (stats.IsDisabledForSession)
                    continue;

                if (stats.LastUpdated == default)
                    continue;

                if ((now - stats.LastUpdated).TotalSeconds > 180)
                    continue;

                var score = ComputeSymbolCompositeScore(symbol, stats);
                candidates.Add((symbol, stats, score));
            }

            if (candidates.Count == 0)
                return;

            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            var best = candidates[0];

            var minInterval = _minSymbolRotationInterval;
            if (best.Stats.LastHeat >= _rules.HighHeatRotationThreshold)
            {
                minInterval = TimeSpan.FromSeconds(_rules.HighHeatRotationIntervalSeconds);
            }

            if ((now - _lastSymbolRotationTime) < minInterval)
                return;

            var current = _activeSymbol;
            if (string.IsNullOrWhiteSpace(current))
            {
                SetActiveSymbol(best.Symbol);
                _lastSymbolRotationTime = now;
                return;
            }

            double currentScore = 0.0;
            if (_symbolStats.TryGetValue(current, out var currentStats))
            {
                currentScore = ComputeSymbolCompositeScore(current, currentStats);
            }

            if (best.Symbol.Equals(current, StringComparison.OrdinalIgnoreCase))
                return;

            var scoreDelta = best.Stats.LastHeat >= _rules.HighHeatRotationThreshold
                ? _rules.RotationScoreDeltaHighHeat
                : _rules.RotationScoreDelta;

            if (best.Score < currentScore + scoreDelta)
                return;

            RaiseBotEvent($"Auto-rotation: switching active symbol from {current} to {best.Symbol} (score {best.Score:F1} vs {currentScore:F1}).");

            SetActiveSymbol(best.Symbol);
            _lastSymbolRotationTime = now;
        }

private void OnTickReceived(Tick tick)
        {
            if (tick == null)
                return;

            // Always update per-symbol analytics (for rotation and UI)
            UpdatePerSymbolState(tick);

            // Optionally rotate active symbol
            TryRotateActiveSymbol(tick);

            // Only drive the main context from the active symbol
            if (!string.IsNullOrWhiteSpace(_activeSymbol) &&
                !string.Equals(tick.Symbol, _activeSymbol, StringComparison.OrdinalIgnoreCase))
            {
                // We used this tick for analytics above but do not trade on it.
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeSymbol) && !string.IsNullOrWhiteSpace(tick.Symbol))
            {
                _activeSymbol = tick.Symbol;
            }

            _context.AddTick(tick);
            UpdateForwardTrades(tick);

            var baseDiag = _context.AnalyzeRegime() ?? new MarketDiagnostics();

            var quotes = _context.GetLastQuotes(120);
            double aiScore = 0.0;
            var aiRegime = _regimeClassifier.Classify(
                quotes,
                baseDiag.Volatility,
                baseDiag.TrendSlope,
                out aiScore);

            baseDiag.Regime = aiRegime;
            baseDiag.RegimeScore = aiScore;
            baseDiag.Time = tick.Time;

            CurrentDiagnostics = baseDiag;

            MarketHeatScore = AIHelpers.ComputeMarketHeatIndex(_context, CurrentDiagnostics);

            if (!_running &&
                !_autoPaused &&
                !_userStopped &&
                _autoPauseReason == AutoPauseReason.None &&
                AutoStartEnabled &&
                _context.TickWindow.Count >= _minTicksBeforeAutoStart &&
                MarketHeatScore >= _minHeatForAutoStart &&
                IsGoodTradingConditions())
            {
                Start();
                string volText = CurrentDiagnostics.Volatility.HasValue
                    ? CurrentDiagnostics.Volatility.Value.ToString("F4")
                    : "n/a";
                RaiseBotEvent(
                    $"Auto-started bot based on favorable market conditions " +
                    $"(Symbol: {_activeSymbol ?? tick.Symbol}, Regime: {CurrentDiagnostics.Regime}, Score={CurrentDiagnostics.RegimeScore:F2}, Heat={MarketHeatScore:F1}, Vol={volText}).");
            }

            if (!IsRunning)
            {
                // If we are auto-paused due to a global risk gate, keep that specific reason.
                if (_autoPaused && _autoPauseReason != AutoPauseReason.None)
                {
                    // e.g., DAILY_DRAWDOWN_LIMIT, DAILY_LOSS_LIMIT, etc. already set in CheckGlobalRiskGates().
                    // Do not overwrite it with BOT_NOT_RUNNING; just exit quietly.
                    return;
                }

                // If user manually stopped, MANUAL_STOP was already set in Stop().
                if (_userStopped)
                {
                    return;
                }

                // Otherwise this really is "bot never started" or "stopped without a specific reason";
                // keep the generic message.
                SetSkipReason("BOT_NOT_RUNNING", "Bot is not currently in a running state.");
                return;
            }


            if (_shortTermPLBlockActive)
            {
                if (DateTime.Now < _shortTermPLBlockUntil)
                {
                    SetSkipReason("SHORT_TERM_PL_BLOCK", "Short-term P/L block active to protect from drawdown.");
                    return;
                }

                _shortTermPLBlockActive = false;
                RaiseBotEvent("Short-term P/L block expired. Resuming normal conditions check.");
            }

            if (CheckGlobalRiskGates())
            {
                _running = false;
                _autoPaused = true;
                // Skip reason already set inside CheckGlobalRiskGates
                return;
            }

            var now = DateTime.Now;
            if (!IsWithinSession(now))
            {
                SetSkipReason("OUTSIDE_SESSION", "Local time is outside configured trading session.");
                return;
            }

            var effectiveCooldown = _rules.TradeCooldown;
            if (_consecutiveLosses >= 2 && _rules.LossCooldownMultiplierSeconds > 0)
            {
                var extraSeconds = Math.Min(
                    _consecutiveLosses * _rules.LossCooldownMultiplierSeconds,
                    _rules.MaxLossCooldownSeconds);
                if (extraSeconds > 0)
                    effectiveCooldown += TimeSpan.FromSeconds(extraSeconds);
            }

            if ((now - _lastTradeTime) < effectiveCooldown)
            {
                SetSkipReason("COOLDOWN", "Trade cooldown has not yet expired.");
                return;
            }

            _tradeTimes.RemoveAll(t => (now - t) > TimeSpan.FromHours(1));

            // If MaxTradesPerHour <= 0, treat as "no per-hour limit".
            if (_rules.MaxTradesPerHour > 0 && _tradeTimes.Count >= _rules.MaxTradesPerHour)
            {
                SetSkipReason("MAX_TRADES_PER_HOUR", "Max trades per hour reached.");
                return;
            }

            FeatureVector? entryFeatures = null;
            if (_featureExtractor != null && CurrentDiagnostics != null)
            {
                try
                {
                    entryFeatures = _featureExtractor.Extract(
                        _context,
                        tick,
                        CurrentDiagnostics,
                        MarketHeatScore);
                }
                catch
                {
                    // non-fatal; fall back to null features
                }
            }

            var decisions = new List<StrategyDecision>();

            foreach (var strategy in _strategies)
            {
                if (strategy == null)
                    continue;

                if (IsStrategyTemporarilyBlocked(strategy))
                    continue;

                if (strategy is IRegimeAwareStrategy ra && !ra.ShouldTradeIn(CurrentDiagnostics))
                    continue;

                StrategyDecision dec;

                if (strategy is IAITradingStrategy ai)
                {
                    dec = ai.Decide(tick, _context, CurrentDiagnostics);
                }
                else
                {
                    var sig = strategy.OnNewTick(tick, _context);

                    double baseConf = 0.0;
                    if (sig != TradeSignal.None)
                    {
                        baseConf = 0.55;

                        if (CurrentDiagnostics.Regime == MarketRegime.TrendingUp && sig == TradeSignal.Buy)
                            baseConf += 0.10;
                        else if (CurrentDiagnostics.Regime == MarketRegime.TrendingDown && sig == TradeSignal.Sell)
                            baseConf += 0.10;

                        if (baseConf > 0.95) baseConf = 0.95;
                    }

                    dec = new StrategyDecision
                    {
                        StrategyName = strategy.Name,
                        Signal = sig,
                        Confidence = baseConf
                    };
                }

                EnsureDecisionDuration(dec, strategy);

                if (dec.Signal != TradeSignal.None && dec.Confidence > 0)
                    decisions.Add(dec);
            }


            if (decisions.Count == 0)
            {
                SetSkipReason("NO_SIGNAL", "No strategy produced a valid trade signal for current tick.");
                return;
            }

            StrategyDecision ensemble;
            if (CurrentDiagnostics != null)
            {
                bool useMl = _strategySelector != null &&
                             (_rules.MinTradesBeforeMl <= 0 || _tradeHistory.Count >= _rules.MinTradesBeforeMl);

                var selector = useMl ? _strategySelector : _fallbackSelector;
                ensemble = selector.SelectBest(
                    tick,
                    _context,
                    CurrentDiagnostics,
                    entryFeatures,
                    MarketHeatScore,
                    _strategyStats,
                    decisions);
            }
            else
            {
                // Fallback to the original ensemble voting logic
                ensemble = AIHelpers.EnsembleVote(decisions);
            }

            if (ensemble.Signal == TradeSignal.None || ensemble.Confidence < _rules.MinEnsembleConfidence)
            {
                SetSkipReason("LOW_CONFIDENCE",
                    $"Ensemble confidence too low (conf={ensemble.Confidence:F2}), or no agreement.");
                return;
            }


            var recentTrades = _tradeHistory.OrderByDescending(t => t.Time).Take(40).ToList();
            double expectedProfitScore = AIHelpers.EstimateExpectedProfitScore(ensemble, CurrentDiagnostics, recentTrades);


            // Allow neutral edge; block only if score is meaningfully negative.
            double expectedBlock = _rules.ExpectedProfitBlockThreshold;
            double expectedWarn = _rules.ExpectedProfitWarnThreshold;

            if (expectedProfitScore <= expectedBlock &&
                MarketHeatScore < _rules.MinMarketHeatToTrade)
            {
                SetSkipReason("NEGATIVE_EXPECTED_PROFIT",
                    $"AI expected-profit score is {expectedProfitScore:F2} (Heat={MarketHeatScore:F1}); skipping trade in cold regime.");
                RaiseBotEvent(
                    $"[NEGATIVE_EXPECTED_PROFIT] Blocked trade due to expected-profit score {expectedProfitScore:F2} (Heat={MarketHeatScore:F1}) in cold regime.");
                return;
            }

            // Warn but allow when slightly negative in warmer regimes.
            if (expectedProfitScore < expectedWarn && MarketHeatScore >= _rules.MinMarketHeatToTrade)
            {
                RaiseBotEvent(
                    $"[NEGATIVE_EXPECTED_PROFIT_WARN] Expected-profit score is {expectedProfitScore:F2} " +
                    $"but regime heat={MarketHeatScore:F1}; proceeding cautiously.");
            }


            if (!IsGoodTradingConditions())
            {
                SetSkipReason("ENVIRONMENT_FILTER",
                    "Environment / conditions filter failed at final execution step.");
                RaiseBotEvent("Conditions check failed at execution step; skipping trade.");
                return;
            }

            SetSkipReason(null, "Trade taken. Conditions and filters satisfied.");
            ExecuteTrade(tick, ensemble, now, entryFeatures);
        }

        private bool IsWithinSession(DateTime nowLocal)
        {
            if (_rules.SessionStartLocal == null || _rules.SessionEndLocal == null)
                return true;

            var t = nowLocal.TimeOfDay;
            return t >= _rules.SessionStartLocal.Value && t <= _rules.SessionEndLocal.Value;
        }

        private bool CheckGlobalRiskGates()
        {
            if (DisableGlobalRiskGatesForTesting)
                return false;

            RiskSettings settings;
            double bal;
            double start;
            int consecutiveLosses;
            int totalTrades;
            int wins;
            DateTime sessionStartTime;

            lock (_stateLock)
            {
                settings = _riskManager?.Settings;
                if (settings == null)
                    return false;

                bal = _balance;
                start = _sessionStartBalance > 0 ? _sessionStartBalance : bal;
                consecutiveLosses = _consecutiveLosses;

                sessionStartTime = _sessionStartTime;

                if (sessionStartTime == DateTime.MinValue)
                    sessionStartTime = DateTime.Now.AddHours(-24); // safe fallback

                totalTrades = _tradeHistory.Count(t => t.Time >= sessionStartTime);
                wins = _tradeHistory.Count(t => t.Time >= sessionStartTime && t.Profit >= 0);
            }


            AutoPauseReason reason = AutoPauseReason.None;
            string skipCode = null;
            string skipMsg = null;
            string eventMsg = null;

            // 1) Daily drawdown from peak equity
            if (_riskManager.IsDailyDrawdownExceeded(start, bal))
            {
                reason = AutoPauseReason.DailyDrawdownLimit;
                skipCode = "DAILY_DRAWDOWN_LIMIT";
                skipMsg = $"Daily drawdown limit reached. Start={start:F2}, Balance={bal:F2}.";
                eventMsg = $"Auto-paused: daily drawdown limit reached (Start={start:F2}, Balance={bal:F2}).";
            }
            // 2) Absolute daily loss amount
            else if (_riskManager.IsDailyLossAmountExceeded(start, bal))
            {
                reason = AutoPauseReason.DailyLossAmountLimit;
                skipCode = "DAILY_LOSS_LIMIT";
                skipMsg = $"Daily loss amount limit reached. Start={start:F2}, Balance={bal:F2}.";
                eventMsg = $"Auto-paused: daily loss amount limit reached (Start={start:F2}, Balance={bal:F2}).";
            }
            // 3) Daily profit target (percent of starting balance)
            else if (settings.DailyProfitTarget > 0 && start > 0)
            {
                double profitPct = (bal - start) / start * 100.0;
                if (profitPct >= settings.DailyProfitTarget)
                {
                    reason = AutoPauseReason.DailyProfitLimit;
                    skipCode = "DAILY_PROFIT_LIMIT";
                    skipMsg = $"Daily profit target reached: {profitPct:F2}% (target {settings.DailyProfitTarget:F2}%).";
                    eventMsg = $"Auto-paused: daily profit target reached ({profitPct:F2}% >= {settings.DailyProfitTarget:F2}%).";
                }
            }
            // 4) Absolute daily profit amount (optional)
            else if (settings.MaxDailyProfitAmount > 0 && (bal - start) >= settings.MaxDailyProfitAmount)
            {
                reason = AutoPauseReason.DailyProfitLimit;
                skipCode = "DAILY_PROFIT_AMOUNT_LIMIT";
                skipMsg = $"Daily profit amount limit reached: Profit={(bal - start):F2} (limit {settings.MaxDailyProfitAmount:F2}).";
                eventMsg = $"Auto-paused: daily profit amount limit reached (Profit={(bal - start):F2}).";
            }
            // 5) Consecutive losses
            else if (settings.MaxConsecutiveLosses > 0 && consecutiveLosses >= settings.MaxConsecutiveLosses)
            {
                reason = AutoPauseReason.ConsecutiveLossLimit;
                skipCode = "CONSECUTIVE_LOSS_LIMIT";
                skipMsg = $"Max consecutive losses reached: {consecutiveLosses}/{settings.MaxConsecutiveLosses}.";
                eventMsg = $"Auto-paused: max consecutive losses reached ({consecutiveLosses}/{settings.MaxConsecutiveLosses}).";
            }
            // 6) Global win-rate floor after enough trades
            else if (settings.MinTradesBeforeWinRateCheck > 0 &&
                     settings.MinWinRatePercentToContinue > 0 &&
                     totalTrades >= settings.MinTradesBeforeWinRateCheck)
            {
                double winRate = totalTrades > 0 ? (wins * 100.0 / totalTrades) : 0.0;
                if (winRate < settings.MinWinRatePercentToContinue)
                {
                    reason = AutoPauseReason.WinRateBelowThreshold;
                    skipCode = "WINRATE_BELOW_THRESHOLD";
                    skipMsg = $"Win rate below threshold: {winRate:F1}% (min {settings.MinWinRatePercentToContinue:F1}%) after {totalTrades} trades.";
                    eventMsg = $"Auto-paused: win rate {winRate:F1}% below {settings.MinWinRatePercentToContinue:F1}% after {totalTrades} trades.";
                }
            }

            if (reason != AutoPauseReason.None)
            {
                lock (_stateLock)
                {
                    _autoPauseReason = reason;
                }

                SetSkipReason(skipCode, skipMsg);
                RaiseBotEvent(eventMsg);
                return true;
            }

            return false;
        }

        private bool IsGoodTradingConditions()
        {
            var diag = CurrentDiagnostics;
            if (diag == null)
            {
                SetSkipReason("NO_DIAGNOSTICS", "No diagnostics available for current environment.");
                return false;
            }

            // ===== 1) REGIME GATING (with TEST-mode relaxation for VolatileChoppy) =====

            // Always block completely unknown regimes in all modes.
            if (diag.Regime == MarketRegime.Unknown)
            {
                SetSkipReason("BAD_REGIME", $"Regime is {diag.Regime}, not suitable for trading.");
                return false;
            }

            // VolatileChoppy is normally blocked, but can be relaxed in TEST mode.
            if (diag.Regime == MarketRegime.VolatileChoppy)
            {
                if (!RelaxEnvironmentForTesting)
                {
                    // Production / conservative behaviour: block.
                    SetSkipReason("BAD_REGIME", $"Regime is {diag.Regime}, not suitable for trading.");
                    return false;
                }

                // TEST mode:
                // Do NOT block here – just emit a clear warning so you see it in the log.
                RaiseBotEvent(
                    $"[BAD_REGIME] [TEST MODE] Regime is {diag.Regime}. Normally blocked, but allowed while testing.");
            }

            bool regimeOk =
                diag.Regime == MarketRegime.TrendingUp ||
                diag.Regime == MarketRegime.TrendingDown ||
                diag.Regime == MarketRegime.RangingHighVol ||
                diag.Regime == MarketRegime.RangingLowVol ||
                (RelaxEnvironmentForTesting && diag.Regime == MarketRegime.VolatileChoppy);

            if (!regimeOk)
            {
                SetSkipReason("UNSUPPORTED_REGIME", $"Regime {diag.Regime} is not in supported list.");
                return false;
            }

            // ===== 1b) HEAT + REGIME CONFIDENCE GATING =====

            double heatScore = Math.Clamp(MarketHeatScore, 0.0, 100.0);
            if (heatScore < _rules.MinMarketHeatToTrade)
            {
                if (!RelaxEnvironmentForTesting)
                {
                    SetSkipReason("HEAT_LOW", $"Market heat too low ({heatScore:F1} < {_rules.MinMarketHeatToTrade:F1}).");
                    return false;
                }

                RaiseBotEvent(
                    $"[HEAT_LOW] Heat {heatScore:F1} below {_rules.MinMarketHeatToTrade:F1}. [TEST MODE: warning only, not blocking.]");
            }

            if (_rules.MaxMarketHeatToTrade > 0 && heatScore > _rules.MaxMarketHeatToTrade)
            {
                if (!RelaxEnvironmentForTesting)
                {
                    SetSkipReason("HEAT_HIGH", $"Market heat too high ({heatScore:F1} > {_rules.MaxMarketHeatToTrade:F1}).");
                    return false;
                }

                RaiseBotEvent(
                    $"[HEAT_HIGH] Heat {heatScore:F1} above {_rules.MaxMarketHeatToTrade:F1}. [TEST MODE: warning only, not blocking.]");
            }

            if (diag.RegimeScore.HasValue && diag.RegimeScore.Value < _rules.MinRegimeScoreToTrade)
            {
                if (!RelaxEnvironmentForTesting)
                {
                    SetSkipReason("REGIME_WEAK",
                        $"Regime confidence too low ({diag.RegimeScore.Value:F2} < {_rules.MinRegimeScoreToTrade:F2}).");
                    return false;
                }

                RaiseBotEvent(
                    $"[REGIME_WEAK] Regime score {diag.RegimeScore.Value:F2} below {_rules.MinRegimeScoreToTrade:F2}. [TEST MODE: warning only, not blocking.]");
            }

            // ===== 2) VOLATILITY BAND (with TEST-mode relaxation) =====

            if (diag.Volatility is double vol)
            {
                if (vol < _rules.MinVolatilityToTrade || vol > _rules.MaxVolatilityToTrade)
                {
                    if (!RelaxEnvironmentForTesting)
                    {
                        // Normal behaviour: treat this as a hard environment failure.
                        SetSkipReason("VOL_BAND", $"Volatility {vol:F4} outside safe band.");
                        return false;
                    }

                    // TEST mode:
                    // Keep the diagnostic but do not block the trade.
                    RaiseBotEvent(
                        $"[VOL_BAND] Volatility {vol:F4} outside safe band. [TEST MODE: warning only, not blocking.]");
                }
            }

            // ===== 3) TREND-SLOPE EXTREMES (unchanged – still block in all modes) =====

            if (diag.TrendSlope is double slope)
            {
                double absSlope = Math.Abs(slope);

                // Base limit.
                double slopeLimit = 0.02;

                // Relax the slope limit as volatility increases, capped ~0.06.
                if (diag.Volatility is double volatilityForSlope)
                {
                    slopeLimit = 0.02 + 0.015 * Math.Min(volatilityForSlope, 2.0);
                    if (slopeLimit > 0.06)
                        slopeLimit = 0.06;
                }

                if (absSlope > slopeLimit)
                {
                    SetSkipReason("SLOPE_EXTREME",
                        $"Trend slope too extreme ({slope:F5}) vs limit {slopeLimit:F5}.");
                    return false;
                }
            }

            // ===== 4) 3-SIGMA SPIKE FILTER (unchanged) =====

            var recentQuotes = _context.GetLastQuotes(25);
            if (recentQuotes.Count >= 5)
            {
                double mean = recentQuotes.Average();
                double sd = Math.Sqrt(recentQuotes.Average(q => (q - mean) * (q - mean)));

                if (sd > 0)
                {
                    for (int i = 1; i < recentQuotes.Count; i++)
                    {
                        double step = Math.Abs(recentQuotes[i] - recentQuotes[i - 1]);
                        if (step > 3.0 * sd)
                        {
                            SetSkipReason("SPIKE_DETECTED", "Recent 3-sigma spike detected. Waiting for market to calm.");
                            RaiseBotEvent("Conditions filter: recent spike detected, delaying trades.");
                            return false;
                        }
                    }
                }
            }

            // NOTE: Recent P/L based cooldown (RECENT_PL_POOR / SHORT_TERM_PL_BLOCK)
            // has been deliberately removed so that the bot does not stop or cool down
            // just because of a small losing streak. Environment filters still apply.

            return true;
        }


        private bool IsStrategyTemporarilyBlocked(ITradingStrategy strategy)
        {
            if (_strategyBlockedUntil.TryGetValue(strategy.Name, out var until))
            {
                if (DateTime.Now < until)
                    return true;
            }
            return false;
        }

        private async void ExecuteTrade(Tick tick, StrategyDecision decision, DateTime now, FeatureVector? entryFeatures)
        {
            double baseStake;
            double currentBalanceSnapshot;
            StrategyStats? strategyStats = null;
            List<TradeRecord> recentTradesSnapshot;
            MarketDiagnostics diagnosticsSnapshot;
            RiskSettings riskSettings;

            int openTradesCount;
            int maxOpenTrades;

            // 1) Take a safe snapshot of state needed for stake sizing
            lock (_stateLock)
            {
                baseStake = _riskManager.ComputeStake(_balance);
                if (baseStake <= 0)
                {
                    // DO NOT call SetSkipReason here (it locks). Just bail with a simple return.
                    return;
                }

                if (!string.IsNullOrWhiteSpace(decision.StrategyName) &&
                    _strategyStats.TryGetValue(decision.StrategyName, out var stats))
                {
                    strategyStats = new StrategyStats
                    {
                        Wins = stats.Wins,
                        Losses = stats.Losses,
                        NetPL = stats.NetPL
                    };
                }

                currentBalanceSnapshot = _balance;

                recentTradesSnapshot = _tradeHistory
                    .OrderByDescending(t => t.Time)
                    .Take(25)
                    .ToList();

                diagnosticsSnapshot = CurrentDiagnostics ?? new MarketDiagnostics();
                riskSettings = _riskManager.Settings;

                openTradesCount = _openTrades.Count;
                maxOpenTrades = _rules?.MaxOpenTrades ?? 0;
            }

            // 1b) Enforce open-trade cap outside lock (avoids deadlock)
            if (maxOpenTrades > 0 && openTradesCount >= maxOpenTrades)
            {
                SetSkipReason("MAX_OPEN_TRADES",
                    $"Max open trades reached ({openTradesCount}/{maxOpenTrades}). Waiting for contract(s) to finish.");
                return;
            }

            // 2) Dynamic stake sizing
            double modelConfidence = Math.Max(0.0, Math.Min(1.0, decision.Confidence));
            double edgeProbability = decision.EdgeProbability ?? 0.5;
            double stake = DynamicRiskHelper.ComputeDynamicStake(
                baseStake,
                currentBalanceSnapshot,
                modelConfidence,
                edgeProbability,
                diagnosticsSnapshot.Regime,
                diagnosticsSnapshot.RegimeScore,
                MarketHeatScore,
                strategyStats,
                recentTradesSnapshot,
                riskSettings);

            // 3) Round + clamp
            stake = Math.Round(stake, 2, MidpointRounding.AwayFromZero);
            stake = _riskManager.ClampStake(stake);

            if (stake <= 0)
            {
                SetSkipReason("NO_STAKE", "Dynamic stake calculation returned zero after rounding/clamping.");
                return;
            }

            Guid tradeId;
            string dirText;

            // 4) Mutate controller state before sending order
            lock (_stateLock)
            {
                _lastTradeTime = now;

                _tradeTimes.RemoveAll(t => (now - t) > TimeSpan.FromHours(1));
                _tradeTimes.Add(now);

                ActiveStrategyName = decision.StrategyName;

                tradeId = Guid.NewGuid();
                dirText = decision.Signal == TradeSignal.Buy ? "Buy" : "Sell";

                var record = new TradeRecord
                {
                    Time = now,
                    Symbol = tick.Symbol,
                    StrategyName = decision.StrategyName,
                    Direction = dirText,
                    Stake = stake,
                    Profit = 0.0,
                    EntryFeatures = entryFeatures,
                    Decision = decision
                };

                _openTrades[tradeId] = record;
            }

            RaiseBotEvent(
                $"[Ensemble] Placing {dirText} trade via {decision.StrategyName} on {tick.Symbol} " +
                $"(stake={stake:F2}, conf={decision.Confidence:F2}, heat={MarketHeatScore:F1}, dur={decision.Duration}{decision.DurationUnit})");

            if (ForwardTestEnabled)
            {
                bool started = await TryStartForwardTestTradeAsync(tick, decision, stake, tradeId);
                if (!started)
                {
                    lock (_stateLock)
                    {
                        _openTrades.Remove(tradeId);
                    }

                    SetSkipReason("FORWARD_TEST_FAIL", "Forward test proposal failed; trade skipped.");
                }

                return;
            }

            await _deriv.BuyRiseFallAsync(
                tick.Symbol,
                stake,
                decision.Signal,
                decision.StrategyName,
                tradeId,
                duration: decision.Duration,
                durationUnit: decision.DurationUnit,
                currency: "USD");
        }


        private async Task<bool> TryStartForwardTestTradeAsync(
            Tick tick,
            StrategyDecision decision,
            double stake,
            Guid tradeId)
        {
            var proposalProvider = _deriv as IProposalProvider;
            if (proposalProvider == null)
            {
                RaiseBotEvent("Forward test unavailable: proposal provider missing.");
                return false;
            }

            string contractType = decision.Signal == TradeSignal.Buy ? "CALL" : "PUT";
            var request = new ProposalRequest
            {
                Symbol = tick.Symbol,
                ContractType = contractType,
                Stake = stake,
                Duration = decision.Duration,
                DurationUnit = decision.DurationUnit,
                Currency = string.IsNullOrWhiteSpace(_deriv.Currency) ? "USD" : _deriv.Currency
            };

            ProposalQuote quote;
            try
            {
                quote = await proposalProvider.GetProposalAsync(request);
            }
            catch (Exception ex)
            {
                RaiseBotEvent($"Forward test proposal failed: {ex.Message}");
                return false;
            }

            double payoutMultiplier = ExtractProposalMultiplier(quote, stake);
            if (payoutMultiplier <= 0.0)
            {
                RaiseBotEvent("Forward test proposal missing payout; trade skipped.");
                return false;
            }

            var forwardTrade = new ForwardTestTrade
            {
                TradeId = tradeId,
                Symbol = tick.Symbol ?? string.Empty,
                Direction = decision.Signal,
                EntryPrice = tick.Quote,
                EntryTime = tick.Time,
                Stake = stake,
                PayoutMultiplier = payoutMultiplier
            };

            var span = ResolveDurationSpan(decision.Duration, decision.DurationUnit);
            if (span.HasValue)
            {
                forwardTrade.ExitTime = tick.Time + span.Value;
            }
            else
            {
                forwardTrade.RemainingTicks = Math.Max(1, decision.Duration);
            }

            lock (_stateLock)
            {
                _forwardTrades[tradeId] = forwardTrade;
            }

            RaiseBotEvent($"[ForwardTest] Scheduled {contractType} {tick.Symbol} {decision.Duration}{decision.DurationUnit} payout x{payoutMultiplier:F2}.");
            return true;
        }

        private void UpdateForwardTrades(Tick tick)
        {
            if (!ForwardTestEnabled || tick == null)
                return;

            List<(Guid TradeId, double Profit)> closed = null;

            lock (_stateLock)
            {
                foreach (var kvp in _forwardTrades.ToList())
                {
                    var trade = kvp.Value;
                    if (!string.Equals(trade.Symbol, tick.Symbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool shouldExit;
                    if (trade.ExitTime.HasValue)
                    {
                        shouldExit = tick.Time >= trade.ExitTime.Value;
                    }
                    else
                    {
                        trade.RemainingTicks = Math.Max(0, trade.RemainingTicks - 1);
                        shouldExit = trade.RemainingTicks <= 0;
                    }

                    if (!shouldExit)
                        continue;

                    double diff = tick.Quote - trade.EntryPrice;
                    if (trade.Direction == TradeSignal.Sell)
                        diff = -diff;

                    double profit = diff > 0
                        ? trade.Stake * trade.PayoutMultiplier
                        : -trade.Stake;

                    if (diff == 0.0)
                        profit = 0.0;

                    closed ??= new List<(Guid, double)>();
                    closed.Add((trade.TradeId, profit));
                }

                if (closed != null)
                {
                    foreach (var item in closed)
                    {
                        _forwardTrades.Remove(item.TradeId);
                    }
                }
            }

            if (closed != null)
            {
                foreach (var item in closed)
                {
                    OnContractFinished("ForwardTest", item.TradeId, item.Profit);
                }
            }
        }

        private static TimeSpan? ResolveDurationSpan(int duration, string unit)
        {
            if (duration <= 0) duration = 1;
            if (string.IsNullOrWhiteSpace(unit)) unit = "t";

            if (unit.Equals("t", StringComparison.OrdinalIgnoreCase))
                return null;

            return unit.ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(duration),
                "m" => TimeSpan.FromMinutes(duration),
                "h" => TimeSpan.FromHours(duration),
                "d" => TimeSpan.FromDays(duration),
                _ => TimeSpan.FromSeconds(duration)
            };
        }

        private static double ExtractProposalMultiplier(ProposalQuote quote, double stake)
        {
            if (quote == null || stake <= 0)
                return 0.0;

            if (quote.Profit.HasValue && quote.Profit.Value > 0)
                return quote.Profit.Value / stake;

            if (quote.Payout.HasValue)
                return (quote.Payout.Value - stake) / stake;

            if (quote.AskPrice.HasValue && quote.AskPrice.Value > 0 && quote.Payout.HasValue)
                return (quote.Payout.Value - quote.AskPrice.Value) / stake;

            return 0.0;
        }

        private void OnContractFinished(string strategyName, Guid tradeId, double profit)
        {
                lock (_stateLock)
                {
                    if (_openTrades.TryGetValue(tradeId, out var record))
                    {
                        _openTrades.Remove(tradeId);

                        record.Profit = profit;
                        _tradeHistory.Add(record);

                        // Log trade with entry-time features to avoid label leakage
                        if (_tradeLogger != null)
                        {
                            var features = record.EntryFeatures;

                            if (features == null && _featureExtractor != null && CurrentDiagnostics != null)
                            {
                                var latestTick = _context.TickWindow.LastOrDefault();
                                if (latestTick != null)
                                {
                                    features = _featureExtractor.Extract(
                                        _context,
                                        latestTick,
                                        CurrentDiagnostics,
                                        MarketHeatScore);
                                }
                            }

                            if (features != null)
                            {
                                var signal = record.Direction == "Buy"
                                    ? TradeSignal.Buy
                                    : TradeSignal.Sell;

                                var decision = record.Decision ?? new StrategyDecision
                                {
                                    StrategyName = strategyName,
                                    Signal = signal,
                                    Confidence = CurrentDiagnostics?.RegimeScore ?? 0.0,
                                    EdgeProbability = record.Decision?.EdgeProbability
                                };

                                _tradeLogger.Log(features, decision, record.Stake, profit);
                            }
                        }

                        if (!_strategyStats.TryGetValue(strategyName, out var stats))
                        {
                            stats = new StrategyStats();
                            _strategyStats[strategyName] = stats;
                        }

                        if (profit >= 0)
                        {
                            stats.Wins++;
                            _consecutiveLosses = 0;
                        }
                        else
                        {
                            stats.Losses++;
                            _consecutiveLosses++;
                        }

                        stats.NetPL += profit;

                        // Update per-symbol performance stats
                        if (!string.IsNullOrWhiteSpace(record.Symbol))
                        {
                            var symStats = GetOrCreateSymbolStats(record.Symbol);
                            if (profit >= 0)
                                symStats.Wins++;
                            else
                                symStats.Losses++;

                            symStats.NetPL += profit;
                            symStats.LastUpdated = record.Time != default ? record.Time : DateTime.Now;
                        }

                        RaiseBotEvent($"Trade finished [{strategyName}] dir={record.Direction} stake={record.Stake:F2} P/L={profit:F2}");

                        EvaluateStrategyHealth(strategyName);
                        //EvaluateShortTermPL();

                        if (CheckGlobalRiskGates())
                        {
                            _running = false;
                            _autoPaused = true;
                        }
                    }
                }
        }

        private void OnBuyError(string symbol, string errorCode, string message)
        {
            var sym = string.IsNullOrWhiteSpace(symbol) ? "UNKNOWN" : symbol.Trim();
            var code = errorCode ?? string.Empty;

            // 1) Symbol not offered → disable for this session
            if (string.Equals(code, "InvalidOfferings", StringComparison.OrdinalIgnoreCase))
            {
                lock (_stateLock)
                {
                    // Mark symbol as disabled for this session so rotation and selection will skip it.
                    var stats = GetOrCreateSymbolStats(sym);
                    stats.IsDisabledForSession = true;

                    // If this was the active symbol, clear it so that auto-rotation can promote another one.
                    if (!string.IsNullOrWhiteSpace(_activeSymbol) &&
                        string.Equals(_activeSymbol, sym, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeSymbol = null;
                    }
                }

                RaiseBotEvent($"Symbol {sym} disabled for this session due to InvalidOfferings from Deriv.");
                SetSkipReason("SYMBOL_NOT_OFFERED",
                    $"Trading is not offered for symbol {sym} on this account. It has been disabled for this session.");

                return;
            }

            // 2) InvalidPrice → treat as "no trade", surface diagnostic
            if (string.Equals(code, "InvalidPrice", StringComparison.OrdinalIgnoreCase))
            {
                SetSkipReason("INVALID_PRICE",
                    $"Deriv rejected price/stake for {sym}: {message ?? "Invalid price."}");
                RaiseBotEvent($"[BUY_ERROR_INVALID_PRICE] {sym}: {message}");
                return;
            }

            // 3) Generic buy error: log and surface a skip reason
            SetSkipReason("BUY_ERROR",
                $"Buy error [{code}] for symbol {sym}: {message ?? "Unknown buy error."}");
            RaiseBotEvent($"[BUY_ERROR] [{code}] {sym}: {message}");
        }

        private void EvaluateStrategyHealth(string strategyName)
        {
            var recent = _tradeHistory
                .Where(t => t.StrategyName == strategyName)
                .OrderByDescending(t => t.Time)
                .Take(Math.Max(_rules.StrategyProbationMinTrades, 20))
                .ToList();

            if (recent.Count < _rules.StrategyProbationMinTrades)
                return;

            int wins = recent.Count(t => t.Profit >= 0);
            double winRate = (double)wins / recent.Count * 100.0;

            bool threeLosses = recent.Take(3).All(t => t.Profit < 0);

            if (threeLosses || winRate < _rules.StrategyProbationWinRate)
            {
                int blockMinutes = threeLosses ? _rules.StrategyProbationLossBlockMinutes : _rules.StrategyProbationBlockMinutes;
                _strategyBlockedUntil[strategyName] = DateTime.Now.AddMinutes(blockMinutes);
                RaiseBotEvent($"Strategy '{strategyName}' temporarily disabled ({blockMinutes} min) due to poor recent performance (win rate last {recent.Count} trades: {winRate:F1}%).");
            }
        }

        private void EvaluateShortTermPL()
        {
            // Short-term P/L based cool-down has been disabled to allow
            // continuous ML testing without auto-pauses due to recent losses.
            // Environment filters (regime, volatility, spikes, slope) still apply.
            return;
        }
        private static bool IsQuietRepeatSkipCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            return string.Equals(code, "NO_SIGNAL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "NEGATIVE_EXPECTED_PROFIT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "RECENT_PL_POOR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "ENVIRONMENT_FILTER", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "SHORT_TERM_PL_BLOCK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "BOT_NOT_RUNNING", StringComparison.OrdinalIgnoreCase);
        }


        private void SetSkipReason(string code, string message)
        {
            lock (_stateLock)
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;

                var now = DateTime.Now;
                string prefix = string.IsNullOrWhiteSpace(code) ? "INFO" : code;

                bool isRepeat =
                    string.Equals(code, _lastSkipCode, StringComparison.Ordinal) &&
                    string.Equals(message, _lastSkipMessage, StringComparison.Ordinal) &&
                    (now - _lastSkipTimestamp).TotalSeconds <= 5;

                if (isRepeat && _skipReasonLog.Count > 0)
                {
                    _lastSkipRepeatCount = Math.Max(1, _lastSkipRepeatCount + 1);

                    string baseLine = $"{now:HH:mm:ss} [{prefix}] {message}";
                    string aggregatedLine = $"{baseLine} (x{_lastSkipRepeatCount})";

                    _lastSkipReason = aggregatedLine;
                    _skipReasonLog[_skipReasonLog.Count - 1] = aggregatedLine;

                    // For "quiet" repeat reasons, do not spam the UI log.
                    if (!IsQuietRepeatSkipCode(code))
                    {
                        RaiseBotEvent($"[SkipReason] {aggregatedLine}");
                    }
                }
                else
                {
                    _lastSkipCode = code;
                    _lastSkipMessage = message;
                    _lastSkipTimestamp = now;
                    _lastSkipRepeatCount = 1;

                    _lastSkipReason = $"{now:HH:mm:ss} [{prefix}] {message}";
                    _skipReasonLog.Add(_lastSkipReason);

                    if (_skipReasonLog.Count > MaxSkipReasonLogEntries)
                        _skipReasonLog.RemoveAt(0);

                    RaiseBotEvent($"[SkipReason] {_lastSkipReason}");
                }
            }
        }

        private void RaiseBotEvent(string msg)
        {
            BotEvent?.Invoke(msg);
        }
    }

    #endregion
}
