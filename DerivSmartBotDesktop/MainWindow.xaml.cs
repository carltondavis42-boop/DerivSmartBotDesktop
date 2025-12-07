using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using DerivSmartBotDesktop.Core;
using DerivSmartBotDesktop.Deriv;
using DerivSmartBotDesktop.Settings;

namespace DerivSmartBotDesktop
{
    public partial class MainWindow : Window
    {
        private BotViewModel _viewModel;
        private SmartBotController _controller;
        private DerivWebSocketClient _derivClient;
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsService.Load();

            if (!_settings.IsValid)
            {
                if (!ShowSettingsDialog())
                {
                    MessageBox.Show("Settings are required to run the bot.",
                        "Deriv Smart Bot", MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                    return;
                }
            }

            await InitializeBotAsync();
        }

        private bool ShowSettingsDialog()
        {
            var win = new SettingsWindow(_settings)
            {
                Owner = this
            };
            bool? result = win.ShowDialog();
            if (result == true && win.ResultSettings != null)
            {
                _settings = win.ResultSettings;
                SettingsService.Save(_settings);
                return true;
            }
            return false;
        }

        private async Task InitializeBotAsync()
        {
            // 1) Create client and connect
            _derivClient?.Dispose();
            _derivClient = new DerivWebSocketClient(_settings.AppId);
            await _derivClient.ConnectAsync();

            // 2) Authorize
            await _derivClient.AuthorizeAsync(_settings.ApiToken);

            try
            {
                await _derivClient.WaitUntilAuthorizedAsync();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Authorization failed: {ex.Message}",
                    "Deriv Smart Bot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // 3) Build profile + risk
            var profileCfg = BotProfileConfig.ForProfile(BotProfile.Balanced);
            var riskManager = new RiskManager(profileCfg.Risk);

            // 4) Build strategies
            var maTrend = new MovingAverageTrendStrategy();
            var breakout = new RangeBreakoutStrategy();
            var meanRev = new MeanReversionStrategy();
            var smc = new SmartMoneyConceptStrategy();
            var filteredMeanRev = new VolatilityFilteredStrategy(
                meanRev, "LowVol",
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
                maTrend,
                filteredBreakout,
                filteredMeanRev,
                smc
                // new DebugPingPongStrategy() // optional debug
            };

            // 5) Create controller
            _controller = new SmartBotController(
                riskManager,
                strategies,
                profileCfg.Rules,
                _derivClient);

            // 6) Symbols to watch (primary + default list)
            var watchList = new List<string>();
            if (!string.IsNullOrWhiteSpace(_settings.Symbol))
                watchList.Add(_settings.Symbol.Trim());

            string[] defaults =
            {
                "R_10", "R_25", "R_50", "R_75", "R_100",
                "1HZ10V", "1HZ15V", "1HZ25V", "1HZ30V", "1HZ90V", "1HZ100V", "1HZ75V",
                "STPRNG", "STPRNG2", "STPRNG3", "STPRNG4", "STPRNG5",
                "BOOM1000", "CRASH300N",
                "JD10", "JD25", "JD50", "JD75", "JD100"
            };

            foreach (var s in defaults)
            {
                if (!watchList.Contains(s))
                    watchList.Add(s);
            }

            _controller.SetSymbolsToWatch(watchList);
            _controller.SetAutoSymbolMode(AutoSymbolMode.Manual); // multi-symbol infra kept, logic still driven by active symbol

            // 7) ViewModel & DataContext
            _viewModel = new BotViewModel(_controller);
            DataContext = _viewModel;

            // 8) Deriv logs -> ViewModel log
            _derivClient.LogMessage += msg =>
            {
                Dispatcher.Invoke(() => _viewModel.AddLog(msg));
            };

            // 9) Subscribe ticks for all watched symbols
            foreach (var s in watchList)
            {
                await _derivClient.SubscribeTicksAsync(s);
            }

            // 10) Request balance
            await _derivClient.RequestBalanceAsync();
        }

        private void StartBot_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.StartBot();
        }

        private void StopBot_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.StopBot();
        }

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (ShowSettingsDialog())
            {
                _viewModel?.StopBot();
                await InitializeBotAsync();
            }
        }

        // Auto-scroll log to bottom
        private void LogTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            LogTextBox.ScrollToEnd();
        }
    }
}
