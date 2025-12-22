using System.Collections.ObjectModel;

namespace DerivSmartBotDesktop.ViewModels
{
    public class AlertsViewModel : ViewModelBase
    {
        public AlertsViewModel()
        {
            Alerts = new ObservableCollection<AlertItemViewModel>();
        }

        public ObservableCollection<AlertItemViewModel> Alerts { get; }
    }
}
