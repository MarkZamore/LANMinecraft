using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Minecraft.Tests;

public sealed class CatalogPresentationTests
{
    private static CatalogPack ModifiedPack()
    {
        var content = JsonSerializer.Deserialize<CatalogDescription>("""
            {
              "id": "derived-pack", "relativePath": "Derived Pack", "name": "Derived Pack",
              "minecraftVersion": "1.20.1", "modsTitle": "Дополнительные моды",
              "baseModIds": ["base", "base_library"],
              "visibleModIds": ["addition", "requested_parent", "requested_child"],
              "mods": [
                { "id": "base", "name": "Base" },
                { "id": "base_library", "name": "Base library", "isLibrary": true },
                { "id": "addition", "name": "Addition", "parentId": "base" },
                { "id": "requested_parent", "name": "Requested parent" },
                { "id": "requested_child", "name": "Requested child", "parentId": "requested_parent" },
                { "id": "dependency", "name": "Automatic dependency" },
                { "id": "nested_helper", "name": "Nested helper", "isLibrary": true },
                { "id": "language_provider", "name": "Language provider", "isLibrary": true, "isLoadedMod": false }
              ]
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var pack = new CatalogPack
        {
            Entry = new CatalogSource(content.Id, content.RelativePath, new PackSyncSource("author", "pack", "pack-latest")),
            Content = content,
            Preview = new BitmapImage()
        };
        pack.BuildTree();
        return pack;
    }

    [Fact]
    public void SelectedAdditionsKeepTheCompleteLoadedCountAndAnAddonOfAHiddenBaseRemainsVisible()
    {
        var pack = ModifiedPack();
        Assert.Equal(7, pack.ModCount);
        Assert.Equal("Minecraft 1.20.1 (7 модов)", pack.VersionLabel);
        Assert.Equal("Дополнительные моды", pack.ModsTitle);
        Assert.Equal(new[] { "Addition", "Requested parent" }, pack.Mods.Select(mod => mod.Name));
        Assert.Equal("Requested child", Assert.Single(pack.Mods[1].Children).Name);
        Assert.Empty(pack.Libraries);
        Assert.False(pack.HasLibraries);
        Assert.Equal(8, pack.Content.Mods.Count);

        pack.BuildTree();
        Assert.Equal(2, pack.Mods.Count);
        Assert.Single(pack.Mods[1].Children);
    }

    [Fact]
    public void ExistingCardsDefaultToTheCompleteTreeAndAnEmptySelectionIsDistinctFromNoSelection()
    {
        var pack = ModifiedPack();
        pack.Content.VisibleModIds = null;
        pack.Content.ModsTitle = new CatalogDescription().ModsTitle;
        pack.BuildTree();
        Assert.Equal("Моды", pack.ModsTitle);
        Assert.Equal(3, pack.Mods.Count);
        Assert.Equal("Addition", Assert.Single(pack.Mods.Single(mod => mod.Name == "Base").Children).Name);
        Assert.Equal(3, pack.Libraries.Count);
        Assert.True(pack.HasLibraries);

        pack.Content.VisibleModIds = [];
        pack.BuildTree();
        Assert.Empty(pack.Mods);
        Assert.Empty(pack.Libraries);
        Assert.Equal(7, pack.ModCount);
    }

    [Fact]
    public void DetailViewBindsThePublishedHeadingAndHidesAnEmptyLibrarySection()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var library = (PackLibraryView)window.FindName("LibraryView");
            var pack = ModifiedPack();
            foreach (UIElement child in ((Grid)window.FindName("RootGrid")).Children)
                if (child != library) child.Visibility = Visibility.Hidden;
            library.Visibility = Visibility.Visible;
            library.ShowDetails(pack);
            WindowCanvasTests.LayoutWindow(window, new Size(1000, 800));
            Assert.Equal("Дополнительные моды", ((TextBlock)library.FindName("ModsHeading")).Text);
            Assert.Equal(Visibility.Collapsed, ((StackPanel)library.FindName("LibrariesSection")).Visibility);
            return true;
        });
    }
}
