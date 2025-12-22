using System;
using System.Windows.Media;

namespace DerivSmartBotDesktop.ViewModels
{
    public enum LogSeverity
    {
        Info,
        Warning,
        Error
    }

    public class LogItemViewModel : ViewModelBase
    {
        private string _id = string.Empty;
        private DateTime _time;
        private string _message = string.Empty;
        private LogSeverity _severity;
        private Brush _severityBrush = Brushes.Transparent;

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

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        public LogSeverity Severity
        {
            get => _severity;
            set { _severity = value; OnPropertyChanged(); }
        }

        public Brush SeverityBrush
        {
            get => _severityBrush;
            set { _severityBrush = value; OnPropertyChanged(); }
        }
    }
}
