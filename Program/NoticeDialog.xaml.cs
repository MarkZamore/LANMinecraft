using System.Windows;
using System.Windows.Input;

namespace Minecraft;

/// <summary>
/// Says that something did not work, in the launcher's own window rather than
/// the system's.
/// </summary>
/// <remarks>
/// What stood here was MessageBox.Show, seven times over. A system box is a
/// light dialog dropped on a dark launcher, and it says whatever .NET put in
/// the exception - a player who could not install Java was shown "Access to the
/// path 'C:\...\.java-17.install.3c821788dc2f40a298bb11a8070c9751' is denied",
/// which names a folder that no longer exists and asks nothing of them.
///
/// So the heading says what failed in the player's terms and the detail carries
/// the message underneath it, in the paragraph style the rest of the launcher
/// uses for the small print. The detail is still the exception's own text: it is
/// what a bug report needs, and hiding it would only move the guessing.
/// </remarks>
public partial class NoticeDialog : Window
{
    private NoticeDialog(string heading, string detail)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        HeadingText.Text = heading;
        DetailText.Text = detail;
        // A heading with nothing under it should not leave a gap where the
        // small print would have been.
        if (string.IsNullOrWhiteSpace(detail)) DetailText.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => DialogScale.MatchOwner(this, NoticePanel);
    }

    /// <summary>
    /// Shows <paramref name="heading"/> over <paramref name="detail"/> and waits.
    /// </summary>
    /// <remarks>
    /// The owner is taken only once it is loaded. The first of these fires from
    /// Window_Loaded when the launcher itself failed to start, and a dialog owned
    /// by a window that has not finished opening centres on nothing.
    /// </remarks>
    public static void Show(Window? owner, string heading, string detail)
    {
        var dialog = new NoticeDialog(heading, detail);
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
    }

    // There is no button. Nothing here is a decision, and a dialog that only
    // says what happened is dismissed the way every other window is - by
    // closing it. Esc has to be caught by hand for that, because what used
    // to answer it was the button's IsCancel.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Escape and not Key.Enter) return;
        e.Handled = true;
        Close();
    }
}
