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
