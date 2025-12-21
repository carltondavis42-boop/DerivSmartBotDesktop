using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using DerivSmartBotDesktop.Core;

namespace DerivSmartBotDesktop
{
    public class StrategySummary
    {
        public string Name { get; set; }
        public double WinRate { get; set; }
        public int TotalTrades { get; set; }
        public double NetPL { get; set; }
    }

    public class SymbolStatsViewModel
    {
        public string Symbol { get; set; }
        public double Heat { get; set; }
        public string Regime { get; set; }
        public double RegimeScore { get; set; }
        public double WinRate { get; set; }
        public int TotalTrades { get; set; }
        public double NetPL { get; set; }
    }

    public class BotViewModel : INotifyPropertyChanged
    {
        private readonly SmartBotController _controller;
        private readonly DispatcherTimer _timer;

        private double _balance;
        private double _todaysPL;
        private bool _isRunning;
        private double _winRate;
        private int _todaysTrades;
        private BotProfile _selectedProfile = BotProfile.Balanced;
        private string _activeStrategyName;
        private bool _isConnected;
        private bool _relaxEnvironmentFiltersForTesting;

        private string _currentRegime;
        private string _autoPauseReasonText;
        private string _marketConditionsSummary;

        private string _statusText;
        private string _rulesSummary;
        private double _marketHeatScore;
        private string _activeSymbol;
        private string _watchlistText = string.Empty;
        private string _lastSkipReason;
        private string _accountModeText = "DEMO";
        private bool _isLiveMode;
        private bool _isForwardTestEnabled;

        public double Balance
        {
            get => _balance;
            set { _balance = value; OnPropertyChanged(); }
        }

        public double TodaysPL
        {
            get => _todaysPL;
            set { _todaysPL = value; OnPropertyChanged(); }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); }
        }

        public bool RelaxEnvironmentFiltersForTesting
        {
            get => _relaxEnvironmentFiltersForTesting;
            set
            {
                if (_relaxEnvironmentFiltersForTesting != value)
                {
                    _relaxEnvironmentFiltersForTesting = value;

                    // Push down into controller so the core logic uses it
                    if (_controller != null)
                    {
                        _controller.RelaxEnvironmentForTesting = value;
                    }

                    OnPropertyChanged(nameof(RelaxEnvironmentFiltersForTesting));
                }
            }
        }


        public double WinRate
        {
            get => _winRate;
            set { _winRate = value; OnPropertyChanged(); }
        }

        public int TodaysTrades
        {
            get => _todaysTrades;
            set { _todaysTrades = value; OnPropertyChanged(); }
        }

        public string ActiveStrategyName
        {
            get => _activeStrategyName;
            set { _activeStrategyName = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public string CurrentRegime
        {
            get => _currentRegime;
            set { _currentRegime = value; OnPropertyChanged(); }
        }

        public string AutoPauseReasonText
        {
            get => _autoPauseReasonText;
            set { _autoPauseReasonText = value; OnPropertyChanged(); }
        }

        public string MarketConditionsSummary
        {
            get => _marketConditionsSummary;
            set { _marketConditionsSummary = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public string RulesSummary
        {
            get => _rulesSummary;
            set { _rulesSummary = value; OnPropertyChanged(); }
        }

        public double MarketHeatScore
        {
            get => _marketHeatScore;
            set { _marketHeatScore = value; OnPropertyChanged(); }
        }

        public string ActiveSymbol
        {
            get => _activeSymbol;
            set { _activeSymbol = value; OnPropertyChanged(); }
        }

        public string LastSkipReason
        {
            get => _lastSkipReason;
            set { _lastSkipReason = value; OnPropertyChanged(); }
        }

        public Array Profiles => Enum.GetValues(typeof(BotProfile));

        public BotProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (_selectedProfile != value)
                {
                    _selectedProfile = value;
                    OnPropertyChanged();
                    ApplyProfile(_selectedProfile);
                }
            }
        }

        // Auto-start toggle
        public bool AutoStartEnabled
        {
            get => _controller?.AutoStartEnabled ?? false;
            set
            {
                if (_controller != null && _controller.AutoStartEnabled != value)
                {
                    _controller.AutoStartEnabled = value;
                    OnPropertyChanged();
                    AddLog("Auto-start " + (value ? "enabled." : "disabled."));
                    UpdateStatusText();
                }
            }
        }

        public bool AutoSymbolRotationEnabled
        {
            get => _controller?.AutoSymbolMode == AutoSymbolMode.Auto;
            set
            {
                if (_controller == null) return;

                var newMode = value ? AutoSymbolMode.Auto : AutoSymbolMode.Manual;
                if (_controller.AutoSymbolMode != newMode)
                {
                    _controller.SetAutoSymbolMode(newMode);
                    OnPropertyChanged();
                    AddLog("Auto symbol rotation " + (value ? "enabled." : "disabled."));
                    UpdateStatusText();
                }
            }
        }

        public string WatchlistText
        {
            get => _watchlistText;
            set
            {
                if (_watchlistText != value)
                {
                    _watchlistText = value;
                    OnPropertyChanged();
                }
            }
        }

        public void ApplyWatchlist()
        {
            if (_controller == null)
                return;

            var symbols = (WatchlistText ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct()
                .ToList();

            if (symbols.Count == 0)
                return;

            _controller.SetSymbolsToWatch(symbols);
            AddLog("Updated watchlist: " + string.Join(", ", symbols));
        }

        public ObservableCollection<StrategySummary> StrategySummaries { get; } = new();
        public ObservableCollection<SymbolStatsViewModel> SymbolStats { get; } = new();
        public ObservableCollection<TradeRecord> TradeHistory { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();
        public string CombinedLog
        {
            get
            {
                try
                {
                    // Only materialize the last 200 entries to keep the UI responsive.
                    const int maxLines = 200;
                    var snapshot = LogEntries.ToArray();
                    if (snapshot.Length > maxLines)
                    {
                        snapshot = snapshot[^maxLines..];
                    }
                    return string.Join(Environment.NewLine, snapshot);
                }
                catch
                {
                    return string.Empty;
                }
            }
        }


        internal BotViewModel(SmartBotController controller)
        {
            _controller = controller;

            _relaxEnvironmentFiltersForTesting = _controller.RelaxEnvironmentForTesting;

            _controller.BotEvent += msg =>
            {
                var dispatcher = App.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        AddLog(msg);
                    }));
                }
                else
                {
                    AddLog(msg);
                }
            };

            // Initialize watchlist UI from controller
            _watchlistText = string.Join(", ", _controller.SymbolsToWatch ?? System.Array.Empty<string>());

            ApplyProfile(_selectedProfile);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            OnPropertyChanged(nameof(AutoStartEnabled));
            OnPropertyChanged(nameof(AutoSymbolRotationEnabled));
            OnPropertyChanged(nameof(WatchlistText));
        }

        public void SetAccountMode(bool isDemo)
        {
            IsLiveMode = !isDemo;
            AccountModeText = isDemo ? "DEMO" : "LIVE";
        }

        public void SetForwardTestEnabled(bool enabled)
        {
            IsForwardTestEnabled = enabled;
        }

        public void StartBot()
        {
            _controller.Start();
            IsRunning = _controller.IsRunning;
            if (IsRunning)
                AddLog("Bot started.");
            UpdateStatusText();
        }

        public void StopBot()
        {
            _controller.Stop();
            IsRunning = false;
            AddLog("Bot stopped.");
            UpdateStatusText();
        }

        public void ClearAutoPause()
        {
            _controller.ClearAutoPause();
            IsRunning = _controller.IsRunning;
            UpdateStatusText();
        }

        public void Refresh()
        {
            if (_controller == null) return;

            Balance = _controller.Balance;
            TodaysPL = _controller.TodaysPL;
            IsRunning = _controller.IsRunning;
            IsConnected = _controller.IsConnected;
            ActiveStrategyName = _controller.ActiveStrategyName;
            ActiveSymbol = _controller.ActiveSymbol;
            MarketHeatScore = _controller.MarketHeatScore;

            var diag = _controller.CurrentDiagnostics;
            CurrentRegime = diag?.Regime.ToString() ?? "Unknown";
            AutoPauseReasonText = _controller.LastAutoPauseReason.ToString();
            LastSkipReason = _controller.LastSkipReason;

            var stats = _controller.StrategyStats.Values;
            int totalTrades = stats.Sum(s => s.TotalTrades);
            int totalWins = stats.Sum(s => s.Wins);

            TodaysTrades = totalTrades;
            WinRate = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0.0;

            var strategyRows = _controller.StrategyStats
                .OrderByDescending(x => x.Value.NetPL)
                .Select(kvp => new StrategySummary
                {
                    Name = kvp.Key,
                    WinRate = kvp.Value.WinRate,
                    TotalTrades = kvp.Value.TotalTrades,
                    NetPL = kvp.Value.NetPL
                })
                .ToList();
            SyncCollection(StrategySummaries, strategyRows);

            var symbolRows = _controller.SymbolStats
                .OrderByDescending(x => x.Value.NetPL)
                .Select(kvp => new SymbolStatsViewModel
                {
                    Symbol = kvp.Key,
                    Heat = kvp.Value.LastHeat,
                    Regime = kvp.Value.LastRegime.ToString(),
                    RegimeScore = kvp.Value.LastRegimeScore,
                    WinRate = kvp.Value.WinRate,
                    TotalTrades = kvp.Value.TotalTrades,
                    NetPL = kvp.Value.NetPL
                })
                .ToList();
            SyncCollection(SymbolStats, symbolRows);

            var tradeRows = _controller.TradeHistory
                .OrderByDescending(x => x.Time)
                .ToList();
            SyncCollection(TradeHistory, tradeRows);

            // Keep UI in sync if AutoStartEnabled changed elsewhere
            OnPropertyChanged(nameof(AutoStartEnabled));

            UpdateStatusText();
            UpdateRulesSummary();
        }

        private void UpdateMarketConditionsSummary(MarketDiagnostics diag)
        {
            if (diag == null)
            {
                MarketConditionsSummary = "Waiting for diagnostics...\nLet the bot stream data for a few seconds.";
                return;
            }

            var sb = new StringBuilder();

            var symbol = ActiveSymbol;
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = _controller?.ActiveSymbol ?? "-";

            sb.AppendLine($"Symbol: {symbol} · Regime: {diag.Regime} (score {diag.RegimeScore:F1})");
            sb.AppendLine($"Volatility: {diag.Volatility:F4} · Trend slope: {diag.TrendSlope:F5}");
            sb.AppendLine($"Market heat: {_controller.MarketHeatScore:F1}/100");

            if (!string.IsNullOrWhiteSpace(LastSkipReason))
            {
                // Try to extract the filter code inside [BRACKETS] from the last skip reason
                string friendly = LastSkipReason;
                var open = LastSkipReason.IndexOf('[');
                var close = open >= 0 ? LastSkipReason.IndexOf(']', open + 1) : -1;
                if (open >= 0 && close > open)
                {
                    var code = LastSkipReason.Substring(open + 1, close - open - 1);
                    sb.AppendLine($"Last block: {code} filter was active.");
                }
                else
                {
                    sb.AppendLine("Last block: " + LastSkipReason);
                }
            }
            else
            {
                sb.AppendLine("Last block: none recently (conditions OK).");
            }

            MarketConditionsSummary = sb.ToString();
        }

        private void ApplyProfile(BotProfile profile)
        {
            var cfg = BotProfileConfig.ForProfile(profile);
            _controller.UpdateConfigs(cfg.Risk, cfg.Rules);
            AddLog($"Profile changed to {profile}. Risk and rules updated.");
            UpdateRulesSummary();
            UpdateStatusText();
        }

        private void UpdateStatusText()
        {
            if (!IsConnected)
            {
                StatusText = "Disconnected: waiting for Deriv connection";
                return;
            }

            if (IsRunning)
            {
                StatusText = $"Trading (Profile: {SelectedProfile}, Regime: {CurrentRegime}, Symbol: {ActiveSymbol})";
                return;
            }

            if (_controller.LastAutoPauseReason != AutoPauseReason.None)
            {
                StatusText = $"Paused by risk: {_controller.LastAutoPauseReason}";
                return;
            }

            if (AutoStartEnabled)
            {
                StatusText = $"Watching market for good conditions (Regime: {CurrentRegime}, Symbol: {ActiveSymbol})";
                return;
            }

            StatusText = "Idle (bot stopped)";
        }

        private void UpdateRulesSummary()
        {
            var cfg = BotProfileConfig.ForProfile(SelectedProfile);
            var r = cfg.Risk;
            var rules = cfg.Rules;

            RulesSummary =
                $"DD: {r.MaxDailyDrawdownFraction:P0} | Profit: {r.MaxDailyProfitFraction:P0} | Max/h: {rules.MaxTradesPerHour} | Cooldown: {rules.TradeCooldown.TotalSeconds:F0}s | Losses: {r.MaxConsecutiveLosses} | WinRate: {r.MinWinRatePercentToContinue:F0}%/{r.MinTradesBeforeWinRateCheck} trades";
        }

        private static void SyncCollection<T>(ObservableCollection<T> target, IList<T> items)
        {
            if (target == null) return;

            int i = 0;
            for (; i < items.Count; i++)
            {
                if (i < target.Count)
                {
                    target[i] = items[i];
                }
                else
                {
                    target.Add(items[i]);
                }
            }

            while (target.Count > items.Count)
            {
                target.RemoveAt(target.Count - 1);
            }
        }

        public string AccountModeText
        {
            get => _accountModeText;
            private set { _accountModeText = value; OnPropertyChanged(); }
        }

        public bool IsLiveMode
        {
            get => _isLiveMode;
            private set { _isLiveMode = value; OnPropertyChanged(); }
        }

        public bool IsForwardTestEnabled
        {
            get => _isForwardTestEnabled;
            private set { _isForwardTestEnabled = value; OnPropertyChanged(); }
        }

        public void AddLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            void Append()
            {
                if (LogEntries.Count > 500)
                    LogEntries.RemoveAt(0);

                LogEntries.Add(message);
                OnPropertyChanged(nameof(CombinedLog));
            }

            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(Append));
            else
                Append();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
