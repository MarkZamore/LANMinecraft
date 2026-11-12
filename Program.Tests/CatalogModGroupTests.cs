using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlyphPath = System.Windows.Shapes.Path;

namespace Minecraft.Tests;

public sealed class CatalogModGroupTests
{
    private static readonly string[] Titles = ["Зрелищные", "Аниме", "Хоррор", "Дополнительные"];

    internal static CatalogPack Fixture(bool grouped = true)
    {
        var pack = new CatalogPack
        {
            Entry = new("grouped-fixture", "Grouped Fixture", new("author", "fixture", "pack-latest")),
            Content = new()
            {
                SchemaVersion = 1, Id = "grouped-fixture", RelativePath = "Grouped Fixture", Name = "Grouped fixture",
                MinecraftVersion = "1.20.1", Loader = "forge", Description = "Metadata-owned categories with normal addon trees.",
                ModsTitle = "Добавленные моды (Остальные в All The Mods 9)",
                VisibleModIds = ["visual", "anime", "horror", "extra", "child"], BaseModIds = ["base"],
                Mods = [Mod("base", "Base"), Mod("helper", "Helper", library: true),
                    Mod("visual", "A long mod name that wraps to another line in a narrow normal mod column"),
                    Mod("anime", "Anime", "base"), Mod("horror", "Horror"), Mod("extra", "Additional"),
                    Mod("child", "Addon", "visual")],
                ModGroups = grouped ? Titles.Select((title, index) => new CatalogModGroup
                    { Title = title, ModIds = [new[] { "visual", "anime", "horror", "extra" }[index]] }).ToList() : null
            },
            Preview = new BitmapImage()
        };
        pack.BuildTree();
        return pack;
    }

    private static CatalogMod Mod(string id, string name, string? parent = null, bool library = false) => new()
        { Id = id, Name = name, ParentId = parent, IsLibrary = library, WebsiteUrl = "https://example.org/projects/" + id };

    [Fact]
    public void GroupedRootsKeepMetadataOrderChildrenAndTheCompleteInventory()
    {
        var pack = Fixture();
        ValidatePublishedContent(pack);
        Assert.Equal(Titles, pack.ModGroups.Select(group => group.Title));
        Assert.Equal(new[] { "visual", "anime", "horror", "extra" }, pack.ModGroups.SelectMany(group => group.Mods).Select(mod => mod.Id));
        Assert.Equal(7, pack.ModCount);
        Assert.Equal(5, pack.DisplayModCount);
        Assert.Equal(4, pack.Mods.Count);
        Assert.Empty(pack.Libraries);
        Assert.Equal("child", Assert.Single(pack.ModGroups[0].Mods[0].Children).Id);
        Assert.All(pack.ModGroups.SelectMany(group => group.Mods), node => Assert.Same(node, pack.Mods.Single(root => root.Id == node.Id)));
        pack.BuildTree();
        Assert.Equal(4, pack.ModGroups.Count);
        Assert.Equal(4, pack.ModGroups.SelectMany(group => group.Mods).Distinct().Count());
    }

    [Fact]
    public void AbsentGroupsKeepFlatCardsAndAnEmptySelectionKeepsNoSections()
    {
        var pack = Fixture(false);
        ValidatePublishedContent(pack);
        Assert.False(pack.HasModGroups);
        Assert.Equal(4, pack.Mods.Count);
        pack.Content.VisibleModIds = [];
        pack.Content.ModGroups = [];
        pack.BuildTree();
        Assert.False(pack.HasModGroups);
        Assert.Empty(pack.Mods);
        Assert.Equal(7, pack.ModCount);
        Assert.Equal(0, pack.DisplayModCount);
    }

    [Fact]
    public void PublishedGroupsRejectDuplicatesHiddenLibrariesChildrenAndIncompleteCoverage()
    {
        foreach (var invalid in new[]
        {
            new List<CatalogModGroup>(),
            [new() { Title = "All", ModIds = ["visual", "anime", "horror", "extra", "missing"] }],
            [new() { Title = "All", ModIds = ["visual", "anime", "horror", "extra", "base"] }],
            [new() { Title = "All", ModIds = ["visual", "anime", "horror", "extra", "helper"] }],
            [new() { Title = "All", ModIds = ["visual", "anime", "horror", "extra", "child"] }],
            [new() { Title = "All", ModIds = ["visual", "anime", "horror", "extra", "visual"] }],
            [new() { Title = "All", ModIds = ["visual", "anime", "horror", "extra"] }, new() { Title = "All", ModIds = [] }],
            [new() { Title = " ", ModIds = ["visual", "anime", "horror", "extra"] }],
            [new() { Title = "All", ModIds = null! }],
            [null!]
        })
        {
            var pack = Fixture();
            pack.Content.ModGroups = invalid;
            var exception = Assert.Throws<TargetInvocationException>(() => ValidatePublishedContent(pack));
            Assert.IsType<InvalidDataException>(exception.InnerException);
        }
    }

    private static void ValidatePublishedContent(CatalogPack pack) => typeof(PackCatalogService)
        .GetMethod("ValidateContent", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [pack.Entry, pack.Content, null]);

    [Fact]
    public void UnknownCountScopesAreRejectedAndAllContentRemainsIndependentOfGroups()
    {
        foreach (var scope in new[] { null, "", "ALL", "native" })
        {
            var invalid = Fixture();
            invalid.Content.ModCountScope = scope!;
            var exception = Assert.Throws<TargetInvocationException>(() => ValidatePublishedContent(invalid));
            Assert.IsType<InvalidDataException>(exception.InnerException);
        }
        var pack = Fixture();
        pack.Content.ModCountScope = "all";
        ValidatePublishedContent(pack);
        pack.BuildTree();
        Assert.Equal(6, pack.DisplayModCount);
        Assert.Equal(7, pack.ModCount);
        Assert.Equal(4, pack.ModGroups.Sum(group => group.Mods.Count));
        pack.Mods[0].IsExpanded = false;
        Assert.Equal(6, pack.DisplayModCount);
        pack.Content.ModGroups = null;
        pack.BuildTree();
        Assert.Equal(6, pack.DisplayModCount);
        Assert.Equal(4, pack.Mods.Count);
    }

    [Theory]
    [InlineData(false, 1d)]
    [InlineData(false, 2d)]
    [InlineData(true, 1d)]
    [InlineData(true, 2d)]
    public void NormalModTreesKeepLinksSeparateAndSizeChevronsToTextInGroupedAndFlatCards(bool grouped, double dpi)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var view = (PackLibraryView)window.FindName("LibraryView");
            foreach (UIElement child in ((Grid)window.FindName("RootGrid")).Children)
                if (child != view) child.Visibility = Visibility.Hidden;
            view.Visibility = Visibility.Visible;
            VisualTreeHelper.SetRootDpi(window, new(dpi, dpi));
            var pack = Fixture(grouped);
            view.SetCatalog([pack]);
            WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
            Assert.Equal(5, pack.DisplayModCount);
            var cards = (ItemsControl)view.FindName("CatalogCards");
            Assert.Single(Descendants<TextBlock>(cards), text => text.Text == "Minecraft 1.20.1 Forge (5 модов)");
            view.ShowDetails(pack);
            WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
            var host = (StackPanel)view.FindName("GroupedModsHost");
            Assert.Single(Descendants<TextBlock>((StackPanel)view.FindName("DetailContent")), text => text.Text == "Minecraft 1.20.1 Forge (5 модов)");
            Assert.Equal(grouped ? Titles : [], host.Children.OfType<TextBlock>().Select(text => text.Text));
            var helperSection = (Expander)view.FindName("LibrariesSection");
            Assert.Equal(Visibility.Collapsed, helperSection.Visibility);
            Assert.All(host.Children.OfType<TextBlock>(), heading =>
            {
                Assert.Equal(window.FindResource("Font.WeightEmphasis"), heading.FontWeight);
                Assert.Equal(window.FindResource("Gap.Above4"), heading.Margin);
            });
            var trees = Descendants<TreeView>(view).ToArray();
            Assert.Equal(4, trees.Length);
            Assert.Equal(4, trees.Select(tree => Assert.IsType<CatalogModNode>(((TreeViewItem)tree.Items[0]).Header).Id).Distinct().Count());
            var parent = Descendants<TreeViewItem>(view).Single(item => item.DataContext is CatalogModNode { Id: "visual" });
            var node = (CatalogModNode)parent.DataContext;
            var toggle = (ToggleButton)parent.Template.FindName("Expander", parent);
            var glyph = (GlyphPath)toggle.Template.FindName("Chevron", toggle);
            var text = Descendants<TextBlock>(parent).First(block => block.Inlines.OfType<Hyperlink>().Any(link => ReferenceEquals(link.DataContext, node)));
            Assert.Equal(text.FontSize, glyph.Height);
            Assert.Equal(text.FontSize, glyph.Width);
            Assert.Equal(Stretch.Fill, glyph.Stretch);
            Assert.Equal(VerticalAlignment.Center, glyph.VerticalAlignment);
            Assert.True(text.ActualHeight > text.FontSize, "The long name must wrap in the normal mod column.");
            Assert.True(toggle.ActualHeight >= (double)window.FindResource("Font.LineHeightBody"));
            var opened = new List<Uri>();
            view.ModPageRequested += opened.Add;
            var link = text.Inlines.OfType<Hyperlink>().Single();
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal(node.WebsiteUri, Assert.Single(opened));
            Assert.False(node.IsExpanded);
            toggle.IsChecked = true;
            WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
            Assert.True(node.IsExpanded);
            Assert.Equal(5, pack.DisplayModCount);
            Assert.Single(opened);
            Assert.Equal(text.FontSize, glyph.ActualHeight, 2);
            var childItem = Descendants<TreeViewItem>(parent).Single();
            var childToggle = (ToggleButton)childItem.Template.FindName("Expander", childItem);
            Assert.Equal(Visibility.Hidden, childToggle.Visibility);
            Assert.Equal(toggle.ActualWidth, childToggle.ActualWidth, 2);
            view.ShowCatalog();
            view.ShowDetails(pack);
            Assert.True(node.IsExpanded);
            return true;
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
