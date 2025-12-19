using System;
using System.Collections.Generic;
using System.Linq;

namespace DerivSmartBotDesktop.Core
{
    public sealed class BacktestResult
    {
        public double StartingBalance { get; init; }
        public double EndingBalance { get; init; }
        public int TotalTrades { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public double MaxDrawdown { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Very simple offline backtester that replays ticks through the strategies.
    /// This is kept independent of DerivWebSocketClient so you can feed in
    /// tick data from CSV, JSON, etc.
    /// </summary>
    public sealed class BacktestEngine
    {
        private readonly IList<ITradingStrategy> _strategies;
        private readonly RiskManager _riskManager;
        private readonly StrategyContext _context = new();
        private readonly IMarketRegimeClassifier _regimeClassifier;

        public BacktestEngine(
            IEnumerable<ITradingStrategy> strategies,
            RiskManager riskManager,
            IMarketRegimeClassifier? regimeClassifier = null)
        {
            _strategies = strategies.ToList();
            _riskManager = riskManager;
            _regimeClassifier = regimeClassifier ?? new AiMarketRegimeClassifier();
        }

        public BacktestResult Run(IReadOnlyList<Tick> ticks, double startingBalance)
        {
            if (ticks == null || ticks.Count == 0)
                throw new ArgumentException("Ticks collection must not be empty.", nameof(ticks));

            double balance = startingBalance;
            double peak = startingBalance;
            double maxDrawdown = 0.0;
            int wins = 0, losses = 0, totalTrades = 0;

            for (int i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                _context.AddTick(tick);

                var diag = _context.AnalyzeRegime() ?? new MarketDiagnostics();
                var quotes = _context.GetLastQuotes(120);
                double regimeScore;
                var regime = _regimeClassifier.Classify(
                    quotes,
                    diag.Volatility,
                    diag.TrendSlope,
                    out regimeScore);

                diag.Regime = regime;
                diag.RegimeScore = regimeScore;

                var decisions = new List<StrategyDecision>();
                foreach (var s in _strategies)
                {
                    var sig = s.OnNewTick(tick, _context);
                    if (sig == TradeSignal.None) continue;

                    decisions.Add(new StrategyDecision
                    {
                        StrategyName = s.Name,
                        Signal = sig,
                        Confidence = 0.5
                    });
                }

                var decision = AIHelpers.EnsembleVote(decisions);
                if (decision.Signal == TradeSignal.None)
                    continue;

                double stake = _riskManager.ComputeStake(balance);
                if (stake <= 0)
                    continue;

                double profit = SimulateOutcome(ticks, i, decision.Signal, stake);
                balance += profit;

                totalTrades++;
                if (profit >= 0) wins++; else losses++;

                peak = Math.Max(peak, balance);
                double drawdown = peak - balance;
                if (drawdown > maxDrawdown)
                    maxDrawdown = drawdown;
            }

            var firstTime = ticks.Min(t => t.Time);
            var lastTime = ticks.Max(t => t.Time);

            return new BacktestResult
            {
                StartingBalance = startingBalance,
                EndingBalance = balance,
                TotalTrades = totalTrades,
                Wins = wins,
                Losses = losses,
                MaxDrawdown = maxDrawdown,
                Duration = lastTime - firstTime
            };
        }

        // Very rough placeholder outcome model – replace with Deriv contract math later.
        private static double SimulateOutcome(
            IReadOnlyList<Tick> ticks,
            int entryIndex,
            TradeSignal signal,
            double stake)
        {
            int lookAhead = Math.Min(5, ticks.Count - entryIndex - 1);
            if (lookAhead <= 0)
                return 0.0;

            var entry = ticks[entryIndex];
            var exit = ticks[entryIndex + lookAhead];

            double diff = exit.Quote - entry.Quote;
            if (signal == TradeSignal.Sell)
                diff = -diff;

            // Treat diff as % movement, capped at ±100% of stake.
            double pct = Math.Clamp(diff * 100.0, -1.0, 1.0);
            return stake * pct;
        }
    }
}
