using System.Windows;

namespace Minecraft;

/// <summary>
/// Asked before the pack's key layout is written over the player's.
/// </summary>
/// <remarks>
/// It was a system MessageBox, which put a light Windows dialog in the system
/// font on top of a dark launcher in Montserrat - and at a size that could not
/// match, because the launcher draws its canvas through a Viewbox and a
/// MessageBox draws at face value. Now it is a window of ours, on the owner's
/// own scale, and it says which file it found the difference in rather than
/// asking the player to take the change on trust.
/// </remarks>
public partial class ControlsPresetConfirmationDialog : Window
{
    private ControlsPresetConfirmationDialog(string? firstDifference)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = "Заменить настройки управления пресетом сборки?";
        DifferenceText.Text = string.IsNullOrWhiteSpace(firstDifference)
            ? "Заменятся только клавиши. Всё остальное в настройках игры останется как есть."
            // The first line the game's file does not have, so "но я же уже
            // применял" has an answer that is not six hundred lines by hand.
            : $"Сейчас расходится: {firstDifference}. Заменятся только клавиши, " +
              "всё остальное в настройках игры останется как есть.";
        Loaded += (_, _) => DialogScale.MatchOwner(this, PresetPanel);
    }

    public bool Replace { get; private set; }

    /// <summary>Asks, and answers no for a dialog closed any other way.</summary>
    public static bool Ask(Window? owner, string? firstDifference)
    {
        var dialog = new ControlsPresetConfirmationDialog(firstDifference);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Replace;
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e) => Close(replace: true);

    private void KeepButton_Click(object sender, RoutedEventArgs e) => Close(replace: false);

    private void Close(bool replace)
    {
        Replace = replace;
        DialogResult = replace;
        Close();
    }
}
