using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DerivSmartBotDesktop.Core;
using DerivSmartBotDesktop.Deriv;
using DerivSmartBotDesktop.Settings;
using DerivSmartBotDesktop.ViewModels;

namespace DerivSmartBotDesktop.Services
{
    public class BotRuntimeService
    {
        private readonly object _lock = new();
        private SmartBotController? _controller;
        private DerivWebSocketClient? _client;
        private AppSettings _settings = new();
        private Timer? _snapshotTimer;
        private readonly List<LogItemViewModel> _logs = new();
        private readonly List<AlertItemViewModel> _alerts = new();
        private readonly List<double> _equitySeries = new();
        private readonly List<double> _drawdownSeries = new();
        private double _equityPeak;
        private bool _useMockData;
        private DateTime _lastSnapshot = DateTime.MinValue;
        private RiskSettings? _riskSettings;
        private BotRules? _botRules;

        public event Action<BotSnapshot>? SnapshotAvailable;

        public async Task InitializeAsync(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();

            if (!_settings.IsValid)
            {
                _useMockData = true;
                _snapshotTimer ??= new Timer(_ => PublishSnapshot(), null, 0, 300);
                return;
            }

            _client?.Dispose();
            var client = new DerivWebSocketClient(_settings.AppId);
            _client = client;
            await client.ConnectAsync();
            await client.AuthorizeAsync(_settings.ApiToken);
            await client.WaitUntilAuthorizedAsync();
            await client.RequestBalanceAsync();

            var profileCfg = BotProfileConfig.ForProfile(BotProfile.Balanced);
            _riskSettings = profileCfg.Risk;
            _botRules = profileCfg.Rules;

            var riskManager = new RiskManager(profileCfg.Risk);

            var scalping = new ScalpingStrategy();
            var momentum = new MomentumStrategy();
            var range = new RangeTradingStrategy();
            var breakout = new BreakoutStrategy();
            var smc = new SmartMoneyConceptStrategy();
            var pa = new AdvancedPriceActionStrategy();
            var supplyDemand = new SupplyDemandPullbackStrategy(timeframeMinutes: 30);

            var filteredRange = new VolatilityFilteredStrategy(
                range, "RangeLowVol",
                minVol: null,
                maxVol: 0.5,
                trendThreshold: 0.0);

            var filteredBreakout = new VolatilityFilteredStrategy(
                breakout, "TrendBreakout",
                minVol: 0.2,
                maxVol: null,
                trendThreshold: 0.0005);

            var strategies = new List<ITradingStrategy>
            {
                scalping,
                momentum,
                filteredBreakout,
                filteredRange,
                smc,
                pa,
                supplyDemand
            };

            var featureExtractor = new SimpleFeatureExtractor();
            var tradeLogger = new CsvTradeDataLogger();

            IStrategySelector strategySelector;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string edgeModelPath = System.IO.Path.Combine(baseDir, "Data", "ML", "edge-linear-v1.json");
                if (System.IO.File.Exists(edgeModelPath))
                {
                    var edgeModel = new JsonStrategyEdgeModel(edgeModelPath);
                    strategySelector = new MlStrategySelector(edgeModel, fallback: new RuleBasedStrategySelector());
                }
                else
                {
                    strategySelector = new RuleBasedStrategySelector();
                }
            }
            catch
            {
                strategySelector = new RuleBasedStrategySelector();
            }

            IMarketRegimeClassifier regimeClassifier;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelPath = System.IO.Path.Combine(baseDir, "Data", "ML", "regime-linear-v1.json");
                if (System.IO.File.Exists(modelPath))
                {
                    var mlModel = new JsonRegimeModel(modelPath);
                    regimeClassifier = new MLMarketRegimeClassifier(mlModel, fallback: new AiMarketRegimeClassifier(), minConfidence: 0.55);
                }
                else
                {
                    regimeClassifier = new AiMarketRegimeClassifier();
                }
            }
            catch
            {
                regimeClassifier = new AiMarketRegimeClassifier();
            }

            var controller = new SmartBotController(
                riskManager,
                strategies,
                profileCfg.Rules,
                client,
                regimeClassifier,
                featureExtractor,
                tradeLogger,
                strategySelector);

            controller.ForwardTestEnabled = _settings.ForwardTestEnabled;
            controller.RelaxEnvironmentForTesting = _settings.RelaxEnvironmentForTesting;
            controller.SetSymbolsToWatch(new[] { _settings.Symbol });
            controller.BotEvent += OnBotEvent;
            _controller = controller;

            _snapshotTimer ??= new Timer(_ => PublishSnapshot(), null, 0, 300);
        }

        public void Start()
        {
            _controller?.Start();
        }

        public void Stop()
        {
            _controller?.Stop();
        }

        public void ClearAutoPause()
        {
            _controller?.ClearAutoPause();
        }

        public void ApplyWatchlist(IEnumerable<string> symbols)
        {
            _controller?.SetSymbolsToWatch(symbols);
        }

        public void SetActiveSymbol(string symbol)
        {
            _controller?.SetActiveSymbol(symbol);
        }

        private void OnBotEvent(string message)
        {
            lock (_lock)
            {
                _logs.Add(new LogItemViewModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Time = DateTime.Now,
                    Message = message,
                    Severity = LogSeverity.Info,
                    SeverityBrush = Brushes.LightGray
                });

                if (_logs.Count > 500)
                    _logs.RemoveRange(0, _logs.Count - 500);
            }
        }

        private void PublishSnapshot()
        {
            if ((DateTime.UtcNow - _lastSnapshot).TotalMilliseconds < 250)
                return;

            _lastSnapshot = DateTime.UtcNow;

            var snapshot = _useMockData ? BuildMockSnapshot() : BuildSnapshot();
            SnapshotAvailable?.Invoke(snapshot);
        }

        private BotSnapshot BuildSnapshot()
        {
            var snapshot = new BotSnapshot
            {
                Timestamp = DateTime.Now,
                IsConnected = _controller?.IsConnected ?? false,
                IsRunning = _controller?.IsRunning ?? false,
                ModeBadgeText = _settings.IsDemo ? "DEMO" : "LIVE",
                Balance = _controller?.Balance ?? 0,
                Equity = _controller?.Balance ?? 0,
                TodaysPL = _controller?.TodaysPL ?? 0,
                ActiveSymbol = _controller?.ActiveSymbol ?? "-",
                ActiveStrategy = _controller?.ActiveStrategyName ?? "-",
                MarketRegime = _controller?.CurrentDiagnostics?.Regime.ToString() ?? "Unknown",
                RiskState = _controller?.LastAutoPauseReason.ToString() ?? "None",
                DailyLossLimit = _riskSettings?.MaxDailyDrawdownFraction ?? 0,
                MaxConsecutiveLosses = _riskSettings?.MaxConsecutiveLosses ?? 0,
                CooldownSeconds = _botRules?.TradeCooldown.TotalSeconds > 0 ? (int)_botRules.TradeCooldown.TotalSeconds : 0,
                StakeModel = _riskSettings?.EnableDynamicStakeScaling == true ? "Dynamic" : "Fixed",
                RelaxGatesEnabled = _settings.RelaxEnvironmentForTesting,
                ConnectionStatus = _controller?.IsConnected == true ? "Connected" : "Disconnected",
                MessageRate = 0,
                UiRefreshRate = 300,
                Latency = "-",
                LatestException = "-"
            };

            var trades = _controller?.TradeHistory ?? new List<TradeRecord>();
            snapshot.TradesToday = trades.Count;

            var wins = trades.Count(t => t.Profit >= 0);
            snapshot.WinRateToday = trades.Count > 0 ? (double)wins / trades.Count * 100.0 : 0;

            var last200 = trades.TakeLast(200).ToList();
            var wins200 = last200.Count(t => t.Profit >= 0);
            snapshot.WinRate200 = last200.Count > 0 ? (double)wins200 / last200.Count * 100.0 : 0;

            UpdateEquitySeries(snapshot.Balance);
            snapshot.EquitySeries = _equitySeries.ToList();
            snapshot.DrawdownSeries = _drawdownSeries.ToList();
            snapshot.Drawdown = _drawdownSeries.LastOrDefault();

            snapshot.Trades = trades.Select(t => new TradeRowViewModel
            {
                Id = $"{t.Time:O}-{t.Symbol}-{t.StrategyName}",
                Time = t.Time,
                Symbol = t.Symbol,
                Strategy = t.StrategyName,
                Direction = t.Direction,
                Stake = t.Stake,
                Profit = t.Profit
            }).ToList();

            snapshot.Strategies = BuildStrategyRows(trades);
            snapshot.Symbols = BuildSymbolTiles();
            snapshot.Watchlist = _controller?.SymbolsToWatch?.ToList() ?? new List<string>();

            lock (_lock)
            {
                UpdateAlerts(snapshot);
                snapshot.Logs = _logs.ToList();
                snapshot.Alerts = _alerts.ToList();
            }

            var lastTrade = trades.LastOrDefault();
            if (lastTrade != null)
            {
                snapshot.ActiveContractId = lastTrade.Time.ToString("HHmmss");
                snapshot.ActiveDirection = lastTrade.Direction;
                snapshot.ActiveStake = lastTrade.Stake;
                snapshot.ActiveDuration = "1t";
                snapshot.ActiveRemaining = "-";
                snapshot.ActiveStatus = lastTrade.Profit >= 0 ? "Won" : "Lost";
            }
            else
            {
                snapshot.ActiveContractId = "-";
                snapshot.ActiveDirection = "-";
                snapshot.ActiveDuration = "-";
                snapshot.ActiveRemaining = "-";
                snapshot.ActiveStatus = "-";
            }

            return snapshot;
        }

        private void UpdateAlerts(BotSnapshot snapshot)
        {
            if (!snapshot.IsConnected)
            {
                AddAlert("disconnect", "Disconnected", "Websocket connection lost.", "Connection");
            }

            if (!string.Equals(snapshot.RiskState, "None", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(snapshot.RiskState, "Nominal", StringComparison.OrdinalIgnoreCase))
            {
                AddAlert($"risk-{snapshot.RiskState}", "Risk Gate Triggered", snapshot.RiskState, "Risk");
            }
        }

        private void AddAlert(string id, string title, string description, string category)
        {
            if (_alerts.Any(a => a.Id == id))
                return;

            _alerts.Insert(0, new AlertItemViewModel
            {
                Id = id,
                Time = DateTime.Now,
                Title = title,
                Description = description,
                Category = category
            });

            if (_alerts.Count > 200)
                _alerts.RemoveAt(_alerts.Count - 1);
        }

        private List<StrategyRowViewModel> BuildStrategyRows(IReadOnlyList<TradeRecord> trades)
        {
            var stats = _controller?.StrategyStats ?? new Dictionary<string, StrategyStats>();
            var last200 = trades.TakeLast(200).ToList();
            var last50 = trades.TakeLast(50).ToList();

            var rows = new List<StrategyRowViewModel>();
            foreach (var kvp in stats)
            {
                var name = kvp.Key;
                var total = kvp.Value.Wins + kvp.Value.Losses;
                var winRate = total > 0 ? (double)kvp.Value.Wins / total * 100.0 : 0;

                var win50 = ComputeWinRate(last50, name);
                var win200 = ComputeWinRate(last200, name);

                rows.Add(new StrategyRowViewModel
                {
                    Strategy = name,
                    WinRate50 = win50,
                    WinRate200 = win200,
                    AvgPL = total > 0 ? kvp.Value.NetPL / total : 0,
                    Trades = total,
                    IsEnabled = true,
                    RecommendedDuration = "1t"
                });
            }

            return rows.OrderByDescending(r => r.WinRate50).ToList();
        }

        private List<SymbolTileViewModel> BuildSymbolTiles()
        {
            var stats = _controller?.SymbolStats ?? new Dictionary<string, SymbolPerformanceStats>();
            return stats.Values.Select(s => new SymbolTileViewModel
            {
                Symbol = s.Symbol,
                Heat = s.LastHeat,
                Regime = s.LastRegime.ToString(),
                WinRate = s.WinRate,
                LastSignal = s.IsDisabledForSession ? "Disabled" : "Active",
                Volatility = s.LastRegimeScore
            }).ToList();
        }

        private void UpdateEquitySeries(double equity)
        {
            if (_equitySeries.Count == 0)
                _equityPeak = equity;

            _equitySeries.Add(equity);
            if (_equitySeries.Count > 200)
                _equitySeries.RemoveAt(0);

            if (equity > _equityPeak)
                _equityPeak = equity;

            var drawdown = _equityPeak > 0 ? (equity - _equityPeak) / _equityPeak * 100.0 : 0;
            _drawdownSeries.Add(drawdown);
            if (_drawdownSeries.Count > 200)
                _drawdownSeries.RemoveAt(0);
        }

        private static double ComputeWinRate(IEnumerable<TradeRecord> trades, string strategyName)
        {
            var filtered = trades.Where(t => string.Equals(t.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count == 0)
                return 0;

            var wins = filtered.Count(t => t.Profit >= 0);
            return (double)wins / filtered.Count * 100.0;
        }

        private BotSnapshot BuildMockSnapshot()
        {
            var rand = new Random();
            var balance = 10000 + rand.Next(-200, 200);
            UpdateEquitySeries(balance);

            return new BotSnapshot
            {
                Timestamp = DateTime.Now,
                IsConnected = false,
                IsRunning = false,
                ModeBadgeText = "DEMO",
                Balance = balance,
                Equity = balance,
                TodaysPL = rand.Next(-50, 80),
                WinRateToday = rand.Next(40, 70),
                WinRate200 = rand.Next(45, 65),
                Drawdown = _drawdownSeries.LastOrDefault(),
                TradesToday = rand.Next(5, 30),
                MarketRegime = "Sideways",
                ActiveSymbol = "R_100",
                ActiveStrategy = "Momentum",
                ActiveContractId = "-",
                ActiveDirection = "CALL",
                ActiveStake = 2.5,
                ActiveDuration = "1t",
                ActiveRemaining = "00:30",
                ActiveStatus = "Idle",
                RiskState = "Nominal",
                DailyLossLimit = 0.05,
                MaxConsecutiveLosses = 3,
                CooldownSeconds = 12,
                StakeModel = "Fixed",
                RelaxGatesEnabled = true,
                EquitySeries = _equitySeries.ToList(),
                DrawdownSeries = _drawdownSeries.ToList(),
                ConnectionStatus = "Disconnected",
                MessageRate = 0,
                UiRefreshRate = 300,
                Latency = "-",
                LatestException = "-",
                Watchlist = new List<string> { "R_100", "R_50", "R_25", "R_10" },
                Trades = BuildMockTrades(),
                Strategies = BuildMockStrategies(),
                Symbols = BuildMockSymbols(),
                Logs = BuildMockLogs(),
                Alerts = BuildMockAlerts()
            };
        }

        private static List<TradeRowViewModel> BuildMockTrades()
        {
            return new List<TradeRowViewModel>
            {
                new TradeRowViewModel
                {
                    Id = "mock-1",
                    Time = DateTime.Now.AddMinutes(-3),
                    Symbol = "R_100",
                    Strategy = "Momentum",
                    Direction = "CALL",
                    Stake = 2.5,
                    Profit = 1.9
                },
                new TradeRowViewModel
                {
                    Id = "mock-2",
                    Time = DateTime.Now.AddMinutes(-2),
                    Symbol = "R_50",
                    Strategy = "RangeLowVol",
                    Direction = "PUT",
                    Stake = 2.0,
                    Profit = -2.0
                },
                new TradeRowViewModel
                {
                    Id = "mock-3",
                    Time = DateTime.Now.AddMinutes(-1),
                    Symbol = "R_25",
                    Strategy = "TrendBreakout",
                    Direction = "CALL",
                    Stake = 3.0,
                    Profit = 2.4
                }
            };
        }

        private static List<StrategyRowViewModel> BuildMockStrategies()
        {
            return new List<StrategyRowViewModel>
            {
                new StrategyRowViewModel
                {
                    Strategy = "Momentum",
                    WinRate50 = 62.5,
                    WinRate200 = 58.2,
                    AvgPL = 0.85,
                    Trades = 120,
                    IsEnabled = true,
                    RecommendedDuration = "1t"
                },
                new StrategyRowViewModel
                {
                    Strategy = "RangeLowVol",
                    WinRate50 = 54.1,
                    WinRate200 = 51.9,
                    AvgPL = 0.32,
                    Trades = 96,
                    IsEnabled = true,
                    RecommendedDuration = "2t"
                },
                new StrategyRowViewModel
                {
                    Strategy = "TrendBreakout",
                    WinRate50 = 48.0,
                    WinRate200 = 52.3,
                    AvgPL = -0.12,
                    Trades = 84,
                    IsEnabled = false,
                    RecommendedDuration = "1t"
                }
            };
        }

        private static List<SymbolTileViewModel> BuildMockSymbols()
        {
            return new List<SymbolTileViewModel>
            {
                new SymbolTileViewModel
                {
                    Symbol = "R_100",
                    Heat = 0.72,
                    Regime = "Trending",
                    WinRate = 61.2,
                    LastSignal = "Active",
                    Volatility = 0.38
                },
                new SymbolTileViewModel
                {
                    Symbol = "R_50",
                    Heat = 0.44,
                    Regime = "Sideways",
                    WinRate = 53.7,
                    LastSignal = "Active",
                    Volatility = 0.22
                },
                new SymbolTileViewModel
                {
                    Symbol = "R_25",
                    Heat = 0.31,
                    Regime = "Choppy",
                    WinRate = 47.5,
                    LastSignal = "Watch",
                    Volatility = 0.18
                }
            };
        }

        private static List<LogItemViewModel> BuildMockLogs()
        {
            return new List<LogItemViewModel>
            {
                new LogItemViewModel
                {
                    Id = "log-1",
                    Time = DateTime.Now.AddMinutes(-2),
                    Message = "Mock mode: waiting for connection.",
                    Severity = LogSeverity.Warning,
                    SeverityBrush = Brushes.Goldenrod
                },
                new LogItemViewModel
                {
                    Id = "log-2",
                    Time = DateTime.Now.AddMinutes(-1),
                    Message = "Snapshot feed running (mock).",
                    Severity = LogSeverity.Info,
                    SeverityBrush = Brushes.LightGray
                }
            };
        }

        private static List<AlertItemViewModel> BuildMockAlerts()
        {
            return new List<AlertItemViewModel>
            {
                new AlertItemViewModel
                {
                    Id = "alert-mock",
                    Time = DateTime.Now,
                    Title = "Mock Mode",
                    Description = "No live connection. Using demo data.",
                    Category = "Info"
                }
            };
        }
    }
}
