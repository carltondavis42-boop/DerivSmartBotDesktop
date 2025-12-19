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
            FeatureVector? features,
            double marketHeatScore,
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
            FeatureVector? features,
            double marketHeatScore,
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

                    // If we have a statistically meaningful sample, lean more on win rate quality.
                    if (stats.TotalTrades >= 12)
                    {
                        if (stats.WinRate < 45)
                        {
                            score -= 15.0;
                        }
                        else if (stats.WinRate > 65)
                        {
                            score += 10.0;
                        }
                    }
                }

                // Regime-aware biasing using current diagnostics + strategy name hints.
                score += StrategySelectionScoring.ComputeRegimeBias(diagnostics, decision);

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

            best.QualityScore = bestScore;

            return best;
        }
    }

    /// <summary>
    /// Edge model loader that understands modern per-strategy logistic regression
    /// JSON as well as legacy weight-based configs.
    /// </summary>
    public sealed class JsonStrategyEdgeModel
    {
        private readonly Dictionary<string, LogisticModel> _perStrategyModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly LogisticModel? _globalModel;
        private readonly Dictionary<string, StrategyEdgeConfig> _legacyWeights = new(StringComparer.OrdinalIgnoreCase);

        public JsonStrategyEdgeModel(string jsonPath)
        {
            if (jsonPath == null) throw new ArgumentNullException(nameof(jsonPath));

            string json = File.ReadAllText(jsonPath);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string modelType = root.TryGetProperty("model_type", out var mt)
                ? mt.GetString() ?? string.Empty
                : string.Empty;

            if (string.Equals(modelType, "per_strategy_logistic_regression", StringComparison.OrdinalIgnoreCase))
            {
                var featureNames = ReadFeatureNames(root);

                if (root.TryGetProperty("strategies", out var strategiesEl) && strategiesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in strategiesEl.EnumerateArray())
                    {
                        var lm = LogisticModel.FromJsonElement(s, featureNames);
                        if (!string.IsNullOrWhiteSpace(lm.Strategy))
                        {
                            _perStrategyModels[lm.Strategy] = lm;
                        }
                    }
                }

                if (root.TryGetProperty("global_model", out var globalEl))
                {
                    _globalModel = LogisticModel.FromJsonElement(globalEl, featureNames, strategyName: "global");
                }
            }
            else if (string.Equals(modelType, "binary_logistic_regression", StringComparison.OrdinalIgnoreCase))
            {
                _globalModel = LogisticModel.FromJsonElement(root, ReadFeatureNames(root), strategyName: "global");
            }
            else
            {
                // Legacy weights config
                var model = JsonSerializer.Deserialize<StrategyEdgeModelConfig>(json)
                            ?? new StrategyEdgeModelConfig();

                foreach (var s in model.Strategies.Where(s => !string.IsNullOrWhiteSpace(s.Strategy)))
                {
                    _legacyWeights[s.Strategy] = s;
                }
            }
        }

        /// <summary>
        /// Predicts edge probability P(win) for the given strategy using the most
        /// appropriate model available. Returns 0.5 when no model is present.
        /// </summary>
        public double PredictProbability(string? strategyName, FeatureVector? features, MarketDiagnostics diagnostics)
        {
            if (!string.IsNullOrWhiteSpace(strategyName) &&
                _perStrategyModels.TryGetValue(strategyName, out var model))
            {
                return model.Predict(features, diagnostics);
            }

            if (_globalModel != null)
                return _globalModel.Predict(features, diagnostics);

            if (!string.IsNullOrWhiteSpace(strategyName) &&
                _legacyWeights.TryGetValue(strategyName, out var legacy))
            {
                double vol = diagnostics.Volatility ?? 0.0;
                double slope = diagnostics.TrendSlope ?? 0.0;
                double regimeScore = diagnostics.RegimeScore ?? 0.0;

                double score = legacy.Bias
                               + legacy.VolatilityWeight * vol
                               + legacy.TrendSlopeWeight * slope
                               + legacy.RegimeScoreWeight * regimeScore;

                return Sigmoid(score);
            }

            return 0.5;
        }

        private static List<string> ReadFeatureNames(JsonElement root)
        {
            if (root.TryGetProperty("feature_names", out var fn) && fn.ValueKind == JsonValueKind.Array)
            {
                return fn.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }

            return new List<string>();
        }

        private static double Sigmoid(double x)
        {
            double ex = Math.Exp(Math.Clamp(x, -50, 50)); // guard overflow
            return ex / (1.0 + ex);
        }

        private sealed class LogisticModel
        {
            public string Strategy { get; }
            public double[] Intercept { get; }
            public double[][] Coef { get; }
            public IReadOnlyList<string> FeatureNames { get; }

            private LogisticModel(string strategy, double[] intercept, double[][] coef, IReadOnlyList<string> featureNames)
            {
                Strategy = strategy;
                Intercept = intercept;
                Coef = coef;
                FeatureNames = featureNames;
            }

            public static LogisticModel FromJsonElement(JsonElement el, IReadOnlyList<string> parentFeatureNames, string? strategyName = null)
            {
                string strategy = strategyName ?? (el.TryGetProperty("strategy", out var sEl) ? sEl.GetString() ?? string.Empty : string.Empty);

                var featureNames = parentFeatureNames;
                if (el.TryGetProperty("feature_names", out var fn) && fn.ValueKind == JsonValueKind.Array)
                {
                    featureNames = fn.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString() ?? string.Empty)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }

                double[][] coef = el.TryGetProperty("coef", out var cEl) && cEl.ValueKind == JsonValueKind.Array
                    ? cEl.Deserialize<double[][]>() ?? Array.Empty<double[]>()
                    : Array.Empty<double[]>();

                double[] intercept = el.TryGetProperty("intercept", out var iEl) && iEl.ValueKind == JsonValueKind.Array
                    ? iEl.Deserialize<double[]>() ?? Array.Empty<double>()
                    : Array.Empty<double>();

                return new LogisticModel(strategy, intercept, coef, featureNames);
            }

            public double Predict(FeatureVector? features, MarketDiagnostics diagnostics)
            {
                if (Coef.Length == 0 || Coef[0].Length == 0 || FeatureNames.Count == 0)
                    return 0.5;

                var vector = BuildFeatureVector(features, diagnostics, FeatureNames, Coef[0].Length);

                double logit = (Intercept.Length > 0 ? Intercept[0] : 0.0);
                for (int i = 0; i < Coef[0].Length; i++)
                {
                    double w = Coef[0][i];
                    double x = i < vector.Length ? vector[i] : 0.0;
                    logit += w * x;
                }

                return Math.Clamp(Sigmoid(logit), 0.0001, 0.9999);
            }

            private static double[] BuildFeatureVector(FeatureVector? features, MarketDiagnostics diagnostics, IReadOnlyList<string> featureNames, int expectedLength)
            {
                double[] values = new double[expectedLength];

                for (int i = 0; i < expectedLength; i++)
                {
                    string name = i < featureNames.Count ? featureNames[i] : string.Empty;
                    values[i] = ResolveFeatureValue(name, features, diagnostics);
                }

                return values;
            }

            private static double ResolveFeatureValue(string name, FeatureVector? features, MarketDiagnostics diagnostics)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return 0.0;

                string key = name.Trim().ToLowerInvariant();

                double ReadValueByIndex(int index)
                {
                    if (features?.Values == null || index < 0 || index >= features.Values.Count)
                        return 0.0;
                    return features.Values[index];
                }

                switch (key)
                {
                    case "price":
                        return ReadValueByIndex(0);
                    case "mean":
                        return ReadValueByIndex(1);
                    case "std":
                    case "stdev":
                    case "stddev":
                        return ReadValueByIndex(2);
                    case "range":
                        return ReadValueByIndex(3);
                    case "volatility":
                    case "vol":
                        return diagnostics.Volatility ?? ReadValueByIndex(4);
                    case "trendslope":
                    case "slope":
                        return diagnostics.TrendSlope ?? ReadValueByIndex(5);
                    case "regimescore":
                        return diagnostics.RegimeScore ?? ReadValueByIndex(6);
                    case "heat":
                    case "marketheat":
                        return features?.Heat ?? 0.0;
                    case "netresult":
                        // Entry-time features should not leak outcome; keep neutral.
                        return 0.0;
                    default:
                        return 0.0;
                }
            }
        }

        /// <summary>
        /// Root JSON object for legacy weight config.
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
        /// Per-strategy configuration (legacy weight-based edge).
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
            FeatureVector? features,
            double marketHeatScore,
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

                    // ML edge probability (Pwin) if available; default to neutral 0.5
                    double edgeProb = _model.PredictProbability(decision.StrategyName, features, diagnostics);
                    decision.EdgeProbability = edgeProb;

                    // Center edge probability around 0.5 to create a symmetric score.
                    score += (edgeProb - 0.5) * 120.0; // -60 .. +60

                    // Live stats as in RuleBasedStrategySelector.
                    if (!string.IsNullOrEmpty(decision.StrategyName) &&
                        strategyStats != null &&
                        strategyStats.TryGetValue(decision.StrategyName, out var stats))
                    {
                        score += stats.WinRate;
                        score += stats.NetPL * 0.1;

                        if (stats.TotalTrades >= 15)
                        {
                            // Reward sustained quality, penalize prolonged underperformance.
                            score += (stats.WinRate - 50.0) * 0.8;
                        }
                    }

                    // Regime/heat-aware biasing; keeps ML aligned with current tape conditions.
                    score += StrategySelectionScoring.ComputeRegimeBias(diagnostics, decision);

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
                best.QualityScore = bestScore;

                return best;
            }
            catch
            {
                // If anything goes wrong in the ML path, fall back to rule-based behavior.
                return _fallback.SelectBest(tick, context, diagnostics, strategyStats, candidates);
            }
        }
    }

    internal static class StrategySelectionScoring
    {
        /// <summary>
        /// Provides a small regime-aware bias to nudge strategy selection toward
        /// approaches that fit the current tape. This is intentionally lightweight
        /// so it can be combined with both rule-based and ML scoring.
        /// </summary>
        public static double ComputeRegimeBias(MarketDiagnostics diagnostics, StrategyDecision decision)
        {
            if (diagnostics == null || decision == null)
                return 0.0;

            double bias = 0.0;
            string name = decision.StrategyName ?? string.Empty;
            string lname = name.ToLowerInvariant();

            // Factor in how sure we are about the regime itself.
            if (diagnostics.RegimeScore.HasValue)
            {
                double certainty = Math.Clamp(diagnostics.RegimeScore.Value, 0.0, 1.0);
                bias += (certainty - 0.5) * 20.0; // maps [0,1] -> [-10,+10]

                if (certainty < 0.35)
                    bias -= 5.0; // do not lean in when the regime is fuzzy
            }

            switch (diagnostics.Regime)
            {
                case MarketRegime.TrendingUp:
                case MarketRegime.TrendingDown:
                    if (lname.Contains("trend") || lname.Contains("momentum") || lname.Contains("breakout"))
                        bias += 12.0;
                    if (lname.Contains("range") || lname.Contains("mean"))
                        bias -= 8.0;
                    break;

                case MarketRegime.RangingLowVol:
                case MarketRegime.RangingHighVol:
                    if (lname.Contains("range") || lname.Contains("pullback") || lname.Contains("mean"))
                        bias += 12.0;
                    if (lname.Contains("breakout") || lname.Contains("momentum"))
                        bias -= 6.0;
                    break;

                case MarketRegime.VolatileChoppy:
                    if (lname.Contains("scalp") || lname.Contains("scalping"))
                        bias += 8.0;
                    if (lname.Contains("trend") && diagnostics.RegimeScore.GetValueOrDefault() < 0.65)
                        bias -= 4.0;
                    break;

                default:
                    bias -= 2.0; // unknown regime: stay conservative
                    break;
            }

            return Math.Clamp(bias, -20.0, 20.0);
        }
    }
}
