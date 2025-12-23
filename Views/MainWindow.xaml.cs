using System.Windows;
using DerivSmartBotDesktop.Services;
using DerivSmartBotDesktop.Settings;
using DerivSmartBotDesktop.ViewModels;

namespace DerivSmartBotDesktop.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ThemeService _themeService;

        public MainWindow()
        {
            InitializeComponent();

            var runtimeService = new BotRuntimeService();
            _themeService = new ThemeService();
            var toastService = new ToastService();
            var exportService = new ExportService();

            _viewModel = new MainViewModel(runtimeService, _themeService, toastService, exportService);
            _viewModel.RequestOpenSettings += OpenSettings;
            _viewModel.RequestOpenLogs += OpenLogs;

            DataContext = _viewModel;
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Load();
            if (!settings.IsValid)
            {
                if (!OpenSettingsDialog(settings, out var updated))
                {
                    Close();
                    return;
                }
                settings = updated;
                SettingsService.Save(settings);
            }

            _viewModel.InitializeRuntime(settings);
        }

        private void OpenSettings()
        {
            var settings = SettingsService.Load();
            if (OpenSettingsDialog(settings, out var updated))
            {
                SettingsService.Save(updated);
                _viewModel.InitializeRuntime(updated);
            }
        }

        private void OpenLogs()
        {
            var win = new LogsWindow
            {
                Owner = this,
                DataContext = _viewModel.Logs
            };
            win.Show();
        }

        private bool OpenSettingsDialog(AppSettings settings, out AppSettings updated)
        {
            var vm = new DerivSmartBotDesktop.ViewModels.SettingsViewModel(settings, _themeService);
            var win = new SettingsWindow(vm)
            {
                Owner = this
            };

            var result = win.ShowDialog();
            updated = result == true ? vm.ToSettings() : settings;
            return result == true;
        }
    }
}
