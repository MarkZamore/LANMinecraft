using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace Minecraft;

public partial class PackLibraryView : UserControl
{
    public event Action? ReturnRequested;
    public event Action<CatalogPack>? MembershipRequested;
    public event Action<Uri>? ModPageRequested;
    public event Action<string>? ScrollSnapshot;
    private CatalogPack? _selected;
    private double _catalogOffset;
    private int _snapshotReasons;
    private bool _snapshotQueued;
    public Func<string, bool>? IsAdded { get; set; }
    public bool CanChangeMembership { get; set; } = true;
    public PackLibraryView()
    {
        InitializeComponent();
        SmoothScrollingController.Attach(CatalogScroll);
        SmoothScrollingController.Attach(DetailScroll);
        IsVisibleChanged += (_, _) => QueueScrollSnapshot(8);
        Loaded += (_, _) => QueueScrollSnapshot(16);
        DetailScroll.Loaded += (_, _) => QueueScrollSnapshot(32);
    }
    public void SetCatalog(IEnumerable<CatalogPack> packs)
    {
        CatalogCards.ItemsSource = packs;
        if (_selected is { } selected)
        {
            _selected = packs.FirstOrDefault(p => p.Entry.RelativePath == selected.Entry.RelativePath);
            DetailContent.DataContext = _selected;
            if (_selected is null) ShowCatalog();
        }
        RefreshMembership();
        QueueScrollSnapshot(1);
    }
    public void RefreshMembership()
    {
        var added = _selected is not null && IsAdded?.Invoke(_selected.Entry.RelativePath) == true;
        MembershipButton.Content = added ? "Убрать" : "Добавить";
        MembershipButton.Style = TryFindResource(added ? typeof(Button) : "LibraryAddButtonStyle") as Style;
        MembershipButton.IsEnabled = CanChangeMembership;
        MembershipButton.ToolTip = CanChangeMembership ? null : "Дождитесь завершения игры, подготовки или передачи мира.";
    }
    public void ShowCatalog()
    {
        DetailScroll.Visibility = Visibility.Collapsed;
        LibraryTopBar.Visibility = Visibility.Visible;
        CatalogScroll.Visibility = Visibility.Visible;
        CatalogScroll.ScrollToVerticalOffset(_catalogOffset);
        QueueScrollSnapshot(2);
    }
    public void ShowDetails(CatalogPack pack)
    {
        _catalogOffset = CatalogScroll.VerticalOffset;
        _selected = pack;
        DetailContent.DataContext = pack;
        LibraryTopBar.Visibility = Visibility.Collapsed;
        CatalogScroll.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        DetailScroll.ScrollToTop();
        RefreshMembership();
        QueueScrollSnapshot(4);
    }
    private void QueueScrollSnapshot(int reason)
    {
        if (ScrollSnapshot is null) return;
        _snapshotReasons |= reason;
        if (_snapshotQueued) return;
        _snapshotQueued = true;
        // Coalesce navigation/visibility events, then observe after layout.
        // No LayoutUpdated/render-frame subscriptions or forced measure pass.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _snapshotQueued = false;
            var reasons = _snapshotReasons;
            _snapshotReasons = 0;
            ScrollSnapshot?.Invoke(LibraryScrollDiagnostics.Capture(this, CatalogScroll, DetailScroll, reasons));
        }));
    }
    private void ReturnLauncher_Click(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke();
    private void ReturnCatalog_Click(object sender, RoutedEventArgs e) => ShowCatalog();
    private void PackCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CatalogPack pack }) ShowDetails(pack);
    }
    private void Membership_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && CanChangeMembership) MembershipRequested?.Invoke(_selected);
    }
    private void ModLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { DataContext: CatalogModNode node }) RequestModPage(node);
        e.Handled = true;
    }
    internal void RequestModPage(CatalogModNode node)
    {
        if (node.WebsiteUri is { } uri) ModPageRequested?.Invoke(uri);
    }
}
