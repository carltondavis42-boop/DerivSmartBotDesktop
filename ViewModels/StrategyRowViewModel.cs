namespace DerivSmartBotDesktop.ViewModels
{
    public class StrategyRowViewModel : ViewModelBase
    {
        private string _strategy = string.Empty;
        private double _winRate50;
        private double _winRate200;
        private double _avgPl;
        private int _trades;
        private bool _isEnabled;
        private string _recommendedDuration = string.Empty;

        public string Strategy
        {
            get => _strategy;
            set { _strategy = value; OnPropertyChanged(); }
        }

        public double WinRate50
        {
            get => _winRate50;
            set { _winRate50 = value; OnPropertyChanged(); }
        }

        public double WinRate200
        {
            get => _winRate200;
            set { _winRate200 = value; OnPropertyChanged(); }
        }

        public double AvgPL
        {
            get => _avgPl;
            set { _avgPl = value; OnPropertyChanged(); }
        }

        public int Trades
        {
            get => _trades;
            set { _trades = value; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public string RecommendedDuration
        {
            get => _recommendedDuration;
            set { _recommendedDuration = value; OnPropertyChanged(); }
        }
    }
}
