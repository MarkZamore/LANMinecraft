using System.Windows;
using System.Windows.Input;

namespace Minecraft;

public enum WorldRemovalAnswer
{
    Keep,
    WorldOnly,
    AllWorlds
}

/// <summary>Asks whether to remove the selected world or all local worlds.</summary>
public partial class WorldRemovalConfirmationDialog : Window
{
    private WorldRemovalConfirmationDialog()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loaded += (_, _) =>
        {
            DialogScale.MatchOwner(this, RemovalPanel);
            QuestionText.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(WorldRemovalAnswer.Keep);
        };
    }

    internal static WorldRemovalConfirmationDialog Prepared() => new();

    public WorldRemovalAnswer Answer { get; private set; }

    public static WorldRemovalAnswer Ask(Window owner, string? displayName, bool canDeleteAll = true)
    {
        var dialog = DialogPreloadCache.Take(Prepared);
        if (owner.IsLoaded) dialog.Owner = owner;
        dialog.QuestionText.Text = displayName is null
            ? "Какие миры удалить?"
            : $"Вы точно хотите удалить мир «{displayName}»?";
        dialog.DeleteButton.IsEnabled = displayName is not null;
        dialog.AllWorldsButton.IsEnabled = canDeleteAll;
        dialog.ShowDialog();
        return dialog.Answer;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => Close(WorldRemovalAnswer.WorldOnly);

    private void AllWorldsButton_Click(object sender, RoutedEventArgs e) => Close(WorldRemovalAnswer.AllWorlds);

    private void Close(WorldRemovalAnswer answer)
    {
        Answer = answer;
        DialogResult = answer != WorldRemovalAnswer.Keep;
    }
}
