using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Chooses which strategy decision to execute given a set of candidates
    /// and live statistics for each strategy.
    /// </summary>
    public interface IStrategySelector
    {
        StrategyDecision SelectBest(
            Tick tick,
            StrategyContext context,
            MarketDiagnostics diagnostics,
            IReadOnlyDictionary<string, StrategyStats> strategyStats,
            IReadOnlyList<StrategyDecision> candidates);
    }

    /// <summary>
    /// Simple rule-based selector that ranks strategies using live performance
    /// statistics plus the confidence reported by the strategy.
    /// 
    /// No dependency on MaxDrawdown or any properties you do not have.
    /// </summary>
    public sealed class RuleBasedStrategySelector : IStrategySelector
    {
        public StrategyDecision SelectBest(
            Tick tick,
            StrategyContext context,
            MarketDiagnostics diagnostics,
            IReadOnlyDictionary<string, StrategyStats> strategyStats,
            IReadOnlyList<StrategyDecision> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                // Defensive fallback: no signal
                return new StrategyDecision
                {
                    StrategyName = "RuleBasedSelector",
                    Signal = TradeSignal.None,
                    Confidence = 0.0
                };
            }

            StrategyDecision? best = null;
            double bestScore = double.NegativeInfinity;

            foreach (var decision in candidates)
            {
                double score = 0.0;

                // Base score from strategy self-confidence (0..1 → 0..100).
                score += decision.Confidence * 100.0;

                // Blend in live performance stats if available.
                if (!string.IsNullOrEmpty(decision.StrategyName) &&
                    strategyStats != null &&
                    strategyStats.TryGetValue(decision.StrategyName, out var stats))
                {
                    // WinRate is already 0..100.
                    score += stats.WinRate;

                    // NetPL is in account currency; scale it down so it does not dominate.
                    score += stats.NetPL * 0.1;
                }

                // Light regime bias: in trending regimes, nudge momentum / breakout / scalping.
                if (diagnostics != null &&
                    (diagnostics.Regime == MarketRegime.TrendingUp ||
                     diagnostics.Regime == MarketRegime.TrendingDown) &&
                    !string.IsNullOrEmpty(decision.StrategyName))
                {
                    if (decision.StrategyName.Contains("Momentum", StringComparison.OrdinalIgnoreCase) ||
                        decision.StrategyName.Contains("Breakout", StringComparison.OrdinalIgnoreCase) ||
                        decision.StrategyName.Contains("Scalping", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5.0;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = decision;
                }
            }

            if (best == null)
                best = candidates[0];

            // Clamp confidence to a reasonable band; keep the original if already set.
            if (best.Confidence < 0.5) best.Confidence = 0.5;
            if (best.Confidence > 0.99) best.Confidence = 0.99;

            return best;
        }
    }

    /// <summary>
    /// Configuration for the ML strategy edge model.
    /// Each entry describes a static "edge" score per strategy, with optional
    /// weights for different diagnostics dimensions.
    /// </summary>
    public sealed class JsonStrategyEdgeModel
    {
        private readonly Dictionary<string, StrategyEdgeConfig> _byStrategyName;

        public JsonStrategyEdgeModel(string jsonPath)
        {
            if (jsonPath == null) throw new ArgumentNullException(nameof(jsonPath));

            using var stream = File.OpenRead(jsonPath);
            var model = JsonSerializer.Deserialize<StrategyEdgeModelConfig>(stream)
                        ?? new StrategyEdgeModelConfig();

            _byStrategyName = model.Strategies
                .Where(s => !string.IsNullOrWhiteSpace(s.Strategy))
                .ToDictionary(
                    s => s.Strategy,
                    s => s,
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns an "edge" score for the given strategy in the current diagnostics context.
        /// If the strategy is not present in the model, returns 0.
        /// </summary>
        public double GetEdgeScore(string strategyName, MarketDiagnostics diagnostics)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
                return 0.0;

            if (!_byStrategyName.TryGetValue(strategyName, out var cfg))
                return 0.0;

            double vol = diagnostics.Volatility ?? 0.0;
            double slope = diagnostics.TrendSlope ?? 0.0;
            double regimeScore = diagnostics.RegimeScore ?? 0.0;

            double score = cfg.Bias
                           + cfg.VolatilityWeight * vol
                           + cfg.TrendSlopeWeight * slope
                           + cfg.RegimeScoreWeight * regimeScore;

            return score;
        }

        /// <summary>
        /// Root JSON object.
        /// {
        ///   "strategies": [
        ///     { "strategy": "Scalping", "bias": 0.1, "volatilityWeight": 0.5, ... }
        ///   ]
        /// }
        /// </summary>
        public sealed class StrategyEdgeModelConfig
        {
            public List<StrategyEdgeConfig> Strategies { get; set; } = new();
        }

        /// <summary>
        /// Per-strategy configuration.
        /// </summary>
        public sealed class StrategyEdgeConfig
        {
            public string Strategy { get; set; } = string.Empty;

            public double Bias { get; set; } = 0.0;
            public double VolatilityWeight { get; set; } = 0.0;
            public double TrendSlopeWeight { get; set; } = 0.0;
            public double RegimeScoreWeight { get; set; } = 0.0;
        }
    }

    /// <summary>
    /// Strategy selector that uses a lightweight ML "edge" model on top of the
    /// rule-based selector. If anything goes wrong, it falls back to the provided
    /// selector.
    /// </summary>
    public sealed class MlStrategySelector : IStrategySelector
    {
        private readonly JsonStrategyEdgeModel _model;
        private readonly IStrategySelector _fallback;

        public MlStrategySelector(JsonStrategyEdgeModel model, IStrategySelector fallback)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public StrategyDecision SelectBest(
            Tick tick,
            StrategyContext context,
            MarketDiagnostics diagnostics,
            IReadOnlyDictionary<string, StrategyStats> strategyStats,
            IReadOnlyList<StrategyDecision> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return new StrategyDecision
                {
                    StrategyName = "MlStrategySelector",
                    Signal = TradeSignal.None,
                    Confidence = 0.0
                };
            }

            try
            {
                StrategyDecision? best = null;
                double bestScore = double.NegativeInfinity;

                foreach (var decision in candidates)
                {
                    double score = 0.0;

                    // Base confidence.
                    score += decision.Confidence * 100.0;

                    // Live stats as in RuleBasedStrategySelector.
                    if (!string.IsNullOrEmpty(decision.StrategyName) &&
                        strategyStats != null &&
                        strategyStats.TryGetValue(decision.StrategyName, out var stats))
                    {
                        score += stats.WinRate;
                        score += stats.NetPL * 0.1;
                    }

                    // ML-based edge score from JSON model.
                    if (!string.IsNullOrEmpty(decision.StrategyName))
                    {
                        score += _model.GetEdgeScore(decision.StrategyName, diagnostics);
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = decision;
                    }
                }

                if (best == null)
                    best = candidates[0];

                if (best.Confidence < 0.5) best.Confidence = 0.5;
                if (best.Confidence > 0.99) best.Confidence = 0.99;

                return best;
            }
            catch
            {
                // If anything goes wrong in the ML path, fall back to rule-based behavior.
                return _fallback.SelectBest(tick, context, diagnostics, strategyStats, candidates);
            }
        }
    }
}
