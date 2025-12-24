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
            double currentBalance,
            double modelConfidence,
            double edgeProbability,
            MarketRegime regime,
            double? regimeScore,
            double marketHeatScore,
            StrategyStats? strategyStats,
            IReadOnlyList<TradeRecord> recentTrades,
            RiskSettings settings)
        {
            if (baseStake <= 0.0)
                return 0.0;

            // If dynamic scaling is disabled, just respect min stake and return.
            if (!settings.EnableDynamicStakeScaling)
            {
                return baseStake < settings.MinStake ? settings.MinStake : baseStake;
            }

            // Only allow dynamic scaling when confidence and regime quality are strong.
            double confidenceGate = Math.Clamp(modelConfidence, 0.0, 1.0);
            double regimeGate = Math.Clamp(regimeScore ?? 0.0, 0.0, 1.0);
            if (confidenceGate < settings.MinConfidenceForDynamicStake ||
                regimeGate < settings.MinRegimeScoreForDynamicStake ||
                marketHeatScore < settings.MinHeatForDynamicStake)
            {
                return baseStake < settings.MinStake ? settings.MinStake : baseStake;
            }

            double stake = baseStake;

            // 1) Confidence-based adjustment
            modelConfidence = Math.Max(0.0, Math.Min(1.0, modelConfidence));
            edgeProbability = Math.Clamp(edgeProbability, 0.0, 1.0);

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

            // 2b) Confidence in the regime itself and market "heat" gating for auto-upsizing.
            double heat = Math.Clamp(marketHeatScore, 0.0, 100.0);
            double regimeCertainty = Math.Clamp(regimeScore ?? 0.0, 0.0, 1.0);
            double quality = (
                0.4 * modelConfidence +
                0.4 * edgeProbability +
                0.1 * regimeCertainty +
                0.1 * (1.0 - Math.Abs((heat / 100.0) - 0.6))
            );

            if (regimeCertainty >= 0.65 && heat is >= 45.0 and <= 80.0)
            {
                // Conditions are aligned and not overheated → carefully lean in.
                stake *= 1.10;
            }
            else if (regimeCertainty < 0.45 || heat > 85.0)
            {
                // Uncertain regime or overheated tape → size down to protect capital.
                stake *= 0.8;
            }

            // 2c) Quality gate scaling (smooth, capped)
            if (quality < 0.45)
            {
                stake *= 0.55;
            }
            else if (quality > 0.70)
            {
                stake *= 1.30;
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

            // 5) Enforce balance-based cap if configured.
            if (settings.MaxStakeAsBalanceFraction > 0 && currentBalance > 0)
            {
                double maxByBalance = currentBalance * settings.MaxStakeAsBalanceFraction;
                if (stake > maxByBalance)
                    stake = maxByBalance;
            }

            // 6) Respect min stake and let RiskManager clamp the rest.
            if (stake < settings.MinStake)
                stake = settings.MinStake;

            return stake;
        }
    }
}
