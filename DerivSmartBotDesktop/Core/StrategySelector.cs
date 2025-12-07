using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    public interface IStrategySelector
    {
        StrategyDecision SelectBest(
            Tick tick,
            StrategyContext context,
            MarketDiagnostics diagnostics,
            IReadOnlyDictionary<string, StrategyStats> stats,
            IEnumerable<StrategyDecision> decisions);
    }

    /// <summary>
    /// Rule-based meta-strategy: chooses the "best" strategy decision
    /// using a combination of confidence, win rate, and PL.
    /// </summary>
    public sealed class RuleBasedStrategySelector : IStrategySelector
    {
        public StrategyDecision SelectBest(
            Tick tick,
            StrategyContext context,
            MarketDiagnostics diagnostics,
            IReadOnlyDictionary<string, StrategyStats> stats,
            IEnumerable<StrategyDecision> decisions)
        {
            if (decisions == null) throw new ArgumentNullException(nameof(decisions));

            var list = decisions
                .Where(d => d != null && d.Signal != TradeSignal.None)
                .ToList();

            if (list.Count == 0)
            {
                return new StrategyDecision
                {
                    StrategyName = "Selector",
                    Signal = TradeSignal.None,
                    Confidence = 0.0
                };
            }

            StrategyDecision? best = null;
            double bestScore = double.NegativeInfinity;

            foreach (var d in list)
            {
                stats.TryGetValue(d.StrategyName ?? string.Empty, out var s);

                double winRate = s?.WinRate ?? 0.0;
                double netPl = s?.NetPL ?? 0.0;

                double score =
                    d.Confidence * 0.6 +
                    (winRate / 100.0) * 0.25 +
                    Math.Tanh(netPl / 20.0) * 0.15;

                // Small penalty if trading against a strong trend
                if (diagnostics != null &&
                    diagnostics.TrendSlope is double slope &&
                    diagnostics.Regime is MarketRegime.TrendingUp or MarketRegime.TrendingDown)
                {
                    bool isUpSignal = d.Signal == TradeSignal.Buy;
                    bool trendUp = slope >= 0.0;

                    if (isUpSignal != trendUp)
                        score -= 0.1;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = d;
                }
            }

            best ??= AIHelpers.EnsembleVote(list);

            return new StrategyDecision
            {
                StrategyName = best.StrategyName,
                Signal = best.Signal,
                Confidence = Math.Clamp(bestScore, 0.0, 1.0)
            };
        }
    }
}
