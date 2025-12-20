using System;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    public sealed class BacktestResult
    {
        public double StartingBalance { get; init; }
        public double EndingBalance { get; init; }
        public int TotalTrades { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public double MaxDrawdown { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Paper/forward testing engine that replays ticks and resolves trades using
    /// strategy-driven durations instead of a fixed look-ahead.
    /// </summary>
    public sealed class BacktestEngine
    {
        private readonly IList<ITradingStrategy> _strategies;
        private readonly RiskManager _riskManager;
        private readonly StrategyContext _context = new();
        private readonly IMarketRegimeClassifier _regimeClassifier;
        private readonly IContractPricingModel _pricingModel;

        public BacktestEngine(
            IEnumerable<ITradingStrategy> strategies,
            RiskManager riskManager,
            IMarketRegimeClassifier? regimeClassifier = null,
            IContractPricingModel? pricingModel = null)
        {
            _strategies = strategies.ToList();
            _riskManager = riskManager;
            _regimeClassifier = regimeClassifier ?? new AiMarketRegimeClassifier();
            _pricingModel = pricingModel ?? new FixedPayoutPricingModel();
        }

        public BacktestResult Run(IReadOnlyList<Tick> ticks, double startingBalance)
        {
            if (ticks == null || ticks.Count == 0)
                throw new ArgumentException("Ticks collection must not be empty.", nameof(ticks));

            double balance = startingBalance;
            double peak = startingBalance;
            double maxDrawdown = 0.0;
            int wins = 0, losses = 0, totalTrades = 0;

            for (int i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                _context.AddTick(tick);

                var diag = _context.AnalyzeRegime() ?? new MarketDiagnostics();
                var quotes = _context.GetLastQuotes(120);
                double regimeScore;
                var regime = _regimeClassifier.Classify(
                    quotes,
                    diag.Volatility,
                    diag.TrendSlope,
                    out regimeScore);

                diag.Regime = regime;
                diag.RegimeScore = regimeScore;

                var decisions = new List<StrategyDecision>();
                foreach (var s in _strategies)
                {
                    if (s == null) continue;

                    StrategyDecision strategyDecision;
                    if (s is IAITradingStrategy ai)
                    {
                        strategyDecision = ai.Decide(tick, _context, diag);
                        if (strategyDecision.Duration <= 0 || string.IsNullOrWhiteSpace(strategyDecision.DurationUnit))
                        {
                            var (duration, unit) = ResolveDefaultDuration(s);
                            strategyDecision.Duration = duration;
                            strategyDecision.DurationUnit = unit;
                        }
                    }
                    else
                    {
                        var sig = s.OnNewTick(tick, _context);
                        if (sig == TradeSignal.None) continue;

                        var (duration, unit) = ResolveDefaultDuration(s);
                        strategyDecision = new StrategyDecision
                        {
                            StrategyName = s.Name,
                            Signal = sig,
                            Confidence = 0.5,
                            Duration = duration,
                            DurationUnit = unit
                        };
                    }

                    if (strategyDecision.Signal != TradeSignal.None)
                        decisions.Add(strategyDecision);
                }

                var decision = AIHelpers.EnsembleVote(decisions);
                if (decision.Signal == TradeSignal.None)
                    continue;

                double stake = _riskManager.ComputeStake(balance);
                if (stake <= 0)
                    continue;

                double profit = ResolveOutcome(ticks, i, decision, stake);
                balance += profit;

                totalTrades++;
                if (profit >= 0) wins++; else losses++;

                peak = Math.Max(peak, balance);
                double drawdown = peak - balance;
                if (drawdown > maxDrawdown)
                    maxDrawdown = drawdown;
            }

            var firstTime = ticks.Min(t => t.Time);
            var lastTime = ticks.Max(t => t.Time);

            return new BacktestResult
            {
                StartingBalance = startingBalance,
                EndingBalance = balance,
                TotalTrades = totalTrades,
                Wins = wins,
                Losses = losses,
                MaxDrawdown = maxDrawdown,
                Duration = lastTime - firstTime
            };
        }

        // Resolve a paper trade outcome using duration + payout model.
        private double ResolveOutcome(
            IReadOnlyList<Tick> ticks,
            int entryIndex,
            StrategyDecision decision,
            double stake)
        {
            if (decision == null || decision.Signal == TradeSignal.None)
                return 0.0;

            int exitIndex = ResolveExitIndex(ticks, entryIndex, decision.Duration, decision.DurationUnit);
            if (exitIndex <= entryIndex)
                return 0.0;

            var entry = ticks[entryIndex];
            var exit = ticks[exitIndex];

            double diff = exit.Quote - entry.Quote;
            if (decision.Signal == TradeSignal.Sell)
                diff = -diff;

            bool win = diff > 0.0;
            if (!win && diff == 0.0)
                return 0.0;

            double payoutMultiplier = _pricingModel.GetPayoutMultiplier(
                entry.Symbol,
                decision.Duration,
                decision.DurationUnit);

            return win ? stake * payoutMultiplier : -stake;
        }

        private static (int Duration, string Unit) ResolveDefaultDuration(ITradingStrategy strategy)
        {
            if (strategy is ITradeDurationProvider provider &&
                provider.DefaultDuration > 0 &&
                !string.IsNullOrWhiteSpace(provider.DefaultDurationUnit))
            {
                return (provider.DefaultDuration, provider.DefaultDurationUnit);
            }

            return (1, "t");
        }

        private static int ResolveExitIndex(
            IReadOnlyList<Tick> ticks,
            int entryIndex,
            int duration,
            string durationUnit)
        {
            if (duration <= 0) duration = 1;
            if (string.IsNullOrWhiteSpace(durationUnit)) durationUnit = "t";

            if (durationUnit.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Min(entryIndex + duration, ticks.Count - 1);
            }

            var entryTime = ticks[entryIndex].Time;
            TimeSpan delta = durationUnit.ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(duration),
                "m" => TimeSpan.FromMinutes(duration),
                "h" => TimeSpan.FromHours(duration),
                "d" => TimeSpan.FromDays(duration),
                _ => TimeSpan.FromSeconds(duration)
            };

            var target = entryTime + delta;
            for (int i = entryIndex + 1; i < ticks.Count; i++)
            {
                if (ticks[i].Time >= target)
                    return i;
            }

            return entryIndex;
        }
    }

    public interface IContractPricingModel
    {
        double GetPayoutMultiplier(string symbol, int duration, string durationUnit);
    }

    public sealed class FixedPayoutPricingModel : IContractPricingModel
    {
        private readonly double _payoutMultiplier;

        public FixedPayoutPricingModel(double payoutMultiplier = 0.85)
        {
            _payoutMultiplier = Math.Max(0.1, Math.Min(2.0, payoutMultiplier));
        }

        public double GetPayoutMultiplier(string symbol, int duration, string durationUnit)
        {
            return _payoutMultiplier;
        }
    }
}
