using System.IO;
using System.Linq;

namespace Minecraft.Tests;

/// <summary>
/// What Steam being down is allowed to take away.
/// </summary>
/// <remarks>
/// Steam answers exactly one question here: who this player is. Two things need
/// the answer and cannot be faked without it - a world is sent to somebody, and
/// the game is started as somebody. Everything else in the window was gated on
/// it too, which meant that the moment Steam went down the launcher greyed out
/// the build list, the world list and the player list at once, and looked
/// broken to a player who had opened it because Steam was down.
/// </remarks>
public sealed class SteamGatedControlsTests
{
    [Fact]
    public void PlayAndTransfer_AreWhatWaitForSteam()
    {
        var refresh = RefreshUiBody();

        Assert.Contains(
            "PlayButton.IsEnabled = configurationEnabled && IsIdentityBound",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains("var canTransfer = interactiveEnabled && IsIdentityBound", refresh, StringComparison.Ordinal);
        // The bar under the button follows the button, so it needs no identity
        // of its own - it is already off whenever the transfer cannot happen.
        Assert.Contains("TransferProgressArea.IsEnabled = _transferActive || canTransfer", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingElse_WaitsForSteam()
    {
        var refresh = RefreshUiBody();

        // The one place it used to be decided for the whole window.
        Assert.Contains("var interactiveEnabled = !_busy;", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("!_busy && IsIdentityBound", refresh, StringComparison.Ordinal);

        // Three lists a player reads rather than acts with. Reading who is
        // online, in what, and which worlds are here is most of what the window
        // is for, and none of it needs to know the reader's own name.
        foreach (var line in new[]
                 {
                     "BuildComboBox.IsEnabled = configurationEnabled",
                     "WorldComboBox.IsEnabled = listsEnabled",
                     "OnlinePlayerComboBox.IsEnabled = listsEnabled",
                 })
        {
            Assert.Contains(line, refresh, StringComparison.Ordinal);
        }
    }

    private static string RefreshUiBody()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var from = source.IndexOf("private void RefreshUi()", StringComparison.Ordinal);
        Assert.True(from >= 0, "RefreshUi should still be there");
        var rest = source[from..];
        var to = rest.IndexOf("\n    private void RefreshTransferStatus()", StringComparison.Ordinal);
        return to < 0 ? rest : rest[..to];
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
