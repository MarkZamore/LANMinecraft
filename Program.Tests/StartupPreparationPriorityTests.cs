namespace Minecraft.Tests;

public sealed class StartupPreparationPriorityTests
{
    [Fact]
    public void CachedLaunchListWarmsFirstAndVerifiedPlayStatePrecedesAutomaticHiddenPreparation()
    {
        var source = ReadWindow();
        var loaded = Method(source, "private async void Window_Loaded(");
        var painted = loaded.IndexOf("await _firstFrame.Task;", StringComparison.Ordinal);
        Assert.True(painted >= 0 && painted < loaded.IndexOf("_catalogStartup = InitializeCatalogAsync();", StringComparison.Ordinal));
        Assert.DoesNotContain("LibraryView.Preloader =", loaded, StringComparison.Ordinal);
        Assert.DoesNotContain("Enqueue(\"news\"", loaded, StringComparison.Ordinal);

        var catalog = Method(source, "private async Task InitializeCatalogCoreAsync()");
        Assert.Contains("stagedStartup: true", catalog, StringComparison.Ordinal);
        var published = catalog.IndexOf("_catalog = catalog;", StringComparison.Ordinal);
        var builds = catalog.IndexOf("RefreshBuilds();", StringComparison.Ordinal);
        var play = catalog.IndexOf("RefreshUi();", StringComparison.Ordinal);
        var menu = catalog.IndexOf("Enqueue(\"build-menu\"", StringComparison.Ordinal);
        var verified = catalog.IndexOf("await Task.Run(() => catalog.InitializeIndexAsync(token), token);", StringComparison.Ordinal);
        Assert.True(published >= 0 && builds > published && play > builds && menu > play && verified > menu,
            "The lightweight local launch list and its menu must arrive before the online index request.");
        Assert.DoesNotContain("RefreshOnceAsync", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareLibraryAsync", catalog, StringComparison.Ordinal);
        Assert.Contains("PrepareSelectedLaunchAsync();", catalog[..verified], StringComparison.Ordinal);

        var completedPass = catalog[verified..];
        var refreshedBuilds = completedPass.IndexOf("RefreshBuilds();", StringComparison.Ordinal);
        var readyPlay = completedPass.IndexOf("RefreshUi();", StringComparison.Ordinal);
        var refreshedMenu = completedPass.IndexOf("Enqueue(\"build-menu\"", StringComparison.Ordinal);
        Assert.True(refreshedBuilds >= 0 && readyPlay > refreshedBuilds && refreshedMenu > readyPlay &&
                    completedPass.IndexOf("PrepareSelectedLaunchAsync();", StringComparison.Ordinal) > refreshedMenu,
            "Refresh authoritative membership and select its launch task without awaiting library bundles.");
        var preparation = Method(source, "private async Task PrepareSelectedLaunchCoreAsync(");
        Assert.Contains("catalog.EnsureLaunchReadyAsync(path, token)", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareLibraryAsync", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryStartup", preparation, StringComparison.Ordinal);
        var library = Method(source, "private async Task PrepareLibraryCoreAsync(");
        Assert.Contains("catalog.PrepareLibraryAsync(token)", library, StringComparison.Ordinal);
        Assert.Contains("RefreshLibraryCatalog();", library, StringComparison.Ordinal);

        var data = Method(source, "private async Task RefreshLauncherDataCoreAsync(");
        Assert.Contains("await _catalogStartup;", data, StringComparison.Ordinal);
        Assert.Contains("catalog.EnsureLaunchReadyAsync(pack, _catalogLifetimeCts.Token)", data, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => _packSync.RefreshLauncherDataAsync", data, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryStartup", data, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshOnceAsync", data, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareLibraryAsync", data, StringComparison.Ordinal);
        var dataQueue = Method(source, "private Task RefreshLauncherDataAsync()");
        Assert.Contains("_launcherDataByPack.GetValueOrDefault(pack", dataQueue, StringComparison.Ordinal);

        // Play keeps its integrity gate and must not await UI warming.
        var click = Method(source, "private async void PlayButton_Click(");
        Assert.Contains("!SelectedBuildIsReady", click, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog?.IsReady", click, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryStartup", click, StringComparison.Ordinal);
        Assert.DoesNotContain("_preload", click, StringComparison.Ordinal);
        Assert.DoesNotContain("await _launcherDataRefresh;", click, StringComparison.Ordinal);
        Assert.Contains("_launcherDataByPack.GetValueOrDefault(build.RelativePath", click, StringComparison.Ordinal);
        var readiness = source[source.IndexOf("private bool SelectedBuildIsReady", StringComparison.Ordinal)..];
        readiness = readiness[..(readiness.IndexOf(';') + 1)];
        Assert.Contains("catalog.IsLaunchReady(build.RelativePath)", readiness, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog.IsReady", readiness, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNewsRequestCanBypassTheAutomaticPreparationBarrier()
    {
        var click = Method(ReadWindow(), "private void SidePanelToggleButton_Click(");
        Assert.Contains("Enqueue(\"news\"", click, StringComparison.Ordinal);
        Assert.True(click.IndexOf("Enqueue(\"news\"", StringComparison.Ordinal) <
                    click.IndexOf("ShowSidePanel(", StringComparison.Ordinal));
    }

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " must exist");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string ReadWindow()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "Program", "MainWindow.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("The launcher source was not found.");
    }
}
