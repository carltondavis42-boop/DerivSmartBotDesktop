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
    }

    // 🆕 Non-trade explanation model
    public class NonTradeRecord
    {
        public DateTime Time { get; set; }
        public string Symbol { get; set; }
        public string Reason { get; set; }   // e.g. "RiskGate", "ExpectedProfitFilter", "ConditionsFilter", "Cooldown", etc.
        public string Detail { get; set; }   // human readable explanation
        public double? Heat { get; set; }    // MarketHeatScore at the time
        public MarketRegime? Regime { get; set; } // Market regime at the time
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
    }

    public interface ITradingStrategy
    {
        string Name { get; }
        TradeSignal OnNewTick(Tick tick, StrategyContext context);
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

    public class MovingAverageTrendStrategy : ITradingStrategy, IRegimeAwareStrategy
    {
        public string Name => "MA Trend";

        private readonly int _fast;
        private readonly int _slow;
        private readonly double _threshold;

        public MovingAverageTrendStrategy(int fast = 10, int slow = 30, double threshold = 0.0)
        {
            _fast = fast;
            _slow = slow;
            _threshold = threshold;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var fast = context.GetSimpleMovingAverage(_fast);
            var slow = context.GetSimpleMovingAverage(_slow);
            if (fast == null || slow == null) return TradeSignal.None;

            double diff = fast.Value - slow.Value;

            if (diff > _threshold)
                return TradeSignal.Buy;
            if (diff < -_threshold)
                return TradeSignal.Sell;

            return TradeSignal.None;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown;
        }
    }

    public class RangeBreakoutStrategy : ITradingStrategy, IRegimeAwareStrategy
    {
        public string Name => "Range Breakout";

        private readonly int _lookback;
        private readonly double _buffer;

        public RangeBreakoutStrategy(int lookback = 30, double buffer = 0.0)
        {
            _lookback = lookback;
            _buffer = buffer;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var quotes = context.GetLastQuotes(_lookback);
            if (quotes.Count < _lookback) return TradeSignal.None;

            double recentHigh = quotes.Max();
            double recentLow = quotes.Min();
            double price = tick.Quote;

            if (price > recentHigh + _buffer)
                return TradeSignal.Buy;

            if (price < recentLow - _buffer)
                return TradeSignal.Sell;

            return TradeSignal.None;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            return diagnostics.Regime == MarketRegime.TrendingUp ||
                   diagnostics.Regime == MarketRegime.TrendingDown ||
                   diagnostics.Regime == MarketRegime.RangingHighVol;
        }
    }

    public class MeanReversionStrategy : ITradingStrategy, IRegimeAwareStrategy
    {
        public string Name => "Mean Reversion";

        private readonly int _lookback;
        private readonly double _multiplier;

        public MeanReversionStrategy(int lookback = 50, double multiplier = 1.0)
        {
            _lookback = lookback;
            _multiplier = multiplier;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var ma = context.GetSimpleMovingAverage(_lookback);
            var sd = context.GetStdDev(_lookback);
            if (ma == null || sd == null) return TradeSignal.None;

            double upper = ma.Value + _multiplier * sd.Value;
            double lower = ma.Value - _multiplier * sd.Value;
            double p = tick.Quote;

            if (p > upper)
                return TradeSignal.Sell;
            if (p < lower)
                return TradeSignal.Buy;

            return TradeSignal.None;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            return diagnostics.Regime == MarketRegime.RangingLowVol ||
                   diagnostics.Regime == MarketRegime.RangingHighVol;
        }
    }

    public class SmartMoneyConceptStrategy : IAITradingStrategy, IRegimeAwareStrategy
    {
        public string Name => "SMC Liquidity Sweep";

        private readonly int _swingLookback;
        private readonly int _bosLookback;

        public SmartMoneyConceptStrategy(int swingLookback = 10, int bosLookback = 20)
        {
            _swingLookback = swingLookback;
            _bosLookback = bosLookback;
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            return Decide(tick, context, context.AnalyzeRegime()).Signal;
        }

        public StrategyDecision Decide(Tick tick, StrategyContext context, MarketDiagnostics diag)
        {
            var quotes = context.GetLastQuotes(_bosLookback);
            if (quotes.Count < Math.Max(_swingLookback + 2, _bosLookback))
                return new StrategyDecision { StrategyName = Name, Signal = TradeSignal.None, Confidence = 0.0 };

            double price = tick.Quote;
            int n = quotes.Count;

            var recentSegment = quotes.Skip(n - _swingLookback).ToArray();
            double swingHigh = recentSegment.Max();
            double swingLow = recentSegment.Min();

            double mean = quotes.Average();
            double prev = quotes[^2];

            TradeSignal signal = TradeSignal.None;
            double confidence = 0.0;

            if (prev > swingHigh && price < mean)
            {
                signal = TradeSignal.Sell;
                confidence = 0.6;
            }
            else if (prev < swingLow && price > mean)
            {
                signal = TradeSignal.Buy;
                confidence = 0.6;
            }

            if (signal != TradeSignal.None && diag != null)
            {
                if (diag.Regime == MarketRegime.TrendingUp && signal == TradeSignal.Buy)
                    confidence += 0.1;
                else if (diag.Regime == MarketRegime.TrendingDown && signal == TradeSignal.Sell)
                    confidence += 0.1;
                else if (diag.Regime == MarketRegime.RangingHighVol)
                    confidence += 0.05;

                if (confidence > 0.95) confidence = 0.95;
            }

            return new StrategyDecision
            {
                StrategyName = Name,
                Signal = signal,
                Confidence = confidence
            };
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            return diagnostics.Regime != MarketRegime.Unknown;
        }
    }

    public class VolatilityFilteredStrategy : ITradingStrategy
    {
        private readonly ITradingStrategy _inner;
        private readonly string _name;

        private readonly double? _minVol;
        private readonly double? _maxVol;
        private readonly double? _trendThreshold;

        public string Name => _name;

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
        public double RiskPerTradeFraction { get; set; } = 0.01;
        public double MinStake { get; set; } = 0.35;
        public double MaxStake { get; set; } = 50.0;

        public double MaxDailyDrawdownFraction { get; set; } = 0.2;
        public double MaxDailyProfitFraction { get; set; } = 0.3;

        public double MaxDailyLossAmount { get; set; } = 0.0;
        public double MaxDailyProfitAmount { get; set; } = 0.0;

        public int MaxConsecutiveLosses { get; set; } = 0;

        public double MinWinRatePercentToContinue { get; set; } = 0.0;
        public int MinTradesBeforeWinRateCheck { get; set; } = 20;
    }

    public class BotRules
    {
        public TimeSpan TradeCooldown { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxTradesPerHour { get; set; } = 30;

        public TimeSpan? SessionStartLocal { get; set; } = null;
        public TimeSpan? SessionEndLocal { get; set; } = null;
    }

    public class RiskManager
    {
        private RiskSettings _settings;

        public RiskManager(RiskSettings settings)
        {
            _settings = settings;
        }

        public RiskSettings Settings => _settings;

        public void UpdateSettings(RiskSettings settings)
        {
            _settings = settings;
        }

        public double ComputeStake(double balance)
        {
            double stake = balance * _settings.RiskPerTradeFraction;
            if (stake < _settings.MinStake) stake = _settings.MinStake;
            if (stake > _settings.MaxStake) stake = _settings.MaxStake;
            return stake;
        }

        public bool IsDailyDrawdownExceeded(double startingBalance, double currentBalance)
        {
            if (startingBalance <= 0) return false;

            double dd = (startingBalance - currentBalance) / startingBalance;
            if (dd < 0) dd = 0;
            return dd >= _settings.MaxDailyDrawdownFraction;
        }

        public bool IsDailyLossAmountExceeded(double startingBalance, double currentBalance)
        {
            if (_settings.MaxDailyLossAmount <= 0) return false;
            double loss = startingBalance - currentBalance;
            return loss >= _settings.MaxDailyLossAmount;
        }

        public bool IsDailyProfitLimitReached(double startingBalance, double currentBalance)
        {
            if (startingBalance <= 0) return false;

            double profit = currentBalance - startingBalance;
            bool fractionHit = _settings.MaxDailyProfitFraction > 0 &&
                               (profit / startingBalance) >= _settings.MaxDailyProfitFraction;
            bool amountHit = _settings.MaxDailyProfitAmount > 0 &&
                             profit >= _settings.MaxDailyProfitAmount;

            return fractionHit || amountHit;
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
                            MaxConsecutiveLosses = 3,
                            MinWinRatePercentToContinue = 55,
                            MinTradesBeforeWinRateCheck = 30
                        },
                        Rules = new BotRules
                        {
                            TradeCooldown = TimeSpan.FromSeconds(20),
                            MaxTradesPerHour = 10
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
                            MaxConsecutiveLosses = 10,
                            MinWinRatePercentToContinue = 0
                        },
                        Rules = new BotRules
                        {
                            TradeCooldown = TimeSpan.FromSeconds(3),
                            MaxTradesPerHour = 60
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
                            MaxConsecutiveLosses = 6,
                            MinWinRatePercentToContinue = 50,
                            MinTradesBeforeWinRateCheck = 25
                        },
                        Rules = new BotRules
                        {
                            TradeCooldown = TimeSpan.FromSeconds(8),
                            MaxTradesPerHour = 30
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

            if (buyScore > sellScore)
            {
                finalSignal = TradeSignal.Buy;
                finalScore = buyScore;
                finalVotes = buyVotes;
            }
            else
            {
                finalSignal = TradeSignal.Sell;
                finalScore = sellScore;
                finalVotes = sellVotes;
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
                Confidence = combinedConfidence
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

    #region SmartBotController (with Non-Trade Logging & Auto Symbol)

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
        private readonly RiskManager _riskManager;
        private readonly List<ITradingStrategy> _strategies;
        private BotRules _rules;
        private readonly DerivWebSocketClient _deriv;

        // Per-symbol contexts
        private readonly Dictionary<string, StrategyContext> _contexts = new();
        private readonly Dictionary<string, MarketDiagnostics> _symbolDiagnostics = new();
        private readonly Dictionary<string, double> _symbolHeatScores = new();

        private readonly List<string> _symbolsToWatch = new();
        private string _activeSymbol;
        private AutoSymbolMode _autoSymbolMode = AutoSymbolMode.Auto;

        // Auto symbol selection timing
        private DateTime _lastSymbolEvalTime = DateTime.MinValue;
        private DateTime _lastSymbolSwitchTime = DateTime.MinValue;
        private readonly TimeSpan _symbolEvalInterval = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _symbolSwitchCooldown = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, StrategyStats> _strategyStats = new();
        private readonly List<TradeRecord> _tradeHistory = new();
        private readonly Dictionary<Guid, TradeRecord> _openTrades = new();

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

        public MarketDiagnostics CurrentDiagnostics { get; private set; } = new MarketDiagnostics();
        public double MarketHeatScore { get; private set; } = 0.0;

        public event Action<string> BotEvent;

        // 🆕 Non-trade logging
        private readonly List<NonTradeRecord> _nonTradeRecords = new();
        public IReadOnlyList<NonTradeRecord> NonTradeRecords => _nonTradeRecords;
        public event Action<NonTradeRecord> NonTradeEvent;

        public bool AutoStartEnabled { get; set; } = true;
        private readonly int _minTicksBeforeAutoStart = 80;
        private readonly double _minHeatForAutoStart = 55.0;

        private readonly IMarketRegimeClassifier _regimeClassifier;

        public SmartBotController(
            RiskManager riskManager,
            IEnumerable<ITradingStrategy> strategies,
            BotRules rules,
            DerivWebSocketClient deriv,
            IMarketRegimeClassifier regimeClassifier = null)
        {
            _riskManager = riskManager;
            _strategies = strategies.ToList();
            _rules = rules;
            _deriv = deriv;
            _regimeClassifier = regimeClassifier ?? new AiMarketRegimeClassifier();

            _deriv.TickReceived += OnTickReceived;
            _deriv.BalanceUpdated += OnBalanceUpdated;
            _deriv.ContractFinished += OnContractFinished;

            foreach (var s in _strategies)
            {
                if (!_strategyStats.ContainsKey(s.Name))
                    _strategyStats[s.Name] = new StrategyStats();
            }
        }

        #region Public props

        public bool IsRunning => _running && !_autoPaused;
        public bool IsConnected => _deriv?.IsConnected ?? false;

        public double Balance => _balance;
        public double TodaysPL => _balance - _sessionStartBalance;

        public string ActiveStrategyName { get; private set; }
        public AutoPauseReason LastAutoPauseReason => _autoPauseReason;
        public int ConsecutiveLosses => _consecutiveLosses;

        public IReadOnlyDictionary<string, StrategyStats> StrategyStats => _strategyStats;
        public IReadOnlyList<TradeRecord> TradeHistory => _tradeHistory;

        public IReadOnlyList<string> SymbolsToWatch => _symbolsToWatch;
        public string ActiveSymbol => _activeSymbol;
        public AutoSymbolMode AutoSymbolMode => _autoSymbolMode;

        #endregion

        #region Multi-symbol setup

        public void SetSymbolsToWatch(IEnumerable<string> symbols)
        {
            _symbolsToWatch.Clear();

            if (symbols != null)
            {
                foreach (var s in symbols)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        var sym = s.Trim();
                        if (!_symbolsToWatch.Contains(sym))
                            _symbolsToWatch.Add(sym);
                    }
                }
            }

            if (_symbolsToWatch.Count > 0 && string.IsNullOrWhiteSpace(_activeSymbol))
            {
                _activeSymbol = _symbolsToWatch[0];
                RaiseBotEvent($"Active symbol set to {_activeSymbol}.");
            }

            RaiseBotEvent($"Symbols to watch: {string.Join(", ", _symbolsToWatch)}");
        }

        public void SetAutoSymbolMode(AutoSymbolMode mode)
        {
            _autoSymbolMode = mode;
            RaiseBotEvent($"Auto symbol mode set to {mode}.");
        }

        public void SetActiveSymbol(string symbol)
        {
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                _activeSymbol = symbol.Trim();
                RaiseBotEvent($"Active symbol manually set to {_activeSymbol}.");
            }
        }

        private StrategyContext GetOrCreateContext(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                symbol = _activeSymbol ?? _symbolsToWatch.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(symbol))
            {
                symbol = "R_50"; // fallback
            }

            if (!_contexts.TryGetValue(symbol, out var ctx))
            {
                ctx = new StrategyContext();
                _contexts[symbol] = ctx;
            }

            return ctx;
        }

        #endregion

        #region Control

        public void Start()
        {
            _autoPaused = false;
            _userStopped = false;
            _autoPauseReason = AutoPauseReason.None;
            _running = true;
            _sessionStartBalance = _balance;
            _consecutiveLosses = 0;

            if (string.IsNullOrWhiteSpace(_activeSymbol) && _symbolsToWatch.Count > 0)
                _activeSymbol = _symbolsToWatch[0];

            RaiseBotEvent("Bot started.");
        }

        public void Stop()
        {
            _running = false;
            _userStopped = true;
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
        }

        #endregion

        #region Tick handling + Non-trade logging

        private void OnTickReceived(Tick tick)
        {
            if (tick == null) return;

            var symbol = string.IsNullOrWhiteSpace(tick.Symbol)
                ? (_activeSymbol ?? _symbolsToWatch.FirstOrDefault())
                : tick.Symbol;

            if (string.IsNullOrWhiteSpace(symbol))
                return;

            if (string.IsNullOrWhiteSpace(_activeSymbol))
                _activeSymbol = symbol;

            var ctx = GetOrCreateContext(symbol);
            ctx.AddTick(tick);

            // Diagnostics & heat for this symbol
            var baseDiag = ctx.AnalyzeRegime() ?? new MarketDiagnostics();
            var quotes = ctx.GetLastQuotes(120);
            double aiScore = 0.0;

            var aiRegime = _regimeClassifier.Classify(
                quotes,
                baseDiag.Volatility,
                baseDiag.TrendSlope,
                out aiScore);

            baseDiag.Regime = aiRegime;
            baseDiag.RegimeScore = aiScore;
            baseDiag.Time = tick.Time;

            _symbolDiagnostics[symbol] = baseDiag;

            var heat = AIHelpers.ComputeMarketHeatIndex(ctx, baseDiag);
            _symbolHeatScores[symbol] = heat;

            if (symbol == _activeSymbol)
            {
                CurrentDiagnostics = baseDiag;
                MarketHeatScore = heat;
            }

            // Auto-select best symbol (smart logic)
            MaybeSwitchActiveSymbol(DateTime.Now);

            // Only trading on the active symbol
            if (symbol != _activeSymbol)
                return;

            var diag = CurrentDiagnostics;

            // Auto-start based on good conditions
            if (!_running &&
                !_autoPaused &&
                !_userStopped &&
                AutoStartEnabled &&
                ctx.TickWindow.Count >= _minTicksBeforeAutoStart &&
                MarketHeatScore >= _minHeatForAutoStart &&
                IsGoodTradingConditions(ctx))
            {
                Start();
                string volText = diag.Volatility.HasValue
                    ? diag.Volatility.Value.ToString("F4")
                    : "n/a";

                RaiseBotEvent(
                    $"Auto-started bot based on favorable market conditions " +
                    $"(Symbol: {symbol}, Regime: {diag.Regime}, Score={diag.RegimeScore:F2}, Heat={MarketHeatScore:F1}, Vol={volText}).");
            }

            if (!IsRunning) return;

            if (_shortTermPLBlockActive)
            {
                if (DateTime.Now < _shortTermPLBlockUntil)
                {
                    LogNonTrade(symbol, "ShortTermPLBlock", "Short-term P/L block active; trade skipped.");
                    return;
                }

                _shortTermPLBlockActive = false;
                RaiseBotEvent("Short-term P/L block expired. Resuming normal conditions check.");
            }

            if (CheckGlobalRiskGatesWithNonTrade(symbol))
            {
                _running = false;
                _autoPaused = true;
                return;
            }

            var now = DateTime.Now;
            if (!IsWithinSession(now))
            {
                LogNonTrade(symbol, "SessionFilter", "Outside configured trading session.");
                return;
            }

            if ((now - _lastTradeTime) < _rules.TradeCooldown)
            {
                LogNonTrade(symbol, "Cooldown", "Trade cooldown in effect.");
                return;
            }

            _tradeTimes.RemoveAll(t => (now - t) > TimeSpan.FromHours(1));
            if (_tradeTimes.Count >= _rules.MaxTradesPerHour)
            {
                LogNonTrade(symbol, "TradesPerHourLimit", "Max trades per hour reached.");
                return;
            }

            var decisions = new List<StrategyDecision>();

            foreach (var strategy in _strategies)
            {
                if (IsStrategyTemporarilyBlocked(strategy))
                    continue;

                if (strategy is IRegimeAwareStrategy ra &&
                    !ra.ShouldTradeIn(diag))
                {
                    continue;
                }

                StrategyDecision dec;

                if (strategy is IAITradingStrategy ai)
                {
                    dec = ai.Decide(tick, ctx, diag);
                }
                else
                {
                    var sig = strategy.OnNewTick(tick, ctx);

                    double baseConf = 0.0;
                    if (sig != TradeSignal.None)
                    {
                        baseConf = 0.55;

                        if (diag.Regime == MarketRegime.TrendingUp && sig == TradeSignal.Buy)
                            baseConf += 0.1;
                        else if (diag.Regime == MarketRegime.TrendingDown && sig == TradeSignal.Sell)
                            baseConf += 0.1;
                    }

                    if (baseConf > 0.95) baseConf = 0.95;

                    dec = new StrategyDecision
                    {
                        StrategyName = strategy.Name,
                        Signal = sig,
                        Confidence = baseConf
                    };
                }

                if (dec.Signal != TradeSignal.None && dec.Confidence > 0)
                    decisions.Add(dec);
            }

            if (decisions.Count == 0)
            {
                LogNonTrade(symbol, "NoStrategySignal", "No strategy produced a valid signal.");
                return;
            }

            var ensemble = AIHelpers.EnsembleVote(decisions);
            if (ensemble.Signal == TradeSignal.None || ensemble.Confidence < 0.6)
            {
                LogNonTrade(symbol, "LowEnsembleConfidence", $"Ensemble confidence {ensemble.Confidence:F2} too low or no unanimous direction.");
                return;
            }

            var recentTrades = _tradeHistory.OrderByDescending(t => t.Time).Take(40).ToList();
            double expectedProfitScore = AIHelpers.EstimateExpectedProfitScore(ensemble, diag, recentTrades);

            if (expectedProfitScore <= 0.0)
            {
                string msg = $"AI filter blocked trade: expected profit score {expectedProfitScore:F2} (Heat={MarketHeatScore:F1}).";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "ExpectedProfitFilter", msg);
                return;
            }

            if (!IsGoodTradingConditions(ctx))
            {
                string msg = "Conditions check failed at execution step; skipping trade.";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "ConditionsFilter", msg);
                return;
            }

            ExecuteTrade(tick, ensemble, now);
        }

        /// <summary>
        /// Auto-symbol switching based on heat, regime, ticks, cooldown.
        /// </summary>
        private void MaybeSwitchActiveSymbol(DateTime now)
        {
            if (_autoSymbolMode != AutoSymbolMode.Auto)
                return;

            if (_symbolsToWatch == null || _symbolsToWatch.Count == 0)
                return;

            if ((now - _lastSymbolEvalTime) < _symbolEvalInterval)
                return;

            _lastSymbolEvalTime = now;

            // Do not switch while trades are open (1-tick trades should clear fast)
            if (_openTrades.Count > 0)
                return;

            if ((now - _lastSymbolSwitchTime) < _symbolSwitchCooldown)
                return;

            var currentSymbol = _activeSymbol ?? _symbolsToWatch[0];
            var currentCtx = GetOrCreateContext(currentSymbol);
            bool currentHasMinTicks = currentCtx.TickWindow.Count >= 50;

            _symbolHeatScores.TryGetValue(currentSymbol, out double currentHeat);

            string bestSymbol = null;
            double bestHeat = double.MinValue;
            MarketDiagnostics bestDiag = null;

            foreach (var sym in _symbolsToWatch)
            {
                var ctx = GetOrCreateContext(sym);
                if (ctx.TickWindow.Count < 50)
                    continue;

                if (!_symbolDiagnostics.TryGetValue(sym, out var diag))
                {
                    diag = ctx.AnalyzeRegime();
                    if (diag == null)
                        continue;
                }

                if (diag.Regime != MarketRegime.TrendingUp &&
                    diag.Regime != MarketRegime.TrendingDown &&
                    diag.Regime != MarketRegime.RangingHighVol)
                    continue;

                if (!_symbolHeatScores.TryGetValue(sym, out var h))
                {
                    h = AIHelpers.ComputeMarketHeatIndex(ctx, diag);
                    _symbolHeatScores[sym] = h;
                }

                if (h > bestHeat)
                {
                    bestHeat = h;
                    bestSymbol = sym;
                    bestDiag = diag;
                }
            }

            if (bestSymbol == null)
                return;

            if (!currentHasMinTicks || bestHeat >= currentHeat + 10.0)
            {
                if (bestSymbol == currentSymbol)
                    return;

                _activeSymbol = bestSymbol;
                CurrentDiagnostics = bestDiag;
                MarketHeatScore = bestHeat;
                _lastSymbolSwitchTime = now;

                RaiseBotEvent(
                    $"Auto-symbol: switched to {bestSymbol} " +
                    $"(Heat={bestHeat:F1}, Regime={bestDiag.Regime}).");
            }
        }

        #endregion

        #region Risk / Session / Conditions

        private bool IsWithinSession(DateTime nowLocal)
        {
            if (_rules.SessionStartLocal == null || _rules.SessionEndLocal == null)
                return true;

            var t = nowLocal.TimeOfDay;
            return t >= _rules.SessionStartLocal.Value && t <= _rules.SessionEndLocal.Value;
        }

        // 🆕 version that logs non-trade reasons when risk gates block trading
        private bool CheckGlobalRiskGatesWithNonTrade(string symbol)
        {
            var r = _riskManager.Settings;
            double start = _sessionStartBalance;
            double cur = _balance;

            if (_riskManager.IsDailyDrawdownExceeded(start, cur))
            {
                _autoPauseReason = AutoPauseReason.DailyDrawdownLimit;
                string msg = "Auto-paused: daily drawdown limit reached.";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "RiskGate:DailyDrawdownLimit", msg);
                return true;
            }

            if (_riskManager.IsDailyLossAmountExceeded(start, cur))
            {
                _autoPauseReason = AutoPauseReason.DailyLossAmountLimit;
                string msg = "Auto-paused: daily loss amount limit reached.";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "RiskGate:DailyLossAmountLimit", msg);
                return true;
            }

            if (_riskManager.IsDailyProfitLimitReached(start, cur))
            {
                _autoPauseReason = AutoPauseReason.DailyProfitLimit;
                string msg = "Auto-paused: daily profit limit reached.";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "RiskGate:DailyProfitLimit", msg);
                return true;
            }

            int totalTrades = _strategyStats.Values.Sum(s => s.TotalTrades);
            int totalWins = _strategyStats.Values.Sum(s => s.Wins);
            double winRate = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0.0;

            if (r.MinWinRatePercentToContinue > 0 &&
                totalTrades >= r.MinTradesBeforeWinRateCheck &&
                winRate < r.MinWinRatePercentToContinue)
            {
                _autoPauseReason = AutoPauseReason.WinRateBelowThreshold;
                string msg = $"Auto-paused: win rate {winRate:F1}% below threshold {r.MinWinRatePercentToContinue:F1}%.";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "RiskGate:WinRateBelowThreshold", msg);
                return true;
            }

            if (r.MaxConsecutiveLosses > 0 &&
                _consecutiveLosses >= r.MaxConsecutiveLosses)
            {
                _autoPauseReason = AutoPauseReason.ConsecutiveLossLimit;
                string msg = $"Auto-paused: max consecutive losses ({_consecutiveLosses}) reached.";
                RaiseBotEvent(msg);
                LogNonTrade(symbol, "RiskGate:ConsecutiveLossLimit", msg);
                return true;
            }

            return false;
        }

        private bool IsGoodTradingConditions(StrategyContext ctx)
        {
            var diag = CurrentDiagnostics;
            if (diag == null || ctx == null) return false;

            if (diag.Regime == MarketRegime.Unknown ||
                diag.Regime == MarketRegime.VolatileChoppy)
                return false;

            bool regimeOk = diag.Regime == MarketRegime.TrendingUp ||
                            diag.Regime == MarketRegime.TrendingDown ||
                            diag.Regime == MarketRegime.RangingHighVol ||
                            diag.Regime == MarketRegime.RangingLowVol;

            if (!regimeOk)
                return false;

            if (diag.Volatility is double vol)
            {
                if (vol < 0.02 || vol > 2.0)
                    return false;
            }

            if (diag.TrendSlope is double slope)
            {
                if (Math.Abs(slope) > 0.02)
                    return false;
            }

            var recentQuotes = ctx.GetLastQuotes(25);
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
                            RaiseBotEvent("Conditions filter: recent spike detected, delaying trades.");
                            return false;
                        }
                    }
                }
            }

            var recentTrades = _tradeHistory
                .OrderByDescending(t => t.Time)
                .Take(10)
                .ToList();

            if (recentTrades.Count >= 3)
            {
                double sumPL = recentTrades.Sum(t => t.Profit);
                bool lastThreeLosses = recentTrades.Take(3).All(t => t.Profit < 0);

                if (sumPL < -5.0 || lastThreeLosses)
                {
                    RaiseBotEvent($"Conditions filter: recent P/L unfavorable (last {recentTrades.Count} P/L={sumPL:F2}), delaying trades.");
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Strategy health / staking / execution

        private bool IsStrategyTemporarilyBlocked(ITradingStrategy strategy)
        {
            if (_strategyBlockedUntil.TryGetValue(strategy.Name, out var until))
            {
                if (DateTime.Now < until)
                    return true;
            }
            return false;
        }

        private double ComputeDynamicStake(double baseStake)
        {
            double stake = baseStake;

            if (_consecutiveLosses >= 6)
            {
                stake *= 0.25;
            }
            else if (_consecutiveLosses >= 3)
            {
                stake *= 0.5;
            }
            else
            {
                int totalTrades = _strategyStats.Values.Sum(s => s.TotalTrades);
                int totalWins = _strategyStats.Values.Sum(s => s.Wins);

                if (totalTrades >= 50)
                {
                    double winRate = (double)totalWins / totalTrades * 100.0;
                    if (winRate >= 60.0 && _consecutiveLosses == 0)
                    {
                        stake *= 1.2;
                    }
                }
            }

            var rs = _riskManager.Settings;
            if (stake < rs.MinStake) stake = rs.MinStake;
            if (stake > rs.MaxStake) stake = rs.MaxStake;

            return stake;
        }

        private async void ExecuteTrade(Tick tick, StrategyDecision decision, DateTime now)
        {
            double baseStake = _riskManager.ComputeStake(_balance);
            double stake = ComputeDynamicStake(baseStake);

            if (stake <= 0) return;

            _lastTradeTime = now;
            _tradeTimes.Add(now);

            ActiveStrategyName = decision.StrategyName;

            var tradeId = Guid.NewGuid();

            string dirText = decision.Signal == TradeSignal.Buy ? "Buy" : "Sell";

            var record = new TradeRecord
            {
                Time = now,
                Symbol = tick.Symbol,
                StrategyName = decision.StrategyName,
                Direction = dirText,
                Stake = stake,
                Profit = 0.0
            };

            _openTrades[tradeId] = record;
            RaiseBotEvent($"[Ensemble] Placing {dirText} trade via {decision.StrategyName} at {tick.Quote:F4} stake={stake:F2}, conf={decision.Confidence:F2}, heat={MarketHeatScore:F1}, symbol={tick.Symbol}");

            await _deriv.BuyRiseFallAsync(
                tick.Symbol,
                stake,
                decision.Signal,
                decision.StrategyName,
                tradeId,
                durationTicks: 1,
                currency: "USD");
        }

        private void OnContractFinished(string strategyName, Guid tradeId, double profit)
        {
            if (_openTrades.TryGetValue(tradeId, out var record))
            {
                _openTrades.Remove(tradeId);

                record.Profit = profit;
                _tradeHistory.Add(record);

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

                RaiseBotEvent($"Trade finished [{strategyName}] dir={record.Direction} stake={record.Stake:F2} P/L={profit:F2} (symbol={record.Symbol})");

                EvaluateStrategyHealth(strategyName);
                EvaluateShortTermPL();

                if (CheckGlobalRiskGatesWithNonTrade(record.Symbol))
                {
                    _running = false;
                    _autoPaused = true;
                }
            }
        }

        private void EvaluateStrategyHealth(string strategyName)
        {
            var recent = _tradeHistory
                .Where(t => t.StrategyName == strategyName)
                .OrderByDescending(t => t.Time)
                .Take(20)
                .ToList();

            if (recent.Count < 10) return;

            int wins = recent.Count(t => t.Profit >= 0);
            double winRate = (double)wins / recent.Count * 100.0;

            bool threeLosses = recent.Take(3).All(t => t.Profit < 0);

            if (threeLosses || winRate < 40.0)
            {
                int blockMinutes = threeLosses ? 15 : 30;
                _strategyBlockedUntil[strategyName] = DateTime.Now.AddMinutes(blockMinutes);
                RaiseBotEvent($"Strategy '{strategyName}' temporarily disabled ({blockMinutes} min) due to poor recent performance (win rate last {recent.Count} trades: {winRate:F1}%).");
            }
        }

        private void EvaluateShortTermPL()
        {
            var recent = _tradeHistory
                .OrderByDescending(t => t.Time)
                .Take(10)
                .ToList();

            if (recent.Count < 5) return;

            double totalPL = recent.Sum(t => t.Profit);

            if (totalPL < -5.0 && !_shortTermPLBlockActive)
            {
                _shortTermPLBlockActive = true;
                _shortTermPLBlockUntil = DateTime.Now.AddMinutes(15);
                RaiseBotEvent($"Temporarily pausing trades due to short-term drawdown (last {recent.Count} trades P/L: {totalPL:F2}).");
            }
        }

        private void LogNonTrade(string symbol, string reason, string detail)
        {
            var rec = new NonTradeRecord
            {
                Time = DateTime.Now,
                Symbol = symbol ?? _activeSymbol,
                Reason = reason,
                Detail = detail,
                Heat = MarketHeatScore,
                Regime = CurrentDiagnostics?.Regime
            };

            _nonTradeRecords.Add(rec);
            NonTradeEvent?.Invoke(rec);
        }

        private void RaiseBotEvent(string msg)
        {
            BotEvent?.Invoke(msg);
        }

        #endregion
    }

    #endregion
}
