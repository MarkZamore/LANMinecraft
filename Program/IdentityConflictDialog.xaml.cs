using System.Windows;

namespace Minecraft;

/// <summary>
/// Asked exactly once, and only when a machine has two histories: a legacy
/// UUID.json that was never bound, and worlds that already hold progress under
/// the profile this Steam account derives. Both stay on disk either way.
/// </summary>
public partial class IdentityConflictDialog : Window
{
    private IdentityConflictDialog(IdentityConflict conflict)
    {
        InitializeComponent();
        var worlds = string.Join(", ", conflict.ConflictingWorlds);
        ExplanationText.Text =
            $"На этом компьютере найден старый профиль игрока (UUID {conflict.LegacyUuid:D}) из " +
            $"Minecraft\\Personal\\UUID.json. Одновременно в мирах ({worlds}) уже есть прогресс, " +
            $"привязанный к вашему аккаунту Steam {conflict.PersonaName} ({conflict.SteamId64}).";
    }

    public IdentityConflictDecision Decision { get; private set; } = IdentityConflictDecision.Cancel;

    public static IdentityConflictDecision Show(Window? owner, IdentityConflict conflict)
    {
        var dialog = new IdentityConflictDialog(conflict);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Decision;
    }

    private void KeepLegacyButton_Click(object sender, RoutedEventArgs e) =>
        Close(IdentityConflictDecision.KeepLegacy);

    private void UseDerivedButton_Click(object sender, RoutedEventArgs e) =>
        Close(IdentityConflictDecision.UseDerived);

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        Close(IdentityConflictDecision.Cancel);

    private void Close(IdentityConflictDecision decision)
    {
        Decision = decision;
        DialogResult = decision != IdentityConflictDecision.Cancel;
        Close();
    }
}

/// <summary>Runs the dialog on the UI thread for the identity service.</summary>
public sealed class WpfIdentityConflictResolver(Window owner) : IIdentityConflictResolver
{
    public Task<IdentityConflictDecision> ResolveAsync(IdentityConflict conflict, CancellationToken token) =>
        owner.Dispatcher.InvokeAsync(() => IdentityConflictDialog.Show(owner, conflict)).Task;
}
