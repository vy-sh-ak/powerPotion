using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerPotion.Host;

/// <summary>
/// Branded startup splash. A separate top-level window is required because the renderer is an
/// HwndHost (airspace): WPF content inside the main window cannot be drawn on top of it.
/// </summary>
public partial class SplashWindow : Window
{
    private readonly DoubleAnimation _bar = new(-68, 210, TimeSpan.FromSeconds(1.1))
    {
        RepeatBehavior = RepeatBehavior.Forever,
    };

    public SplashWindow()
    {
        InitializeComponent();
        BarTransform.BeginAnimation(TranslateTransform.XProperty, _bar);
    }

    public void SetStatus(string message)
    {
        StatusLine.Text = message;
    }
}
