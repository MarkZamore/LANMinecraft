using System.IO;
using System.Reflection;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The preset button has to offer itself the moment there is a layout to
/// apply. Two things used to stop it: the check ran on startup and in the
/// selection handler, which is suppressed when the launcher picks the build
/// itself, and the preset file only arrived with a full pack sync - so a
/// layout published an hour ago was invisible until somebody pressed Play for
/// other reasons.
/// </summary>
public sealed class ControlsPresetWatchTests
{
    [Fact]
    public void TheTimer_AsksAboutThePreset_NotJustTheBuildList()
    {
        var source = ReadWindow();
        var tick = Between(source, "_uiTimer.Tick += (_, _) =>", "};");

        Assert.Contains("RefreshControlsPresetStatus();", tick, StringComparison.Ordinal);
        Assert.True(
            tick.IndexOf("RefreshControlsPresetStatus();", StringComparison.Ordinal) <
            tick.IndexOf("RefreshUi();", StringComparison.Ordinal),
            "the status has to be worked out before it is painted");
    }

    /// <summary>
    /// A build the launcher chose on its own goes through no selection handler,
    /// so RefreshBuilds itself has to write the choice down and look at its
    /// preset.
    /// </summary>
    [Fact]
    public void AnAutomaticChoice_IsSaved_AndItsPresetLookedAt()
    {
        var refreshBuilds = Between(ReadWindow(), "private void RefreshBuilds()", "\n    }");

        Assert.Contains("_settings.ClientRelativePath = selectedBuild.RelativePath;", refreshBuilds, StringComparison.Ordinal);
        Assert.Contains("_settingsService.Save(_settings);", refreshBuilds, StringComparison.Ordinal);
        Assert.Contains("RefreshControlsPresetStatus();", refreshBuilds, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pack's word to the launcher is one small asset; fetching it needs no
    /// launch. The ceiling keeps that promise honest - it must never pull a
    /// pack's jars while a window is opening.
    /// </summary>
    [Fact]
    public void TheLauncherFolder_IsFetchedOnItsOwn_AndOnlyIfItIsSmall()
    {
        var sync = File.ReadAllText(FindRepositoryFile("Program", "PortablePackSyncService.cs"));

        Assert.Contains("public async Task<bool> RefreshLauncherDataAsync(", sync, StringComparison.Ordinal);
        Assert.Contains("LauncherDataPrefix = \"launcher/\"", sync, StringComparison.Ordinal);
        Assert.Contains("MaximumLauncherDataBytes", sync, StringComparison.Ordinal);
        var method = Between(sync, "public async Task<bool> RefreshLauncherDataAsync(", "\n    }");
        Assert.Contains("HasFileWithHash", method, StringComparison.Ordinal);
        Assert.Contains("> MaximumLauncherDataBytes) return false;", method, StringComparison.Ordinal);
    }

    /// <summary>The window asks for it when a build is chosen, by hand or not.</summary>
    [Fact]
    public void TheWindow_AsksForItAtStartupAndOnSelection()
    {
        var source = ReadWindow();
        var loaded = Between(source, "private async void Window_Loaded(", "private async void Window_Closing(");
        var selection = Between(source, "private async void BuildComboBox_SelectionChanged(", "\n    }");

        Assert.Contains("RefreshLauncherDataAsync(", loaded, StringComparison.Ordinal);
        Assert.Contains("RefreshLauncherDataAsync(", selection, StringComparison.Ordinal);
    }

    private static string ReadWindow() => File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));

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
