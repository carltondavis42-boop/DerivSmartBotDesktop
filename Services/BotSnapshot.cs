using System;
using System.Collections.Generic;
using DerivSmartBotDesktop.ViewModels;

namespace DerivSmartBotDesktop.Services
{
    public class BotSnapshot
    {
        public DateTime Timestamp { get; set; }
        public bool IsConnected { get; set; }
        public bool IsRunning { get; set; }
        public string ModeBadgeText { get; set; } = string.Empty;
        public double Balance { get; set; }
        public double Equity { get; set; }
        public double TodaysPL { get; set; }
        public double WinRateToday { get; set; }
        public double WinRate200 { get; set; }
        public double Drawdown { get; set; }
        public int TradesToday { get; set; }
        public string MarketRegime { get; set; } = string.Empty;
        public string ActiveSymbol { get; set; } = string.Empty;
        public string ActiveStrategy { get; set; } = string.Empty;
        public string ActiveContractId { get; set; } = string.Empty;
        public string ActiveDirection { get; set; } = string.Empty;
        public double ActiveStake { get; set; }
        public string ActiveDuration { get; set; } = string.Empty;
        public string ActiveRemaining { get; set; } = string.Empty;
        public string ActiveStatus { get; set; } = string.Empty;
        public string RiskState { get; set; } = string.Empty;
        public double DailyLossLimit { get; set; }
        public int MaxConsecutiveLosses { get; set; }
        public int CooldownSeconds { get; set; }
        public string StakeModel { get; set; } = string.Empty;
        public bool RelaxGatesEnabled { get; set; }
        public string ConnectionStatus { get; set; } = string.Empty;
        public double MessageRate { get; set; }
        public double UiRefreshRate { get; set; }
        public string LatestException { get; set; } = string.Empty;
        public string Latency { get; set; } = string.Empty;

        public List<double> EquitySeries { get; set; } = new();
        public List<double> DrawdownSeries { get; set; } = new();
        public List<TradeRowViewModel> Trades { get; set; } = new();
        public List<StrategyRowViewModel> Strategies { get; set; } = new();
        public List<SymbolTileViewModel> Symbols { get; set; } = new();
        public List<LogItemViewModel> Logs { get; set; } = new();
        public List<AlertItemViewModel> Alerts { get; set; } = new();
        public List<string> Watchlist { get; set; } = new();
    }
}
