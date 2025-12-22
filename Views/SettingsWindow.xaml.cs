using System.Windows;
using DerivSmartBotDesktop.ViewModels;

namespace DerivSmartBotDesktop.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(DerivSmartBotDesktop.ViewModels.SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += OnRequestClose;
        }

        private void OnRequestClose(bool result)
        {
            DialogResult = result;
            Close();
        }
    }
}
