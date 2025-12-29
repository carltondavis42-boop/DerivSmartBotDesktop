using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using DerivSmartBotDesktop.Core;
using DerivSmartBotDesktop.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace DerivSmartBotDesktop.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly BotRuntimeService _runtimeService;
        private readonly ThemeService _themeService;
        private readonly ToastService _toastService;
        private readonly ExportService _exportService;
        private readonly DispatcherTimer _uiTimer;
        private readonly LineSeries _equitySeries;
        private readonly LineSeries _drawdownSeries;
        private BotSnapshot? _latestSnapshot;

        private bool _isConnected;
        private bool _isRunning;
        private string _modeBadgeText = string.Empty;
        private bool _isLiveMode;
        private Brush _modeBadgeBrush = Brushes.Transparent;
        private Brush _connectionBrush = Brushes.Transparent;
        private string _activeSymbol = string.Empty;
        private string _activeStrategy = string.Empty;
        private string _activeContractId = string.Empty;
        private string _activeDirection = string.Empty;
        private double _activeStake;
        private string _activeDuration = string.Empty;
        private string _activeRemaining = string.Empty;
        private string _activeStatus = string.Empty;
        private string _riskState = string.Empty;
        private string _marketRegime = string.Empty;
        private double _marketHeatScore;
        private double _dailyLossLimit;
        private int _maxConsecutiveLosses;
        private int _cooldownSeconds;
        private string _stakeModel = string.Empty;
        private bool _relaxGatesEnabled;
        private string _lastSkipReason = string.Empty;
        private bool _autoStartEnabled = true;
        private bool _autoRotateEnabled = true;
        private bool _relaxEnvFiltersEnabled;
        private BotProfile _selectedProfile = BotProfile.HighQuality;

        public MainViewModel(BotRuntimeService runtimeService, ThemeService themeService, ToastService toastService, ExportService exportService)
        {
            _runtimeService = runtimeService;
            _themeService = themeService;
            _toastService = toastService;
            _exportService = exportService;

            Trades = new TradesViewModel(exportService);
            Logs = new LogsViewModel();
            Alerts = new AlertsViewModel();
            Diagnostics = new DiagnosticsViewModel();

            Kpis = new ObservableCollection<KpiItemViewModel>();
            StrategyRows = new ObservableCollection<StrategyRowViewModel>();
            SymbolTiles = new ObservableCollection<SymbolTileViewModel>();
            WatchlistSymbols = new ObservableCollection<string>();

            StartCommand = new RelayCommand(StartBot);
            StopCommand = new RelayCommand(StopBot);
            OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());
            ExportTradesCommand = Trades.ExportCommand;
            PinSymbolCommand = new RelayCommand<SymbolTileViewModel>(PinSymbol);
            EmergencyStopCommand = new RelayCommand(EmergencyStop);
            OpenLogsWindowCommand = new RelayCommand(() => RequestOpenLogs?.Invoke());
            ClearAutoPauseCommand = new RelayCommand(ClearAutoPause);
            TrainNowCommand = new RelayCommand(TrainNow);
            ExportStrategiesCommand = new RelayCommand(ExportStrategies);
            ExportSymbolsCommand = new RelayCommand(ExportSymbols);

            EquityPlotModel = CreatePlotModel(OxyColor.Parse("#4C8DFF"), out _equitySeries);
            DrawdownPlotModel = CreatePlotModel(OxyColor.Parse("#FF6477"), out _drawdownSeries);

            _runtimeService.SnapshotAvailable += snapshot => _latestSnapshot = snapshot;

            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _uiTimer.Tick += (_, _) => ApplySnapshot();
            _uiTimer.Start();
        }

        public event Action? RequestOpenSettings;
        public event Action? RequestOpenLogs;

        public ObservableCollection<KpiItemViewModel> Kpis { get; }
        public ObservableCollection<StrategyRowViewModel> StrategyRows { get; }
        public ObservableCollection<SymbolTileViewModel> SymbolTiles { get; }
        public ObservableCollection<string> WatchlistSymbols { get; }

        public TradesViewModel Trades { get; }
        public LogsViewModel Logs { get; }
        public AlertsViewModel Alerts { get; }
        public DiagnosticsViewModel Diagnostics { get; }

        public PlotModel EquityPlotModel { get; }
        public PlotModel DrawdownPlotModel { get; }

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand ExportTradesCommand { get; }
        public RelayCommand<SymbolTileViewModel> PinSymbolCommand { get; }
        public RelayCommand EmergencyStopCommand { get; }
        public RelayCommand OpenLogsWindowCommand { get; }
        public RelayCommand ClearAutoPauseCommand { get; }
        public RelayCommand TrainNowCommand { get; }
        public RelayCommand ExportStrategiesCommand { get; }
        public RelayCommand ExportSymbolsCommand { get; }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); }
        }

        public string ModeBadgeText
        {
            get => _modeBadgeText;
            set { _modeBadgeText = value; OnPropertyChanged(); }
        }

        public bool IsLiveMode
        {
            get => _isLiveMode;
            set { _isLiveMode = value; OnPropertyChanged(); }
        }

        public Brush ModeBadgeBrush
        {
            get => _modeBadgeBrush;
            set { _modeBadgeBrush = value; OnPropertyChanged(); }
        }

        public Brush ConnectionBrush
        {
            get => _connectionBrush;
            set { _connectionBrush = value; OnPropertyChanged(); }
        }

        public string ActiveSymbol
        {
            get => _activeSymbol;
            set { _activeSymbol = value; OnPropertyChanged(); }
        }

        public string ActiveStrategy
        {
            get => _activeStrategy;
            set { _activeStrategy = value; OnPropertyChanged(); }
        }

        public string ActiveContractId
        {
            get => _activeContractId;
            set { _activeContractId = value; OnPropertyChanged(); }
        }

        public string ActiveDirection
        {
            get => _activeDirection;
            set { _activeDirection = value; OnPropertyChanged(); }
        }

        public double ActiveStake
        {
            get => _activeStake;
            set { _activeStake = value; OnPropertyChanged(); }
        }

        public string ActiveDuration
        {
            get => _activeDuration;
            set { _activeDuration = value; OnPropertyChanged(); }
        }

        public string ActiveRemaining
        {
            get => _activeRemaining;
            set { _activeRemaining = value; OnPropertyChanged(); }
        }

        public string ActiveStatus
        {
            get => _activeStatus;
            set { _activeStatus = value; OnPropertyChanged(); }
        }

        public string RiskState
        {
            get => _riskState;
            set { _riskState = value; OnPropertyChanged(); }
        }

        public string MarketRegime
        {
            get => _marketRegime;
            set { _marketRegime = value; OnPropertyChanged(); }
        }

        public double MarketHeatScore
        {
            get => _marketHeatScore;
            set { _marketHeatScore = value; OnPropertyChanged(); }
        }

        public double DailyLossLimit
        {
            get => _dailyLossLimit;
            set { _dailyLossLimit = value; OnPropertyChanged(); }
        }

        public int MaxConsecutiveLosses
        {
            get => _maxConsecutiveLosses;
            set { _maxConsecutiveLosses = value; OnPropertyChanged(); }
        }

        public int CooldownSeconds
        {
            get => _cooldownSeconds;
            set { _cooldownSeconds = value; OnPropertyChanged(); }
        }

        public string StakeModel
        {
            get => _stakeModel;
            set { _stakeModel = value; OnPropertyChanged(); }
        }

        public bool RelaxGatesEnabled
        {
            get => _relaxGatesEnabled;
            set { _relaxGatesEnabled = value; OnPropertyChanged(); }
        }

        public string LastSkipReason
        {
            get => _lastSkipReason;
            set { _lastSkipReason = value; OnPropertyChanged(); }
        }

        public bool AutoStartEnabled
        {
            get => _autoStartEnabled;
            set
            {
                _autoStartEnabled = value;
                OnPropertyChanged();
                _runtimeService.SetAutoStart(value);
            }
        }

        public bool AutoRotateEnabled
        {
            get => _autoRotateEnabled;
            set
            {
                _autoRotateEnabled = value;
                OnPropertyChanged();
                _runtimeService.SetAutoRotation(value);
            }
        }

        public bool RelaxEnvFiltersEnabled
        {
            get => _relaxEnvFiltersEnabled;
            set
            {
                _relaxEnvFiltersEnabled = value;
                OnPropertyChanged();
                _runtimeService.SetRelaxEnvironment(value);
            }
        }

        public BotProfile[] ProfileOptions => new[]
        {
            BotProfile.HighQuality,
            BotProfile.Conservative,
            BotProfile.Balanced,
            BotProfile.Aggressive
        };

        public BotProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                _selectedProfile = value;
                OnPropertyChanged();
                _runtimeService.ApplyProfile(value);
            }
        }


        public ObservableCollection<ToastItem> Toasts => _toastService.Toasts;

        public async void InitializeRuntime(Settings.AppSettings settings)
        {
            try
            {
                await _runtimeService.InitializeAsync(settings);
            }
            catch (Exception ex)
            {
                _toastService.Show("Startup", ex.Message);
            }
        }

        public void ApplyTheme(ThemeKind theme) => _themeService.ApplyTheme(theme);

        private void StartBot()
        {
            _runtimeService.Start();
        }

        private void StopBot()
        {
            _runtimeService.Stop();
        }

        private void ClearAutoPause()
        {
            _runtimeService.ClearAutoPause();
            _toastService.Show("Auto-pause cleared", "Trading can resume.");
        }

        private void TrainNow()
        {
            _runtimeService.TriggerTrainingNow();
        }

        private void ExportStrategies()
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var file = System.IO.Path.Combine(folder, $"Deriv_Strategies_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _exportService.ExportStrategyStatsCsv(StrategyRows, file);
            _toastService.Show("Exported", "Strategy stats CSV saved to Desktop.");
        }

        private void ExportSymbols()
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var file = System.IO.Path.Combine(folder, $"Deriv_Symbols_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _exportService.ExportSymbolStatsCsv(SymbolTiles, file);
            _toastService.Show("Exported", "Symbol stats CSV saved to Desktop.");
        }

        private void EmergencyStop()
        {
            _runtimeService.Stop();
            _toastService.Show("Emergency Stop", "Bot halted by operator.");
        }

        private void PinSymbol(SymbolTileViewModel tile)
        {
            if (tile == null)
                return;

            _runtimeService.SetActiveSymbol(tile.Symbol);
            _toastService.Show("Pinned", $"Focused {tile.Symbol}");
        }

        private void ApplySnapshot()
        {
            var snapshot = _latestSnapshot;
            if (snapshot == null)
                return;

            IsConnected = snapshot.IsConnected;
            IsRunning = snapshot.IsRunning;
            ModeBadgeText = snapshot.ModeBadgeText;
            IsLiveMode = snapshot.ModeBadgeText == "LIVE";
            ModeBadgeBrush = snapshot.ModeBadgeText == "LIVE"
                ? (Brush)App.Current.FindResource("BadgeLiveBrush")
                : (Brush)App.Current.FindResource("BadgeDemoBrush");
            ConnectionBrush = snapshot.IsConnected
                ? (Brush)App.Current.FindResource("PositiveBrush")
                : (Brush)App.Current.FindResource("NegativeBrush");

            ActiveSymbol = snapshot.ActiveSymbol;
            ActiveStrategy = snapshot.ActiveStrategy;
            ActiveContractId = snapshot.ActiveContractId;
            ActiveDirection = snapshot.ActiveDirection;
            ActiveStake = snapshot.ActiveStake;
            ActiveDuration = snapshot.ActiveDuration;
            ActiveRemaining = snapshot.ActiveRemaining;
            ActiveStatus = snapshot.ActiveStatus;

            RiskState = snapshot.RiskState;
            MarketRegime = snapshot.MarketRegime;
            MarketHeatScore = snapshot.MarketHeatScore;
            DailyLossLimit = snapshot.DailyLossLimit;
            MaxConsecutiveLosses = snapshot.MaxConsecutiveLosses;
            CooldownSeconds = snapshot.CooldownSeconds;
            StakeModel = snapshot.StakeModel;
            RelaxGatesEnabled = snapshot.RelaxGatesEnabled;
            LastSkipReason = snapshot.LastSkipReason;

            Diagnostics.ConnectionStatus = snapshot.ConnectionStatus;
            Diagnostics.MessageRate = snapshot.MessageRate;
            Diagnostics.UiRefreshRate = snapshot.UiRefreshRate;
            Diagnostics.LatestException = snapshot.LatestException;
            Diagnostics.Latency = snapshot.Latency;
            Diagnostics.AutoTrainStatus = snapshot.AutoTrainStatus;
            Diagnostics.LastModelUpdate = snapshot.LastModelUpdate;
            Diagnostics.AutoTrainAvailable = snapshot.AutoTrainAvailable;
            Diagnostics.StrategyDiagnostics = snapshot.StrategyDiagnostics;

            SyncKpis(snapshot);
            UpdateSeries(_equitySeries, snapshot.EquitySeries);
            EquityPlotModel.InvalidatePlot(true);
            UpdateSeries(_drawdownSeries, snapshot.DrawdownSeries);
            DrawdownPlotModel.InvalidatePlot(true);

            CollectionSyncService.Sync(Trades.Trades, snapshot.Trades, t => t.Id, CopyTrade);
            Trades.TradesView.Refresh();
            CollectionSyncService.Sync(StrategyRows, snapshot.Strategies, s => s.Strategy, CopyStrategy);
            CollectionSyncService.Sync(SymbolTiles, snapshot.Symbols, s => s.Symbol, CopySymbol);
            CollectionSyncService.Sync(Logs.Logs, snapshot.Logs, l => l.Id, CopyLog);
            CollectionSyncService.Sync(Alerts.Alerts, snapshot.Alerts, a => a.Id, CopyAlert);
            CollectionSyncService.Sync(WatchlistSymbols, snapshot.Watchlist, s => s, (t, s) => { });
        }

        private void SyncKpis(BotSnapshot snapshot)
        {
            var items = new[]
            {
                new KpiItemViewModel { Title = "Balance", Value = $"${snapshot.Balance:0.00}", SubValue = "Equity", AccentBrush = (Brush)App.Current.FindResource("AccentBrush") },
                new KpiItemViewModel { Title = "Today P/L", Value = $"${snapshot.TodaysPL:0.00}", SubValue = "Session", AccentBrush = (Brush)App.Current.FindResource("PositiveBrush") },
                new KpiItemViewModel { Title = "Win Rate", Value = $"{snapshot.WinRateToday:0.0}%", SubValue = $"Last 200: {snapshot.WinRate200:0.0}%", AccentBrush = (Brush)App.Current.FindResource("InfoBrush") },
                new KpiItemViewModel { Title = "Trades (session)", Value = snapshot.TradesToday.ToString(), SubValue = "Session", AccentBrush = (Brush)App.Current.FindResource("AccentBrush") },
                new KpiItemViewModel { Title = "Market Heat", Value = $"{snapshot.MarketHeatScore:0.0}", SubValue = snapshot.MarketRegime, AccentBrush = (Brush)App.Current.FindResource("WarningBrush") }
            };

            CollectionSyncService.Sync(Kpis, items, k => k.Title, CopyKpi);
        }

        private static void CopyTrade(TradeRowViewModel target, TradeRowViewModel source)
        {
            target.Time = source.Time;
            target.Symbol = source.Symbol;
            target.Strategy = source.Strategy;
            target.Direction = source.Direction;
            target.Stake = source.Stake;
            target.Profit = source.Profit;
        }

        private static void CopyStrategy(StrategyRowViewModel target, StrategyRowViewModel source)
        {
            target.WinRate50 = source.WinRate50;
            target.WinRate200 = source.WinRate200;
            target.AvgPL = source.AvgPL;
            target.Trades = source.Trades;
            target.IsEnabled = source.IsEnabled;
            target.RecommendedDuration = source.RecommendedDuration;
        }

        private static void CopySymbol(SymbolTileViewModel target, SymbolTileViewModel source)
        {
            target.Heat = source.Heat;
            target.Regime = source.Regime;
            target.WinRate = source.WinRate;
            target.LastSignal = source.LastSignal;
            target.Volatility = source.Volatility;
            target.Trades = source.Trades;
            target.NetPL = source.NetPL;
        }

        private static void CopyLog(LogItemViewModel target, LogItemViewModel source)
        {
            target.Time = source.Time;
            target.Message = source.Message;
            target.Severity = source.Severity;
            target.SeverityBrush = source.SeverityBrush;
        }

        private static void CopyAlert(AlertItemViewModel target, AlertItemViewModel source)
        {
            target.Time = source.Time;
            target.Title = source.Title;
            target.Description = source.Description;
            target.Category = source.Category;
        }

        private static void CopyKpi(KpiItemViewModel target, KpiItemViewModel source)
        {
            target.Value = source.Value;
            target.SubValue = source.SubValue;
            target.AccentBrush = source.AccentBrush;
        }


        private static PlotModel CreatePlotModel(OxyColor lineColor, out LineSeries series)
        {
            var model = new PlotModel
            {
                PlotAreaBorderThickness = new OxyThickness(0),
                PlotAreaBorderColor = OxyColors.Transparent,
                Background = OxyColors.Transparent
            };

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                IsAxisVisible = false,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                MinimumPadding = 0,
                MaximumPadding = 0
            });

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                IsAxisVisible = false,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                MinimumPadding = 0,
                MaximumPadding = 0
            });

            series = new LineSeries
            {
                Color = lineColor,
                StrokeThickness = 2,
                CanTrackerInterpolatePoints = false,
                LineStyle = LineStyle.Solid
            };

            model.Series.Add(series);
            return model;
        }

        private static void UpdateSeries(LineSeries series, System.Collections.Generic.IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                series.Points.Clear();
                return;
            }

            var min = values.Min();
            var max = values.Max();
            var range = Math.Abs(max - min) < 0.0001 ? 1 : max - min;

            var count = values.Count;
            var existingCount = series.Points.Count;
            var limit = Math.Min(count, existingCount);

            for (int i = 0; i < limit; i++)
            {
                var normalized = (values[i] - min) / range;
                var y = 100 - (normalized * 100);
                series.Points[i] = new DataPoint(i, y);
            }

            for (int i = limit; i < count; i++)
            {
                var normalized = (values[i] - min) / range;
                var y = 100 - (normalized * 100);
                series.Points.Add(new DataPoint(i, y));
            }

            for (int i = series.Points.Count - 1; i >= count; i--)
            {
                series.Points.RemoveAt(i);
            }
        }
    }
}
