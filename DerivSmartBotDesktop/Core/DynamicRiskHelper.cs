using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    public static class DynamicRiskHelper
    {
        /// <summary>
        /// Example advanced stake sizing helper that incorporates
        /// model confidence, regime, and recent performance.
        /// </summary>
        public static double ComputeDynamicStake(
            double baseStake,
            double modelConfidence,
            MarketRegime regime,
            StrategyStats? stats,
            IReadOnlyList<TradeRecord> recentTrades,
            RiskSettings risk)
        {
            if (risk == null || baseStake <= 0)
                return 0.0;

            double confFactor = 0.5 + Math.Clamp(modelConfidence, 0.0, 1.0) * 0.5;

            double performanceFactor = 1.0;
            if (stats != null && stats.TotalTrades >= 20)
            {
                if (stats.NetPL < 0)
                    performanceFactor *= 0.7;
                if (stats.WinRate < 45.0)
                    performanceFactor *= 0.8;
            }

            double recentPl = recentTrades?.Sum(t => t.Profit) ?? 0.0;
            if (recentPl < 0)
                performanceFactor *= 0.8;

            double regimeFactor = regime switch
            {
                MarketRegime.TrendingUp or MarketRegime.TrendingDown => 1.1,
                MarketRegime.RangingLowVol => 0.9,
                MarketRegime.RangingHighVol => 0.95,
                MarketRegime.VolatileChoppy => 0.75,
                _ => 1.0
            };

            double stake = baseStake * confFactor * performanceFactor * regimeFactor;

            if (stake < risk.MinStake) stake = risk.MinStake;
            if (stake > risk.MaxStake) stake = risk.MaxStake;

            return stake;
        }
    }
}
