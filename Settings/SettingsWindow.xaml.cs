using System.Windows;

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
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var s = new AppSettings
            {
                AppId = AppIdTextBox.Text?.Trim(),
                ApiToken = ApiTokenBox.Password?.Trim(),
                Symbol = string.IsNullOrWhiteSpace(SymbolTextBox.Text)
                    ? "R_100"
                    : SymbolTextBox.Text.Trim()
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
    }
}
