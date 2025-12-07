using System;
using System.Globalization;
using System.IO;

namespace DerivSmartBotDesktop.Core
{
    public interface ITradeDataLogger
    {
        /// <summary>
        /// Log a completed trade with the feature vector that produced it.
        /// </summary>
        void Log(
            FeatureVector features,
            StrategyDecision decision,
            double stake,
            double profit);
    }

    /// <summary>
    /// Simple CSV logger: logs one line per trade to Data/Trades/trades_YYYYMMDD.csv
    /// </summary>
    public sealed class CsvTradeDataLogger : ITradeDataLogger
    {
        private readonly string _rootDirectory;
        private readonly object _sync = new();

        public CsvTradeDataLogger(string? rootDirectory = null)
        {
            _rootDirectory = rootDirectory ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Trades");
        }

        public void Log(FeatureVector features, StrategyDecision decision, double stake, double profit)
        {
            if (features == null) throw new ArgumentNullException(nameof(features));
            if (decision == null) throw new ArgumentNullException(nameof(decision));

            lock (_sync)
            {
                Directory.CreateDirectory(_rootDirectory);

                string fileName = $"trades_{features.Time:yyyyMMdd}.csv";
                string path = Path.Combine(_rootDirectory, fileName);

                bool writeHeader = !File.Exists(path);

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);

                if (writeHeader)
                {
                    writer.WriteLine("Time,Symbol,Price,Volatility,TrendSlope,RSI,ATR,MarketHeat,Regime,Strategy,Signal,Confidence,Stake,Profit");
                }

                string line = string.Join(",",
                    features.Time.ToString("o"),
                    Escape(features.Symbol),
                    D(features.Price),
                    D(features.Volatility),
                    D(features.TrendSlope),
                    D(features.Rsi),
                    D(features.Atr),
                    D(features.MarketHeat),
                    features.Regime.ToString(),
                    Escape(decision.StrategyName ?? string.Empty),
                    decision.Signal.ToString(),
                    D(decision.Confidence),
                    D(stake),
                    D(profit));

                writer.WriteLine(line);
            }
        }

        private static string D(double value) =>
            value.ToString("G17", CultureInfo.InvariantCulture);

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains(',') || value.Contains('"'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }
    }
}
