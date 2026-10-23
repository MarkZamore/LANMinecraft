using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Minecraft;

public partial class CenteredDropDown : UserControl
{
    private bool _synchronizingSelection;
    private ScrollViewer? _lockedScroll;
    private double _lockedVerticalOffset;
    private double _lockedHorizontalOffset;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(CenteredDropDown),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem),
        typeof(object),
        typeof(CenteredDropDown),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            SelectedItemChanged));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate),
        typeof(DataTemplate),
        typeof(CenteredDropDown),
        new PropertyMetadata(null));

    public static readonly RoutedEvent SelectionChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(SelectionChanged),
        RoutingStrategy.Bubble,
        typeof(SelectionChangedEventHandler),
        typeof(CenteredDropDown));

    public CenteredDropDown()
    {
        ControlMotion.Initialize();
        InitializeComponent();
        Unloaded += (_, _) => DropDownPopup.IsOpen = false;
        Loaded += (_, _) => ControlMotion.TurnChevron(DropDownChevron, DropDownPopup.IsOpen, animate: false);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public event SelectionChangedEventHandler SelectionChanged
    {
        add => AddHandler(SelectionChangedEvent, value);
        remove => RemoveHandler(SelectionChangedEvent, value);
    }

    private static void SelectedItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (CenteredDropDown)sender;
        control._synchronizingSelection = true;
        try
        {
            control.ItemsList.SelectedItem = args.NewValue;
        }
        finally
        {
            control._synchronizingSelection = false;
        }

        var removed = args.OldValue is null ? Array.Empty<object>() : new[] { args.OldValue };
        var added = args.NewValue is null ? Array.Empty<object>() : new[] { args.NewValue };
        control.RaiseEvent(new SelectionChangedEventArgs(SelectionChangedEvent, removed, added));
    }

    private void DropDownButton_Checked(object sender, RoutedEventArgs e)
    {
        DropDownPopup.IsOpen = true;
    }

    private void DropDownButton_Unchecked(object sender, RoutedEventArgs e)
    {
        DropDownPopup.IsOpen = false;
    }

    private void DropDownPopup_Opened(object? sender, EventArgs e)
    {
        LockParentScroll();
        ControlMotion.TurnChevron(DropDownChevron, expanded: true, animate: IsLoaded);
        ItemsList.SelectedItem = SelectedItem;
        if (SelectedItem is not null)
        {
            ItemsList.ScrollIntoView(SelectedItem);
        }
        ItemsList.Focus();
    }

    private void DropDownPopup_Closed(object? sender, EventArgs e)
    {
        UnlockParentScroll();
        ControlMotion.TurnChevron(DropDownChevron, expanded: false, animate: IsLoaded);
        DropDownButton.IsChecked = false;
    }

    private void LockParentScroll()
    {
        UnlockParentScroll();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            _lockedScroll = scroll;
            _lockedVerticalOffset = scroll.VerticalOffset;
            _lockedHorizontalOffset = scroll.HorizontalOffset;
            SmoothScrollingController.Attach(scroll).IsScrollLocked = true;
            scroll.ScrollChanged += RestoreParentScroll;
            break;
        }
    }

    private void UnlockParentScroll()
    {
        if (_lockedScroll is not { } scroll) return;
        SmoothScrollingController.Attach(scroll).IsScrollLocked = false;
        scroll.ScrollChanged -= RestoreParentScroll;
        _lockedScroll = null;
    }

    private void RestoreParentScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_lockedScroll is not { } scroll || !ReferenceEquals(e.OriginalSource, scroll)) return;
        // Keep the viewport fixed for wheel, keyboard and the external scrollbar.
        // The popup has its own ScrollViewer and remains independently scrollable.
        var vertical = Math.Min(_lockedVerticalOffset, scroll.ScrollableHeight);
        var horizontal = Math.Min(_lockedHorizontalOffset, scroll.ScrollableWidth);
        if (Math.Abs(scroll.VerticalOffset - vertical) > 0.01) scroll.ScrollToVerticalOffset(vertical);
        if (Math.Abs(scroll.HorizontalOffset - horizontal) > 0.01) scroll.ScrollToHorizontalOffset(horizontal);
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection)
        {
            return;
        }

        SelectedItem = ItemsList.SelectedItem;
        if (DropDownPopup.IsOpen && e.AddedItems.Count > 0)
        {
            DropDownPopup.IsOpen = false;
            DropDownButton.Focus();
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.ContainerFromElement((DependencyObject)e.OriginalSource) is not ListBoxItem item)
        {
            return;
        }

        SelectedItem = item.DataContext;
        DropDownPopup.IsOpen = false;
        DropDownButton.Focus();
        e.Handled = true;
    }

    private void ControlRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4 ||
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key is Key.Down or Key.Up))
        {
            DropDownButton.IsChecked = !DropDownPopup.IsOpen;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DropDownPopup.IsOpen)
        {
            DropDownPopup.IsOpen = false;
            DropDownButton.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && DropDownPopup.IsOpen)
        {
            DropDownPopup.IsOpen = false;
            DropDownButton.Focus();
            e.Handled = true;
            return;
        }

        if (!DropDownPopup.IsOpen &&
            e.Key is Key.Enter or Key.Space or Key.Down)
        {
            DropDownButton.IsChecked = true;
            e.Handled = true;
        }
    }
}
