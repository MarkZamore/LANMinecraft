using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Minecraft;

/// <summary>Reflows the same controls without changing their type size or state.</summary>
internal sealed class AdaptiveLauncherLayout
{
    private static readonly ConditionalWeakTable<Window, AdaptiveLauncherLayout> Layouts = new();
    private readonly Window _window;
    private readonly Grid _root;
    private readonly Grid _settings;
    private readonly Grid[] _settingsRows;
    private readonly Grid _footer;
    private readonly Grid _side;
    private readonly Border _wideHost;
    private readonly Border _inlineHost;
    private LayoutMode? _mode;

    private enum LayoutMode { Wide, Narrow, Compact }

    private AdaptiveLauncherLayout(Window window)
    {
        _window = window;
        _root = Find<Grid>("RootGrid");
        _settings = Find<Grid>("SettingsGrid");
        _settingsRows = [Find<Grid>("NameSettingsRow"), Find<Grid>("BuildSettingsRow"),
            Find<Grid>("MemorySettingsRow"), Find<Grid>("PresetSettingsRow")];
        _footer = Find<Grid>("FooterGrid");
        _side = Find<Grid>("SidePanel");
        _wideHost = Find<Border>("WideSideHost");
        _inlineHost = Find<Border>("InlineSideHost");
    }

    /// <summary>Client dimensions, including the canvas margins, in WPF DIPs.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var client)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        // A wide grid's minimum columns can keep its ActualWidth larger than
        // a shrinking window. Read the HWND viewport, never the content's size.
        Apply(window, (client.Right - client.Left) / dpi.DpiScaleX,
            (client.Bottom - client.Top) / dpi.DpiScaleY);
    }

    /// <summary>Also used by finite layout passes that do not create an HWND.</summary>
    public static void Apply(Window window, double clientWidth, double clientHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(clientWidth) || !double.IsFinite(clientHeight) || clientWidth <= 0 || clientHeight <= 0)
            return;
        Layouts.GetValue(window, static owner => new AdaptiveLauncherLayout(owner)).Update(clientWidth, clientHeight);
    }

    private void Update(double clientWidth, double clientHeight)
    {
        var mode = clientWidth >= Number("Size.WideBreakpoint") ? LayoutMode.Wide
            : clientWidth >= Number("Size.CompactBreakpoint") ? LayoutMode.Narrow : LayoutMode.Compact;
        var wide = mode == LayoutMode.Wide;
        var contentWidth = Math.Max(0, clientWidth - _root.Margin.Left - _root.Margin.Right);
        var sideWidth = wide
            ? Math.Min(Math.Clamp(contentWidth / 3, Number("Size.SidePanelMin"), Number("Size.SidePanelMax")),
                Math.Max(0, contentWidth - Number("Size.MainColumnMin")))
            : 0;
        _root.ColumnDefinitions[0].MinWidth = wide ? Number("Size.MainColumnMin") : 0;
        _root.ColumnDefinitions[1].MinWidth = wide ? Number("Size.SidePanelMin") : 0;
        _root.ColumnDefinitions[1].MaxWidth = wide ? Number("Size.SidePanelMax") : double.PositiveInfinity;
        _root.ColumnDefinitions[1].Width = new GridLength(sideWidth);
        if (wide) _footer.ColumnDefinitions[1].Width = new GridLength(sideWidth);

        if (_mode == mode)
        {
            FitReport(clientWidth, clientHeight, sideWidth, wide);
            return;
        }
        _mode = mode;
        var focused = Keyboard.FocusedElement as FrameworkElement;
        var restoreFocus = focused is not null && _side.IsAncestorOf(focused);
        // Only the side-panel container changes parent, and only at a breakpoint.
        // Fields in the main panel never leave their visual tree while editing.
        _wideHost.Child = null;
        _inlineHost.Child = null;
        (wide ? _wideHost : _inlineHost).Child = _side;
        _wideHost.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _inlineHost.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(Find<Grid>("PlayArea"), wide ? 1 : 2);
        Grid.SetColumnSpan(Find<ScrollViewer>("BodyScroll"), wide ? 1 : 2);

        var compact = mode == LayoutMode.Compact;
        _settings.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _settings.RowDefinitions[2].Height = compact ? GridLength.Auto : new GridLength(0);
        _settings.RowDefinitions[3].Height = compact ? GridLength.Auto : new GridLength(0);
        for (var index = 0; index < _settingsRows.Length; index++)
        {
            var row = _settingsRows[index];
            Grid.SetColumn(row, compact ? 0 : index / 2);
            Grid.SetRow(row, compact ? index : index % 2);
            row.Margin = Gap(compact ? (index == 0 ? "Gap.None" : "Gap.Above2") : index switch
            {
                0 => "Gap.Right2", 1 => "Gap.Above2Right2", 2 => "Gap.Left2", _ => "Gap.Left2Above2"
            });
        }

        _footer.ColumnDefinitions[0].Width = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        if (!wide) _footer.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        var update = Find<Grid>("UpdateArea");
        Grid.SetColumnSpan(update, wide ? 1 : 2);
        var motto = Find<StackPanel>("FooterMotto");
        Grid.SetColumnSpan(motto, compact ? 2 : 1);
        var actions = Find<Grid>("FooterActions");
        Grid.SetRow(actions, compact ? 2 : 1);
        Grid.SetColumn(actions, compact ? 0 : 1);
        Grid.SetColumnSpan(actions, compact ? 2 : 1);
        actions.Margin = Gap(compact ? "Gap.Above2" : wide ? "Gap.Left5" : "Gap.Left3");
        FitReport(clientWidth, clientHeight, sideWidth, wide);
        if (restoreFocus) focused!.Focus();
    }

    private void FitReport(double clientWidth, double clientHeight, double sideWidth, bool wide)
    {
        var report = Find<TextBox>("BugReportMessageTextBox");
        var minimum = Number("Size.TextArea");
        // ScrollViewer measures its content at infinite height. Keep the text
        // editor finite, so a long report scrolls inside the field rather than
        // growing the entire page by hundreds of lines.
        var width = Math.Max(0, clientWidth - _root.Margin.Left - _root.Margin.Right);
        var mainWidth = Math.Max(0, width - sideWidth);
        var play = Find<Grid>("PlayArea");
        play.Measure(new Size(mainWidth, double.PositiveInfinity));
        _footer.Measure(new Size(width, double.PositiveInfinity));
        var body = Find<ScrollViewer>("BodyScroll");
        var bodyHeight = Math.Max(0, clientHeight - _root.Margin.Top - _root.Margin.Bottom
            - play.DesiredSize.Height - _footer.DesiredSize.Height - body.Margin.Top - body.Margin.Bottom);
        var inlineHeight = wide ? 0 : _inlineHost.Height + _inlineHost.Margin.Top + _inlineHost.Margin.Bottom;
        var fixedHeight = FixedBodyHeight(mainWidth);
        if (fixedHeight + minimum + inlineHeight > bodyHeight)
            fixedHeight = FixedBodyHeight(Math.Max(0, mainWidth - Number("Size.Scrollbar")));
        report.Height = Math.Max(minimum, bodyHeight - inlineHeight - fixedHeight);
    }

    private double FixedBodyHeight(double width)
    {
        var reportPanel = Find<Grid>("ReportPanel");
        var fixedRows = Find<Grid>("MainPanel").Children.OfType<FrameworkElement>()
            .Where(child => !ReferenceEquals(child, reportPanel))
            .Concat(reportPanel.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) != 2));
        var height = reportPanel.Margin.Top + reportPanel.Margin.Bottom;
        foreach (var row in fixedRows)
        {
            row.Measure(new Size(width, double.PositiveInfinity));
            height += row.DesiredSize.Height;
        }
        var description = reportPanel.Children.OfType<FrameworkElement>().Single(child => Grid.GetRow(child) == 2);
        return height + description.Margin.Top + description.Margin.Bottom;
    }

    private T Find<T>(string name) where T : FrameworkElement =>
        (T)(_window.FindName(name) ?? throw new InvalidOperationException($"Missing layout element: {name}"));
    private double Number(string key) => (double)_window.FindResource(key);
    private Thickness Gap(string key) => (Thickness)_window.FindResource(key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
