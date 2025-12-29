namespace DerivSmartBotDesktop.ViewModels
{
    public class DiagnosticsViewModel : ViewModelBase
    {
        private string _connectionStatus = string.Empty;
        private double _messageRate;
        private double _uiRefreshRate;
        private string _latestException = string.Empty;
        private string _latency = string.Empty;
        private string _autoTrainStatus = string.Empty;
        private string _lastModelUpdate = string.Empty;
        private bool _autoTrainAvailable;
        private string _strategyDiagnostics = string.Empty;

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

        public string AutoTrainStatus
        {
            get => _autoTrainStatus;
            set { _autoTrainStatus = value; OnPropertyChanged(); }
        }

        public string LastModelUpdate
        {
            get => _lastModelUpdate;
            set { _lastModelUpdate = value; OnPropertyChanged(); }
        }

        public bool AutoTrainAvailable
        {
            get => _autoTrainAvailable;
            set { _autoTrainAvailable = value; OnPropertyChanged(); }
        }

        public string StrategyDiagnostics
        {
            get => _strategyDiagnostics;
            set { _strategyDiagnostics = value; OnPropertyChanged(); }
        }
    }
}
