using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DerivSmartBotDesktop.Core;
using DerivSmartBotDesktop.Deriv;
using DerivSmartBotDesktop.Settings;
using DerivSmartBotDesktop.ViewModels;
using System.Text.Json;

namespace DerivSmartBotDesktop.Services
{
    public class BotRuntimeService
    {
        private static readonly string[] DefaultSymbols =
        {
            "R_25", "R_50", "R_75", "R_100"
        };
        private readonly object _lock = new();
        private SmartBotController? _controller;
        private DerivWebSocketClient? _client;
        private AppSettings _settings = new();
        private Timer? _snapshotTimer;
        private Timer? _reconnectWatchdog;
        private readonly List<LogItemViewModel> _logs = new();
        private readonly List<AlertItemViewModel> _alerts = new();
        private readonly List<double> _equitySeries = new();
        private readonly List<double> _drawdownSeries = new();
        private double _equityPeak;
        private bool _useMockData;
        private DateTime _lastSnapshot = DateTime.MinValue;
        private RiskSettings? _riskSettings;
        private BotRules? _botRules;
        private BotProfile _currentProfile = BotProfile.HighQuality;
        private bool _autoStartEnabled = true;
        private bool _autoRotateEnabled = true;
        private bool _relaxEnvFilters;
        private AutoTrainingService? _autoTrainingService;
        private DateTime _lastModelLoadUtc = DateTime.MinValue;
        private string _autoTrainStatus = "Auto-train initializing...";
        private bool _autoTrainAvailable;
        private int _reconnectInProgress;
        private CancellationTokenSource? _reconnectCts;
        private const string DefaultTradeLogDir = @"C:\Users\Ian\DerivSmartBotDesktop\Data\Trades";
        private const int DefaultMinSamplesPerStrategy = 50;

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
            _client.ConnectionClosed += OnConnectionClosed;
            await client.ConnectAsync();
            await client.AuthorizeAsync(_settings.ApiToken);
            await client.WaitUntilAuthorizedAsync();
            await client.RequestBalanceAsync();
            _ = SubscribeSymbolsAsync(new[] { _settings.Symbol });

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var trainScript = ResolveTrainScriptPath(baseDir);
            var logDir = string.IsNullOrWhiteSpace(_settings.TradeLogDirectory)
                ? DefaultTradeLogDir
                : _settings.TradeLogDirectory.Trim();
            var mlDir = ResolveMlDir(baseDir, logDir);
            var minSamplesPerStrategy = _settings.MinSamplesPerStrategy > 0
                ? _settings.MinSamplesPerStrategy
                : DefaultMinSamplesPerStrategy;
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(mlDir);
            _autoTrainingService = new AutoTrainingService(trainScript, tradesPerTrain: 200, minSamplesPerStrategy, logDir, mlDir, LogAutoTrain);
            _autoTrainingService.TrainingCompleted += updated =>
            {
                if (updated)
                    TryReloadModelsIfUpdated();
            };
            _autoTrainingService.StatusChanged += (status, available) =>
            {
                _autoTrainStatus = status;
                _autoTrainAvailable = available;
            };

            var profileCfg = BotProfileConfig.ForProfile(_currentProfile);
            ApplyRiskOverrides(profileCfg.Risk, profileCfg.Rules);
            _riskSettings = profileCfg.Risk;
            _botRules = profileCfg.Rules;

            var riskManager = new RiskManager(profileCfg.Risk);

            var strategies = BuildStrategiesForProfile(_currentProfile);

            var featureExtractor = new SimpleFeatureExtractor();
            var tradeLogger = new CsvTradeDataLogger(logDir);

            IStrategySelector strategySelector;
            try
            {
                string edgeModelPath = Path.Combine(mlDir, "edge-linear-v1.json");
                if (File.Exists(edgeModelPath))
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
                string modelPath = Path.Combine(mlDir, "regime-linear-v1.json");
                if (File.Exists(modelPath))
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

            _lastModelLoadUtc = GetLatestModelWriteUtc();

            controller.ForwardTestEnabled = _settings.ForwardTestEnabled;
            controller.RelaxEnvironmentForTesting = _settings.RelaxEnvironmentForTesting || _relaxEnvFilters;
            controller.AutoStartEnabled = _autoStartEnabled;
            controller.SetSymbolsToWatch(new[] { _settings.Symbol });
            controller.BotEvent += OnBotEvent;
            _controller = controller;

            var watchlist = ParseSymbols(_settings.WatchlistCsv);
            if (watchlist.Count == 0)
                watchlist = DefaultSymbols.ToList();

            controller.SetSymbolsToWatch(watchlist);
            controller.SetAutoSymbolMode(_autoRotateEnabled ? AutoSymbolMode.Auto : AutoSymbolMode.Manual);
            _ = SubscribeSymbolsAsync(watchlist);

            PersistEffectiveConfig(profileCfg, watchlist);

            _snapshotTimer ??= new Timer(_ => PublishSnapshot(), null, 0, 300);
            _reconnectWatchdog ??= new Timer(_ => WatchReconnect(), null, 2000, 5000);
        }

        public void Start()
        {
            _controller?.Start();
            _ = SubscribeSymbolsAsync(_controller?.SymbolsToWatch);
        }

        public void Stop()
        {
            _controller?.Stop();
        }

        private void WatchReconnect()
        {
            if (_useMockData)
                return;

            var client = _client;
            if (client == null)
                return;

            if (client.IsConnected && client.IsAuthorized)
                return;

            if (_settings == null || !_settings.IsValid)
                return;

            if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 1) == 1)
                return;

            _ = HandleReconnectAsync("Watchdog: client disconnected or unauthorized.");
        }

        public void ClearAutoPause()
        {
            _controller?.ClearAutoPause();
        }

        private void OnConnectionClosed(string reason)
        {
            _ = HandleReconnectAsync(reason);
        }

        private async Task HandleReconnectAsync(string reason)
        {
            if (_useMockData)
                return;

            if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 1)
                return;

            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            OnBotEvent($"[Reconnect] Disconnected: {reason}");

            var delays = new[] { 2, 5, 10, 20, 30 };
            var attempt = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var delay = delays[Math.Min(attempt, delays.Length - 1)];
                    attempt++;

                    try
                    {
                        OnBotEvent($"[Reconnect] Attempt {attempt} in {delay}s...");
                        await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);

                        var client = _client;
                        if (client == null)
                            return;

                        await client.ConnectAsync().ConfigureAwait(false);
                        await client.AuthorizeAsync(_settings.ApiToken).ConfigureAwait(false);
                        await client.WaitUntilAuthorizedAsync().ConfigureAwait(false);
                        await client.RequestBalanceAsync().ConfigureAwait(false);

                        var watchlist = _controller?.SymbolsToWatch?.ToList()
                                        ?? ParseSymbols(_settings.WatchlistCsv);
                        if (watchlist.Count == 0)
                            watchlist = DefaultSymbols.ToList();

                        await SubscribeSymbolsAsync(watchlist).ConfigureAwait(false);

                        OnBotEvent("[Reconnect] Reconnected and resubscribed.");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        OnBotEvent($"[Reconnect] Failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectInProgress, 0);
            }
        }

        public void ApplyWatchlist(IEnumerable<string> symbols)
        {
            _controller?.SetSymbolsToWatch(symbols);
            _ = SubscribeSymbolsAsync(symbols);
        }

        public void ApplyProfile(BotProfile profile)
        {
            _currentProfile = profile;
            var cfg = BotProfileConfig.ForProfile(profile);
            ApplyRiskOverrides(cfg.Risk, cfg.Rules);
            _riskSettings = cfg.Risk;
            _botRules = cfg.Rules;
            _controller?.UpdateConfigs(cfg.Risk, cfg.Rules);
            _controller?.UpdateStrategies(BuildStrategiesForProfile(profile));
            PersistEffectiveConfig(cfg, _controller?.SymbolsToWatch?.ToList() ?? new List<string>());
        }

        public void SetAutoStart(bool enabled)
        {
            _autoStartEnabled = enabled;
            if (_controller != null)
                _controller.AutoStartEnabled = enabled;
        }

        public void SetAutoRotation(bool enabled)
        {
            _autoRotateEnabled = enabled;
            _controller?.SetAutoSymbolMode(enabled ? AutoSymbolMode.Auto : AutoSymbolMode.Manual);
        }

        public void SetRelaxEnvironment(bool enabled)
        {
            _relaxEnvFilters = enabled;
            if (_controller != null)
                _controller.RelaxEnvironmentForTesting = enabled;
        }

        public void SetActiveSymbol(string symbol)
        {
            _controller?.SetActiveSymbol(symbol);
            _ = SubscribeSymbolsAsync(new[] { symbol });
        }

        public void TriggerTrainingNow()
        {
            if (_autoTrainingService == null)
                return;

            if (!_autoTrainAvailable)
            {
                OnBotEvent("[AutoTrain] Not available. Check Python deps.");
                return;
            }

            _autoTrainingService.TrainNow();
        }

        private void ApplyRiskOverrides(RiskSettings risk, BotRules rules)
        {
            if (_settings.DailyDrawdownPercent > 0)
                risk.MaxDailyDrawdownPercent = _settings.DailyDrawdownPercent;
            if (_settings.MaxDailyLossAmount > 0)
                risk.MaxDailyLossAmount = _settings.MaxDailyLossAmount;
            if (_settings.MaxConsecutiveLosses > 0)
                risk.MaxConsecutiveLosses = _settings.MaxConsecutiveLosses;
            if (_settings.MaxTradesPerHour > 0)
                rules.MaxTradesPerHour = _settings.MaxTradesPerHour;
            if (_settings.MaxOpenTrades > 0)
                rules.MaxOpenTrades = _settings.MaxOpenTrades;
            if (_settings.TradeCooldownSeconds > 0)
                rules.TradeCooldown = TimeSpan.FromSeconds(_settings.TradeCooldownSeconds);
            if (_settings.MinMarketHeatToTrade >= 0)
                rules.MinMarketHeatToTrade = _settings.MinMarketHeatToTrade;
            if (_settings.MaxMarketHeatToTrade > 0)
                rules.MaxMarketHeatToTrade = _settings.MaxMarketHeatToTrade;
            if (_settings.MinRegimeScoreToTrade > 0)
                rules.MinRegimeScoreToTrade = _settings.MinRegimeScoreToTrade;
            if (_settings.MinEnsembleConfidence > 0)
                rules.MinEnsembleConfidence = _settings.MinEnsembleConfidence;
            if (_settings.ExpectedProfitBlockThreshold > -9990)
                rules.ExpectedProfitBlockThreshold = _settings.ExpectedProfitBlockThreshold;
            if (_settings.ExpectedProfitWarnThreshold > -9990)
                rules.ExpectedProfitWarnThreshold = _settings.ExpectedProfitWarnThreshold;
            if (_settings.MinVolatilityToTrade > 0)
                rules.MinVolatilityToTrade = _settings.MinVolatilityToTrade;
            if (_settings.MaxVolatilityToTrade > 0)
                rules.MaxVolatilityToTrade = _settings.MaxVolatilityToTrade;
            if (_settings.LossCooldownMultiplierSeconds > 0)
                rules.LossCooldownMultiplierSeconds = _settings.LossCooldownMultiplierSeconds;
            if (_settings.MaxLossCooldownSeconds > 0)
                rules.MaxLossCooldownSeconds = _settings.MaxLossCooldownSeconds;
            if (_settings.MinTradesBeforeMl > 0)
                rules.MinTradesBeforeMl = _settings.MinTradesBeforeMl;
            if (_settings.StrategyProbationMinTrades > 0)
                rules.StrategyProbationMinTrades = _settings.StrategyProbationMinTrades;
            if (_settings.StrategyProbationWinRate > 0)
                rules.StrategyProbationWinRate = _settings.StrategyProbationWinRate;
            if (_settings.StrategyProbationBlockMinutes > 0)
                rules.StrategyProbationBlockMinutes = _settings.StrategyProbationBlockMinutes;
            if (_settings.StrategyProbationLossBlockMinutes > 0)
                rules.StrategyProbationLossBlockMinutes = _settings.StrategyProbationLossBlockMinutes;
            if (_settings.HighHeatRotationThreshold > 0)
                rules.HighHeatRotationThreshold = _settings.HighHeatRotationThreshold;
            if (_settings.HighHeatRotationIntervalSeconds > 0)
                rules.HighHeatRotationIntervalSeconds = _settings.HighHeatRotationIntervalSeconds;
            if (_settings.RotationScoreDelta > 0)
                rules.RotationScoreDelta = _settings.RotationScoreDelta;
            if (_settings.RotationScoreDeltaHighHeat > 0)
                rules.RotationScoreDeltaHighHeat = _settings.RotationScoreDeltaHighHeat;
            if (_settings.MinConfidenceForDynamicStake > 0)
                risk.MinConfidenceForDynamicStake = _settings.MinConfidenceForDynamicStake;
            if (_settings.MinRegimeScoreForDynamicStake > 0)
                risk.MinRegimeScoreForDynamicStake = _settings.MinRegimeScoreForDynamicStake;
            if (_settings.MinHeatForDynamicStake > 0)
                risk.MinHeatForDynamicStake = _settings.MinHeatForDynamicStake;
            if (_settings.EnableProposalEvGate)
                rules.EnableProposalEvGate = true;
            if (_settings.MinExpectedValue >= 0)
                rules.MinExpectedValue = _settings.MinExpectedValue;
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

        private void LogAutoTrain(string message)
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

        private Task SubscribeSymbolsAsync(IEnumerable<string> symbols)
        {
            var client = _client;
            if (client == null || symbols == null)
                return Task.CompletedTask;

            return Task.Run(async () =>
            {
                foreach (var symbol in symbols.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    try
                    {
                        await client.SubscribeTicksAsync(symbol.Trim()).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore duplicate or transient subscribe errors.
                    }
                }
            });
        }

        private static List<ITradingStrategy> BuildStrategiesForProfile(BotProfile profile)
        {
            var trendProfile = new StrategyMarketProfile
            {
                Name = "Trend",
                PreferredRegimes = new[] { MarketRegime.TrendingUp, MarketRegime.TrendingDown },
                MinHeat = 45.0,
                MaxHeat = 90.0,
                MinVol = 0.03,
                MaxVol = 1.5,
                MatchBonus = 14.0,
                MismatchPenalty = 10.0
            };

            var rangeProfile = new StrategyMarketProfile
            {
                Name = "Range",
                PreferredRegimes = new[] { MarketRegime.RangingLowVol, MarketRegime.RangingHighVol },
                MinHeat = 35.0,
                MaxHeat = 75.0,
                MinVol = 0.01,
                MaxVol = 0.8,
                MatchBonus = 12.0,
                MismatchPenalty = 9.0
            };

            var scalpProfile = new StrategyMarketProfile
            {
                Name = "Scalp",
                PreferredRegimes = new[] { MarketRegime.VolatileChoppy },
                MinHeat = 50.0,
                MaxHeat = 95.0,
                MinVol = 0.05,
                MaxVol = 2.5,
                MatchBonus = 10.0,
                MismatchPenalty = 8.0
            };

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

            if (profile == BotProfile.HighQuality)
                strategies.Insert(0, new HtfPullbackBosStrategy());

            foreach (var strategy in strategies)
            {
                if (strategy == null) continue;
                if (strategy is HtfPullbackBosStrategy)
                    StrategyTagRegistry.Register(strategy.Name, trendProfile);
                else if (strategy is SupplyDemandPullbackStrategy)
                    StrategyTagRegistry.Register(strategy.Name, trendProfile);
                else if (strategy is MomentumStrategy)
                    StrategyTagRegistry.Register(strategy.Name, trendProfile);
                else if (strategy is BreakoutStrategy || strategy.Name.Contains("Breakout", StringComparison.OrdinalIgnoreCase))
                    StrategyTagRegistry.Register(strategy.Name, trendProfile);
                else if (strategy is RangeTradingStrategy || strategy.Name.Contains("Range", StringComparison.OrdinalIgnoreCase))
                    StrategyTagRegistry.Register(strategy.Name, rangeProfile);
                else if (strategy is ScalpingStrategy)
                    StrategyTagRegistry.Register(strategy.Name, scalpProfile);
                else
                    StrategyTagRegistry.Register(strategy.Name, trendProfile);
            }

            return strategies;
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
                MarketHeatScore = _controller?.MarketHeatScore ?? 0,
                RiskState = _controller?.LastAutoPauseReason.ToString() ?? "None",
                DailyLossLimit = _riskSettings?.MaxDailyDrawdownFraction ?? 0,
                MaxConsecutiveLosses = _riskSettings?.MaxConsecutiveLosses ?? 0,
                CooldownSeconds = _botRules?.TradeCooldown.TotalSeconds > 0 ? (int)_botRules.TradeCooldown.TotalSeconds : 0,
                StakeModel = _riskSettings?.EnableDynamicStakeScaling == true ? "Dynamic" : "Fixed",
                RelaxGatesEnabled = _settings.RelaxEnvironmentForTesting,
                LastSkipReason = _controller?.LastSkipReason ?? "-",
                ConnectionStatus = _controller?.IsConnected == true ? "Connected" : "Disconnected",
                MessageRate = 0,
                UiRefreshRate = 300,
                Latency = "-",
                LatestException = "-",
                AutoTrainStatus = _autoTrainStatus,
                AutoTrainAvailable = _autoTrainAvailable,
                LastModelUpdate = _lastModelLoadUtc == DateTime.MinValue
                    ? "-"
                    : _lastModelLoadUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            };

            var trades = _controller?.TradeHistory ?? new List<TradeRecord>();
            _autoTrainingService?.TryQueueTraining(trades.Count);
            snapshot.TradesToday = trades.Count;

            var wins = trades.Count(t => t.Profit > 0);
            snapshot.WinRateToday = trades.Count > 0 ? (double)wins / trades.Count * 100.0 : 0;

            var last200 = trades.TakeLast(200).ToList();
            var wins200 = last200.Count(t => t.Profit > 0);
            snapshot.WinRate200 = last200.Count > 0 ? (double)wins200 / last200.Count * 100.0 : 0;

            UpdateEquitySeries(snapshot.Balance);
            snapshot.EquitySeries = _equitySeries.ToList();
            snapshot.DrawdownSeries = _drawdownSeries.ToList();
            snapshot.Drawdown = _drawdownSeries.LastOrDefault();

            snapshot.Trades = trades
                .OrderByDescending(t => t.Time)
                .Take(500)
                .Select(t => new TradeRowViewModel
                {
                    Id = $"{t.Time:O}-{t.Symbol}-{t.StrategyName}-{t.Direction}-{t.Stake:0.00}-{t.Profit:0.00}",
                    Time = t.Time,
                    Symbol = t.Symbol,
                    Strategy = t.StrategyName,
                    Direction = t.Direction,
                    Stake = t.Stake,
                    Profit = t.Profit
                }).ToList();

            snapshot.Strategies = BuildStrategyRows(trades);
            snapshot.Symbols = BuildSymbolTiles();

            var watchlist = _controller?.SymbolsToWatch?.ToList() ?? new List<string>();
            if (watchlist.Count == 0)
            {
                var settingsList = ParseSymbols(_settings.WatchlistCsv);
                watchlist.AddRange(settingsList.Count > 0 ? settingsList : DefaultSymbols);
            }
            snapshot.Watchlist = watchlist;

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
                snapshot.ActiveStatus = lastTrade.Profit > 0 ? "Won" : "Lost";
            }
            else
            {
                snapshot.ActiveContractId = "-";
                snapshot.ActiveDirection = "-";
                snapshot.ActiveDuration = "-";
                snapshot.ActiveRemaining = "-";
                snapshot.ActiveStatus = "-";
            }

            snapshot.StrategyDiagnostics = _controller?.GetStrategyDiagnostics(_controller?.ActiveSymbol) ?? "-";

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
                Volatility = s.LastRegimeScore,
                Trades = s.TotalTrades,
                NetPL = s.NetPL
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

            var wins = filtered.Count(t => t.Profit > 0);
            return (double)wins / filtered.Count * 100.0;
        }

        private BotSnapshot BuildMockSnapshot()
        {
            var rand = new Random();
            var balance = 10000 + rand.Next(-200, 200);
            UpdateEquitySeries(balance);

            var settingsWatchlist = ParseSymbols(_settings.WatchlistCsv);
            if (settingsWatchlist.Count == 0)
                settingsWatchlist = DefaultSymbols.ToList();

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
                MarketHeatScore = 18.0,
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
                LastSkipReason = "Waiting for ticks",
                EquitySeries = _equitySeries.ToList(),
                DrawdownSeries = _drawdownSeries.ToList(),
                ConnectionStatus = "Disconnected",
                MessageRate = 0,
                UiRefreshRate = 300,
                Latency = "-",
                LatestException = "-",
                AutoTrainStatus = _autoTrainStatus,
                AutoTrainAvailable = _autoTrainAvailable,
                LastModelUpdate = _lastModelLoadUtc == DateTime.MinValue
                    ? "-"
                    : _lastModelLoadUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                StrategyDiagnostics = "HTF_BOS gate=DATA_WARMUP",
                Watchlist = settingsWatchlist,
                Trades = BuildMockTrades(),
                Strategies = BuildMockStrategies(),
                Symbols = BuildMockSymbols(),
                Logs = BuildMockLogs(),
                Alerts = BuildMockAlerts()
            };
        }

        private static List<string> ParseSymbols(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private DateTime GetLatestModelWriteUtc()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = string.IsNullOrWhiteSpace(_settings?.TradeLogDirectory)
                ? DefaultTradeLogDir
                : _settings.TradeLogDirectory.Trim();
            var mlDir = ResolveMlDir(baseDir, logDir);
            var edgePath = Path.Combine(mlDir, "edge-linear-v1.json");
            var regimePath = Path.Combine(mlDir, "regime-linear-v1.json");

            var latest = DateTime.MinValue;
            if (File.Exists(edgePath))
                latest = File.GetLastWriteTimeUtc(edgePath);
            if (File.Exists(regimePath))
                latest = new[] { latest, File.GetLastWriteTimeUtc(regimePath) }.Max();

            return latest;
        }

        private void PersistEffectiveConfig(BotProfileConfig profileCfg, List<string> watchlist)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logDir = Path.Combine(baseDir, "Data", "Logs");
                Directory.CreateDirectory(logDir);

                var snapshot = new EffectiveConfigSnapshot
                {
                    TimestampUtc = DateTime.UtcNow,
                    Profile = _currentProfile.ToString(),
                    Risk = profileCfg.Risk,
                    Rules = profileCfg.Rules,
                    Watchlist = watchlist ?? new List<string>(),
                    Settings = new AppSettingsSnapshot
                    {
                        Symbol = _settings.Symbol ?? string.Empty,
                        WatchlistCsv = _settings.WatchlistCsv ?? string.Empty,
                        TradeLogDirectory = _settings.TradeLogDirectory ?? string.Empty,
                        DailyDrawdownPercent = _settings.DailyDrawdownPercent,
                        MaxDailyLossAmount = _settings.MaxDailyLossAmount,
                        MaxConsecutiveLosses = _settings.MaxConsecutiveLosses,
                        MaxTradesPerHour = _settings.MaxTradesPerHour,
                        MaxOpenTrades = _settings.MaxOpenTrades,
                        TradeCooldownSeconds = _settings.TradeCooldownSeconds,
                        MinSamplesPerStrategy = _settings.MinSamplesPerStrategy,
                        MinMarketHeatToTrade = _settings.MinMarketHeatToTrade,
                        MaxMarketHeatToTrade = _settings.MaxMarketHeatToTrade,
                        MinRegimeScoreToTrade = _settings.MinRegimeScoreToTrade,
                        MinEnsembleConfidence = _settings.MinEnsembleConfidence,
                        ExpectedProfitBlockThreshold = _settings.ExpectedProfitBlockThreshold,
                        ExpectedProfitWarnThreshold = _settings.ExpectedProfitWarnThreshold,
                        MinVolatilityToTrade = _settings.MinVolatilityToTrade,
                        MaxVolatilityToTrade = _settings.MaxVolatilityToTrade,
                        LossCooldownMultiplierSeconds = _settings.LossCooldownMultiplierSeconds,
                        MaxLossCooldownSeconds = _settings.MaxLossCooldownSeconds,
                        MinTradesBeforeMl = _settings.MinTradesBeforeMl,
                        StrategyProbationMinTrades = _settings.StrategyProbationMinTrades,
                        StrategyProbationWinRate = _settings.StrategyProbationWinRate,
                        StrategyProbationBlockMinutes = _settings.StrategyProbationBlockMinutes,
                        StrategyProbationLossBlockMinutes = _settings.StrategyProbationLossBlockMinutes,
                        HighHeatRotationThreshold = _settings.HighHeatRotationThreshold,
                        HighHeatRotationIntervalSeconds = _settings.HighHeatRotationIntervalSeconds,
                        RotationScoreDelta = _settings.RotationScoreDelta,
                        RotationScoreDeltaHighHeat = _settings.RotationScoreDeltaHighHeat,
                        MinConfidenceForDynamicStake = _settings.MinConfidenceForDynamicStake,
                        MinRegimeScoreForDynamicStake = _settings.MinRegimeScoreForDynamicStake,
                        MinHeatForDynamicStake = _settings.MinHeatForDynamicStake,
                        EnableProposalEvGate = _settings.EnableProposalEvGate,
                        MinExpectedValue = _settings.MinExpectedValue,
                        IsDemo = _settings.IsDemo,
                        ForwardTestEnabled = _settings.ForwardTestEnabled,
                        RelaxEnvironmentForTesting = _settings.RelaxEnvironmentForTesting
                    }
                };

                var fileName = $"session-config-{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var path = Path.Combine(logDir, fileName);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignore config snapshot failures
            }
        }

        private void TryReloadModelsIfUpdated()
        {
            var latest = GetLatestModelWriteUtc();
            if (latest <= _lastModelLoadUtc)
                return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = string.IsNullOrWhiteSpace(_settings?.TradeLogDirectory)
                ? DefaultTradeLogDir
                : _settings.TradeLogDirectory.Trim();
            var mlDir = ResolveMlDir(baseDir, logDir);
            var edgePath = Path.Combine(mlDir, "edge-linear-v1.json");
            var regimePath = Path.Combine(mlDir, "regime-linear-v1.json");

            IStrategySelector? selector = null;
            IMarketRegimeClassifier? classifier = null;

            try
            {
                if (File.Exists(edgePath))
                {
                    var edgeModel = new JsonStrategyEdgeModel(edgePath);
                    selector = new MlStrategySelector(edgeModel, fallback: new RuleBasedStrategySelector());
                }
            }
            catch (Exception ex)
            {
                OnBotEvent($"[AutoTrain] Edge model reload failed: {ex.Message}");
            }

            try
            {
                if (File.Exists(regimePath))
                {
                    var regimeModel = new JsonRegimeModel(regimePath);
                    classifier = new MLMarketRegimeClassifier(regimeModel, fallback: new AiMarketRegimeClassifier(), minConfidence: 0.55);
                }
            }
            catch (Exception ex)
            {
                OnBotEvent($"[AutoTrain] Regime model reload failed: {ex.Message}");
            }

            if (selector != null)
                _controller?.UpdateStrategySelector(selector);
            if (classifier != null)
                _controller?.UpdateRegimeClassifier(classifier);

            _lastModelLoadUtc = latest;
            OnBotEvent("[AutoTrain] Models reloaded.");
        }

        private static string ResolveMlDir(string baseDir, string logDir)
        {
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                try
                {
                    var parent = Directory.GetParent(logDir.Trim());
                    if (parent != null)
                    {
                        var candidate = Path.Combine(parent.FullName, "ML");
                        if (Directory.Exists(candidate))
                            return candidate;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            var baseCandidate = Path.Combine(baseDir, "Data", "ML");
            if (Directory.Exists(baseCandidate))
                return baseCandidate;

            var fallback = @"C:\Users\Ian\DerivSmartBotDesktop\Data\ML";
            return fallback;
        }

        private static string ResolveTrainScriptPath(string baseDir)
        {
            var candidates = new[]
            {
                Path.Combine(baseDir, "train_models.py"),
                Path.Combine(baseDir, "..", "..", "..", "train_models.py"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "DerivSmartBotDesktop", "train_models.py")
            };

            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                    return full;
            }

            return Path.Combine(baseDir, "train_models.py");
        }

        private static List<TradeRowViewModel> BuildMockTrades()
        {
            var trades = new List<TradeRowViewModel>
            {
                new TradeRowViewModel
                {
                    Time = DateTime.Now.AddMinutes(-1),
                    Symbol = "R_25",
                    Strategy = "TrendBreakout",
                    Direction = "CALL",
                    Stake = 3.0,
                    Profit = 2.4
                },
                new TradeRowViewModel
                {
                    Time = DateTime.Now.AddMinutes(-2),
                    Symbol = "R_50",
                    Strategy = "RangeLowVol",
                    Direction = "PUT",
                    Stake = 2.0,
                    Profit = -2.0
                },
                new TradeRowViewModel
                {
                    Time = DateTime.Now.AddMinutes(-3),
                    Symbol = "R_100",
                    Strategy = "Momentum",
                    Direction = "CALL",
                    Stake = 2.5,
                    Profit = 1.9
                }
            };

            foreach (var trade in trades)
            {
                trade.Id = $"{trade.Time:O}-{trade.Symbol}-{trade.Strategy}-{trade.Direction}-{trade.Stake:0.00}-{trade.Profit:0.00}";
            }

            return trades;
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
                    Volatility = 0.38,
                    Trades = 64,
                    NetPL = 32.6
                },
                new SymbolTileViewModel
                {
                    Symbol = "R_50",
                    Heat = 0.44,
                    Regime = "Sideways",
                    WinRate = 53.7,
                    LastSignal = "Active",
                    Volatility = 0.22,
                    Trades = 48,
                    NetPL = -12.4
                },
                new SymbolTileViewModel
                {
                    Symbol = "R_25",
                    Heat = 0.31,
                    Regime = "Choppy",
                    WinRate = 47.5,
                    LastSignal = "Watch",
                    Volatility = 0.18,
                    Trades = 30,
                    NetPL = -5.2
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
