using System.Windows.Media;

namespace DerivSmartBotDesktop.ViewModels
{
    public class KpiItemViewModel : ViewModelBase
    {
        private string _title = string.Empty;
        private string _value = string.Empty;
        private string _subValue = string.Empty;
        private Brush _accentBrush = Brushes.Transparent;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); }
        }

        public string SubValue
        {
            get => _subValue;
            set { _subValue = value; OnPropertyChanged(); }
        }

        public Brush AccentBrush
        {
            get => _accentBrush;
            set { _accentBrush = value; OnPropertyChanged(); }
        }
    }
}
