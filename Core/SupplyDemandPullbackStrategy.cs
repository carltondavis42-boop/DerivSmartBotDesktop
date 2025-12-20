using System;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Supply/Demand pullback continuation strategy, inspired by M30 price-action:
    /// - Identify confirmed swing highs/lows (pivots)
    /// - Build a supply zone from the latest lower-high pivot (bear trend)
    /// - Build a demand zone from the latest higher-low pivot (bull trend)
    /// - Wait for pullback into the zone and a rejection candle
    /// - Trigger entry on break of the rejection candle (tick-level)
    ///
    /// This strategy is self-contained and resets cleanly on symbol changes, so it is safe
    /// with auto symbol rotation.
    /// </summary>
    class SupplyDemandPullbackStrategy : IAITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => $"SupplyDemand Pullback M{_timeframeMinutes}";
        public int DefaultDuration => Math.Max(3, _timeframeMinutes / 2);
        public string DefaultDurationUnit => "m";

        private readonly int _timeframeMinutes;
        private readonly int _atrPeriod;
        private readonly int _pivotStrength;
        private readonly int _minBars;
        private readonly int _minBarsBetweenTrades;
        private readonly double _rejectionWickBodyRatio;
        private readonly double _minWickAtrFraction;
        private readonly double _trendSlopeLookback;
        private readonly double _trendSlopeThreshold;

        private sealed class TfBar
        {
            public DateTime Start { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
        }

        private sealed class Zone
        {
            public double Low { get; set; }   // lower boundary
            public double High { get; set; }  // upper boundary
            public DateTime CreatedAt { get; set; }
            public string Kind { get; set; } = ""; // "SUPPLY" or "DEMAND"
            public int PivotIndex { get; set; } = -1;
        }

        private enum SetupState
        {
            None = 0,
            BearArmed = 1,
            BearWaitingBreak = 2,
            BullArmed = 3,
            BullWaitingBreak = 4
        }

        private sealed class SymbolState
        {
            public string Symbol { get; set; } = string.Empty;

            public readonly List<TfBar> Bars = new List<TfBar>(256);
            public TfBar? Current;

            public Zone? Supply;
            public Zone? Demand;

            public double? LastPivotHigh;
            public double? LastPivotLow;

            public double? PrevPivotHigh;
            public double? PrevPivotLow;

            public SetupState State = SetupState.None;

            public double TriggerPrice;
            public DateTime LastTradeBarStart = DateTime.MinValue;

            public void Reset()
            {
                Bars.Clear();
                Current = null;
                Supply = null;
                Demand = null;
                LastPivotHigh = null;
                LastPivotLow = null;
                PrevPivotHigh = null;
                PrevPivotLow = null;
                State = SetupState.None;
                TriggerPrice = 0.0;
                LastTradeBarStart = DateTime.MinValue;
            }
        }

        private readonly Dictionary<string, SymbolState> _states = new Dictionary<string, SymbolState>(StringComparer.OrdinalIgnoreCase);

        public SupplyDemandPullbackStrategy(
            int timeframeMinutes = 30,
            int atrPeriod = 14,
            int pivotStrength = 3,
            int minBars = 40,
            int minBarsBetweenTrades = 2,
            double rejectionWickBodyRatio = 2.0,
            double minWickAtrFraction = 0.20,
            int trendSlopeLookback = 20,
            double trendSlopeThreshold = 0.0002)
        {
            if (timeframeMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(timeframeMinutes));
            if (atrPeriod <= 1) throw new ArgumentOutOfRangeException(nameof(atrPeriod));
            if (pivotStrength <= 0) throw new ArgumentOutOfRangeException(nameof(pivotStrength));
            if (minBars < 10) throw new ArgumentOutOfRangeException(nameof(minBars));

            _timeframeMinutes = timeframeMinutes;
            _atrPeriod = atrPeriod;
            _pivotStrength = pivotStrength;
            _minBars = minBars;
            _minBarsBetweenTrades = Math.Max(0, minBarsBetweenTrades);
            _rejectionWickBodyRatio = Math.Max(1.1, rejectionWickBodyRatio);
            _minWickAtrFraction = Math.Max(0.0, minWickAtrFraction);
            _trendSlopeLookback = Math.Max(10, trendSlopeLookback);
            _trendSlopeThreshold = Math.Max(0.0, trendSlopeThreshold);
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var decision = Decide(tick, context, null);
            return decision.Signal;
        }


        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;

            // This is a pullback-continuation strategy; avoid choppy extreme regimes.
            if (diagnostics.Regime == MarketRegime.VolatileChoppy)
                return false;

            return true;
        }

        public StrategyDecision Evaluate(StrategyContext ctx, MarketDiagnostics diag)
        {
            if (ctx == null || ctx.TickWindow == null || ctx.TickWindow.Count == 0)
                return NoSignal();

            var lastTick = ctx.TickWindow[ctx.TickWindow.Count - 1];
            if (lastTick == null)
                return NoSignal();

            return Decide(lastTick, ctx, diag);
        }


        public StrategyDecision Decide(Tick tick, StrategyContext context, MarketDiagnostics diag)
        {
            if (tick == null) throw new ArgumentNullException(nameof(tick));

            var state = GetState(tick.Symbol);
            UpdateBars(state, tick);

            // Need enough completed bars to detect pivots / ATR reliably.
            if (state.Bars.Count < _minBars)
                return NoSignal();

            var atr = ComputeAtr(state.Bars, _atrPeriod);
            if (atr <= 0)
                return NoSignal();

            // Light trend inference from diagnostics (if provided) + slope of TF closes.
            var trendBias = GetTrendBias(diag, state.Bars);

            // If symbol changes rapidly (auto-rotation), avoid firing on stale triggers.
            InvalidateZonesIfNeeded(state, tick.Quote, atr);

            // If waiting for break trigger, fire on tick-level cross.
            if (state.State == SetupState.BearWaitingBreak && tick.Quote <= state.TriggerPrice)
            {
                if (IsTradeSpaced(state))
                    return BuildDecision(TradeSignal.Sell, confidence: ComputeConfidence(trendBias, atr, strength: 1.0));

                return NoSignal();
            }

            if (state.State == SetupState.BullWaitingBreak && tick.Quote >= state.TriggerPrice)
            {
                if (IsTradeSpaced(state))
                    return BuildDecision(TradeSignal.Buy, confidence: ComputeConfidence(trendBias, atr, strength: 1.0));

                return NoSignal();
            }

            // Evaluate zone interactions on the most recently CLOSED bar.
            // Current bar is not closed; the last bar in state.Bars is closed.
            var last = state.Bars[state.Bars.Count - 1];
            var prev = state.Bars.Count >= 2 ? state.Bars[state.Bars.Count - 2] : null;

            // Attempt to refresh zones from pivots.
            RefreshZonesFromPivots(state, atr, trendBias);

            // If armed zones exist, look for rejection patterns.
            if (trendBias <= -0.25 && state.Supply != null)
            {
                if (state.State == SetupState.None || state.State == SetupState.BearArmed)
                {
                    if (TouchesZone(last, state.Supply) && IsBearRejection(last, prev, atr))
                    {
                        state.State = SetupState.BearWaitingBreak;
                        state.TriggerPrice = last.Low - ComputeTriggerBuffer(atr);

                        return NoSignal();
                    }
                    else
                    {
                        state.State = SetupState.BearArmed;
                    }
                }
            }
            else if (trendBias >= 0.25 && state.Demand != null)
            {
                if (state.State == SetupState.None || state.State == SetupState.BullArmed)
                {
                    if (TouchesZone(last, state.Demand) && IsBullRejection(last, prev, atr))
                    {
                        state.State = SetupState.BullWaitingBreak;
                        state.TriggerPrice = last.High + ComputeTriggerBuffer(atr);

                        return NoSignal();
                    }
                    else
                    {
                        state.State = SetupState.BullArmed;
                    }
                }
            }

            return NoSignal();
        }

        private SymbolState GetState(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "UNKNOWN";

            if (!_states.TryGetValue(symbol, out var s))
            {
                s = new SymbolState { Symbol = symbol };
                _states[symbol] = s;
            }

            return s;
        }

        private StrategyDecision NoSignal()
        {
            return new StrategyDecision
            {
                StrategyName = Name,
                Signal = TradeSignal.None,
                Confidence = 0.0,
                Duration = DefaultDuration,
                DurationUnit = DefaultDurationUnit
            };
        }

        private StrategyDecision BuildDecision(TradeSignal signal, double confidence)
        {
            // Normalize confidence for downstream selectors.
            if (confidence < 0.45) confidence = 0.45;
            if (confidence > 0.95) confidence = 0.95;

            return new StrategyDecision
            {
                StrategyName = Name,
                Signal = signal,
                Confidence = confidence,
                Duration = DefaultDuration,
                DurationUnit = DefaultDurationUnit
            };
        }

        private void UpdateBars(SymbolState state, Tick tick)
        {
            var start = AlignToTimeframe(tick.Time, _timeframeMinutes);

            if (state.Current == null || start > state.Current.Start)
            {
                // Close previous
                if (state.Current != null)
                {
                    state.Bars.Add(state.Current);

                    // Keep memory bounded
                    if (state.Bars.Count > 500)
                        state.Bars.RemoveAt(0);

                    // Confirm pivots using the newly closed bar.
                    ConfirmPivots(state);
                }

                state.Current = new TfBar
                {
                    Start = start,
                    Open = tick.Quote,
                    High = tick.Quote,
                    Low = tick.Quote,
                    Close = tick.Quote
                };
            }
            else
            {
                // Update current bar
                if (tick.Quote > state.Current.High) state.Current.High = tick.Quote;
                if (tick.Quote < state.Current.Low) state.Current.Low = tick.Quote;
                state.Current.Close = tick.Quote;
            }
        }

        private static DateTime AlignToTimeframe(DateTime t, int tfMinutes)
        {
            // Floor to the timeframe boundary
            int minute = (t.Minute / tfMinutes) * tfMinutes;

            return new DateTime(
                t.Year,
                t.Month,
                t.Day,
                t.Hour,
                minute,
                0,
                t.Kind);
        }

        private void ConfirmPivots(SymbolState state)
        {
            int n = state.Bars.Count;
            int s = _pivotStrength;

            if (n < (s * 2) + 1)
                return;

            int candidateIndex = n - 1 - s;
            if (candidateIndex <= s || candidateIndex >= n - s)
                return;

            var candidate = state.Bars[candidateIndex];

            bool isHigh = true;
            bool isLow = true;

            for (int i = candidateIndex - s; i <= candidateIndex + s; i++)
            {
                if (i == candidateIndex) continue;

                if (state.Bars[i].High >= candidate.High) isHigh = false;
                if (state.Bars[i].Low <= candidate.Low) isLow = false;

                if (!isHigh && !isLow) break;
            }

            if (isHigh)
            {
                state.PrevPivotHigh = state.LastPivotHigh;
                state.LastPivotHigh = candidate.High;
            }

            if (isLow)
            {
                state.PrevPivotLow = state.LastPivotLow;
                state.LastPivotLow = candidate.Low;
            }

            // Create/refresh zones from fresh pivots.
            // Zones are created from the pivot candle's body->wick area.
            if (isHigh && state.PrevPivotHigh.HasValue && state.LastPivotHigh.HasValue)
            {
                // Lower-high suggests bearish continuation zone.
                if (state.LastPivotHigh.Value < state.PrevPivotHigh.Value)
                {
                    double bodyTop = Math.Max(candidate.Open, candidate.Close);
                    state.Supply = new Zone
                    {
                        Kind = "SUPPLY",
                        Low = bodyTop,
                        High = candidate.High,
                        CreatedAt = candidate.Start,
                        PivotIndex = candidateIndex
                    };

                    // When a new supply zone is formed, clear bull state to avoid conflict.
                    if (state.State == SetupState.BullArmed || state.State == SetupState.BullWaitingBreak)
                        state.State = SetupState.None;
                }
            }

            if (isLow && state.PrevPivotLow.HasValue && state.LastPivotLow.HasValue)
            {
                // Higher-low suggests bullish continuation zone.
                if (state.LastPivotLow.Value > state.PrevPivotLow.Value)
                {
                    double bodyBottom = Math.Min(candidate.Open, candidate.Close);
                    state.Demand = new Zone
                    {
                        Kind = "DEMAND",
                        Low = candidate.Low,
                        High = bodyBottom,
                        CreatedAt = candidate.Start,
                        PivotIndex = candidateIndex
                    };

                    if (state.State == SetupState.BearArmed || state.State == SetupState.BearWaitingBreak)
                        state.State = SetupState.None;
                }
            }
        }

        private void RefreshZonesFromPivots(SymbolState state, double atr, double trendBias)
        {
            // If zones are ancient relative to current volatility, drop them.
            // This prevents "sticky" zones when the market drifts.
            const int maxAgeBars = 60;
            int lastIndex = state.Bars.Count - 1;

            if (state.Supply != null)
            {
                if (lastIndex - state.Supply.PivotIndex > maxAgeBars)
                    state.Supply = null;
            }

            if (state.Demand != null)
            {
                if (lastIndex - state.Demand.PivotIndex > maxAgeBars)
                    state.Demand = null;
            }

            // Optional: if trend flips hard, drop opposite zone.
            if (trendBias >= 0.50)
                state.Supply = null;
            else if (trendBias <= -0.50)
                state.Demand = null;
        }

        private static bool TouchesZone(TfBar bar, Zone zone)
        {
            if (zone == null) return false;

            // Any overlap between candle range and zone.
            return bar.High >= zone.Low && bar.Low <= zone.High;
        }

        private bool IsBearRejection(TfBar bar, TfBar? prev, double atr)
        {
            double body = Math.Abs(bar.Close - bar.Open);
            double upperWick = bar.High - Math.Max(bar.Open, bar.Close);

            bool wickOk = body > 0 && upperWick >= _rejectionWickBodyRatio * body;
            bool wickAtrOk = upperWick >= (_minWickAtrFraction * atr);

            bool closesDown = bar.Close < bar.Open;
            bool closesLowerHalf = bar.Close <= (bar.Low + (bar.High - bar.Low) * 0.45);

            bool engulf = false;
            if (prev != null)
            {
                engulf = bar.Close < prev.Low && bar.Open >= prev.Close;
            }

            // Rejecting at top: prefer long upper wick + close down.
            return (closesDown && closesLowerHalf && (wickOk || engulf) && wickAtrOk);
        }

        private bool IsBullRejection(TfBar bar, TfBar? prev, double atr)
        {
            double body = Math.Abs(bar.Close - bar.Open);
            double lowerWick = Math.Min(bar.Open, bar.Close) - bar.Low;

            bool wickOk = body > 0 && lowerWick >= _rejectionWickBodyRatio * body;
            bool wickAtrOk = lowerWick >= (_minWickAtrFraction * atr);

            bool closesUp = bar.Close > bar.Open;
            bool closesUpperHalf = bar.Close >= (bar.Low + (bar.High - bar.Low) * 0.55);

            bool engulf = false;
            if (prev != null)
            {
                engulf = bar.Close > prev.High && bar.Open <= prev.Close;
            }

            return (closesUp && closesUpperHalf && (wickOk || engulf) && wickAtrOk);
        }

        private static double ComputeAtr(IReadOnlyList<TfBar> bars, int period)
        {
            if (bars == null || bars.Count < period + 2)
                return 0.0;

            int start = bars.Count - period;
            double sum = 0.0;

            for (int i = start; i < bars.Count; i++)
            {
                var cur = bars[i];
                var prev = bars[i - 1];

                double tr1 = cur.High - cur.Low;
                double tr2 = Math.Abs(cur.High - prev.Close);
                double tr3 = Math.Abs(cur.Low - prev.Close);

                double tr = Math.Max(tr1, Math.Max(tr2, tr3));
                sum += tr;
            }

            return sum / period;
        }

        private double GetTrendBias(MarketDiagnostics diag, IReadOnlyList<TfBar> bars)
        {
            // -1..+1
            double bias = 0.0;

            if (diag != null)
            {
                if (diag.Regime == MarketRegime.TrendingUp) bias += 0.6;
                else if (diag.Regime == MarketRegime.TrendingDown) bias -= 0.6;

                if (diag.TrendSlope.HasValue)
                {
                    if (diag.TrendSlope.Value > 0) bias += 0.2;
                    else if (diag.TrendSlope.Value < 0) bias -= 0.2;
                }
            }

            // Add a small slope term from TF bars.
            if (bars != null && bars.Count >= (int)_trendSlopeLookback + 1)
            {
                int n = (int)_trendSlopeLookback;
                var closes = bars.Skip(bars.Count - n).Select(b => b.Close).ToArray();

                double slope = SimpleSlope(closes);
                if (Math.Abs(slope) >= _trendSlopeThreshold)
                {
                    bias += slope > 0 ? 0.25 : -0.25;
                }
            }

            if (bias > 1.0) bias = 1.0;
            if (bias < -1.0) bias = -1.0;
            return bias;
        }

        private static double SimpleSlope(IReadOnlyList<double> series)
        {
            if (series == null || series.Count < 5) return 0.0;

            // Linear regression slope over x=0..n-1
            int n = series.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;

            for (int i = 0; i < n; i++)
            {
                double x = i;
                double y = series[i];
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumXX += x * x;
            }

            double denom = (n * sumXX - sumX * sumX);
            if (Math.Abs(denom) < 1e-9) return 0.0;

            return (n * sumXY - sumX * sumY) / denom;
        }

        private static double ComputeTriggerBuffer(double atr)
        {
            // Small buffer to avoid firing on noise; keep proportional to volatility.
            return Math.Max(atr * 0.03, 0.0);
        }

        private void InvalidateZonesIfNeeded(SymbolState state, double price, double atr)
        {
            if (state.Supply != null)
            {
                // If price hard breaks above supply, invalidate.
                if (price > state.Supply.High + (atr * 0.05))
                {
                    state.Supply = null;
                    if (state.State == SetupState.BearArmed || state.State == SetupState.BearWaitingBreak)
                        state.State = SetupState.None;
                }
            }

            if (state.Demand != null)
            {
                // If price hard breaks below demand, invalidate.
                if (price < state.Demand.Low - (atr * 0.05))
                {
                    state.Demand = null;
                    if (state.State == SetupState.BullArmed || state.State == SetupState.BullWaitingBreak)
                        state.State = SetupState.None;
                }
            }
        }

        private bool IsTradeSpaced(SymbolState state)
        {
            if (_minBarsBetweenTrades <= 0)
                return true;

            // Use last closed bar as spacing unit.
            var lastBar = state.Bars[state.Bars.Count - 1];
            if (state.LastTradeBarStart == DateTime.MinValue)
            {
                state.LastTradeBarStart = lastBar.Start;
                // clear zones after entry to force a fresh setup
                state.State = SetupState.None;
                state.Supply = null;
                state.Demand = null;
                return true;
            }

            var barsSince = (int)((lastBar.Start - state.LastTradeBarStart).TotalMinutes / _timeframeMinutes);
            if (barsSince >= _minBarsBetweenTrades)
            {
                state.LastTradeBarStart = lastBar.Start;
                state.State = SetupState.None;
                state.Supply = null;
                state.Demand = null;
                return true;
            }

            return false;
        }

        private static double ComputeConfidence(double trendBias, double atr, double strength)
        {
            // Base confidence prefers strong trend alignment; strength is a 0..1 score from pattern quality.
            double conf = 0.55;

            conf += Math.Abs(trendBias) * 0.25;
            conf += Math.Max(0.0, Math.Min(0.20, strength * 0.20));

            // If atr is zero (shouldn't happen), do not inflate.
            if (atr <= 0) conf -= 0.10;

            if (conf < 0.0) conf = 0.0;
            if (conf > 0.99) conf = 0.99;
            return conf;
        }
    }
}
