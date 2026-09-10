using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace GodjiVpn.Controls;

/// <summary>Полоса заполнения "трафик использовано/лимит" с бегущим бликом — аналог
/// AnimatedTrafficBar в PlansScreen.kt/ConnectScreen.kt: ширина заливки анимируется tween'ом
/// при изменении процента, плюс поверх — бесконечно бегущий блик каждые 1800мс.</summary>
public partial class TrafficBar : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(TrafficBar), new PropertyMetadata(0.0, OnChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TrafficBar), new PropertyMetadata(1.0, OnChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private bool _shimmerStarted;

    public TrafficBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TrafficBar)d).Redraw();

    private void Redraw()
    {
        var totalWidth = ActualWidth;
        if (totalWidth <= 0) return;

        var fraction = Maximum > 0 ? Math.Clamp(Value / Maximum, 0.0, 1.0) : 0.0;
        var targetWidth = totalWidth * fraction;

        var widthAnim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Fill.BeginAnimation(WidthProperty, widthAnim);

        Shimmer.Width = Math.Max(targetWidth * 0.3, 20);

        if (!_shimmerStarted && targetWidth > 0)
        {
            _shimmerStarted = true;
            var travel = targetWidth + Shimmer.Width;
            var shimmerAnim = new DoubleAnimation(-Shimmer.Width, travel, TimeSpan.FromMilliseconds(1800))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            ShimmerTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shimmerAnim);
        }
    }
}
