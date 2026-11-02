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
    public event Action<CatalogModNode>? ModPageRequested;
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
        foreach (var node in pack.Mods)
        {
            var tree = new TreeView { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
            // The outer page owns scrolling. Avoid a nested ScrollViewer for every mod.
            var template = new ControlTemplate(typeof(TreeView));
            template.VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter));
            tree.Template = template;
            foreach (var step in AddNode(node, tree.Items)) yield return step;
            yield return () => ModsList.Items.Add(tree);
        }
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
    { if (sender is Hyperlink { DataContext: CatalogModNode node }) ModPageRequested?.Invoke(node); e.Handled = true; }
    public void Dispose()
    {
        DetailScroll.LayoutUpdated -= RestoreOffset;
        SmoothScrollingController.Attach(DetailScroll).Dispose();
        GC.SuppressFinalize(this);
    }
}
