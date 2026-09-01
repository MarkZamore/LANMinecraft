using System.Windows;

namespace Minecraft;

/// <summary>
/// Asked once per incoming world. Under Steam the sender can be any friend
/// running the launcher, so a world is never installed without the receiving
/// player saying yes.
/// </summary>
public partial class WorldTransferConfirmationDialog : Window
{
    private WorldTransferConfirmationDialog(WorldTransferOffer offer)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        var sender = string.IsNullOrWhiteSpace(offer.SenderPlayerName) ||
                     string.Equals(offer.SenderPlayerName, offer.SenderPersonaName, StringComparison.Ordinal)
            ? offer.SenderPersonaName
            : $"{offer.SenderPlayerName} ({offer.SenderPersonaName})";
        ExplanationText.Text =
            $"{sender} передаёт вам мир «{offer.WorldName}»" +
            (offer.ArchiveBytes > 0
                ? $", примерно {offer.ArchiveBytes / (1024d * 1024d):F0} МиБ."
                : ".");
        Loaded += (_, _) => DialogScale.MatchOwner(this, TransferPanel);
    }

    public bool Accepted { get; private set; }

    /// <summary>
    /// Closes itself when the token trips, which is how the sender's idle
    /// timeout ends a dialog nobody answered.
    /// </summary>
    public static bool Show(Window? owner, WorldTransferOffer offer, CancellationToken token)
    {
        var dialog = new WorldTransferConfirmationDialog(offer);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        using var registration = token.Register(() =>
            dialog.Dispatcher.BeginInvoke(() =>
            {
                if (dialog.IsLoaded) dialog.Close();
            }));
        dialog.ShowDialog();
        return dialog.Accepted;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e) => Close(accepted: true);

    private void DeclineButton_Click(object sender, RoutedEventArgs e) => Close(accepted: false);

    private void Close(bool accepted)
    {
        Accepted = accepted;
        DialogResult = accepted;
        Close();
    }
}

/// <summary>Runs the confirmation dialog on the UI thread for the transfer service.</summary>
public sealed class WpfWorldTransferConfirmation(Window owner) : IWorldTransferConfirmation
{
    public Task<bool> ConfirmAsync(WorldTransferOffer offer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return owner.Dispatcher
            .InvokeAsync(() => WorldTransferConfirmationDialog.Show(owner, offer, token))
            .Task;
    }
}
