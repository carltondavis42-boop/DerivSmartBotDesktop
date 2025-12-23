using System;
using System.Linq;
using System.Windows;

namespace DerivSmartBotDesktop.Services
{
    public enum ThemeKind
    {
        Dark,
        Light
    }

    public class ThemeService
    {
        public ThemeKind CurrentTheme { get; private set; } = ThemeKind.Light;

        public void ApplyTheme(ThemeKind theme)
        {
            var app = Application.Current;
            if (app == null)
                return;

            var dictionaries = app.Resources.MergedDictionaries;
            var existing = dictionaries.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Themes/"));
            var source = theme == ThemeKind.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";

            var newDict = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

            if (existing != null)
            {
                var index = dictionaries.IndexOf(existing);
                dictionaries.RemoveAt(index);
                dictionaries.Insert(index, newDict);
            }
            else
            {
                dictionaries.Insert(0, newDict);
            }

            CurrentTheme = theme;
        }
    }
}
