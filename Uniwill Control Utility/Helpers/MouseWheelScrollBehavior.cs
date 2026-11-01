using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Uniwill_Control_Utility.Helpers
{
    public static class MouseWheelScrollBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(MouseWheelScrollBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
        public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element) return;
            if ((bool)e.NewValue)
                element.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
            else
                element.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not FrameworkElement page) return;

            var scrollViewer = page.FindName("mainScroll") as ScrollViewer;

            // A viewer that cannot scroll is not the target: fall through to the
            // ancestor/descendant search instead of dead-ending (the page's own
            // ScrollViewer can be viewport-sized on a page the shell measures
            // tall, in which case the shell's scroller is the one to move).
            if (scrollViewer is not null && scrollViewer.ScrollableHeight <= 0)
                scrollViewer = null;

            if (scrollViewer is null && page.FindName("lbGames") is DependencyObject gamesList)
                scrollViewer = FindScrollableDescendant(gamesList);

            scrollViewer ??= FindScrollableAncestor(e.OriginalSource as DependencyObject);
            scrollViewer ??= FindScrollableDescendant(page);

            if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
            {
                Serilog.Log.Debug("[Wheel] no scrollable target (source {Source})", e.OriginalSource?.GetType().Name);
                return;
            }

            Serilog.Log.Debug("[Wheel] scrolling {Name} by {Delta} (extent {Extent}, viewport {Viewport})",
                string.IsNullOrEmpty(scrollViewer.Name) ? scrollViewer.GetType().Name : scrollViewer.Name,
                e.Delta, scrollViewer.ExtentHeight, scrollViewer.ViewportHeight);

            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private static ScrollViewer? FindScrollableAncestor(DependencyObject? child)
        {
            while (child is not null)
            {
                if (child is ScrollViewer scrollViewer && scrollViewer.ScrollableHeight > 0)
                    return scrollViewer;

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private static ScrollViewer? FindScrollableDescendant(DependencyObject parent)
        {
            ScrollViewer? bestMatch = null;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var candidate = child as ScrollViewer ?? FindScrollableDescendant(child);
                if (candidate is not null && candidate.ScrollableHeight > (bestMatch?.ScrollableHeight ?? 0))
                    bestMatch = candidate;
            }

            return bestMatch;
        }
    }
}
