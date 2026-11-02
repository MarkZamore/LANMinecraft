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
        var published = catalog.IndexOf("_catalog = catalog;", StringComparison.Ordinal);
        var builds = catalog.IndexOf("RefreshBuilds();", StringComparison.Ordinal);
        var play = catalog.IndexOf("RefreshUi();", StringComparison.Ordinal);
        var menu = catalog.IndexOf("Enqueue(\"build-menu\"", StringComparison.Ordinal);
        var verified = catalog.IndexOf("await Task.Run(() => catalog.RefreshOnceAsync(token), token);", StringComparison.Ordinal);
        Assert.True(published >= 0 && builds > published && play > builds && menu > play && verified > menu,
            "The checked local launch list and its menu must arrive before the slower online pass.");

        var completedPass = catalog[verified..];
        var refreshedBuilds = completedPass.IndexOf("RefreshBuilds();", StringComparison.Ordinal);
        var readyPlay = completedPass.IndexOf("RefreshUi();", StringComparison.Ordinal);
        var refreshedMenu = completedPass.IndexOf("Enqueue(\"build-menu\"", StringComparison.Ordinal);
        var cards = completedPass.IndexOf("RefreshLibraryCatalog();", StringComparison.Ordinal);
        var news = completedPass.IndexOf("Enqueue(\"news\"", StringComparison.Ordinal);
        Assert.True(refreshedBuilds >= 0 && readyPlay > refreshedBuilds && refreshedMenu > readyPlay &&
                    cards > refreshedMenu && news > cards,
            "Refresh authoritative builds and Play before automatic preparation of hidden views.");
        var library = Method(source, "private void RefreshLibraryCatalog()");
        Assert.Contains("(_catalog.IsReady || _libraryVisible)", library, StringComparison.Ordinal);
        Assert.True(library.IndexOf("(_catalog.IsReady || _libraryVisible)", StringComparison.Ordinal) <
                    library.IndexOf("LibraryView.Preloader =", StringComparison.Ordinal));

        // Play keeps its integrity gate and must not await UI warming.
        var click = Method(source, "private async void PlayButton_Click(");
        Assert.Contains("_catalog?.IsReady != true", click, StringComparison.Ordinal);
        Assert.DoesNotContain("_preload", click, StringComparison.Ordinal);
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
