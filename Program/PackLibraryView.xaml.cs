using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Minecraft;

public partial class PackLibraryView : UserControl
{
    public event Action? ReturnRequested;
    public event Action<CatalogPack>? MembershipRequested;
    public event Action<Uri>? ModPageRequested;
    private CatalogPack? _selected;
    private double _catalogOffset;
    private CatalogPack[] packs = [];
    private readonly Dictionary<CatalogPack, PackDetailPage> pages = [];
    private readonly Dictionary<CatalogPack, IEnumerator<Action>> preparation = [];
    public UiPreloadQueue? Preloader { get; set; }
    public Func<string, bool>? IsAdded { get; set; }
    public bool CanChangeMembership { get; set; } = true;
    private static string Key(CatalogPack pack) => "detail:" + pack.Entry.Id;
    public PackLibraryView()
    {
        InitializeComponent();
        SmoothScrollingController.Attach(CatalogScroll);
        SizeChanged += (_, _) => InvalidateMeasurements();
    }
    public new object? FindName(string name) => base.FindName(name) ??
        (_selected is not null && pages.TryGetValue(_selected, out var page) ? page.FindName(name) : null);
    public void SetCatalog(IEnumerable<CatalogPack> values)
    {
        var next = values.Select(value => packs.FirstOrDefault(old => old.Entry == value.Entry &&
            value.ContentIdentity.Length > 0 && old.ContentIdentity == value.ContentIdentity) ?? value).ToArray();
        if (packs.SequenceEqual(next)) { RefreshMembership(); return; }
        foreach (var old in packs.Except(next).ToArray())
        {
            Preloader?.Forget(Key(old));
            if (pages.Remove(old, out var page)) page.Dispose();
            if (preparation.Remove(old, out var steps)) steps.Dispose();
        }
        packs = next;
        CatalogCards.ItemsSource = next;
        if (_selected is not null && !next.Contains(_selected)) { _selected = null; ShowCatalog(); }
        if (Preloader is not null)
        {
            Preloader.Forget("library-overview");
            Preloader.Enqueue("library-overview", 20, PrepareOverview());
            foreach (var pack in next.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Entry.Id, StringComparer.Ordinal))
                Preloader.Enqueue(Key(pack), 80, PreparePage(pack));
        }
        RefreshMembership();
    }
    private IEnumerable<Action> PrepareOverview()
    {
        foreach (var step in UiPreparation.Prepare(CatalogCards)) yield return step;
        yield return () => UiPreparation.Measure(CatalogCards, ContentWidth, System.Windows.Media.VisualTreeHelper.GetDpi(this));
    }
    private double ContentWidth
    {
        get
        {
            var width = ActualWidth > 1 ? ActualWidth : (Parent as FrameworkElement)?.ActualWidth - Margin.Left - Margin.Right ?? 1;
            var gutter = ((GridLength)FindResource("Size.ScrollGutter")).Value;
            return Math.Max(1, width - gutter);
        }
    }
    private PackDetailPage Page(CatalogPack pack)
    {
        if (pages.TryGetValue(pack, out var page)) return page;
        page = new PackDetailPage(pack);
        page.ReturnRequested += ShowCatalog;
        page.MembershipRequested += p => MembershipRequested?.Invoke(p);
        page.ModPageRequested += RequestModPage;
        pages.Add(pack, page);
        preparation.Add(pack, page.Prepare(ContentWidth, System.Windows.Media.VisualTreeHelper.GetDpi(this)).GetEnumerator());
        return page;
    }
    private IEnumerable<Action> PreparePage(CatalogPack pack)
    {
        yield return () => Page(pack);
        while (preparation.TryGetValue(pack, out var steps))
        {
            Action? next = null;
            yield return () =>
            {
                if (steps.MoveNext()) next = steps.Current;
                else { steps.Dispose(); preparation.Remove(pack); }
                next?.Invoke();
            };
        }
    }
    public void RefreshMembership()
    {
        foreach (var (pack, page) in pages)
            page.RefreshMembership(IsAdded?.Invoke(pack.Entry.RelativePath) == true, CanChangeMembership);
    }
    public void ShowCatalog()
    {
        if (DetailHost.Content is PackDetailPage oldPage) oldPage.Deactivate();
        DetailHost.Content = null;
        DetailHost.Visibility = Visibility.Collapsed;
        LibraryTopBar.Visibility = Visibility.Visible;
        CatalogScroll.Visibility = Visibility.Visible;
        CatalogScroll.ScrollToVerticalOffset(_catalogOffset);
    }
    public void ShowDetails(CatalogPack pack)
    {
        _catalogOffset = CatalogScroll.VerticalOffset;
        if (DetailHost.Content is PackDetailPage oldPage) oldPage.Deactivate();
        _selected = pack;
        var page = Page(pack);
        if (Preloader is null)
        {
            foreach (var step in PreparePage(pack)) step();
        }
        else
        {
            Preloader.Enqueue(Key(pack), -1, PreparePage(pack));
            Preloader.Promote(Key(pack));
        }
        DetailHost.Content = page;
        LibraryTopBar.Visibility = Visibility.Collapsed;
        CatalogScroll.Visibility = Visibility.Collapsed;
        DetailHost.Visibility = Visibility.Visible;
        page.Activate();
        RefreshMembership();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasurements();
    }
    private void InvalidateMeasurements()
    {
        if (Preloader is null) return;
        foreach (var (pack, page) in pages)
        {
            var key = "measure:" + pack.Entry.Id;
            Preloader.Forget(key);
            Preloader.Enqueue(key, 90, MeasurePage(page));
        }
    }
    private IEnumerable<Action> MeasurePage(PackDetailPage page)
    {
        yield return () => UiPreparation.Measure(page, ContentWidth, System.Windows.Media.VisualTreeHelper.GetDpi(this));
    }
    public void Dispose()
    {
        foreach (var page in pages.Values) page.Dispose();
        foreach (var steps in preparation.Values) steps.Dispose();
        pages.Clear(); preparation.Clear(); packs = [];
        CatalogCards.ItemsSource = null; DetailHost.Content = null;
        SmoothScrollingController.Attach(CatalogScroll).Dispose();
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
