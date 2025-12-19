using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Abstraction over the actual ML model implementation for market regime.
    /// Implementations typically wrap a trained model (e.g., logistic regression).
    /// </summary>
    public interface IMLRegimeModel
    {
        /// <summary>
        /// Predict a market regime from a feature vector.
        /// </summary>
        /// <param name="features">
        /// Feature vector. In this project we use:
        /// [0] = latest price
        /// [1] = volatility
        /// [2] = trend slope
        /// </param>
        /// <returns>
        /// Predicted regime and a confidence score in the range [0, 1].
        /// </returns>
        (MarketRegime Regime, double Confidence) Predict(ReadOnlySpan<double> features);
    }

    /// <summary>
    /// Market regime classifier that delegates to a trained ML model and falls
    /// back to the built-in heuristic <see cref="AiMarketRegimeClassifier"/>
    /// when the model is unavailable, uncertain, or throws.
    /// </summary>
    public sealed class MLMarketRegimeClassifier : IMarketRegimeClassifier
    {
        private readonly IMLRegimeModel _model;
        private readonly IMarketRegimeClassifier _fallback;
        private readonly double _minConfidence;

        public MLMarketRegimeClassifier(
            IMLRegimeModel model,
            IMarketRegimeClassifier? fallback = null,
            double minConfidence = 0.0)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _fallback = fallback ?? new AiMarketRegimeClassifier();
            _minConfidence = Math.Clamp(minConfidence, 0.0, 1.0);
        }

        public MarketRegime Classify(
            IReadOnlyList<double> prices,
            double? volatility,
            double? trendSlope,
            out double score)
        {
            // No data → just use the heuristic classifier.
            if (prices == null || prices.Count == 0)
            {
                return _fallback.Classify(prices ?? Array.Empty<double>(), volatility, trendSlope, out score);
            }

            try
            {
                var features = BuildFeatures(prices, volatility, trendSlope);
                var (regime, confidence) = _model.Predict(features);

                // If the model is unsure or returns Unknown, fall back.
                if (regime == MarketRegime.Unknown || confidence < _minConfidence)
                {
                    return _fallback.Classify(prices, volatility, trendSlope, out score);
                }

                score = Math.Clamp(confidence, 0.0, 1.0);
                return regime;
            }
            catch
            {
                // Any ML/runtime issue → fall back gracefully.
                return _fallback.Classify(prices, volatility, trendSlope, out score);
            }
        }

        /// <summary>
        /// Build the feature vector expected by the ML model:
        /// [0] = latest price
        /// [1] = volatility (or 0 if unknown)
        /// [2] = trend slope (or 0 if unknown)
        /// </summary>
        private static double[] BuildFeatures(
            IReadOnlyList<double> prices,
            double? volatility,
            double? trendSlope)
        {
            double lastPrice = prices[prices.Count - 1];

            return new[]
            {
                lastPrice,
                volatility ?? 0.0,
                trendSlope ?? 0.0
            };
        }
    }

    /// <summary>
    /// JSON-backed implementation of <see cref="IMLRegimeModel"/> that loads
    /// a simple multinomial logistic regression model exported by the
    /// Python training script in the ml/ folder (regime-linear-v1.json).
    /// </summary>
    public sealed class JsonRegimeModel : IMLRegimeModel
    {
        private readonly string[] _classes;
        private readonly double[][] _coef;
        private readonly double[] _intercept;

        private sealed class RegimeModelConfig
        {
            // Must match JSON produced by train_regime_model.py
            public string[]? Classes { get; set; }
            public double[][]? Coef { get; set; }
            public double[]? Intercept { get; set; }
        }

        public JsonRegimeModel(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath))
                throw new ArgumentException("JSON path must be provided.", nameof(jsonPath));

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("Regime model JSON file not found.", jsonPath);

            var json = File.ReadAllText(jsonPath);
            var config = JsonConvert.DeserializeObject<RegimeModelConfig>(json)
                         ?? throw new InvalidOperationException("Failed to deserialize regime model JSON.");

            _classes = config.Classes ?? throw new InvalidOperationException("Missing 'classes' array in regime model JSON.");
            _coef = config.Coef ?? throw new InvalidOperationException("Missing 'coef' array in regime model JSON.");
            _intercept = config.Intercept ?? throw new InvalidOperationException("Missing 'intercept' array in regime model JSON.");

            if (_coef.Length != _classes.Length || _intercept.Length != _classes.Length)
                throw new InvalidOperationException("Inconsistent lengths between classes, coef, and intercept arrays.");

            foreach (var row in _coef)
            {
                if (row.Length != 3)
                    throw new InvalidOperationException(
                        "Expected exactly 3 coefficients per class (Price, Volatility, TrendSlope).");
            }
        }

        public (MarketRegime Regime, double Confidence) Predict(ReadOnlySpan<double> features)
        {
            if (features.Length < 3)
                throw new ArgumentException("Expected at least 3 features (Price, Volatility, TrendSlope).", nameof(features));

            double f0 = features[0];
            double f1 = features[1];
            double f2 = features[2];

            int n = _classes.Length;
            if (n == 0)
                return (MarketRegime.Unknown, 0.0);

            // Compute logits and apply a numerically stable softmax.
            double[] logits = new double[n];
            double maxLogit = double.NegativeInfinity;

            for (int i = 0; i < n; i++)
            {
                var w = _coef[i];
                double z = _intercept[i] + w[0] * f0 + w[1] * f1 + w[2] * f2;
                logits[i] = z;
                if (z > maxLogit)
                    maxLogit = z;
            }

            double sumExp = 0.0;
            for (int i = 0; i < n; i++)
            {
                sumExp += Math.Exp(logits[i] - maxLogit);
            }

            int bestIndex = 0;
            double bestProb = 0.0;

            for (int i = 0; i < n; i++)
            {
                double p = Math.Exp(logits[i] - maxLogit) / sumExp;
                if (p > bestProb)
                {
                    bestProb = p;
                    bestIndex = i;
                }
            }

            var className = _classes[bestIndex];
            if (!Enum.TryParse(className, ignoreCase: true, out MarketRegime regime))
            {
                regime = MarketRegime.Unknown;
            }

            return (regime, bestProb);
        }
    }
}
