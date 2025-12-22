using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace DerivSmartBotDesktop.Services
{
    public class ToastItem
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class ToastService
    {
        private readonly ObservableCollection<ToastItem> _toasts = new();

        public ObservableCollection<ToastItem> Toasts => _toasts;

        public void Show(string title, string message, int durationMs = 3500)
        {
            var toast = new ToastItem { Title = title, Message = message };
            _toasts.Add(toast);

            _ = Task.Run(async () =>
            {
                await Task.Delay(durationMs).ConfigureAwait(false);
                App.Current?.Dispatcher.Invoke(() => _toasts.Remove(toast));
            });
        }
    }
}
