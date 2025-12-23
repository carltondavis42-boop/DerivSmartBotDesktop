using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using DerivSmartBotDesktop.Services;

namespace DerivSmartBotDesktop.ViewModels
{
    public class TradesViewModel : ViewModelBase
    {
        private readonly ExportService _exportService;
        private string _filterSymbol = string.Empty;
        private string _filterStrategy = string.Empty;
        private string _filterResult = string.Empty;

        public TradesViewModel(ExportService exportService)
        {
            _exportService = exportService;
            Trades = new ObservableCollection<TradeRowViewModel>();
            TradesView = CollectionViewSource.GetDefaultView(Trades);
            TradesView.Filter = FilterTrades;
            TradesView.SortDescriptions.Add(new SortDescription(nameof(TradeRowViewModel.Time), ListSortDirection.Descending));
            ApplyFiltersCommand = new RelayCommand(() => TradesView.Refresh());
            ExportCommand = new RelayCommand(ExportTrades);
        }

        public ObservableCollection<TradeRowViewModel> Trades { get; }
        public ICollectionView TradesView { get; }

        public string FilterSymbol
        {
            get => _filterSymbol;
            set { _filterSymbol = value; OnPropertyChanged(); TradesView.Refresh(); }
        }

        public string FilterStrategy
        {
            get => _filterStrategy;
            set { _filterStrategy = value; OnPropertyChanged(); TradesView.Refresh(); }
        }

        public string FilterResult
        {
            get => _filterResult;
            set { _filterResult = value; OnPropertyChanged(); TradesView.Refresh(); }
        }

        public RelayCommand ExportCommand { get; }
        public RelayCommand ApplyFiltersCommand { get; }

        private bool FilterTrades(object obj)
        {
            if (obj is not TradeRowViewModel trade)
                return false;

            if (!string.IsNullOrWhiteSpace(FilterSymbol) && !trade.Symbol.Contains(FilterSymbol, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(FilterStrategy) && !trade.Strategy.Contains(FilterStrategy, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(FilterResult) && !trade.Result.Contains(FilterResult, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private void ExportTrades()
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var file = Path.Combine(folder, $"DerivTrades_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _exportService.ExportTradesCsv(Trades, file);
        }
    }
}
