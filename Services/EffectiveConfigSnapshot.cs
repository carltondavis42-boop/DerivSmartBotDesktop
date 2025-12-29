using System;
using System.Collections.Generic;
using DerivSmartBotDesktop.Core;
using DerivSmartBotDesktop.Settings;

namespace DerivSmartBotDesktop.Services
{
    public sealed class EffectiveConfigSnapshot
    {
        public DateTime TimestampUtc { get; set; }
        public string Profile { get; set; } = string.Empty;
        public RiskSettings? Risk { get; set; }
        public BotRules? Rules { get; set; }
        public List<string> Watchlist { get; set; } = new();
        public AppSettingsSnapshot? Settings { get; set; }
    }

    public sealed class AppSettingsSnapshot
    {
        public string Symbol { get; set; } = string.Empty;
        public string WatchlistCsv { get; set; } = string.Empty;
        public string TradeLogDirectory { get; set; } = string.Empty;
        public double DailyDrawdownPercent { get; set; }
        public double MaxDailyLossAmount { get; set; }
        public int MaxConsecutiveLosses { get; set; }
        public int MaxTradesPerHour { get; set; }
        public int MaxOpenTrades { get; set; }
        public int TradeCooldownSeconds { get; set; }
        public int MinSamplesPerStrategy { get; set; }
        public double MinMarketHeatToTrade { get; set; }
        public double MaxMarketHeatToTrade { get; set; }
        public double MinRegimeScoreToTrade { get; set; }
        public double MinEnsembleConfidence { get; set; }
        public double ExpectedProfitBlockThreshold { get; set; }
        public double ExpectedProfitWarnThreshold { get; set; }
        public double MinVolatilityToTrade { get; set; }
        public double MaxVolatilityToTrade { get; set; }
        public int LossCooldownMultiplierSeconds { get; set; }
        public int MaxLossCooldownSeconds { get; set; }
        public int MinTradesBeforeMl { get; set; }
        public int StrategyProbationMinTrades { get; set; }
        public double StrategyProbationWinRate { get; set; }
        public int StrategyProbationBlockMinutes { get; set; }
        public int StrategyProbationLossBlockMinutes { get; set; }
        public double HighHeatRotationThreshold { get; set; }
        public int HighHeatRotationIntervalSeconds { get; set; }
        public double RotationScoreDelta { get; set; }
        public double RotationScoreDeltaHighHeat { get; set; }
        public double MinConfidenceForDynamicStake { get; set; }
        public double MinRegimeScoreForDynamicStake { get; set; }
        public double MinHeatForDynamicStake { get; set; }
        public bool EnableProposalEvGate { get; set; }
        public double MinExpectedValue { get; set; }
        public bool IsDemo { get; set; }
        public bool ForwardTestEnabled { get; set; }
        public bool RelaxEnvironmentForTesting { get; set; }
    }
}
