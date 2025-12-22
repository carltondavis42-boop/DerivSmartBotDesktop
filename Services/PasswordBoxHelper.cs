using System.Windows;
using System.Windows.Controls;

namespace DerivSmartBotDesktop.Services
{
    public static class PasswordBoxHelper
    {
        public static readonly DependencyProperty BoundPasswordProperty =
            DependencyProperty.RegisterAttached(
                "BoundPassword",
                typeof(string),
                typeof(PasswordBoxHelper),
                new PropertyMetadata(string.Empty, OnBoundPasswordChanged));

        public static readonly DependencyProperty BindPasswordProperty =
            DependencyProperty.RegisterAttached(
                "BindPassword",
                typeof(bool),
                typeof(PasswordBoxHelper),
                new PropertyMetadata(false, OnBindPasswordChanged));

        private static readonly DependencyProperty IsUpdatingProperty =
            DependencyProperty.RegisterAttached(
                "IsUpdating",
                typeof(bool),
                typeof(PasswordBoxHelper));

        public static string GetBoundPassword(DependencyObject dp) =>
            (string)dp.GetValue(BoundPasswordProperty);

        public static void SetBoundPassword(DependencyObject dp, string value) =>
            dp.SetValue(BoundPasswordProperty, value);

        public static bool GetBindPassword(DependencyObject dp) =>
            (bool)dp.GetValue(BindPasswordProperty);

        public static void SetBindPassword(DependencyObject dp, bool value) =>
            dp.SetValue(BindPasswordProperty, value);

        private static bool GetIsUpdating(DependencyObject dp) =>
            (bool)dp.GetValue(IsUpdatingProperty);

        private static void SetIsUpdating(DependencyObject dp, bool value) =>
            dp.SetValue(IsUpdatingProperty, value);

        private static void OnBindPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
        {
            if (dp is not PasswordBox box)
                return;

            if ((bool)e.OldValue)
                box.PasswordChanged -= HandlePasswordChanged;
            if ((bool)e.NewValue)
                box.PasswordChanged += HandlePasswordChanged;
        }

        private static void OnBoundPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
        {
            if (dp is not PasswordBox box)
                return;

            box.PasswordChanged -= HandlePasswordChanged;

            if (!GetIsUpdating(box))
                box.Password = e.NewValue?.ToString() ?? string.Empty;

            box.PasswordChanged += HandlePasswordChanged;
        }

        private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            SetIsUpdating(box, true);
            SetBoundPassword(box, box.Password);
            SetIsUpdating(box, false);
        }
    }
}
