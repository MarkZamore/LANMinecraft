using System.Windows;

namespace Minecraft;

/// <summary>
/// Asked before the pack's key layout is written over the player's.
/// </summary>
public partial class ControlsPresetConfirmationDialog : Window
{
    private ControlsPresetConfirmationDialog()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = "Заменить настройки управления пресетом сборки?";
        Loaded += (_, _) => DialogScale.MatchOwner(this, PresetPanel);
    }

    public bool Replace { get; private set; }

    /// <summary>Asks, and answers no for a dialog closed any other way.</summary>
    public static bool Ask(Window? owner)
    {
        var dialog = new ControlsPresetConfirmationDialog();
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
