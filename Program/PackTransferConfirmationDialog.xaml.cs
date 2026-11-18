using System.Windows;

namespace Minecraft;

public partial class PackTransferConfirmationDialog : Window
{
    private PackTransferConfirmationDialog(string sender, string pack, long archiveBytes)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loaded += (_, _) => DialogScale.MatchOwner(this, TransferPanel);
        OfferText.Text = $"{sender} предлагает сборку «{pack}» ({archiveBytes / (1024d * 1024d):0.#} МиБ).";
    }

    internal static bool Ask(Window owner, string sender, string pack, long archiveBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new PackTransferConfirmationDialog(sender, pack, archiveBytes) { Owner = owner };
        using var registration = token.Register(() => dialog.Dispatcher.BeginInvoke(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        }));
        return dialog.ShowDialog() == true && !token.IsCancellationRequested;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
