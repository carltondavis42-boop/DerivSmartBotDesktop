using System;
using System.Collections.Generic;
using System.IO;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Logs trades + feature vectors for later offline analysis / ML training.
    /// </summary>
    public interface ITradeDataLogger
    {
        /// <summary>
        /// Log a completed trade with its associated feature vector and decision.
        /// </summary>
        /// <param name="features">Feature snapshot at entry/decision time.</param>
        /// <param name="decision">The strategy decision that produced the trade.</param>
        /// <param name="stake">Stake size used on the trade.</param>
        /// <param name="profit">P/L in account currency.</param>
        void Log(FeatureVector features, StrategyDecision decision, double stake, double profit);
    }

    /// <summary>
    /// CSV implementation of trade logging.
    ///
    /// Writes one CSV file per day into:
    ///   [BaseDirectory]/Data/Logs/trades-YYYY-MM-DD.csv
    ///
    /// Columns are designed to be easy to load into Python/R/Excel for ML.
    /// </summary>
    public sealed class CsvTradeDataLogger : ITradeDataLogger
    {
        private readonly string _directory;
        private readonly object _syncRoot = new();

        public CsvTradeDataLogger(string? directory = null)
        {
            // Default to fixed path if no directory is passed in
            string baseDir = string.IsNullOrWhiteSpace(directory)
                ? @"D:\DerivSmartBotDesktop-v5-master\Data\Logs"
                : directory;

            _directory = baseDir;
            Directory.CreateDirectory(_directory);
        }

        public void Log(FeatureVector features, StrategyDecision decision, double stake, double profit)
        {
            if (features == null) throw new ArgumentNullException(nameof(features));
            if (decision == null) throw new ArgumentNullException(nameof(decision));

            lock (_syncRoot)
            {
                string fileName = $"trades-{features.Time:yyyy-MM-dd}.csv";
                string fullPath = Path.Combine(_directory, fileName);

                bool writeHeader = !File.Exists(fullPath);

                using (var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                {
                    if (writeHeader)
                    {
                        writer.WriteLine(
                            "Time,Symbol,Regime,Heat," +
                            "Price,Mean,Std,Range,Volatility,Slope,RegimeScore," +
                            "Stake,Profit,NetResult," +
                            "Strategy,Signal,Confidence,EdgeProbability");
                    }

                    // FeatureVector.Values layout from SimpleFeatureExtractor:
                    // 0: lastPrice
                    // 1: mean
                    // 2: std
                    // 3: range
                    // 4: vol
                    // 5: slope
                    // 6: regimeScore
                    // 7: marketHeatScore (we also have features.Heat, which mirrors this)
                    double price = Get(features.Values, 0);
                    double mean = Get(features.Values, 1);
                    double std = Get(features.Values, 2);
                    double range = Get(features.Values, 3);
                    double vol = Get(features.Values, 4);
                    double slope = Get(features.Values, 5);
                    double regimeScore = Get(features.Values, 6);
                    double heat = features.Heat;

                    double netResult = profit; // for clarity, same as profit.

                    string signalText = decision.Signal.ToString();

                    writer.WriteLine(string.Join(",",
                        Escape(features.Time.ToString("O")),
                        Escape(features.Symbol),
                        Escape(features.Regime),
                        heat.ToString("F4"),

                        price.ToString("F5"),
                        mean.ToString("F5"),
                        std.ToString("F5"),
                        range.ToString("F5"),
                        vol.ToString("F5"),
                        slope.ToString("F6"),
                        regimeScore.ToString("F4"),

                        stake.ToString("F2"),
                        profit.ToString("F2"),
                        netResult.ToString("F2"),

                        Escape(decision.StrategyName ?? string.Empty),
                        Escape(signalText),
                        decision.Confidence.ToString("F4"),
                        (decision.EdgeProbability ?? 0.0).ToString("F4")
                    ));
                }
            }
        }

        private static double Get(IReadOnlyList<double> values, int index)
        {
            if (values == null || index < 0 || index >= values.Count)
                return 0.0;

            return values[index];
        }

        private static string Escape(string input)
        {
            if (input == null)
                return string.Empty;

            if (input.Contains(",") || input.Contains("\""))
            {
                return "\"" + input.Replace("\"", "\"\"") + "\"";
            }

            return input;
        }
    }
}
