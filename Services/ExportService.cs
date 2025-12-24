using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DerivSmartBotDesktop.ViewModels;

namespace DerivSmartBotDesktop.Services
{
    public class ExportService
    {
        public void ExportTradesCsv(IEnumerable<TradeRowViewModel> trades, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Symbol,Strategy,Direction,Stake,Profit");

            foreach (var trade in trades)
            {
                sb.Append(trade.Time.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(Escape(trade.Symbol));
                sb.Append(',');
                sb.Append(Escape(trade.Strategy));
                sb.Append(',');
                sb.Append(Escape(trade.Direction));
                sb.Append(',');
                sb.Append(trade.Stake.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(trade.Profit.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        public void ExportStrategyStatsCsv(IEnumerable<StrategyRowViewModel> strategies, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Strategy,WinRate50,WinRate200,AvgPL,Trades,Enabled,RecommendedDuration");

            foreach (var row in strategies)
            {
                sb.Append(Escape(row.Strategy));
                sb.Append(',');
                sb.Append(row.WinRate50.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.WinRate200.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.AvgPL.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.Trades.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.IsEnabled ? "true" : "false");
                sb.Append(',');
                sb.Append(Escape(row.RecommendedDuration));
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        public void ExportSymbolStatsCsv(IEnumerable<SymbolTileViewModel> symbols, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Symbol,Heat,Regime,WinRate,Trades,NetPL,Volatility,LastSignal");

            foreach (var row in symbols)
            {
                sb.Append(Escape(row.Symbol));
                sb.Append(',');
                sb.Append(row.Heat.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(Escape(row.Regime));
                sb.Append(',');
                sb.Append(row.WinRate.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.Trades.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.NetPL.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(row.Volatility.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(Escape(row.LastSignal));
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";

            return value;
        }
    }
}
