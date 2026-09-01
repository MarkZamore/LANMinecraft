using System.Windows;

namespace Minecraft;

/// <summary>What the player answered when asked about deleting a build.</summary>
public enum BuildRemovalAnswer
{
    /// <summary>Nothing goes.</summary>
    Keep,

    /// <summary>The build goes, the worlds stay where they are under Worlds.</summary>
    BuildOnly,

    /// <summary>The build goes and its worlds go with it.</summary>
    WithWorlds
}

/// <summary>
/// The one question in the launcher whose answer cannot be taken back, so it is
/// asked in full: the build by name, what will actually be deleted, and how
/// many worlds are at stake - because "вместе с мирами" means nothing to
/// somebody who has forgotten how many worlds that build has.
/// </summary>
/// <remarks>
/// It is a window of ours rather than a MessageBox for two reasons. The system
/// box has no three-way form that is not Yes/No/Cancel, and three buttons that
/// say what they do beat three that have to be explained in the text above
/// them. And a MessageBox is a light system dialog dropped on a dark launcher.
/// </remarks>
public partial class BuildRemovalConfirmationDialog : Window
{
    private BuildRemovalConfirmationDialog(string buildName, int worlds, IReadOnlyList<int> java)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = $"Вы точно хотите удалить {buildName} с компьютера?";
        ExplanationText.Text =
            "Будут удалены файлы сборки, её настройки и всё, что лаунчер для неё готовил." +
            (java.Count > 0
                // Worth a line: it is the largest single thing that goes, and a
                // player who deletes a build to free space should be told they
                // got that too rather than wonder where it went.
                ? $" Java {string.Join(" и ", java)} тоже удалится - её больше не просит ни одна сборка."
                : "");
        WorldsText.Text = worlds switch
        {
            0 => "Миров у этой сборки нет. Сборку можно скачать заново в любой момент.",
            _ => $"{Worlds(worlds)} этой сборки останутся в папке Worlds, если выбрать «Только сборку». " +
                 "«Вместе с мирами» удалит и их, и вернуть их будет неоткуда."
        };
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

    private void WithWorldsButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.WithWorlds);

    private void KeepButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.Keep);

    private void Close(BuildRemovalAnswer answer)
    {
        Answer = answer;
        DialogResult = answer != BuildRemovalAnswer.Keep;
        Close();
    }
}
