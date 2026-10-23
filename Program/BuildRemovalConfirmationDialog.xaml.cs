using System.Windows;

namespace Minecraft;

/// <summary>What the player answered when asked about deleting a build.</summary>
public enum BuildRemovalAnswer
{
    /// <summary>Nothing goes.</summary>
    Keep,

    /// <summary>Only launcher membership goes; all files and settings stay.</summary>
    ListOnly,

    /// <summary>The build goes, the worlds stay where they are under Worlds.</summary>
    BuildOnly,

    /// <summary>The build goes and its worlds go with it.</summary>
    WithWorlds
}

/// <summary>
/// Offers hiding a build or deleting it with or without worlds.
/// </summary>
/// <remarks>
/// It is a window of ours rather than a MessageBox for two reasons. The system
/// box cannot offer the four explicit choices with the launcher's theme.
/// </remarks>
public partial class BuildRemovalConfirmationDialog : Window
{
    private BuildRemovalConfirmationDialog(string buildName)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = $"Что сделать со сборкой {buildName}?";
        Loaded += (_, _) => DialogScale.MatchOwner(this, RemovalPanel);
    }

    public BuildRemovalAnswer Answer { get; private set; } = BuildRemovalAnswer.Keep;

    /// <summary>Asks, and answers Keep for a dialog closed any other way.</summary>
    public static BuildRemovalAnswer Ask(Window? owner, string buildName)
    {
        var dialog = new BuildRemovalConfirmationDialog(buildName);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>«3 мира», «21 мир», «14 миров» - the form the number takes.</summary>
    internal static string Worlds(int count)
    {
        var tens = count % 100;
        var units = count % 10;
        var word = tens is >= 11 and <= 14 ? "миров"
            : units == 1 ? "мир"
            : units is >= 2 and <= 4 ? "мира"
            : "миров";
        return $"{count} {word}";
    }

    private void BuildOnlyButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.BuildOnly);

    private void ListOnlyButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.ListOnly);

    private void WithWorldsButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.WithWorlds);

    private void KeepButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.Keep);

    private void Close(BuildRemovalAnswer answer)
    {
        Answer = answer;
        DialogResult = answer != BuildRemovalAnswer.Keep;
        Close();
    }
}
