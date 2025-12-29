using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    public sealed class StrategyMarketProfile
    {
        public string Name { get; init; } = string.Empty;
        public MarketRegime[] PreferredRegimes { get; init; } = Array.Empty<MarketRegime>();
        public double MinHeat { get; init; }
        public double MaxHeat { get; init; }
        public double MinVol { get; init; }
        public double MaxVol { get; init; }
        public double MatchBonus { get; init; }
        public double MismatchPenalty { get; init; }
    }

    public static class StrategyTagRegistry
    {
        private static readonly Dictionary<string, StrategyMarketProfile> Profiles =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Register(string strategyName, StrategyMarketProfile profile)
        {
            if (string.IsNullOrWhiteSpace(strategyName) || profile == null)
                return;

            Profiles[strategyName] = profile;
        }

        public static StrategyMarketProfile? GetProfile(string? strategyName)
        {
            if (string.IsNullOrWhiteSpace(strategyName))
                return null;

            Profiles.TryGetValue(strategyName, out var profile);
            return profile;
        }

        public static void RegisterMany(IEnumerable<(string Name, StrategyMarketProfile Profile)> entries)
        {
            if (entries == null)
                return;

            foreach (var entry in entries)
            {
                Register(entry.Name, entry.Profile);
            }
        }
    }
}
