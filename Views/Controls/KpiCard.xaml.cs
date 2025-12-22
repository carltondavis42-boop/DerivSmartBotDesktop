using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DerivSmartBotDesktop.Views.Controls
{
    public partial class KpiCard : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(KpiCard));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(string), typeof(KpiCard));

        public static readonly DependencyProperty SubValueProperty =
            DependencyProperty.Register(nameof(SubValue), typeof(string), typeof(KpiCard));

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(KpiCard));

        public KpiCard()
        {
            InitializeComponent();
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Value
        {
            get => (string)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public string SubValue
        {
            get => (string)GetValue(SubValueProperty);
            set => SetValue(SubValueProperty, value);
        }

        public Brush AccentBrush
        {
            get => (Brush)GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }
    }
}
