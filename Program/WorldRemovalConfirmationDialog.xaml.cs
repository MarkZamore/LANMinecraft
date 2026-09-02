using System.Windows;

namespace Minecraft;

/// <summary>
/// Asks before a world goes, naming it exactly as the list above the button
/// named it - the world and the build it belongs to, because two builds can
/// each have a "New World" and the name alone would not say which one is about
/// to be deleted.
/// </summary>
/// <remarks>
/// A window of ours rather than a MessageBox, for the reason the build's
/// question is: a system box is a light dialog dropped on a dark launcher, and
/// this one has to be read rather than dismissed. A world is the only thing in
/// the launcher that cannot be downloaded again.
/// </remarks>
public partial class WorldRemovalConfirmationDialog : Window
{
    private WorldRemovalConfirmationDialog(string displayName)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = $"Вы точно хотите удалить мир «{displayName}»?";
        ExplanationText.Text =
            "Мир удалится с диска вместе со всем, что в нём построено, и вернуть его будет неоткуда. " +
            "Копия у друга, которому его передавали, останется.";
    }

    /// <summary>True when the player asked for the world to go.</summary>
    public static bool Ask(Window owner, string displayName)
    {
        var dialog = new WorldRemovalConfirmationDialog(displayName) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void KeepButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
