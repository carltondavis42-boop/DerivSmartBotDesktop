using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Simple container for feature data that can be logged or fed into ML models.
    /// </summary>
    public sealed class FeatureVector
    {
        public DateTime Time { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Regime { get; set; } = string.Empty;

        /// <summary>
        /// Aggregated "heat" of the market at the time of the trade.
        /// </summary>
        public double Heat { get; set; }

        /// <summary>
        /// Numeric feature values in a fixed order.
        /// </summary>
        public IReadOnlyList<double> Values { get; set; } = Array.Empty<double>();
    }

    /// <summary>
    /// Extracts a compact numerical representation of the current market + context.
    /// </summary>
    public interface IFeatureExtractor
    {
        FeatureVector Extract(
            StrategyContext context,
            Tick latestTick,
            MarketDiagnostics diagnostics,
            double marketHeatScore);
    }

    /// <summary>
    /// Very small, robust feature extractor. Computes a handful of descriptive
    /// statistics over the recent window plus regime-related metrics.
    /// </summary>
    public sealed class SimpleFeatureExtractor : IFeatureExtractor
    {
        public FeatureVector Extract(
            StrategyContext context,
            Tick latestTick,
            MarketDiagnostics diagnostics,
            double marketHeatScore)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (latestTick == null) throw new ArgumentNullException(nameof(latestTick));
            if (diagnostics == null) throw new ArgumentNullException(nameof(diagnostics));

            const int lookback = 120;

            var prices = context.GetLastQuotes(lookback);
            double lastPrice = latestTick.Quote;

            double mean = lastPrice;
            double std = 0.0;
            double min = lastPrice;
            double max = lastPrice;

            if (prices.Count > 0)
            {
                mean = prices.Average();

                double sumSq = 0.0;
                min = double.MaxValue;
                max = double.MinValue;

                foreach (double p in prices)
                {
                    if (p < min) min = p;
                    if (p > max) max = p;

                    double d = p - mean;
                    sumSq += d * d;
                }

                std = Math.Sqrt(sumSq / prices.Count);
            }

            double range = max - min;
            double vol = diagnostics.Volatility ?? std;
            double slope = diagnostics.TrendSlope ?? 0.0;
            double regimeScore = diagnostics.RegimeScore ?? 0.0;

            var values = new List<double>
            {
                lastPrice,      // 0: last quote
                mean,           // 1: rolling mean
                std,            // 2: rolling std
                range,          // 3: price range in window
                vol,            // 4: volatility estimate
                slope,          // 5: trend slope
                regimeScore,    // 6: regime score (0..1)
                marketHeatScore // 7: "heat" index from AI helpers
            };

            return new FeatureVector
            {
                Time = latestTick.Time,
                Symbol = latestTick.Symbol ?? string.Empty,
                Regime = diagnostics.Regime.ToString(),
                Heat = marketHeatScore,
                Values = values
            };
        }
    }
}
