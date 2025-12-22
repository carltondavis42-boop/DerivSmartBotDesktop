using System.Windows;
using System.Windows.Controls;

namespace DerivSmartBotDesktop.Views.Controls
{
    public partial class SymbolTile : UserControl
    {
        public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(string), typeof(SymbolTile));

        public static readonly DependencyProperty HeatProperty =
            DependencyProperty.Register(nameof(Heat), typeof(double), typeof(SymbolTile));

        public static readonly DependencyProperty WinRateProperty =
            DependencyProperty.Register(nameof(WinRate), typeof(double), typeof(SymbolTile));

        public static readonly DependencyProperty RegimeProperty =
            DependencyProperty.Register(nameof(Regime), typeof(string), typeof(SymbolTile));

        public static readonly DependencyProperty LastSignalProperty =
            DependencyProperty.Register(nameof(LastSignal), typeof(string), typeof(SymbolTile));

        public static readonly DependencyProperty VolatilityProperty =
            DependencyProperty.Register(nameof(Volatility), typeof(double), typeof(SymbolTile));

        public SymbolTile()
        {
            InitializeComponent();
        }

        public string Symbol
        {
            get => (string)GetValue(SymbolProperty);
            set => SetValue(SymbolProperty, value);
        }

        public double Heat
        {
            get => (double)GetValue(HeatProperty);
            set => SetValue(HeatProperty, value);
        }

        public double WinRate
        {
            get => (double)GetValue(WinRateProperty);
            set => SetValue(WinRateProperty, value);
        }

        public string Regime
        {
            get => (string)GetValue(RegimeProperty);
            set => SetValue(RegimeProperty, value);
        }

        public string LastSignal
        {
            get => (string)GetValue(LastSignalProperty);
            set => SetValue(LastSignalProperty, value);
        }

        public double Volatility
        {
            get => (double)GetValue(VolatilityProperty);
            set => SetValue(VolatilityProperty, value);
        }
    }
}
