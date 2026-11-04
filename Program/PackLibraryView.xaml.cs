using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Collections;
using System.Windows.Media;

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
    private UiPreloadQueue? preloader;
    private bool overviewLoading = true;
    private bool overviewPrepared;
    private bool overviewPreparing;
    private bool disposed;
    private int overviewGeneration;
    private double preparedWidth;
    private DpiScale preparedDpi;
    internal IReadOnlyList<Size> PreparedOverviewCardSizes { get; private set; } = Array.Empty<Size>();
    public UiPreloadQueue? Preloader
    {
        get => preloader;
        set
        {
            if (ReferenceEquals(preloader, value)) return;
            preloader?.Forget("library-overview");
            preloader = value;
            if (!overviewLoading && !overviewPrepared && !disposed) ScheduleOverview();
        }
    }
    public bool IsOverviewReady => !disposed && !overviewLoading && overviewPrepared;
    public bool IsOverviewPreparing => !disposed && overviewPreparing;
    public Func<string, bool>? IsAdded { get; set; }
    public bool CanChangeMembership { get; set; } = true;
    private static string Key(CatalogPack pack) => "detail:" + pack.Entry.Id;
    public PackLibraryView()
    {
        InitializeComponent();
        SmoothScrollingController.Attach(CatalogScroll);
        SizeChanged += (_, _) => InvalidateMeasurements();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _selected is not null) Preloader?.Forget(Key(_selected));
        };
        UpdateOverviewVisibility();
    }
    public new object? FindName(string name) => base.FindName(name) ??
        (_selected is not null && pages.TryGetValue(_selected, out var page) ? page.FindName(name) : null);
    public void SetCatalog(IEnumerable<CatalogPack> values, bool isLoading = false)
    {
        if (disposed) return;
        var next = values.Select(value => packs.FirstOrDefault(old => old.Entry == value.Entry &&
            value.ContentIdentity.Length > 0 && old.ContentIdentity == value.ContentIdentity) ?? value).ToArray();
        var loadingChanged = overviewLoading != isLoading;
        overviewLoading = isLoading;
        if (packs.SequenceEqual(next))
        {
            if (!isLoading && !overviewPrepared && (loadingChanged || !overviewPreparing)) ScheduleOverview();
            RefreshMembership(); UpdateOverviewVisibility(); return;
        }
        Preloader?.Forget("library-overview");
        overviewGeneration++;
        overviewPrepared = false;
        CatalogCards.Visibility = Visibility.Hidden;
        foreach (var old in packs.Except(next).ToArray())
        {
            Preloader?.Forget(Key(old));
            if (pages.Remove(old, out var page)) page.Dispose();
            if (preparation.Remove(old, out var steps)) steps.Dispose();
        }
        packs = next;
        CatalogCards.ItemsSource = next;
        if (_selected is not null && !next.Contains(_selected)) { _selected = null; ShowCatalog(); }
        if (!isLoading) ScheduleOverview();
        RefreshMembership();
        UpdateOverviewVisibility();
    }
    private void ScheduleOverview()
    {
        if (disposed || overviewLoading) return;
        Preloader?.Forget("library-overview");
        var version = ++overviewGeneration;
        overviewPreparing = true;
        overviewPrepared = false;
        UpdateOverviewVisibility();
        if (Preloader is null)
        {
            foreach (var step in PrepareOverview(version)) step();
        }
        else Preloader.Enqueue("library-overview", 20, PrepareOverview(version));
    }
    private IEnumerable<Action> PrepareOverview(int version)
    {
        var detached = false;
        var preparationRoot = new Grid();
        ResourceDictionary? originalResources = null;
        var inheritedOverrides = new List<DependencyProperty>();
        var width = ContentWidth;
        var dpi = VisualTreeHelper.GetDpi(this);
        try
        {
            yield return () =>
            {
                // A collapsed parent never measures its descendants. Move
                // only the hidden overview into its own active layout root,
                // preserving resources and leaving the live parent's slot alone.
                originalResources = CatalogCards.Resources;
                var resources = SnapshotOverviewResources();
                foreach (var property in new[] { Control.FontFamilyProperty, Control.FontSizeProperty, Control.FontWeightProperty,
                    Control.FontStyleProperty, Control.FontStretchProperty, Control.ForegroundProperty, FlowDirectionProperty,
                    UseLayoutRoundingProperty, SnapsToDevicePixelsProperty, TextOptions.TextFormattingModeProperty,
                    TextOptions.TextRenderingModeProperty })
                {
                    if (CatalogCards.ReadLocalValue(property) != DependencyProperty.UnsetValue) continue;
                    var value = CatalogCards.GetValue(property);
                    CatalogCards.SetValue(property, value);
                    inheritedOverrides.Add(property);
                }
                detached = CatalogOverviewHost.Children.Contains(CatalogCards);
                if (detached) CatalogOverviewHost.Children.Remove(CatalogCards);
                CatalogCards.Resources = resources;
                // Removal suspends layout for the complete WPF subtree.
                // Attaching to this new root resumes it before native measure.
                preparationRoot.Children.Add(CatalogCards);
                UiPreparation.Measure(preparationRoot, width, dpi);
            };
            foreach (var step in UiPreparation.Prepare(preparationRoot))
            {
                if (disposed || version != overviewGeneration || overviewLoading) yield break;
                yield return step;
            }
            yield return () =>
            {
                preparationRoot.InvalidateMeasure();
                UiPreparation.Measure(preparationRoot, width, dpi);
                // Retain the native geometry warmed under the independent
                // root; the generated templates stay live after reattachment.
                PreparedOverviewCardSizes = FindOverviewPanel(CatalogCards)?.Children.Cast<FrameworkElement>()
                    .Select(card => card.RenderSize).ToArray() ?? [];
                Restore();
                if (disposed || version != overviewGeneration || overviewLoading) return;
                preparedWidth = width; preparedDpi = dpi;
                overviewPrepared = true;
                overviewPreparing = false;
                UpdateOverviewVisibility();
            };
        }
        finally
        {
            Restore();
            if (version == overviewGeneration) overviewPreparing = false;
        }
        void Restore()
        {
            if (preparationRoot.Children.Contains(CatalogCards)) preparationRoot.Children.Remove(CatalogCards);
            if (originalResources is not null) CatalogCards.Resources = originalResources;
            if (detached && !CatalogOverviewHost.Children.Contains(CatalogCards)) CatalogOverviewHost.Children.Add(CatalogCards);
            foreach (var property in inheritedOverrides) CatalogCards.ClearValue(property);
            inheritedOverrides.Clear();
            detached = false;
        }
    }
    private static LibraryColumnsPanel? FindOverviewPanel(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is LibraryColumnsPanel panel) return panel;
            if (FindOverviewPanel(child) is { } nested) return nested;
        }
        return null;
    }
    private ResourceDictionary SnapshotOverviewResources()
    {
        var result = new ResourceDictionary();
        for (FrameworkElement? element = CatalogCards; element is not null;)
        {
            foreach (DictionaryEntry resource in element.Resources)
                if (!result.Contains(resource.Key)) result.Add(resource.Key, resource.Value);
            element = LogicalTreeHelper.GetParent(element) as FrameworkElement ??
                VisualTreeHelper.GetParent(element) as FrameworkElement;
        }
        if (Application.Current is { } app)
            foreach (DictionaryEntry resource in app.Resources)
                if (!result.Contains(resource.Key)) result.Add(resource.Key, resource.Value);
        return result;
    }
    private void UpdateOverviewVisibility()
    {
        if (DetailHost.Visibility == Visibility.Visible) { CatalogLoadingText.Visibility = Visibility.Collapsed; return; }
        var ready = IsOverviewReady;
        CatalogScroll.Visibility = ready ? Visibility.Visible : Visibility.Hidden;
        CatalogCards.Visibility = ready ? Visibility.Visible : Visibility.Hidden;
        CatalogLoadingText.Text = ready && packs.Length == 0 ? "В библиотеке пока нет сборок." : "Подготавливаем библиотеку…";
        CatalogLoadingText.Visibility = !ready || packs.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        if (_selected is not null) Preloader?.Forget(Key(_selected));
        if (DetailHost.Content is PackDetailPage oldPage) oldPage.Deactivate();
        DetailHost.Content = null;
        DetailHost.Visibility = Visibility.Collapsed;
        LibraryTopBar.Visibility = Visibility.Visible;
        CatalogScroll.Visibility = Visibility.Visible;
        CatalogScroll.ScrollToVerticalOffset(_catalogOffset);
        UpdateOverviewVisibility();
    }
    public void ShowDetails(CatalogPack pack)
    {
        if (disposed || !packs.Contains(pack)) return;
        _catalogOffset = CatalogScroll.VerticalOffset;
        if (_selected is not null) Preloader?.Forget(Key(_selected));
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
        CatalogLoadingText.Visibility = Visibility.Collapsed;
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
        if (disposed) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!overviewLoading && !IsVisible && (!overviewPrepared ||
            Math.Abs(ContentWidth - preparedWidth) > 0.5 || dpi.DpiScaleX != preparedDpi.DpiScaleX || dpi.DpiScaleY != preparedDpi.DpiScaleY))
            ScheduleOverview();
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
        if (disposed) return;
        disposed = true;
        Preloader?.Forget("library-overview");
        foreach (var pack in pages.Keys) { Preloader?.Forget(Key(pack)); Preloader?.Forget("measure:" + pack.Entry.Id); }
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
