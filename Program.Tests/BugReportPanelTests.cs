using System.IO;
using System.Linq;

namespace Minecraft.Tests;

/// <summary>
/// "Сообщить о проблеме" is the way out when something has gone wrong, so
/// nothing in it is ever switched off. It used to grey itself out exactly when
/// it was needed most - no Steam, no friends online - and a grey button says
/// nothing about why. The reason goes into the line under it instead.
/// </summary>
public sealed class BugReportPanelTests
{
    [Fact]
    public void NothingInThePanel_IsEverDisabled()
    {
        var panel = Between(ReadWindowCode(), "internal void RefreshDiagnosticsPanel()", "\n    }");

        Assert.DoesNotContain("DiagnosticLogTargetComboBox.IsEnabled", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("SendBugReportButton.IsEnabled", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("BugReportMessageTextBox.IsEnabled", panel, StringComparison.Ordinal);

        // And no other part of the window reaches in to switch them off either.
        var source = ReadWindowCode();
        foreach (var name in new[] { "DiagnosticLogTargetComboBox", "SendBugReportButton", "BugReportMessageTextBox" })
        {
            Assert.DoesNotContain(name + ".IsEnabled =", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Pressing it when a report cannot go out has to say so. Every early exit
    /// leaves a line behind; a silent return would read as a dead button.
    /// </summary>
    [Fact]
    public void EveryReasonNotToSend_IsSaidOutLoud()
    {
        var click = Between(ReadWindowCode(), "private async void SendBugReportButton_Click(", "\n    }");
        var guards = click.Split("return;", StringSplitOptions.None);

        Assert.True(guards.Length >= 5, "the button answers for itself in every case it cannot send");
        foreach (var guard in guards.Take(guards.Length - 1))
        {
            Assert.Contains("SetBugReportStatus(", guard, StringComparison.Ordinal);
        }
        Assert.Contains("Steam ещё не подключился", click, StringComparison.Ordinal);
        Assert.Contains("Некому отправить отчёт", click, StringComparison.Ordinal);
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
