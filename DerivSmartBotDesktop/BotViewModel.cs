using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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

        private string _currentRegime;
        private string _autoPauseReasonText;

        private string _statusText;
        private string _rulesSummary;

        // New: symbol + market heat
        private string _activeSymbol;
        private double _marketHeatScore;

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

        // New: Active symbol (for header)
        public string ActiveSymbol
        {
            get => _activeSymbol;
            set { _activeSymbol = value; OnPropertyChanged(); }
        }

        // New: Market heat score (for header)
        public double MarketHeatScore
        {
            get => _marketHeatScore;
            set { _marketHeatScore = value; OnPropertyChanged(); }
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

        // Expose AutoStartEnabled to UI
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

        public ObservableCollection<StrategySummary> StrategySummaries { get; } = new();
        public ObservableCollection<TradeRecord> TradeHistory { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();

        public string CombinedLog => string.Join(Environment.NewLine, LogEntries);

        public BotViewModel(SmartBotController controller)
        {
            _controller = controller;

            _controller.BotEvent += msg =>
            {
                AddLog(msg);

                // Force immediate refresh on important events
                App.Current.Dispatcher.Invoke(() =>
                {
                    Refresh();
                });
            };

            ApplyProfile(_selectedProfile);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            OnPropertyChanged(nameof(AutoStartEnabled));
        }

        public void StartBot()
        {
            _controller.Start();
            IsRunning = true;
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

        public void Refresh()
        {
            if (_controller == null) return;

            Balance = _controller.Balance;
            TodaysPL = _controller.TodaysPL;
            IsRunning = _controller.IsRunning;
            IsConnected = _controller.IsConnected;
            ActiveStrategyName = _controller.ActiveStrategyName;

            // New: symbol + heat from controller
            ActiveSymbol = _controller.ActiveSymbol;
            MarketHeatScore = _controller.MarketHeatScore;

            var diag = _controller.CurrentDiagnostics;
            CurrentRegime = diag?.Regime.ToString() ?? "Unknown";
            AutoPauseReasonText = _controller.LastAutoPauseReason.ToString();

            var stats = _controller.StrategyStats.Values;
            int totalTrades = stats.Sum(s => s.TotalTrades);
            int totalWins = stats.Sum(s => s.Wins);

            TodaysTrades = totalTrades;
            WinRate = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0.0;

            StrategySummaries.Clear();
            foreach (var kvp in _controller.StrategyStats.OrderByDescending(x => x.Value.NetPL))
            {
                StrategySummaries.Add(new StrategySummary
                {
                    Name = kvp.Key,
                    WinRate = kvp.Value.WinRate,
                    TotalTrades = kvp.Value.TotalTrades,
                    NetPL = kvp.Value.NetPL
                });
            }

            TradeHistory.Clear();
            foreach (var tr in _controller.TradeHistory.OrderByDescending(x => x.Time))
                TradeHistory.Add(tr);

            // Keep UI in sync if AutoStartEnabled changed elsewhere
            OnPropertyChanged(nameof(AutoStartEnabled));

            UpdateStatusText();
            UpdateRulesSummary();
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
                StatusText = $"Trading (Profile: {SelectedProfile}, Regime: {CurrentRegime})";
                return;
            }

            if (_controller.LastAutoPauseReason != AutoPauseReason.None)
            {
                StatusText = $"Paused by risk: {_controller.LastAutoPauseReason}";
                return;
            }

            if (AutoStartEnabled)
            {
                StatusText = $"Watching market for good conditions (Regime: {CurrentRegime})";
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
                $"DD: {r.MaxDailyDrawdownFraction:P0} | Profit: {r.MaxDailyProfitFraction:P0} | Max/h: {rules.MaxTradesPerHour} | Cooldown: {rules.TradeCooldown.TotalSeconds:F0}s";
        }

        public void AddLog(string message)
        {
            if (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);

            LogEntries.Add(message);
            OnPropertyChanged(nameof(CombinedLog));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
