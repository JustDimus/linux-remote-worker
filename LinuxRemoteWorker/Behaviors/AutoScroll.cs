using System.Windows;
using System.Windows.Controls;

namespace LinuxRemoteWorker.Behaviors;

/// <summary>
/// Attached behavior: keeps a ScrollViewer pinned to the bottom as content grows,
/// but only while the user is already at the bottom (so manual scroll-up is respected).
/// Usage: behaviors:AutoScroll.ToEnd="True"
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty ToEndProperty =
        DependencyProperty.RegisterAttached(
            "ToEnd", typeof(bool), typeof(AutoScroll),
            new PropertyMetadata(false, OnToEndChanged));

    public static bool GetToEnd(DependencyObject o) => (bool)o.GetValue(ToEndProperty);
    public static void SetToEnd(DependencyObject o, bool value) => o.SetValue(ToEndProperty, value);

    private static void OnToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || (bool)e.NewValue == false)
            return;

        var atBottom = true;

        sv.ScrollChanged += (_, args) =>
        {
            // ExtentHeightChange == 0 means the user scrolled (not content growth)
            if (args.ExtentHeightChange == 0)
                atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 1;
            else if (atBottom)
                sv.ScrollToEnd();
        };
    }
}
