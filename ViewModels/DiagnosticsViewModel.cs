namespace DerivSmartBotDesktop.ViewModels
{
    public class DiagnosticsViewModel : ViewModelBase
    {
        private string _connectionStatus = string.Empty;
        private double _messageRate;
        private double _uiRefreshRate;
        private string _latestException = string.Empty;
        private string _latency = string.Empty;

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { _connectionStatus = value; OnPropertyChanged(); }
        }

        public double MessageRate
        {
            get => _messageRate;
            set { _messageRate = value; OnPropertyChanged(); }
        }

        public double UiRefreshRate
        {
            get => _uiRefreshRate;
            set { _uiRefreshRate = value; OnPropertyChanged(); }
        }

        public string LatestException
        {
            get => _latestException;
            set { _latestException = value; OnPropertyChanged(); }
        }

        public string Latency
        {
            get => _latency;
            set { _latency = value; OnPropertyChanged(); }
        }
    }
}
