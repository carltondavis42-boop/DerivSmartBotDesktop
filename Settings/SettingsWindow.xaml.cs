using System;
using System.Threading.Tasks;
using System.Windows;
using DerivSmartBotDesktop.Deriv;

namespace DerivSmartBotDesktop.Settings
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _initialSettings;

        public AppSettings ResultSettings { get; private set; }

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _initialSettings = settings ?? new AppSettings();

            AppIdTextBox.Text = _initialSettings.AppId ?? string.Empty;
            ApiTokenBox.Password = _initialSettings.ApiToken ?? string.Empty;
            SymbolTextBox.Text = _initialSettings.Symbol ?? "R_100";
            ModeComboBox.SelectedIndex = _initialSettings.IsDemo ? 0 : 1;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var s = new AppSettings
            {
                AppId = AppIdTextBox.Text?.Trim(),
                ApiToken = ApiTokenBox.Password?.Trim(),
                Symbol = string.IsNullOrWhiteSpace(SymbolTextBox.Text)
                    ? "R_100"
                    : SymbolTextBox.Text.Trim(),
                IsDemo = ModeComboBox.SelectedIndex != 1
            };

            if (!s.IsValid)
            {
                MessageBox.Show("All fields are required.", "Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultSettings = s;
            DialogResult = true;
            Close();
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var s = new AppSettings
            {
                AppId = AppIdTextBox.Text?.Trim(),
                ApiToken = ApiTokenBox.Password?.Trim(),
                Symbol = string.IsNullOrWhiteSpace(SymbolTextBox.Text)
                    ? "R_100"
                    : SymbolTextBox.Text.Trim(),
                IsDemo = ModeComboBox.SelectedIndex != 1
            };

            if (!s.IsValid)
            {
                MessageBox.Show("All fields are required to test connection.", "Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var button = sender as FrameworkElement;
            if (button != null)
                button.IsEnabled = false;

            try
            {
                using var client = new DerivWebSocketClient(s.AppId);
                await client.ConnectAsync();
                await client.AuthorizeAsync(s.ApiToken);
                await client.WaitUntilAuthorizedAsync();

                var mode = s.IsDemo ? "DEMO" : "LIVE";
                MessageBox.Show(
                    $"Connected ({mode}). Login={client.LoginId ?? "unknown"}, Currency={client.Currency ?? "n/a"}.",
                    "Connection Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Connection test failed: {ex.Message}",
                    "Connection Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (button != null)
                    button.IsEnabled = true;
            }
        }

    }
}
