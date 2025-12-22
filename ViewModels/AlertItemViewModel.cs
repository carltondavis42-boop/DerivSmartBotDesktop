using System;

namespace DerivSmartBotDesktop.ViewModels
{
    public class AlertItemViewModel : ViewModelBase
    {
        private string _id = string.Empty;
        private DateTime _time;
        private string _title = string.Empty;
        private string _description = string.Empty;
        private string _category = string.Empty;

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

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public string Category
        {
            get => _category;
            set { _category = value; OnPropertyChanged(); }
        }
    }
}
