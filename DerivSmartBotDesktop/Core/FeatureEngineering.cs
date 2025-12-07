using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Basic feature vector used for logging and ML models.
    /// </summary>
    public sealed class FeatureVector
    {
        public DateTime Time { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public double Price { get; set; }
        public double Volatility { get; set; }
        public double TrendSlope { get; set; }
        public double Rsi { get; set; }
        public double Atr { get; set; }
        public double MarketHeat { get; set; }
        public MarketRegime Regime { get; set; }
    }

    public interface IFeatureExtractor
    {
        FeatureVector Extract(
            StrategyContext context,
            Tick latestTick,
            MarketDiagnostics diagnostics,
            double marketHeat);
    }

    /// <summary>
    /// Simple, fast feature extractor that uses data already in StrategyContext.
    /// </summary>
    public sealed class SimpleFeatureExtractor : IFeatureExtractor
    {
        public FeatureVector Extract(
            StrategyContext context,
            Tick latestTick,
            MarketDiagnostics diagnostics,
            double marketHeat)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (latestTick == null) throw new ArgumentNullException(nameof(latestTick));

            var quotesForRsi = context.GetLastQuotes(14);
            double price = latestTick.Quote;

            double vol = diagnostics?.Volatility ?? (context.GetStdDev(20) ?? 0.0);
            double slope = diagnostics?.TrendSlope ?? 0.0;

            double rsi = TryComputeRsi(quotesForRsi) ?? 50.0;
            double atr = TryComputeAtr(context, 14) ?? 0.0;

            return new FeatureVector
            {
                Time = latestTick.Time,
                Symbol = latestTick.Symbol,
                Price = price,
                Volatility = vol,
                TrendSlope = slope,
                Rsi = rsi,
                Atr = atr,
                MarketHeat = marketHeat,
                Regime = diagnostics?.Regime ?? MarketRegime.Unknown
            };
        }

        private static double? TryComputeRsi(IReadOnlyList<double> prices)
        {
            if (prices == null || prices.Count < 3)
                return null;

            double gain = 0.0;
            double loss = 0.0;

            for (int i = 1; i < prices.Count; i++)
            {
                double diff = prices[i] - prices[i - 1];
                if (diff > 0) gain += diff;
                else loss -= diff;
            }

            if (gain == 0 && loss == 0)
                return 50.0;

            if (loss == 0)
                return 100.0;

            double rs = gain / loss;
            double rsi = 100.0 - (100.0 / (1.0 + rs));
            return rsi;
        }

        private static double? TryComputeAtr(StrategyContext context, int period)
        {
            if (context.TickWindow.Count < period + 1)
                return null;

            double sumTr = 0.0;
            int start = context.TickWindow.Count - period;

            for (int i = start; i < context.TickWindow.Count; i++)
            {
                var prev = context.TickWindow[i - 1];
                var cur = context.TickWindow[i];
                double tr = Math.Abs(cur.Quote - prev.Quote);
                sumTr += tr;
            }

            return sumTr / period;
        }
    }
}
