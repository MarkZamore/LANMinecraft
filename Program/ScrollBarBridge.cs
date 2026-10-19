using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Minecraft;

/// <summary>Forwards an external scrollbar's native value and wheel actions to its viewer.</summary>
public static class ScrollBarBridge
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(ScrollViewer), typeof(ScrollBarBridge), new PropertyMetadata(null, TargetChanged));
    private static readonly DependencyProperty ConnectionProperty = DependencyProperty.RegisterAttached(
        "Connection", typeof(Connection), typeof(ScrollBarBridge), new PropertyMetadata(null));

    public static ScrollViewer? GetTarget(DependencyObject owner) => (ScrollViewer?)owner.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject owner, ScrollViewer? value) => owner.SetValue(TargetProperty, value);

    private static void TargetChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ScrollBar scrollbar) throw new ArgumentException("The bridge belongs to a ScrollBar.", nameof(owner));
        (scrollbar.GetValue(ConnectionProperty) as Connection)?.Detach();
        scrollbar.ClearValue(ConnectionProperty);
        if (args.NewValue is ScrollViewer viewer)
            scrollbar.SetValue(ConnectionProperty, new Connection(scrollbar, viewer));
    }

    private sealed class Connection
    {
        private readonly ScrollBar _scrollbar;
        private readonly ScrollViewer _viewer;
        private bool _updatingScrollbar;

        public Connection(ScrollBar scrollbar, ScrollViewer viewer)
        {
            _scrollbar = scrollbar;
            _viewer = viewer;
            scrollbar.ValueChanged += ValueChanged;
            scrollbar.PreviewMouseWheel += MouseWheel;
            viewer.ScrollChanged += ScrollChanged;
            UpdateScrollbar();
        }

        public void Detach()
        {
            _scrollbar.ValueChanged -= ValueChanged;
            _scrollbar.PreviewMouseWheel -= MouseWheel;
            _viewer.ScrollChanged -= ScrollChanged;
        }

        private void ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (!_updatingScrollbar && Math.Abs(_viewer.VerticalOffset - args.NewValue) > 0.01)
                _viewer.ScrollToVerticalOffset(args.NewValue);
        }

        private void ScrollChanged(object sender, ScrollChangedEventArgs args) => UpdateScrollbar();

        private void UpdateScrollbar()
        {
            _updatingScrollbar = true;
            try
            {
                // Standalone ScrollBar commands may replace Value's binding.
                // The view owns synchronization explicitly in both directions.
                _scrollbar.SetCurrentValue(RangeBase.ValueProperty, _viewer.VerticalOffset);
            }
            finally
            {
                _updatingScrollbar = false;
            }
        }

        private void MouseWheel(object sender, MouseWheelEventArgs args)
        {
            var lines = SystemParameters.WheelScrollLines;
            var distance = lines < 0 ? _viewer.ViewportHeight : lines * _scrollbar.SmallChange;
            _viewer.ScrollToVerticalOffset(_viewer.VerticalOffset - args.Delta / 120d * distance);
            args.Handled = true;
        }
    }
}
