using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DerivSmartBotDesktop.Views.Controls
{
    public partial class StatusBadge : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge));

        public static readonly DependencyProperty IndicatorBrushProperty =
            DependencyProperty.Register(nameof(IndicatorBrush), typeof(Brush), typeof(StatusBadge));

        public static readonly DependencyProperty BadgeBrushProperty =
            DependencyProperty.Register(nameof(BadgeBrush), typeof(Brush), typeof(StatusBadge));

        public StatusBadge()
        {
            InitializeComponent();
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public Brush IndicatorBrush
        {
            get => (Brush)GetValue(IndicatorBrushProperty);
            set => SetValue(IndicatorBrushProperty, value);
        }

        public Brush BadgeBrush
        {
            get => (Brush)GetValue(BadgeBrushProperty);
            set => SetValue(BadgeBrushProperty, value);
        }
    }
}
