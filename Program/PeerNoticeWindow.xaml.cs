using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>
/// One notice about another player, drawn by the launcher in the corner of the
/// screen.
/// </summary>
/// <remarks>
/// Not a Windows notification, and deliberately not. A real one needs the shell
/// to record an identity for the application - two keys under the current user,
/// both naming the path of the executable - and it writes them the first time a
/// notification is shown, whichever library asks for it. The launcher runs from
/// a folder the player chose and is meant to leave that folder and nothing
/// else, so it draws its own instead. The cost is honest and worth saying: this
/// is not kept in the notification centre, and it will not be seen over a game
/// running full screen.
/// </remarks>
public partial class PeerNoticeWindow : Window
{
    /// <summary>How long a notice stands before it goes on its own.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);

    private readonly DispatcherTimer _timer;
    private bool _closing;

    internal PeerNoticeWindow(Window owner, PeerNotice notice)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(notice);
        Owner = owner;
        TitleText.Text = notice.Title;
        BodyText.Text = notice.Body;
        // The launcher's canvas is drawn smaller than its size says, and a
        // notice standing beside it has to be lettered on the same scale.
        Loaded += (_, _) =>
        {
            DialogScale.MatchOwner(this, NoticeSurface);
            // Nothing in the launcher appears at full strength out of nowhere,
            // and a notice least of all: it arrives on its own, over whatever
            // the player was looking at.
            Fade(to: 1, duration: "Motion.NoticeRise", then: null);
        };
        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => Dismiss();
        _timer.Start();
    }

    /// <summary>Takes the notice away, once, whatever asked.</summary>
    internal void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        Fade(to: 0, duration: "Motion.NoticeFade", then: Close);
    }

    /// <summary>
    /// Goes now, without the fade. For the launcher closing: an animation that
    /// outlives the dispatcher never finishes, and the window would be left
    /// standing over an application that has gone.
    /// </summary>
    internal void CloseNow()
    {
        _closing = true;
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Close();
    }

    private void Fade(double to, string duration, Action? then)
    {
        var length = Application.Current?.TryFindResource(duration) as Duration?;
        if (length is null)
        {
            Opacity = to;
            then?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(to, length.Value);
        if (then is not null) animation.Completed += (_, _) => then();
        BeginAnimation(OpacityProperty, animation);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismiss();
}
