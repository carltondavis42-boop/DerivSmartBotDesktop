using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DerivSmartBotDesktop.Settings
{
    public static class SettingsService
    {
        private sealed class SettingsStore
        {
            public string AppId { get; set; }
            public string Symbol { get; set; }
            public string WatchlistCsv { get; set; }
            public string TradeLogDirectory { get; set; }
            public double DailyDrawdownPercent { get; set; } = -1;
            public double MaxDailyLossAmount { get; set; } = -1;
            public int MaxConsecutiveLosses { get; set; } = -1;
            public int MaxTradesPerHour { get; set; } = -1;
            public int MaxOpenTrades { get; set; } = -1;
            public int TradeCooldownSeconds { get; set; } = -1;
            public int MinSamplesPerStrategy { get; set; } = 50;
            public double MinMarketHeatToTrade { get; set; } = -1;
            public double MaxMarketHeatToTrade { get; set; } = -1;
            public double MinRegimeScoreToTrade { get; set; } = -1;
            public double MinEnsembleConfidence { get; set; } = -1;
            public double ExpectedProfitBlockThreshold { get; set; } = double.NaN;
            public double ExpectedProfitWarnThreshold { get; set; } = double.NaN;
            public double MinVolatilityToTrade { get; set; } = -1;
            public double MaxVolatilityToTrade { get; set; } = -1;
            public int LossCooldownMultiplierSeconds { get; set; } = -1;
            public int MaxLossCooldownSeconds { get; set; } = -1;
            public int MinTradesBeforeMl { get; set; } = -1;
            public int StrategyProbationMinTrades { get; set; } = -1;
            public double StrategyProbationWinRate { get; set; } = -1;
            public int StrategyProbationBlockMinutes { get; set; } = -1;
            public int StrategyProbationLossBlockMinutes { get; set; } = -1;
            public double HighHeatRotationThreshold { get; set; } = -1;
            public int HighHeatRotationIntervalSeconds { get; set; } = -1;
            public double RotationScoreDelta { get; set; } = -1;
            public double RotationScoreDeltaHighHeat { get; set; } = -1;
            public double MinConfidenceForDynamicStake { get; set; } = -1;
            public double MinRegimeScoreForDynamicStake { get; set; } = -1;
            public double MinHeatForDynamicStake { get; set; } = -1;
            public bool EnableProposalEvGate { get; set; } = false;
            public double MinExpectedValue { get; set; } = -1;
            public bool IsDemo { get; set; } = true;
            public bool ForwardTestEnabled { get; set; } = false;
            public bool RelaxEnvironmentForTesting { get; set; } = false;
            public string ApiTokenProtected { get; set; }
            public string ApiToken { get; set; } // legacy
        }

        private static string GetSettingsPath()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DerivSmartBotDesktop");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return Path.Combine(folder, "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path))
                    return new AppSettings();

                var json = File.ReadAllText(path);
                var store = JsonSerializer.Deserialize<SettingsStore>(json);
                if (store == null)
                    return new AppSettings();

                var settings = new AppSettings
                {
                    AppId = store.AppId,
                    Symbol = store.Symbol,
                    WatchlistCsv = string.IsNullOrWhiteSpace(store.WatchlistCsv)
                        ? new AppSettings().WatchlistCsv
                        : store.WatchlistCsv,
                    TradeLogDirectory = string.IsNullOrWhiteSpace(store.TradeLogDirectory)
                        ? new AppSettings().TradeLogDirectory
                        : store.TradeLogDirectory,
                    DailyDrawdownPercent = store.DailyDrawdownPercent,
                    MaxDailyLossAmount = store.MaxDailyLossAmount,
                    MaxConsecutiveLosses = store.MaxConsecutiveLosses,
                    MaxTradesPerHour = store.MaxTradesPerHour,
                    MaxOpenTrades = store.MaxOpenTrades,
                    TradeCooldownSeconds = store.TradeCooldownSeconds,
                    MinSamplesPerStrategy = store.MinSamplesPerStrategy <= 0
                        ? new AppSettings().MinSamplesPerStrategy
                        : store.MinSamplesPerStrategy,
                    MinMarketHeatToTrade = store.MinMarketHeatToTrade,
                    MaxMarketHeatToTrade = store.MaxMarketHeatToTrade,
                    MinRegimeScoreToTrade = store.MinRegimeScoreToTrade,
                    MinEnsembleConfidence = store.MinEnsembleConfidence,
                    ExpectedProfitBlockThreshold = store.ExpectedProfitBlockThreshold,
                    ExpectedProfitWarnThreshold = store.ExpectedProfitWarnThreshold,
                    MinVolatilityToTrade = store.MinVolatilityToTrade,
                    MaxVolatilityToTrade = store.MaxVolatilityToTrade,
                    LossCooldownMultiplierSeconds = store.LossCooldownMultiplierSeconds,
                    MaxLossCooldownSeconds = store.MaxLossCooldownSeconds,
                    MinTradesBeforeMl = store.MinTradesBeforeMl,
                    StrategyProbationMinTrades = store.StrategyProbationMinTrades,
                    StrategyProbationWinRate = store.StrategyProbationWinRate,
                    StrategyProbationBlockMinutes = store.StrategyProbationBlockMinutes,
                    StrategyProbationLossBlockMinutes = store.StrategyProbationLossBlockMinutes,
                    HighHeatRotationThreshold = store.HighHeatRotationThreshold,
                    HighHeatRotationIntervalSeconds = store.HighHeatRotationIntervalSeconds,
                    RotationScoreDelta = store.RotationScoreDelta,
                    RotationScoreDeltaHighHeat = store.RotationScoreDeltaHighHeat,
                    MinConfidenceForDynamicStake = store.MinConfidenceForDynamicStake,
                    MinRegimeScoreForDynamicStake = store.MinRegimeScoreForDynamicStake,
                    MinHeatForDynamicStake = store.MinHeatForDynamicStake,
                    EnableProposalEvGate = store.EnableProposalEvGate,
                    MinExpectedValue = store.MinExpectedValue,
                    IsDemo = store.IsDemo,
                    ForwardTestEnabled = store.ForwardTestEnabled,
                    RelaxEnvironmentForTesting = store.RelaxEnvironmentForTesting
                };

                if (!string.IsNullOrWhiteSpace(store.ApiTokenProtected))
                {
                    settings.ApiToken = UnprotectToken(store.ApiTokenProtected);
                }
                else if (!string.IsNullOrWhiteSpace(store.ApiToken))
                {
                    settings.ApiToken = store.ApiToken;
                }

                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var path = GetSettingsPath();
                var store = new SettingsStore
                {
                    AppId = settings.AppId,
                    Symbol = settings.Symbol,
                    WatchlistCsv = settings.WatchlistCsv,
                    TradeLogDirectory = settings.TradeLogDirectory,
                    DailyDrawdownPercent = settings.DailyDrawdownPercent,
                    MaxDailyLossAmount = settings.MaxDailyLossAmount,
                    MaxConsecutiveLosses = settings.MaxConsecutiveLosses,
                    MaxTradesPerHour = settings.MaxTradesPerHour,
                    MaxOpenTrades = settings.MaxOpenTrades,
                    TradeCooldownSeconds = settings.TradeCooldownSeconds,
                    MinSamplesPerStrategy = settings.MinSamplesPerStrategy,
                    MinMarketHeatToTrade = settings.MinMarketHeatToTrade,
                    MaxMarketHeatToTrade = settings.MaxMarketHeatToTrade,
                    MinRegimeScoreToTrade = settings.MinRegimeScoreToTrade,
                    MinEnsembleConfidence = settings.MinEnsembleConfidence,
                    ExpectedProfitBlockThreshold = settings.ExpectedProfitBlockThreshold,
                    ExpectedProfitWarnThreshold = settings.ExpectedProfitWarnThreshold,
                    MinVolatilityToTrade = settings.MinVolatilityToTrade,
                    MaxVolatilityToTrade = settings.MaxVolatilityToTrade,
                    LossCooldownMultiplierSeconds = settings.LossCooldownMultiplierSeconds,
                    MaxLossCooldownSeconds = settings.MaxLossCooldownSeconds,
                    MinTradesBeforeMl = settings.MinTradesBeforeMl,
                    StrategyProbationMinTrades = settings.StrategyProbationMinTrades,
                    StrategyProbationWinRate = settings.StrategyProbationWinRate,
                    StrategyProbationBlockMinutes = settings.StrategyProbationBlockMinutes,
                    StrategyProbationLossBlockMinutes = settings.StrategyProbationLossBlockMinutes,
                    HighHeatRotationThreshold = settings.HighHeatRotationThreshold,
                    HighHeatRotationIntervalSeconds = settings.HighHeatRotationIntervalSeconds,
                    RotationScoreDelta = settings.RotationScoreDelta,
                    RotationScoreDeltaHighHeat = settings.RotationScoreDeltaHighHeat,
                    MinConfidenceForDynamicStake = settings.MinConfidenceForDynamicStake,
                    MinRegimeScoreForDynamicStake = settings.MinRegimeScoreForDynamicStake,
                    MinHeatForDynamicStake = settings.MinHeatForDynamicStake,
                    EnableProposalEvGate = settings.EnableProposalEvGate,
                    MinExpectedValue = settings.MinExpectedValue,
                    IsDemo = settings.IsDemo,
                    ForwardTestEnabled = settings.ForwardTestEnabled,
                    RelaxEnvironmentForTesting = settings.RelaxEnvironmentForTesting,
                    ApiTokenProtected = ProtectToken(settings.ApiToken)
                };

                var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignore
            }
        }

        private static string ProtectToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(token);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string UnprotectToken(string protectedToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(protectedToken))
                    return string.Empty;

                var bytes = Convert.FromBase64String(protectedToken);
                var unprotectedBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(unprotectedBytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
