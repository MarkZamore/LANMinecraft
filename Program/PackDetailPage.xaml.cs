using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Minecraft;

public partial class PackDetailPage : UserControl, IDisposable
{
    private readonly CatalogPack pack;
    public event Action? ReturnRequested;
    public event Action<CatalogPack>? MembershipRequested;
    public event Action<Uri>? ProjectPageRequested;
    private readonly List<TreeViewItem> nodes = [];
    private double savedOffset;
    private bool active;
    private bool restoring;
    public void Activate()
    {
        active = true; restoring = true;
        DetailScroll.LayoutUpdated -= RestoreOffset;
        DetailScroll.LayoutUpdated += RestoreOffset;
        DetailScroll.ScrollToVerticalOffset(savedOffset);
    }
    private void RestoreOffset(object? sender, EventArgs e)
    {
        if (!DetailScroll.IsArrangeValid || DetailScroll.ViewportHeight <= 0) return;
        DetailScroll.LayoutUpdated -= RestoreOffset;
        DetailScroll.ScrollToVerticalOffset(Math.Min(savedOffset, DetailScroll.ScrollableHeight));
        restoring = false;
    }
    public void Deactivate()
    {
        if (!active) return;
        if (!restoring) savedOffset = DetailScroll.VerticalOffset;
        active = false; restoring = false;
        DetailScroll.LayoutUpdated -= RestoreOffset;
        SmoothScrollingController.Attach(DetailScroll).Cancel();
    }
    public PackDetailPage(CatalogPack pack)
    {
        this.pack = pack;
        InitializeComponent();
        DetailContent.DataContext = pack;
        SmoothScrollingController.Attach(DetailScroll);
    }
    public IEnumerable<Action> Prepare(double width, DpiScale dpi)
    {
        if (pack.HasModGroups)
        {
            yield return () => ModsList.Visibility = Visibility.Collapsed;
            foreach (var group in pack.ModGroups)
            {
                ItemsControl? list = null;
                yield return () =>
                {
                    // Only headings use the existing auxiliary-section look.
                    // Items below reuse the ordinary mod columns/tree verbatim.
                    var heading = new TextBlock { Text = group.Title };
                    heading.SetResourceReference(TextBlock.FontWeightProperty, "Font.WeightEmphasis");
                    heading.SetResourceReference(MarginProperty, "Gap.Above4");
                    list = new ItemsControl { ItemsPanel = ModsList.ItemsPanel };
                    GroupedModsHost.Children.Add(heading);
                    GroupedModsHost.Children.Add(list);
                };
                foreach (var step in PrepareMods(group.Mods, () => list!.Items)) yield return step;
            }
        }
        else foreach (var step in PrepareMods(pack.Mods, () => ModsList.Items)) yield return step;
        foreach (var node in pack.Libraries) yield return () => LibrariesList.Items.Add(node);
        yield return () => UiPreparation.Measure(this, width, dpi);
        foreach (var node in nodes)
        {
            yield return () =>
            {
                node.ApplyTemplate();
                if (node.Template.FindName("ChildrenReveal", node) is Border { Child: FrameworkElement child } reveal)
                {
                    var available = reveal.Parent is Grid grid ? grid.ColumnDefinitions[Grid.GetColumn(reveal)].ActualWidth : 0;
                    if (available <= 0) available = Math.Max(1, node.ActualWidth - ((GridLength)FindResource("Size.LibraryTreeIndent")).Value);
                    child.Measure(new Size(available, double.PositiveInfinity));
                    child.Arrange(new Rect(0, 0, available, child.DesiredSize.Height));
                }
            };
        }
    }
    private IEnumerable<Action> PrepareMods(IEnumerable<CatalogModNode> roots, Func<ItemCollection> target)
    {
        foreach (var node in roots)
        {
            var tree = new TreeView { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
            // The outer page owns scrolling. Avoid a nested ScrollViewer for every mod.
            var template = new ControlTemplate(typeof(TreeView));
            template.VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter));
            tree.Template = template;
            foreach (var step in AddNode(node, tree.Items)) yield return step;
            yield return () => target().Add(tree);
        }
    }
    private IEnumerable<Action> AddNode(CatalogModNode node, ItemCollection items)
    {
        TreeViewItem? item = null;
        yield return () =>
        {
            item = new TreeViewItem { Header = node, DataContext = node,
                Style = (Style)Resources["LibraryTreeItemStyle"],
                HeaderTemplate = (DataTemplate)Resources[new DataTemplateKey(typeof(CatalogModNode))] };
            items.Add(item); nodes.Add(item);
        };
        foreach (var child in node.Children)
            foreach (var step in AddNode(child, item!.Items)) yield return step;
    }
    public void RefreshMembership(bool added, bool enabled)
    {
        MembershipButton.Content = added ? "Убрать" : "Добавить";
        MembershipButton.Style = TryFindResource(added ? typeof(Button) : "LibraryAddButtonStyle") as Style;
        MembershipButton.IsEnabled = enabled;
        MembershipButton.ToolTip = enabled ? null : "Дождитесь завершения игры, подготовки или передачи мира.";
    }
    private void ReturnCatalog_Click(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke();
    private void Membership_Click(object sender, RoutedEventArgs e) => MembershipRequested?.Invoke(pack);
    private void ModLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { DataContext: CatalogModNode { WebsiteUri: { } uri } })
            ProjectPageRequested?.Invoke(uri);
        e.Handled = true;
    }
    private void PackProjectLink_Click(object sender, RoutedEventArgs e)
    {
        if (pack.WebsiteUri is { } uri) ProjectPageRequested?.Invoke(uri);
        e.Handled = true;
    }
    public void Dispose()
    {
        DetailScroll.LayoutUpdated -= RestoreOffset;
        SmoothScrollingController.Attach(DetailScroll).Dispose();
        GC.SuppressFinalize(this);
    }
}
