using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>A nonactivating native toast; exclusive fullscreen presentation remains controlled by Windows/the game.</summary>
public partial class PeerNoticeWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);
    private readonly DispatcherTimer _timer;
    private HwndSource? _source;
    private Size? _available;
    private bool _closing;
    private bool _nativeClosing;
    private bool _closed;
    private int _fadeGeneration;

    internal PeerNoticeWindow(Window themeSource, PeerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(themeSource);
        ArgumentNullException.ThrowIfNull(notice);
        // Share explicit theme resources, without making this an owned native window.
        // Windows automatically hides owned windows when their owner is minimized.
        Resources = themeSource.Resources;
        InitializeComponent();
        TitleText.Text = notice.Title;
        BodyText.Text = notice.Body;
        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => Dismiss();
        Loaded += (_, _) =>
        {
            Fade(to: 1, duration: "Motion.NoticeRise", then: null);
            _timer.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        PeerNoticeNative.MakeNonactivating(handle);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
    }

    private static IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x21) return IntPtr.Zero; // WM_MOUSEACTIVATE
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE: clicks dismiss without taking the game's focus.
    }

    internal void FitToArea(Size available)
    {
        if (_available == available) return;
        _available = available;
        if (Content is ScrollViewer viewer && ReferenceEquals(viewer.Content, NoticeSurface))
        {
            viewer.Content = null;
            Content = NoticeSurface;
        }
        NoticeSurface.Width = (double)FindResource("Size.NoticeWidth");
        NoticeSurface.MaxWidth = double.PositiveInfinity;
        DialogScale.FitToClientArea(this, NoticeSurface, available);
        UpdateLayout();
    }

    internal void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        Fade(to: 0, duration: "Motion.NoticeFade", then: Close);
    }

    internal void CloseNow()
    {
        if (_nativeClosing || _closed) return;
        _closing = true;
        _timer.Stop();
        CancelFade();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _nativeClosing = true;
        _closing = true;
        _timer.Stop();
        CancelFade();
        base.OnClosing(e);
        if (e.Cancel) _nativeClosing = _closing = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _source?.RemoveHook(WindowMessage);
        _source = null;
        base.OnClosed(e);
    }

    private void CancelFade()
    {
        // Removing a clock does not retract Completed events already queued by
        // WPF. Invalidate their callbacks before cancellation can pump rendering.
        _fadeGeneration++;
        BeginAnimation(OpacityProperty, null);
    }

    private void Fade(double to, string duration, Action? then)
    {
        if (_nativeClosing || _closed) return;
        var generation = ++_fadeGeneration;
        void Complete()
        {
            if (generation != _fadeGeneration || _nativeClosing || _closed) return;
            _fadeGeneration++;
            then?.Invoke();
        }
        var length = TryFindResource(duration) as Duration?;
        if (length is null || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
            Complete();
            return;
        }
        var animation = new DoubleAnimation(to, length.Value);
        if (then is not null) animation.Completed += (_, _) => Complete();
        BeginAnimation(OpacityProperty, animation);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismiss();
}
