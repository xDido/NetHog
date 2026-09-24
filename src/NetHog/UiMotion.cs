using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace NetHog;

internal static class UiMotion
{
    private static bool _buttonFeedbackRegistered;

    public static void RegisterButtonFeedback()
    {
        if (_buttonFeedbackRegistered) return;

        EventManager.RegisterClassHandler(
            typeof(ButtonBase),
            ButtonBase.ClickEvent,
            new RoutedEventHandler(Button_Clicked),
            true);
        _buttonFeedbackRegistered = true;
    }

    public static void Reveal(UIElement element, double distance, int durationMilliseconds)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1;
            return;
        }

        var offset = new TranslateTransform(0, distance);
        element.RenderTransform = offset;
        element.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMilliseconds));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, duration)
        {
            EasingFunction = ease
        });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = ease
        });
    }

    public static void SetSessionIndicator(Ellipse indicator, bool isReady)
    {
        indicator.SetResourceReference(Shape.FillProperty, isReady ? "AccentBrush" : "MutedBrush");

        if (!SystemParameters.ClientAreaAnimation) return;
        indicator.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.68, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    public static void Flash(TextBlock textBlock)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            textBlock.Opacity = 1;
            return;
        }

        textBlock.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void Button_Clicked(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || sender is not ButtonBase { IsEnabled: true } button
            || string.Equals(button.Tag as string, "NoClickMotion", StringComparison.Ordinal)) return;

        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var down = new DoubleAnimation(1, 0.975, TimeSpan.FromMilliseconds(55)) { EasingFunction = ease };
        down.Completed += (_, _) =>
        {
            var up = new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(125))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, up);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
    }
}
