using System.Windows;

namespace Minecraft;

public partial class RemovalBatchConfirmationDialog : Window
{
    private RemovalBatchConfirmationDialog(int? builds, int? worlds)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        QuestionText.Text = (builds, worlds) switch
        {
            (int buildCount, int worldCount) => $"Удалить сборки ({buildCount}) и их миры ({worldCount})?",
            (int buildCount, null) => $"Удалить сборки ({buildCount})? Миры останутся.",
            (null, int worldCount) => $"Удалить все миры ({worldCount})?",
            _ => throw new ArgumentException("A confirmation needs a removal count.")
        };
        Loaded += (_, _) => DialogScale.MatchOwner(this, RemovalPanel);
    }

    internal static bool Ask(Window owner, int? builds, int? worlds)
    {
        var dialog = new RemovalBatchConfirmationDialog(builds, worlds);
        if (owner.IsLoaded) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
