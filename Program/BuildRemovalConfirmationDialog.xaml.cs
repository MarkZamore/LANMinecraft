using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
    WithWorlds,

    AllBuilds,

    AllBuildsWithWorlds
}

/// <summary>Asks which installed builds and associated worlds to remove.</summary>
public partial class BuildRemovalConfirmationDialog : Window
{
    private BuildRemovalConfirmationDialog(string buildName)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = $"Что сделать со сборкой {buildName}?";
        Loaded += (_, _) =>
        {
            DialogScale.MatchOwner(this, RemovalPanel);
            QuestionText.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(BuildRemovalAnswer.Keep);
        };
    }

    public BuildRemovalAnswer Answer { get; private set; } = BuildRemovalAnswer.Keep;

    /// <summary>Asks, and answers Keep for a dialog closed any other way.</summary>
    internal static BuildRemovalConfirmationDialog Prepared() => new("");

    public static BuildRemovalAnswer Ask(Window? owner, string buildName, bool canDeleteFiles,
        bool personalOnly = false, bool canDeleteAll = true, bool showBatch = true)
    {
        var dialog = DialogPreloadCache.Take(() => new BuildRemovalConfirmationDialog(buildName));
        dialog.Configure(buildName, canDeleteFiles, personalOnly, canDeleteAll, showBatch);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Answer;
    }

    internal void Configure(string buildName, bool canDeleteFiles, bool personalOnly, bool canDeleteAll, bool showBatch = true)
    {
        QuestionText.Text = $"Что сделать со сборкой {buildName}?";
        ListOnlyButton.Visibility = personalOnly ? Visibility.Collapsed : Visibility.Visible;
        AllBuildsButton.Visibility = personalOnly || !showBatch ? Visibility.Collapsed : Visibility.Visible;
        AllWithWorldsButton.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(BuildOnlyButton, personalOnly ? 0 : 2);
        Grid.SetColumn(WithWorldsButton, personalOnly ? 2 : 0);
        Grid.SetRow(WithWorldsButton, personalOnly ? 0 : 2);
        Grid.SetRow(AllWithWorldsButton, personalOnly ? 2 : 4);
        var rows = ((Grid)AllWithWorldsButton.Parent).RowDefinitions;
        rows[3].Height = personalOnly || !showBatch ? new GridLength(0) : (GridLength)FindResource("Size.DialogActionGap");
        BuildOnlyButton.IsEnabled = WithWorldsButton.IsEnabled = canDeleteFiles;
        AllBuildsButton.IsEnabled = AllWithWorldsButton.IsEnabled = canDeleteAll;
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

    private void AllBuildsButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.AllBuilds);

    private void AllWithWorldsButton_Click(object sender, RoutedEventArgs e) => Close(BuildRemovalAnswer.AllBuildsWithWorlds);

    private void Close(BuildRemovalAnswer answer)
    {
        Answer = answer;
        DialogResult = answer != BuildRemovalAnswer.Keep;
    }
}
