using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Abstraction over the actual ML model implementation.
    /// Implement this using ML.NET, ONNX Runtime, etc.
    /// </summary>
    public interface IMLRegimeModel
    {
        (MarketRegime Regime, double Confidence) Predict(ReadOnlySpan<double> features);
    }

    /// <summary>
    /// IMarketRegimeClassifier implementation backed by an ML model,
    /// with AiMarketRegimeClassifier as a safe fallback.
    /// </summary>
    public sealed class MLMarketRegimeClassifier : IMarketRegimeClassifier
    {
        private readonly IMLRegimeModel _model;
        private readonly IMarketRegimeClassifier _fallback;

        public MLMarketRegimeClassifier(
            IMLRegimeModel model,
            IMarketRegimeClassifier? fallback = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _fallback = fallback ?? new AiMarketRegimeClassifier();
        }

        public MarketRegime Classify(
            IReadOnlyList<double> prices,
            double? volatility,
            double? trendSlope,
            out double score)
        {
            try
            {
                if (prices == null || prices.Count == 0)
                {
                    score = 0.0;
                    return MarketRegime.Unknown;
                }

                var features = BuildFeatures(prices, volatility, trendSlope);
                var (regime, confidence) = _model.Predict(features);

                score = Math.Clamp(confidence, 0.0, 1.0);
                return regime;
            }
            catch
            {
                // On any failure, gracefully fall back to existing heuristic classifier.
                return _fallback.Classify(prices, volatility, trendSlope, out score);
            }
        }

        private static double[] BuildFeatures(IReadOnlyList<double> prices, double? volatility, double? trendSlope)
        {
            const int maxHistory = 60;

            int count = Math.Min(prices.Count, maxHistory);
            int offset = prices.Count - count;

            double mean = prices.Skip(offset).Average();
            double std = Math.Sqrt(
                prices.Skip(offset)
                      .Select(p => Math.Pow(p - mean, 2))
                      .Sum() / Math.Max(1, count));

            var features = new double[count + 2];

            for (int i = 0; i < count; i++)
            {
                double p = prices[offset + i];

                if (std > 0)
                    features[i] = (p - mean) / std;
                else if (mean != 0)
                    features[i] = (p / mean) - 1.0;
                else
                    features[i] = 0.0;
            }

            features[count] = volatility ?? 0.0;
            features[count + 1] = trendSlope ?? 0.0;

            return features;
        }
    }
}
