using System;
using System.ComponentModel;
using System.Threading.Tasks;
using DerivSmartBotDesktop.Deriv;
using DerivSmartBotDesktop.Services;
using DerivSmartBotDesktop.Settings;

namespace DerivSmartBotDesktop.ViewModels
{
    public class SettingsViewModel : ViewModelBase, IDataErrorInfo
    {
        private readonly ThemeService _themeService;
        private readonly AppSettings _initial;
        private string _appId = string.Empty;
        private string _apiToken = string.Empty;
        private string _symbol = string.Empty;
        private string _watchlistCsv = string.Empty;
        private string _tradeLogDirectory = string.Empty;
        private double _dailyDrawdownPercent;
        private double _maxDailyLossAmount;
        private int _maxConsecutiveLosses;
        private int _maxTradesPerHour;
        private int _maxOpenTrades;
        private int _tradeCooldownSeconds;
        private int _minSamplesPerStrategy;
        private double _minMarketHeatToTrade;
        private double _maxMarketHeatToTrade;
        private double _minRegimeScoreToTrade;
        private double _minEnsembleConfidence;
        private double _expectedProfitBlockThreshold;
        private double _expectedProfitWarnThreshold;
        private double _minVolatilityToTrade;
        private double _maxVolatilityToTrade;
        private int _lossCooldownMultiplierSeconds;
        private int _maxLossCooldownSeconds;
        private int _minTradesBeforeMl;
        private int _strategyProbationMinTrades;
        private double _strategyProbationWinRate;
        private int _strategyProbationBlockMinutes;
        private int _strategyProbationLossBlockMinutes;
        private double _highHeatRotationThreshold;
        private int _highHeatRotationIntervalSeconds;
        private double _rotationScoreDelta;
        private double _rotationScoreDeltaHighHeat;
        private double _minConfidenceForDynamicStake;
        private double _minRegimeScoreForDynamicStake;
        private double _minHeatForDynamicStake;
        private bool _isDemo;
        private bool _forwardTestEnabled;
        private bool _relaxEnvironmentForTesting;
        private string _testStatus = string.Empty;
        private bool _isTesting;
        private ThemeKind _selectedTheme;

        public SettingsViewModel(AppSettings settings, ThemeService themeService)
        {
            _initial = settings ?? new AppSettings();
            _themeService = themeService;

            AppId = _initial.AppId;
            ApiToken = _initial.ApiToken;
            Symbol = string.IsNullOrWhiteSpace(_initial.Symbol) ? "R_100" : _initial.Symbol;
            WatchlistCsv = string.IsNullOrWhiteSpace(_initial.WatchlistCsv)
                ? new AppSettings().WatchlistCsv
                : _initial.WatchlistCsv;
            TradeLogDirectory = string.IsNullOrWhiteSpace(_initial.TradeLogDirectory)
                ? new AppSettings().TradeLogDirectory
                : _initial.TradeLogDirectory;
            DailyDrawdownPercent = _initial.DailyDrawdownPercent;
            MaxDailyLossAmount = _initial.MaxDailyLossAmount;
            MaxConsecutiveLosses = _initial.MaxConsecutiveLosses;
            MaxTradesPerHour = _initial.MaxTradesPerHour;
            MaxOpenTrades = _initial.MaxOpenTrades;
            TradeCooldownSeconds = _initial.TradeCooldownSeconds;
            MinSamplesPerStrategy = _initial.MinSamplesPerStrategy;
            MinMarketHeatToTrade = _initial.MinMarketHeatToTrade;
            MaxMarketHeatToTrade = _initial.MaxMarketHeatToTrade;
            MinRegimeScoreToTrade = _initial.MinRegimeScoreToTrade;
            MinEnsembleConfidence = _initial.MinEnsembleConfidence;
            ExpectedProfitBlockThreshold = _initial.ExpectedProfitBlockThreshold;
            ExpectedProfitWarnThreshold = _initial.ExpectedProfitWarnThreshold;
            MinVolatilityToTrade = _initial.MinVolatilityToTrade;
            MaxVolatilityToTrade = _initial.MaxVolatilityToTrade;
            LossCooldownMultiplierSeconds = _initial.LossCooldownMultiplierSeconds;
            MaxLossCooldownSeconds = _initial.MaxLossCooldownSeconds;
            MinTradesBeforeMl = _initial.MinTradesBeforeMl;
            StrategyProbationMinTrades = _initial.StrategyProbationMinTrades;
            StrategyProbationWinRate = _initial.StrategyProbationWinRate;
            StrategyProbationBlockMinutes = _initial.StrategyProbationBlockMinutes;
            StrategyProbationLossBlockMinutes = _initial.StrategyProbationLossBlockMinutes;
            HighHeatRotationThreshold = _initial.HighHeatRotationThreshold;
            HighHeatRotationIntervalSeconds = _initial.HighHeatRotationIntervalSeconds;
            RotationScoreDelta = _initial.RotationScoreDelta;
            RotationScoreDeltaHighHeat = _initial.RotationScoreDeltaHighHeat;
            MinConfidenceForDynamicStake = _initial.MinConfidenceForDynamicStake;
            MinRegimeScoreForDynamicStake = _initial.MinRegimeScoreForDynamicStake;
            MinHeatForDynamicStake = _initial.MinHeatForDynamicStake;
            IsDemo = _initial.IsDemo;
            ForwardTestEnabled = _initial.ForwardTestEnabled;
            RelaxEnvironmentForTesting = _initial.RelaxEnvironmentForTesting;
            SelectedTheme = themeService.CurrentTheme;

            SaveCommand = new RelayCommand(Save, () => IsValid);
            CancelCommand = new RelayCommand(Cancel);
            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsTesting && IsValid);
        }

        public event Action<bool>? RequestClose;

        public string AppId
        {
            get => _appId;
            set { _appId = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); RefreshCommands(); }
        }

        public string ApiToken
        {
            get => _apiToken;
            set { _apiToken = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); RefreshCommands(); }
        }

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); RefreshCommands(); }
        }

        public string WatchlistCsv
        {
            get => _watchlistCsv;
            set { _watchlistCsv = value; OnPropertyChanged(); }
        }

        public string TradeLogDirectory
        {
            get => _tradeLogDirectory;
            set { _tradeLogDirectory = value; OnPropertyChanged(); }
        }

        public double DailyDrawdownPercent
        {
            get => _dailyDrawdownPercent;
            set { _dailyDrawdownPercent = value; OnPropertyChanged(); }
        }

        public double MaxDailyLossAmount
        {
            get => _maxDailyLossAmount;
            set { _maxDailyLossAmount = value; OnPropertyChanged(); }
        }

        public int MaxConsecutiveLosses
        {
            get => _maxConsecutiveLosses;
            set { _maxConsecutiveLosses = value; OnPropertyChanged(); }
        }

        public int MaxTradesPerHour
        {
            get => _maxTradesPerHour;
            set { _maxTradesPerHour = value; OnPropertyChanged(); }
        }

        public int MaxOpenTrades
        {
            get => _maxOpenTrades;
            set { _maxOpenTrades = value; OnPropertyChanged(); }
        }

        public int TradeCooldownSeconds
        {
            get => _tradeCooldownSeconds;
            set { _tradeCooldownSeconds = value; OnPropertyChanged(); }
        }

        public int MinSamplesPerStrategy
        {
            get => _minSamplesPerStrategy;
            set { _minSamplesPerStrategy = value; OnPropertyChanged(); }
        }

        public double MinMarketHeatToTrade
        {
            get => _minMarketHeatToTrade;
            set { _minMarketHeatToTrade = value; OnPropertyChanged(); }
        }

        public double MaxMarketHeatToTrade
        {
            get => _maxMarketHeatToTrade;
            set { _maxMarketHeatToTrade = value; OnPropertyChanged(); }
        }

        public double MinRegimeScoreToTrade
        {
            get => _minRegimeScoreToTrade;
            set { _minRegimeScoreToTrade = value; OnPropertyChanged(); }
        }

        public double MinEnsembleConfidence
        {
            get => _minEnsembleConfidence;
            set { _minEnsembleConfidence = value; OnPropertyChanged(); }
        }

        public double ExpectedProfitBlockThreshold
        {
            get => _expectedProfitBlockThreshold;
            set { _expectedProfitBlockThreshold = value; OnPropertyChanged(); }
        }

        public double ExpectedProfitWarnThreshold
        {
            get => _expectedProfitWarnThreshold;
            set { _expectedProfitWarnThreshold = value; OnPropertyChanged(); }
        }

        public double MinVolatilityToTrade
        {
            get => _minVolatilityToTrade;
            set { _minVolatilityToTrade = value; OnPropertyChanged(); }
        }

        public double MaxVolatilityToTrade
        {
            get => _maxVolatilityToTrade;
            set { _maxVolatilityToTrade = value; OnPropertyChanged(); }
        }

        public int LossCooldownMultiplierSeconds
        {
            get => _lossCooldownMultiplierSeconds;
            set { _lossCooldownMultiplierSeconds = value; OnPropertyChanged(); }
        }

        public int MaxLossCooldownSeconds
        {
            get => _maxLossCooldownSeconds;
            set { _maxLossCooldownSeconds = value; OnPropertyChanged(); }
        }

        public int MinTradesBeforeMl
        {
            get => _minTradesBeforeMl;
            set { _minTradesBeforeMl = value; OnPropertyChanged(); }
        }

        public int StrategyProbationMinTrades
        {
            get => _strategyProbationMinTrades;
            set { _strategyProbationMinTrades = value; OnPropertyChanged(); }
        }

        public double StrategyProbationWinRate
        {
            get => _strategyProbationWinRate;
            set { _strategyProbationWinRate = value; OnPropertyChanged(); }
        }

        public int StrategyProbationBlockMinutes
        {
            get => _strategyProbationBlockMinutes;
            set { _strategyProbationBlockMinutes = value; OnPropertyChanged(); }
        }

        public int StrategyProbationLossBlockMinutes
        {
            get => _strategyProbationLossBlockMinutes;
            set { _strategyProbationLossBlockMinutes = value; OnPropertyChanged(); }
        }

        public double HighHeatRotationThreshold
        {
            get => _highHeatRotationThreshold;
            set { _highHeatRotationThreshold = value; OnPropertyChanged(); }
        }

        public int HighHeatRotationIntervalSeconds
        {
            get => _highHeatRotationIntervalSeconds;
            set { _highHeatRotationIntervalSeconds = value; OnPropertyChanged(); }
        }

        public double RotationScoreDelta
        {
            get => _rotationScoreDelta;
            set { _rotationScoreDelta = value; OnPropertyChanged(); }
        }

        public double RotationScoreDeltaHighHeat
        {
            get => _rotationScoreDeltaHighHeat;
            set { _rotationScoreDeltaHighHeat = value; OnPropertyChanged(); }
        }

        public double MinConfidenceForDynamicStake
        {
            get => _minConfidenceForDynamicStake;
            set { _minConfidenceForDynamicStake = value; OnPropertyChanged(); }
        }

        public double MinRegimeScoreForDynamicStake
        {
            get => _minRegimeScoreForDynamicStake;
            set { _minRegimeScoreForDynamicStake = value; OnPropertyChanged(); }
        }

        public double MinHeatForDynamicStake
        {
            get => _minHeatForDynamicStake;
            set { _minHeatForDynamicStake = value; OnPropertyChanged(); }
        }

        public bool IsDemo
        {
            get => _isDemo;
            set
            {
                _isDemo = value;
                if (!value)
                    RelaxEnvironmentForTesting = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RelaxEnvironmentEnabled));
            }
        }

        public bool ForwardTestEnabled
        {
            get => _forwardTestEnabled;
            set { _forwardTestEnabled = value; OnPropertyChanged(); }
        }

        public bool RelaxEnvironmentForTesting
        {
            get => _relaxEnvironmentForTesting;
            set { _relaxEnvironmentForTesting = value; OnPropertyChanged(); }
        }

        public bool RelaxEnvironmentEnabled => IsDemo;

        public string TestStatus
        {
            get => _testStatus;
            set { _testStatus = value; OnPropertyChanged(); }
        }

        public bool IsTesting
        {
            get => _isTesting;
            set { _isTesting = value; OnPropertyChanged(); RefreshCommands(); }
        }

        public ThemeKind SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                _selectedTheme = value;
                OnPropertyChanged();
                _themeService.ApplyTheme(_selectedTheme);
            }
        }

        public ThemeKind[] ThemeOptions => new[] { ThemeKind.Dark, ThemeKind.Light };

        public RelayCommand SaveCommand { get; }
        public RelayCommand CancelCommand { get; }
        public AsyncRelayCommand TestConnectionCommand { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(AppId) &&
            !string.IsNullOrWhiteSpace(ApiToken) &&
            !string.IsNullOrWhiteSpace(Symbol);

        public AppSettings ToSettings()
        {
            return new AppSettings
            {
                AppId = AppId.Trim(),
                ApiToken = ApiToken.Trim(),
                Symbol = Symbol.Trim(),
                WatchlistCsv = WatchlistCsv.Trim(),
                TradeLogDirectory = TradeLogDirectory.Trim(),
                DailyDrawdownPercent = DailyDrawdownPercent,
                MaxDailyLossAmount = MaxDailyLossAmount,
                MaxConsecutiveLosses = MaxConsecutiveLosses,
                MaxTradesPerHour = MaxTradesPerHour,
                MaxOpenTrades = MaxOpenTrades,
                TradeCooldownSeconds = TradeCooldownSeconds,
                MinSamplesPerStrategy = MinSamplesPerStrategy,
                MinMarketHeatToTrade = MinMarketHeatToTrade,
                MaxMarketHeatToTrade = MaxMarketHeatToTrade,
                MinRegimeScoreToTrade = MinRegimeScoreToTrade,
                MinEnsembleConfidence = MinEnsembleConfidence,
                ExpectedProfitBlockThreshold = ExpectedProfitBlockThreshold,
                ExpectedProfitWarnThreshold = ExpectedProfitWarnThreshold,
                MinVolatilityToTrade = MinVolatilityToTrade,
                MaxVolatilityToTrade = MaxVolatilityToTrade,
                LossCooldownMultiplierSeconds = LossCooldownMultiplierSeconds,
                MaxLossCooldownSeconds = MaxLossCooldownSeconds,
                MinTradesBeforeMl = MinTradesBeforeMl,
                StrategyProbationMinTrades = StrategyProbationMinTrades,
                StrategyProbationWinRate = StrategyProbationWinRate,
                StrategyProbationBlockMinutes = StrategyProbationBlockMinutes,
                StrategyProbationLossBlockMinutes = StrategyProbationLossBlockMinutes,
                HighHeatRotationThreshold = HighHeatRotationThreshold,
                HighHeatRotationIntervalSeconds = HighHeatRotationIntervalSeconds,
                RotationScoreDelta = RotationScoreDelta,
                RotationScoreDeltaHighHeat = RotationScoreDeltaHighHeat,
                MinConfidenceForDynamicStake = MinConfidenceForDynamicStake,
                MinRegimeScoreForDynamicStake = MinRegimeScoreForDynamicStake,
                MinHeatForDynamicStake = MinHeatForDynamicStake,
                IsDemo = IsDemo,
                ForwardTestEnabled = ForwardTestEnabled,
                RelaxEnvironmentForTesting = RelaxEnvironmentForTesting
            };
        }

        private void Save()
        {
            RequestClose?.Invoke(true);
        }

        private void Cancel()
        {
            RequestClose?.Invoke(false);
        }

        private void RefreshCommands()
        {
            SaveCommand?.RaiseCanExecuteChanged();
            TestConnectionCommand?.RaiseCanExecuteChanged();
        }

        private async Task TestConnectionAsync()
        {
            IsTesting = true;
            TestStatus = "Testing connection...";

            try
            {
                using var client = new DerivWebSocketClient(AppId);
                await client.ConnectAsync();
                await client.AuthorizeAsync(ApiToken);
                await client.WaitUntilAuthorizedAsync();
                TestStatus = $"Connected ({(IsDemo ? "DEMO" : "LIVE")}) - {client.LoginId ?? "unknown"}";
            }
            catch (Exception ex)
            {
                TestStatus = $"Connection failed: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        }

        public string Error => string.Empty;

        public string this[string columnName]
        {
            get
            {
                return columnName switch
                {
                    nameof(AppId) when string.IsNullOrWhiteSpace(AppId) => "App ID is required.",
                    nameof(ApiToken) when string.IsNullOrWhiteSpace(ApiToken) => "API token is required.",
                    nameof(Symbol) when string.IsNullOrWhiteSpace(Symbol) => "Symbol is required.",
                    _ => string.Empty
                };
            }
        }
    }
}
