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

        public RelayCommand CopyCommand { get; }

        private bool FilterLogs(object obj)
        {
            if (obj is not LogItemViewModel item)
                return false;

            if (string.IsNullOrWhiteSpace(SearchText))
                return true;

            return item.Message.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CopyLogs()
        {
            var text = string.Join(Environment.NewLine, Logs.TakeLast(200).Select(l => $"[{l.Time:HH:mm:ss}] {l.Message}"));
            Clipboard.SetText(text);
        }
    }
}
