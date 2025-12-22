using System;

namespace DerivSmartBotDesktop.ViewModels
{
    public class TradeRowViewModel : ViewModelBase
    {
        private string _id = string.Empty;
        private DateTime _time;
        private string _symbol = string.Empty;
        private string _strategy = string.Empty;
        private string _direction = string.Empty;
        private double _stake;
        private double _profit;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public DateTime Time
        {
            get => _time;
            set { _time = value; OnPropertyChanged(); }
        }

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); }
        }

        public string Strategy
        {
            get => _strategy;
            set { _strategy = value; OnPropertyChanged(); }
        }

        public string Direction
        {
            get => _direction;
            set { _direction = value; OnPropertyChanged(); }
        }

        public double Stake
        {
            get => _stake;
            set { _stake = value; OnPropertyChanged(); }
        }

        public double Profit
        {
            get => _profit;
            set
            {
                _profit = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Result));
            }
        }

        public string Result => Profit >= 0 ? "Win" : "Loss";
    }
}
