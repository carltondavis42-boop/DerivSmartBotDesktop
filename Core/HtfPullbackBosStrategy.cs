using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Multi-timeframe pullback + BOS strategy:
    /// - H1 bias (EMA50/EMA200 + slope)
    /// - M15 regime filter + pullback into value zone
    /// - M5 break of structure trigger
    /// </summary>
    public sealed class HtfPullbackBosStrategy : IAITradingStrategy, IRegimeAwareStrategy, ITradeDurationProvider
    {
        public string Name => "HTF Pullback BOS";
        public int DefaultDuration => _durationMinutes;
        public string DefaultDurationUnit => "m";

        private readonly int _durationMinutes;
        private readonly int _pivotLookback;
        private readonly int _atrPeriod;
        private readonly double _gapMin;
        private readonly double _atrStopMult;
        private readonly double _minStopAtr;
        private readonly double _maxStopAtr;
        private readonly int _cooldownMinutes;
        private readonly int _emaSlopeLookback;
        private readonly int _m15CrossLookback;

        private enum BiasDirection
        {
            None = 0,
            Long = 1,
            Short = -1
        }

        private enum FsmState
        {
            Idle = 0,
            BiasOk = 1,
            Pullback = 2,
            Armed = 3,
            Cooldown = 4
        }

        private sealed class TfBar
        {
            public DateTime Start { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
        }

        private sealed class Pivot
        {
            public DateTime Time { get; set; }
            public double Price { get; set; }
        }

        private sealed class Ema
        {
            private readonly double _alpha;
            private double? _value;
            private int _count;

            public Ema(int period)
            {
                if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
                _alpha = 2.0 / (period + 1.0);
            }

            public double? Value => _value;
            public bool IsReady(int period) => _count >= period;

            public double? Update(double price)
            {
                if (!_value.HasValue)
                    _value = price;
                else
                    _value = _alpha * price + (1.0 - _alpha) * _value.Value;

                _count++;
                return _value;
            }
        }

        private sealed class SymbolState
        {
            public string Symbol { get; set; } = string.Empty;

            public readonly List<TfBar> H1Bars = new();
            public readonly List<TfBar> M15Bars = new();
            public readonly List<TfBar> M5Bars = new();

            public TfBar? H1Current;
            public TfBar? M15Current;
            public TfBar? M5Current;

            public readonly Ema H1Ema50 = new(50);
            public readonly Ema H1Ema200 = new(200);
            public readonly Ema M15Ema50 = new(50);
            public readonly Ema M15Ema200 = new(200);

            public readonly List<double> H1Ema50Series = new();
            public readonly List<double> M15Ema50Series = new();

            public BiasDirection Bias = BiasDirection.None;
            public FsmState State = FsmState.Idle;

            public DateTime? PullbackStart;
            public DateTime? CooldownUntil;
            public DateTime? LastH1Processed;
            public DateTime? LastM15Processed;
            public DateTime? LastM5Processed;

            public readonly List<Pivot> PivotHighs = new();
            public readonly List<Pivot> PivotLows = new();
        }

        private readonly Dictionary<string, SymbolState> _states = new(StringComparer.OrdinalIgnoreCase);

        public HtfPullbackBosStrategy(
            int durationMinutes = 5,
            int pivotLookback = 2,
            int atrPeriod = 14,
            double gapMin = 0.30,
            double atrStopMult = 1.2,
            double minStopAtr = 0.6,
            double maxStopAtr = 2.5,
            int cooldownMinutes = 15,
            int emaSlopeLookback = 5,
            int m15CrossLookback = 10)
        {
            _durationMinutes = Math.Max(1, durationMinutes);
            _pivotLookback = Math.Max(1, pivotLookback);
            _atrPeriod = Math.Max(5, atrPeriod);
            _gapMin = Math.Max(0.1, gapMin);
            _atrStopMult = Math.Max(0.5, atrStopMult);
            _minStopAtr = Math.Max(0.1, minStopAtr);
            _maxStopAtr = Math.Max(_minStopAtr, maxStopAtr);
            _cooldownMinutes = Math.Max(0, cooldownMinutes);
            _emaSlopeLookback = Math.Max(3, emaSlopeLookback);
            _m15CrossLookback = Math.Max(5, m15CrossLookback);
        }

        public TradeSignal OnNewTick(Tick tick, StrategyContext context)
        {
            var decision = Decide(tick, context, null);
            return decision.Signal;
        }

        public bool ShouldTradeIn(MarketDiagnostics diagnostics)
        {
            if (diagnostics == null) return true;
            return diagnostics.Regime != MarketRegime.VolatileChoppy;
        }

        public StrategyDecision Decide(Tick tick, StrategyContext context, MarketDiagnostics diag)
        {
            if (tick == null) throw new ArgumentNullException(nameof(tick));

            var state = GetState(tick.Symbol);
            UpdateBars(state, tick);

            RefreshCooldown(state, tick.Time);
            UpdateBiasIfH1Closed(state);
            UpdateM15State(state);

            UpdateM5Pivots(state);

            if (state.State != FsmState.Armed)
                return NoSignal();

            if (state.CooldownUntil.HasValue && tick.Time < state.CooldownUntil.Value)
                return NoSignal();

            var atr = ComputeAtr(state.M15Bars, _atrPeriod);
            if (atr <= 0)
                return NoSignal();

            var lastM5 = state.M5Bars.LastOrDefault();
            if (lastM5 == null)
                return NoSignal();

            double bosBuffer = 0.05 * atr;
            double bodyPct = ComputeBodyPct(lastM5);

            if (state.Bias == BiasDirection.Long)
            {
                var pivot = GetLatestPivot(state.PivotHighs, state.PullbackStart);
                if (pivot == null)
                    return NoSignal();

                if (lastM5.Close > pivot.Price + bosBuffer && bodyPct >= 0.5)
                {
                    if (!StopDistanceOk(atr))
                        return NoSignal();

                    EnterCooldown(state, tick.Time);
                    return BuildDecision(TradeSignal.Buy, ComputeConfidence(state, atr, bodyPct));
                }
            }
            else if (state.Bias == BiasDirection.Short)
            {
                var pivot = GetLatestPivot(state.PivotLows, state.PullbackStart);
                if (pivot == null)
                    return NoSignal();

                if (lastM5.Close < pivot.Price - bosBuffer && bodyPct >= 0.5)
                {
                    if (!StopDistanceOk(atr))
                        return NoSignal();

                    EnterCooldown(state, tick.Time);
                    return BuildDecision(TradeSignal.Sell, ComputeConfidence(state, atr, bodyPct));
                }
            }

            return NoSignal();
        }

        private SymbolState GetState(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "UNKNOWN";

            if (!_states.TryGetValue(symbol, out var state))
            {
                state = new SymbolState { Symbol = symbol };
                _states[symbol] = state;
            }

            return state;
        }

        private void UpdateBars(SymbolState state, Tick tick)
        {
            UpdateBar(state, tick, 60, state.H1Bars, ref state.H1Current);
            UpdateBar(state, tick, 15, state.M15Bars, ref state.M15Current);
            UpdateBar(state, tick, 5, state.M5Bars, ref state.M5Current);
        }

        private static void UpdateBar(SymbolState state, Tick tick, int tfMinutes, List<TfBar> bars, ref TfBar? current)
        {
            var start = AlignToTimeframe(tick.Time, tfMinutes);
            if (current == null || start > current.Start)
            {
                if (current != null)
                {
                    bars.Add(current);
                    if (bars.Count > 600)
                        bars.RemoveAt(0);
                }

                current = new TfBar
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
                if (tick.Quote > current.High) current.High = tick.Quote;
                if (tick.Quote < current.Low) current.Low = tick.Quote;
                current.Close = tick.Quote;
            }
        }

        private void UpdateBiasIfH1Closed(SymbolState state)
        {
            var last = state.H1Bars.LastOrDefault();
            if (last == null)
                return;

            if (state.LastH1Processed.HasValue && state.LastH1Processed.Value == last.Start)
                return;

            state.LastH1Processed = last.Start;

            if (state.H1Ema50.Update(last.Close) is double ema50)
            {
                state.H1Ema50Series.Add(ema50);
                if (state.H1Ema50Series.Count > 200)
                    state.H1Ema50Series.RemoveAt(0);
            }

            if (state.H1Ema200.Update(last.Close) is double)
            {
                // no-op, EMA200 stored in object
            }

            if (!state.H1Ema50.IsReady(50) || !state.H1Ema200.IsReady(200))
                return;

            double ema50Val = state.H1Ema50.Value ?? 0.0;
            double ema200Val = state.H1Ema200.Value ?? 0.0;
            double slope = ComputeSlope(state.H1Ema50Series, _emaSlopeLookback);

            BiasDirection bias = BiasDirection.None;
            if (ema50Val > ema200Val && slope > 0 && last.Close > ema50Val)
                bias = BiasDirection.Long;
            else if (ema50Val < ema200Val && slope < 0 && last.Close < ema50Val)
                bias = BiasDirection.Short;

            if (bias != state.Bias)
            {
                state.Bias = bias;
                state.State = bias == BiasDirection.None ? FsmState.Idle : FsmState.BiasOk;
                state.PullbackStart = null;
            }
        }

        private void UpdateM15State(SymbolState state)
        {
            var last = state.M15Bars.LastOrDefault();
            if (last == null)
                return;

            if (state.LastM15Processed.HasValue && state.LastM15Processed.Value == last.Start)
                return;

            state.LastM15Processed = last.Start;

            if (state.M15Ema50.Update(last.Close) is double ema50)
            {
                state.M15Ema50Series.Add(ema50);
                if (state.M15Ema50Series.Count > 200)
                    state.M15Ema50Series.RemoveAt(0);
            }

            if (state.M15Ema200.Update(last.Close) is double)
            {
                // no-op
            }

            if (state.Bias == BiasDirection.None)
            {
                state.State = FsmState.Idle;
                return;
            }

            if (!state.M15Ema50.IsReady(50) || !state.M15Ema200.IsReady(200))
                return;

            var atr = ComputeAtr(state.M15Bars, _atrPeriod);
            if (atr <= 0)
                return;

            if (!RegimeFilterOk(state, atr))
            {
                state.State = FsmState.Idle;
                state.PullbackStart = null;
                return;
            }

            if (PullbackToValueOk(state, atr))
            {
                state.State = FsmState.Armed;
                state.PullbackStart ??= last.Start;
                return;
            }

            state.State = FsmState.BiasOk;
            state.PullbackStart = null;
        }

        private void UpdateM5Pivots(SymbolState state)
        {
            var last = state.M5Bars.LastOrDefault();
            if (last == null)
                return;

            if (state.LastM5Processed.HasValue && state.LastM5Processed.Value == last.Start)
                return;

            state.LastM5Processed = last.Start;

            int n = state.M5Bars.Count;
            int l = _pivotLookback;
            if (n < (l * 2) + 1)
                return;

            int candidateIndex = n - 1 - l;
            if (candidateIndex <= l || candidateIndex >= n - l)
                return;

            var candidate = state.M5Bars[candidateIndex];
            bool isHigh = true;
            bool isLow = true;

            for (int i = candidateIndex - l; i <= candidateIndex + l; i++)
            {
                if (i == candidateIndex) continue;
                if (state.M5Bars[i].High >= candidate.High) isHigh = false;
                if (state.M5Bars[i].Low <= candidate.Low) isLow = false;
                if (!isHigh && !isLow) break;
            }

            if (isHigh)
                AddPivot(state.PivotHighs, candidate.Start, candidate.High);
            if (isLow)
                AddPivot(state.PivotLows, candidate.Start, candidate.Low);
        }

        private static void AddPivot(List<Pivot> list, DateTime time, double price)
        {
            list.Add(new Pivot { Time = time, Price = price });
            if (list.Count > 200)
                list.RemoveAt(0);
        }

        private static Pivot? GetLatestPivot(List<Pivot> pivots, DateTime? since)
        {
            if (pivots.Count == 0)
                return null;

            if (since == null)
                return pivots.Last();

            for (int i = pivots.Count - 1; i >= 0; i--)
            {
                if (pivots[i].Time >= since.Value)
                    return pivots[i];
            }

            return pivots.Last();
        }

        private bool RegimeFilterOk(SymbolState state, double atr)
        {
            if (state.M15Ema50.Value == null || state.M15Ema200.Value == null)
                return false;

            double ema50 = state.M15Ema50.Value.Value;
            double ema200 = state.M15Ema200.Value.Value;
            double gapNorm = Math.Abs(ema50 - ema200) / atr;

            if (gapNorm < _gapMin)
                return false;

            int crosses = CountEmaCrosses(state.M15Bars, ema50, _m15CrossLookback);
            return crosses <= 3;
        }

        private bool PullbackToValueOk(SymbolState state, double atr)
        {
            var last = state.M15Bars.LastOrDefault();
            var prev = state.M15Bars.Count >= 2 ? state.M15Bars[^2] : null;
            if (last == null || prev == null)
                return false;

            double ema50 = state.M15Ema50.Value ?? 0.0;
            double ema200 = state.M15Ema200.Value ?? 0.0;

            double zoneLow = Math.Min(ema50, ema200);
            double zoneHigh = Math.Max(ema50, ema200);

            bool decel = IsDeceleration(last, prev, state.Bias);

            if (state.Bias == BiasDirection.Long)
            {
                bool inZone = last.Low <= zoneHigh;
                bool noBreak = last.Close >= ema200 || last.Low >= ema200 - (0.25 * atr);
                return inZone && noBreak && decel;
            }

            if (state.Bias == BiasDirection.Short)
            {
                bool inZone = last.High >= zoneLow;
                bool noBreak = last.Close <= ema200 || last.High <= ema200 + (0.25 * atr);
                return inZone && noBreak && decel;
            }

            return false;
        }

        private static bool IsDeceleration(TfBar last, TfBar prev, BiasDirection bias)
        {
            double bodyLast = Math.Abs(last.Close - last.Open);
            double bodyPrev = Math.Abs(prev.Close - prev.Open);
            double rangeLast = Math.Max(1e-6, last.High - last.Low);
            double bodyPct = bodyLast / rangeLast;

            bool wicky = bodyPct <= 0.6;
            bool twoCandle;
            if (bias == BiasDirection.Short)
            {
                twoCandle = bodyLast < bodyPrev &&
                            last.Close > last.Open &&
                            prev.Close > prev.Open;
            }
            else
            {
                twoCandle = bodyLast < bodyPrev &&
                            last.Close < last.Open &&
                            prev.Close < prev.Open;
            }

            return wicky || twoCandle;
        }

        private static int CountEmaCrosses(IReadOnlyList<TfBar> bars, double ema, int lookback)
        {
            if (bars.Count < 2)
                return 0;

            int start = Math.Max(1, bars.Count - lookback);
            int crosses = 0;
            for (int i = start; i < bars.Count; i++)
            {
                bool prevAbove = bars[i - 1].Close >= ema;
                bool currAbove = bars[i].Close >= ema;
                if (prevAbove != currAbove)
                    crosses++;
            }

            return crosses;
        }

        private bool StopDistanceOk(double atr)
        {
            double stopDist = _atrStopMult * atr;
            return stopDist >= _minStopAtr * atr && stopDist <= _maxStopAtr * atr;
        }

        private void EnterCooldown(SymbolState state, DateTime now)
        {
            if (_cooldownMinutes <= 0)
                return;

            state.State = FsmState.Cooldown;
            state.CooldownUntil = now.AddMinutes(_cooldownMinutes);
        }

        private static void RefreshCooldown(SymbolState state, DateTime now)
        {
            if (state.State != FsmState.Cooldown || !state.CooldownUntil.HasValue)
                return;

            if (now < state.CooldownUntil.Value)
                return;

            state.CooldownUntil = null;
            state.State = state.Bias == BiasDirection.None ? FsmState.Idle : FsmState.BiasOk;
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

        private static double ComputeSlope(IReadOnlyList<double> series, int lookback)
        {
            if (series == null || series.Count <= lookback)
                return 0.0;

            int idx = series.Count - 1;
            double now = series[idx];
            double prev = series[idx - lookback];
            return now - prev;
        }

        private static double ComputeBodyPct(TfBar bar)
        {
            double range = Math.Max(1e-6, bar.High - bar.Low);
            return Math.Abs(bar.Close - bar.Open) / range;
        }

        private static DateTime AlignToTimeframe(DateTime t, int tfMinutes)
        {
            int minute = (t.Minute / tfMinutes) * tfMinutes;
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, minute, 0, t.Kind);
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
            confidence = Math.Clamp(confidence, 0.55, 0.95);
            return new StrategyDecision
            {
                StrategyName = Name,
                Signal = signal,
                Confidence = confidence,
                Duration = DefaultDuration,
                DurationUnit = DefaultDurationUnit
            };
        }

        private double ComputeConfidence(SymbolState state, double atr, double bodyPct)
        {
            double score = 0.60;

            if (state.M15Ema50.Value.HasValue && state.M15Ema200.Value.HasValue)
            {
                double gapNorm = Math.Abs(state.M15Ema50.Value.Value - state.M15Ema200.Value.Value) / Math.Max(1e-6, atr);
                score += Math.Min(0.20, Math.Max(0.0, gapNorm - _gapMin) * 0.20);
            }

            score += Math.Min(0.15, bodyPct * 0.15);
            return score;
        }
    }
}
