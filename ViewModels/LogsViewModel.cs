using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace DerivSmartBotDesktop.ViewModels
{
    public class LogsViewModel : ViewModelBase
    {
        private string _searchText = string.Empty;
        private bool _autoScroll = true;
        private string _severityFilter = "All";

        public LogsViewModel()
        {
            Logs = new ObservableCollection<LogItemViewModel>();
            LogsView = CollectionViewSource.GetDefaultView(Logs);
            LogsView.Filter = FilterLogs;
            CopyCommand = new RelayCommand(CopyLogs);
        }

        public ObservableCollection<LogItemViewModel> Logs { get; }
        public ICollectionView LogsView { get; }

        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(); LogsView.Refresh(); }
        }

        public bool AutoScroll
        {
            get => _autoScroll;
            set { _autoScroll = value; OnPropertyChanged(); }
        }

        public string SeverityFilter
        {
            get => _severityFilter;
            set { _severityFilter = value; OnPropertyChanged(); LogsView.Refresh(); }
        }

        public string[] SeverityOptions => new[] { "All", "Info", "Warning", "Error" };

        public RelayCommand CopyCommand { get; }

        private bool FilterLogs(object obj)
        {
            if (obj is not LogItemViewModel item)
                return false;

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return MatchesSeverity(item);
            }

            return item.Message.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0
                   && MatchesSeverity(item);
        }

        private bool MatchesSeverity(LogItemViewModel item)
        {
            return SeverityFilter switch
            {
                "Info" => item.Severity == LogSeverity.Info,
                "Warning" => item.Severity == LogSeverity.Warning,
                "Error" => item.Severity == LogSeverity.Error,
                _ => true
            };
        }

        private void CopyLogs()
        {
            var text = string.Join(Environment.NewLine, Logs.TakeLast(200).Select(l => $"[{l.Time:HH:mm:ss}] {l.Message}"));
            Clipboard.SetText(text);
        }
    }
}
