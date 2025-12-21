using System.ComponentModel;
using System.Runtime.CompilerServices;
using DerivSmartBotDesktop.Settings;

namespace DerivSmartBotDesktop
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private string _appId;
        private string _apiToken;
        private string _symbol;
        private bool _isDemo;
        private bool _forwardTestEnabled;

        public string AppId
        {
            get => _appId;
            set { _appId = value; OnPropertyChanged(); }
        }

        public string ApiToken
        {
            get => _apiToken;
            set { _apiToken = value; OnPropertyChanged(); }
        }

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); }
        }

        public bool IsDemo
        {
            get => _isDemo;
            set { _isDemo = value; OnPropertyChanged(); }
        }

        public bool ForwardTestEnabled
        {
            get => _forwardTestEnabled;
            set { _forwardTestEnabled = value; OnPropertyChanged(); }
        }

        public SettingsViewModel(AppSettings settings)
        {
            AppId = settings.AppId;
            ApiToken = settings.ApiToken;
            Symbol = settings.Symbol;
            IsDemo = settings.IsDemo;
            ForwardTestEnabled = settings.ForwardTestEnabled;
        }

        public AppSettings ToSettings() => new AppSettings
        {
            AppId = this.AppId?.Trim(),
            ApiToken = this.ApiToken?.Trim(),
            Symbol = this.Symbol?.Trim(),
            IsDemo = this.IsDemo,
            ForwardTestEnabled = this.ForwardTestEnabled
        };

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
