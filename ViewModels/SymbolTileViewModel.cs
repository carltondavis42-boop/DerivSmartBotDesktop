namespace DerivSmartBotDesktop.ViewModels
{
    public class SymbolTileViewModel : ViewModelBase
    {
        private string _symbol = string.Empty;
        private double _heat;
        private string _lastSignal = string.Empty;
        private double _winRate;
        private string _regime = string.Empty;
        private double _volatility;

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); }
        }

        public double Heat
        {
            get => _heat;
            set { _heat = value; OnPropertyChanged(); }
        }

        public string LastSignal
        {
            get => _lastSignal;
            set { _lastSignal = value; OnPropertyChanged(); }
        }

        public double WinRate
        {
            get => _winRate;
            set { _winRate = value; OnPropertyChanged(); }
        }

        public string Regime
        {
            get => _regime;
            set { _regime = value; OnPropertyChanged(); }
        }

        public double Volatility
        {
            get => _volatility;
            set { _volatility = value; OnPropertyChanged(); }
        }
    }
}
