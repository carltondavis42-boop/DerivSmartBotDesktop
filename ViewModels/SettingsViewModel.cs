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
        private int _tradeCooldownSeconds;
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
            TradeCooldownSeconds = _initial.TradeCooldownSeconds;
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

        public int TradeCooldownSeconds
        {
            get => _tradeCooldownSeconds;
            set { _tradeCooldownSeconds = value; OnPropertyChanged(); }
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
                TradeCooldownSeconds = TradeCooldownSeconds,
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
