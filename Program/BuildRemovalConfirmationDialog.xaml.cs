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
/// Distinguishes hiding a build in the launcher from deleting its files and
/// worlds, and names the data affected by the destructive choices.
/// </summary>
/// <remarks>
/// It is a window of ours rather than a MessageBox for two reasons. The system
/// box cannot offer the four explicit choices with the launcher's theme.
/// </remarks>
public partial class BuildRemovalConfirmationDialog : Window
{
    private BuildRemovalConfirmationDialog(string buildName, int worlds, IReadOnlyList<int> java)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = $"Что сделать со сборкой {buildName}?";
        ExplanationText.Text =
            "«Убрать из списка» скроет сборку в лаунчере, сохранив её файлы, миры, Java и настройки. " +
            "«Удалить сборку» удалит её файлы, настройки и всё, что лаунчер для неё готовил." +
            (java.Count > 0
                // Worth a line: it is the largest single thing that goes, and a
                // player who deletes a build to free space should be told they
                // got that too rather than wonder where it went.
                ? $" Java {string.Join(" и ", java)} тоже удалится - её больше не просит ни одна сборка."
                : "");
        WorldsText.Text = worlds switch
        {
            0 => "Миров у этой сборки нет. Сборку можно скачать заново в любой момент.",
            _ => $"{Worlds(worlds)} этой сборки останутся в папке Worlds, если выбрать «Удалить сборку». " +
                 "«Удалить сборку с мирами» удалит и их, и вернуть их будет неоткуда."
        };
        Loaded += (_, _) => DialogScale.MatchOwner(this, RemovalPanel);
    }

    public BuildRemovalAnswer Answer { get; private set; } = BuildRemovalAnswer.Keep;

    /// <summary>Asks, and answers Keep for a dialog closed any other way.</summary>
    public static BuildRemovalAnswer Ask(Window? owner, string buildName, int worlds, IReadOnlyList<int> java)
    {
        var dialog = new BuildRemovalConfirmationDialog(buildName, worlds, java ?? []);
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
