using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    /// <summary>
    /// Dynamic stake sizing helper. Currently heuristic, but the single entry
    /// point means you can later plug in a trained ML model that maps
    /// (features, stats) -> stake multiplier.
    /// </summary>
    public static class DynamicRiskHelper
    {
        public static double ComputeDynamicStake(
            double baseStake,
            double modelConfidence,
            MarketRegime regime,
            StrategyStats? strategyStats,
            IReadOnlyList<TradeRecord> recentTrades,
            RiskSettings settings)
        {
            if (baseStake <= 0.0)
                return 0.0;

            double stake = baseStake;

            // 1) Confidence-based adjustment
            modelConfidence = Math.Max(0.0, Math.Min(1.0, modelConfidence));

            if (modelConfidence < 0.55)
            {
                stake *= 0.5; // low confidence → half size
            }
            else if (modelConfidence > 0.75)
            {
                stake *= 1.25; // high confidence → modest upsize
            }

            // 2) Regime-based adjustment
            switch (regime)
            {
                case MarketRegime.TrendingUp:
                case MarketRegime.TrendingDown:
                    stake *= 1.10; // trending: slightly more size
                    break;

                case MarketRegime.VolatileChoppy:
                    stake *= 0.7;  // choppy: size down
                    break;

                default:
                    // Ranging / unknown: leave as is
                    break;
            }

            // 3) Strategy-level performance
            if (strategyStats != null && strategyStats.TotalTrades >= 10)
            {
                double wr = strategyStats.WinRate; // 0..100
                if (wr < 45.0)
                {
                    stake *= 0.5; // poor performance
                }
                else if (wr > 60.0)
                {
                    stake *= 1.20; // good performance
                }
            }

            // 4) Short-term P/L feedback
            if (recentTrades != null && recentTrades.Count >= 5)
            {
                double last5Pl = recentTrades
                    .OrderByDescending(t => t.Time)
                    .Take(5)
                    .Sum(t => t.Profit);

                if (last5Pl < -10.0)
                {
                    stake *= 0.5; // recent pain → cut risk
                }
                else if (last5Pl > 10.0)
                {
                    stake *= 1.15; // recent strong run → small boost
                }
            }

            // 5) Respect min stake and let RiskManager clamp the rest.
            if (stake < settings.MinStake)
                stake = settings.MinStake;

            return stake;
        }
    }
}
