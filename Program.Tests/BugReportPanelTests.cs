using System.IO;
using System.Linq;

namespace Minecraft.Tests;

/// <summary>
/// "Сообщить о проблеме" answers for itself by what it lets a player touch,
/// not by a line of prose.
/// </summary>
/// <remarks>
/// It went the other way first: nothing in the panel was ever switched off, and
/// the reason went into the status line instead - "Включите Steam и нажмите
/// «Повторить»" under a live button that would not send. That is two things to
/// read and one of them a lie, and it was asked for the other way round. With
/// nobody to send to, the list and the button are simply dead and the list says
/// "Нет игроков в сети" where the names would be. That is the whole of it.
/// </remarks>
public sealed class BugReportPanelTests
{
    /// <summary>
    /// The status line under the report panel is where the sync says what an
    /// update took away, and nothing used to take it back down. A player who
    /// started a different build, or the same one again, went on being told
    /// about mods removed from an update they had already read about - and the
    /// line reads as news about the launch in front of them.
    ///
    /// Cleared at the top of a launch, it falls back to saying the panel is
    /// fine, which is the honest answer when this update changed nothing.
    /// </summary>
    [Fact]
    public void StartingABuild_ClearsWhatTheLastUpdateSaid()
    {
        var launch = Between(ReadWindowCode(), "private async void PlayButton_Click(", "\n    }");

        var cleared = launch.IndexOf("SetBugReportStatus(string.Empty)", StringComparison.Ordinal);
        var synced = launch.IndexOf("SyncAsync(", StringComparison.Ordinal);
        Assert.True(cleared > 0, "A launch no longer clears what the last update said.");
        Assert.True(synced > cleared, "The line is cleared after the sync, so this update's own word is lost.");
    }

    [Fact]
    public void WithNobodyToSendTo_TheListAndTheButtonGoDown()
    {
        var panel = Between(ReadWindowCode(), "internal void RefreshDiagnosticsPanel()", "\n    }");

        Assert.Contains("DiagnosticLogTargetComboBox.IsEnabled = hasRecipient", panel, StringComparison.Ordinal);
        Assert.Contains("SendBugReportButton.IsEnabled = hasRecipient", panel, StringComparison.Ordinal);
        // What a player types survives having nobody to send it to: the text is
        // the report, and losing it to a friend logging off would be its own bug.
        Assert.DoesNotContain("BugReportMessageTextBox.IsEnabled", ReadWindowCode(), StringComparison.Ordinal);
    }

    /// <summary>
    /// And the line under the button says nothing about either of them.
    /// </summary>
    [Fact]
    public void TheStatusLine_NeverExplainsSteamOrAnEmptyList()
    {
        var source = ReadWindowCode();
        var status = Between(source, "private void ShowBugReportStatus()", ";\n");

        Assert.DoesNotContain("Включите Steam", source, StringComparison.Ordinal);
        Assert.DoesNotContain("некому передать", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Некому отправить", source, StringComparison.Ordinal);
        // At rest it still says the panel is fine, because an empty box under a
        // filled one reads as a box that broke - but only while it is fine.
        Assert.Contains("Всё работает :)", status, StringComparison.Ordinal);
        Assert.Contains("string.Empty", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A press that cannot send is a press that does nothing. The two states the
    /// disabled button already stands for return without a word; the ones a
    /// player cannot see coming still leave a line.
    /// </summary>
    [Fact]
    public void ThePressesTheButtonAlreadyRefuses_SayNothing()
    {
        var click = Between(ReadWindowCode(), "private async void SendBugReportButton_Click(", "\n    }");

        Assert.Contains("if (!IsIdentityBound) return;", click, StringComparison.Ordinal);
        Assert.Contains(
            "is not DiagnosticLogTargetOption recipient) return;",
            click,
            StringComparison.Ordinal);
        // The launcher still starting up is not something the panel shows, so
        // that one is still said.
        Assert.Contains("Лаунчер ещё готовится", click, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hint over the empty field stands on the line the first letter takes,
    /// not in the corner above it - the field's own padding past its border.
    /// </summary>
    [Fact]
    public void TheHintOverTheField_StandsOnTheLineOfTheTyping()
    {
        var markup = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml"));
        var hint = Between(markup, "<TextBlock Text=\"Что случилось", "</TextBlock>");

        Assert.Contains("Margin=\"{StaticResource Pad.FieldText}\"", hint, StringComparison.Ordinal);

        // And the field lays its text out once, not twice: handing the padding
        // to the scroll viewer as well pushed the typing off the hint.
        var styles = File.ReadAllText(FindRepositoryFile("Program", "App.xaml"));
        var host = Between(styles, "<ScrollViewer x:Name=\"PART_ContentHost\"", "/>");
        Assert.DoesNotContain("Margin=", host, StringComparison.Ordinal);
    }

    private static string ReadWindowCode() => File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' should still be there");
        var rest = source[from..];
        var to = rest.IndexOf(end, start.Length, StringComparison.Ordinal);
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
