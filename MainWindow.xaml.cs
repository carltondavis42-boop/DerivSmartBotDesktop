using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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

        // Auto-reconnect state
        private bool _isReconnecting;
        private readonly object _reconnectLock = new object();

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                Loaded += MainWindow_Loaded;
                ApplyTheme(isLight: false);
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(Path.GetTempPath(), "DerivSmartBotDesktop_startup_error.txt");
                File.WriteAllText(logPath, ex.ToString());
                throw;
            }
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
            var scalping = new ScalpingStrategy();
            var momentum = new MomentumStrategy();
            var range = new RangeTradingStrategy();
            var breakout = new BreakoutStrategy();
            var smc = new SmartMoneyConceptStrategy();
            var pa = new AdvancedPriceActionStrategy();
            var supplyDemand = new SupplyDemandPullbackStrategy(timeframeMinutes: 30);

            // Optional volatility filters to adapt behaviour across regimes.
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
                // new DebugPingPongStrategy() // optional debug
            };

            // 5) Create AI helpers
            // FIXED: use the existing SimpleFeatureExtractor type from Core
            var featureExtractor = new SimpleFeatureExtractor();
            var tradeLogger = new CsvTradeDataLogger();

            // We will capture short info messages for the UI log
            string regimeModelInfo = string.Empty;
            string edgeModelInfo = string.Empty;

            // Create strategy selector (ML-based if edge model is available, otherwise rule-based).
            IStrategySelector strategySelector;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string edgeModelPath = Path.Combine(baseDir, "Data", "ML", "edge-linear-v1.json");

                if (File.Exists(edgeModelPath))
                {
                    var edgeModel = new JsonStrategyEdgeModel(edgeModelPath);
                    strategySelector = new MlStrategySelector(
                        edgeModel,
                        fallback: new RuleBasedStrategySelector());

                    edgeModelInfo = $"Using ML edge model: {Path.GetFileName(edgeModelPath)}";
                }
                else
                {
                    strategySelector = new RuleBasedStrategySelector();
                    edgeModelInfo = "ML edge model not found; using RuleBasedStrategySelector.";
                }
            }
            catch
            {
                strategySelector = new RuleBasedStrategySelector();
                edgeModelInfo = "Failed to load ML edge model; using RuleBasedStrategySelector.";
            }

            // 5b) Create regime classifier (ML if model is available, otherwise heuristic).
            IMarketRegimeClassifier regimeClassifier;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelPath = Path.Combine(baseDir, "Data", "ML", "regime-linear-v1.json");

                if (File.Exists(modelPath))
                {
                    var mlModel = new JsonRegimeModel(modelPath);
                    regimeClassifier = new MLMarketRegimeClassifier(
                        mlModel,
                        fallback: new AiMarketRegimeClassifier(),
                        minConfidence: 0.55);

                    regimeModelInfo = $"Using ML regime model: {Path.GetFileName(modelPath)}";
                }
                else
                {
                    regimeClassifier = new AiMarketRegimeClassifier();
                    regimeModelInfo = "ML regime model not found; using AiMarketRegimeClassifier.";
                }
            }
            catch (Exception ex)
            {
                // If anything goes wrong while loading the ML model,
                // fall back to the classic heuristic classifier and surface the error message.
                regimeClassifier = new AiMarketRegimeClassifier();
                regimeModelInfo = $"Failed to load ML regime model ({ex.GetType().Name}: {ex.Message}); using AiMarketRegimeClassifier.";
            }


            // 6) Create controller
            _controller = new SmartBotController(
                riskManager,
                strategies,
                profileCfg.Rules,
                _derivClient,
                regimeClassifier,
                featureExtractor,
                tradeLogger,
                strategySelector);
            _controller.ForwardTestEnabled = _settings.ForwardTestEnabled;

            // 7) Symbols to watch (primary + default list)
            var watchList = new List<string>();
            if (!string.IsNullOrWhiteSpace(_settings.Symbol))
                watchList.Add(_settings.Symbol.Trim());

            string[] defaults =
            {
                "R_10", "R_25", "R_50", "R_75", "R_100",
                "1HZ10V", "1HZ15V", "1HZ25V", "1HZ30V", "1HZ90V", "1HZ100V", "1HZ75V",
                "STPRNG", "STPRNG2", "STPRNG3", "STPRNG4", "STPRNG5",
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
            _viewModel.SetAccountMode(_settings.IsDemo);
            _viewModel.SetForwardTestEnabled(_settings.ForwardTestEnabled);
            _viewModel.AddLog($"Account mode: {(_settings.IsDemo ? "DEMO" : "LIVE")}");
            if (_settings.ForwardTestEnabled)
                _viewModel.AddLog("Forward test enabled: paper trades use live proposal pricing.");

            // Log which ML components are being used
            if (!string.IsNullOrEmpty(regimeModelInfo))
                _viewModel.AddLog(regimeModelInfo);

            if (!string.IsNullOrEmpty(edgeModelInfo))
                _viewModel.AddLog(edgeModelInfo);

            // 8) Deriv logs -> ViewModel log
            _derivClient.LogMessage += msg =>
            {
                Dispatcher.Invoke(() => _viewModel.AddLog(msg));
            };

            // 8b) Auto-reconnect: if the WebSocket drops unexpectedly, attempt to reconnect.
            _derivClient.ConnectionClosed += reason =>
            {
                _ = Task.Run(() => HandleConnectionLostAsync(reason));
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

        private void ClearAutoPause_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ClearAutoPause();
        }

        private void LightTheme_Checked(object sender, RoutedEventArgs e)
        {
            ApplyTheme(isLight: true);
        }

        private void LightTheme_Unchecked(object sender, RoutedEventArgs e)
        {
            ApplyTheme(isLight: false);
        }

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (ShowSettingsDialog())
            {
                _viewModel?.StopBot();
                await InitializeBotAsync();
            }
        }

        private async void ApplyWatchlist_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel == null || _controller == null || _derivClient == null)
                return;

            _viewModel.ApplyWatchlist();

            // Ensure we are subscribed to ticks for the current watchlist so rotation has data
            foreach (var symbol in _controller.SymbolsToWatch)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                try
                {
                    await _derivClient.SubscribeTicksAsync(symbol);
                }
                catch
                {
                    // Subscription errors are logged by Deriv client; ignore here
                }
            }
        }

        // Auto-reconnect handler
        private async Task HandleConnectionLostAsync(string reason)
        {
            lock (_reconnectLock)
            {
                if (_isReconnecting)
                {
                    return;
                }

                _isReconnecting = true;
            }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    _viewModel?.AddLog($"Connection lost: {reason}. Attempting to reconnect...");
                });

                // Small delay before first attempt
                await Task.Delay(TimeSpan.FromSeconds(5));

                const int maxAttempts = 5;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _viewModel?.AddLog($"Reconnect attempt {attempt}...");
                        });

                        // Reconnect socket
                        await _derivClient.ConnectAsync();

                        // Re-authorize with the same token
                        await _derivClient.AuthorizeAsync(_settings.ApiToken);
                        await _derivClient.WaitUntilAuthorizedAsync();

                        // Re-subscribe ticks for all symbols the controller is watching
                        var symbols = _controller?.SymbolsToWatch ?? new List<string>();
                        foreach (var s in symbols)
                        {
                            try
                            {
                                await _derivClient.SubscribeTicksAsync(s);
                            }
                            catch
                            {
                                // subscription errors are logged by Deriv client
                            }
                        }

                        // Refresh balance
                        await _derivClient.RequestBalanceAsync();

                        Dispatcher.Invoke(() =>
                        {
                            _viewModel?.AddLog("Reconnected to Deriv WebSocket and restored subscriptions.");
                        });

                        // Successful reconnect
                        return;
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _viewModel?.AddLog($"Reconnect attempt {attempt} failed: {ex.Message}");
                        });

                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                    }
                }

                // If we reach here, all attempts failed.
                Dispatcher.Invoke(() =>
                {
                    _viewModel?.AddLog("Automatic reconnect failed after multiple attempts. Please restart the bot or check your network/API token.");
                });
            }
            finally
            {
                lock (_reconnectLock)
                {
                    _isReconnecting = false;
                }
            }
        }

        // Auto-scroll log to bottom
        private void LogTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            LogTextBox.ScrollToEnd();
        }

        private void ApplyTheme(bool isLight)
        {
            var baseResources = Application.Current?.Resources ?? Resources;
            var newResources = new ResourceDictionary();

            foreach (DictionaryEntry entry in baseResources)
                newResources[entry.Key] = entry.Value;

            foreach (var dict in baseResources.MergedDictionaries)
                newResources.MergedDictionaries.Add(dict);

            newResources["ShellBackgroundBrush"] = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString(isLight ? "#F6F7FB" : "#0E131A"), 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString(isLight ? "#F2F4F9" : "#121925"), 0.6),
                    new GradientStop((Color)ColorConverter.ConvertFromString(isLight ? "#F8FAFC" : "#0B1118"), 1)
                }
            };

            SetBrushColor(newResources, "PanelBrush", isLight ? "#FFFFFF" : "#121A24");
            SetBrushColor(newResources, "PanelBorderBrush", isLight ? "#E6E9F0" : "#243244");
            SetBrushColor(newResources, "HeaderBrush", isLight ? "#F9FAFD" : "#0F1722");
            SetBrushColor(newResources, "BadgeBrush", isLight ? "#F1F3F8" : "#182231");
            SetBrushColor(newResources, "BadgeBorderBrush", isLight ? "#E1E6EF" : "#2B3A4F");
            SetBrushColor(newResources, "InputBackgroundBrush", isLight ? "#F7F8FC" : "#0E1620");
            SetBrushColor(newResources, "TableRowBrush", isLight ? "#FFFFFF" : "#121B26");
            SetBrushColor(newResources, "TableHeaderBrush", isLight ? "#F1F3F8" : "#0F1822");
            SetBrushColor(newResources, "LogBackgroundBrush", isLight ? "#F7F8FC" : "#0E1620");
            SetBrushColor(newResources, "ButtonBrush", isLight ? "#F1F3F8" : "#1A2533");
            SetBrushColor(newResources, "ButtonBorderBrush", isLight ? "#E1E6EF" : "#2B3A4F");

            SetBrushColor(newResources, "AccentBrush", isLight ? "#5B5CE2" : "#3BAFDA");
            SetBrushColor(newResources, "AccentSoftBrush", isLight ? "#ECECFF" : "#1C2A35");
            SetBrushColor(newResources, "PositiveBrush", isLight ? "#24B47E" : "#3CCB90");
            SetBrushColor(newResources, "NegativeBrush", isLight ? "#E05252" : "#E35D6A");
            SetBrushColor(newResources, "TextPrimaryBrush", isLight ? "#1F2430" : "#F1F5F9");
            SetBrushColor(newResources, "TextSecondaryBrush", isLight ? "#6B7280" : "#A9B4C4");

            if (Application.Current != null)
                Application.Current.Resources = newResources;
            else
                Resources = newResources;
        }

        private void SetBrushColor(ResourceDictionary dictionary, string key, string color)
        {
            dictionary[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
