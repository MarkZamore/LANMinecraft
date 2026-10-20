using System.Windows;
using System.Windows.Controls;

namespace Minecraft;

public partial class PackLibraryView : UserControl
{
    public event Action? ReturnRequested;
    public event Action<CatalogPack>? MembershipRequested;
    private CatalogPack? _selected;
    private double _catalogOffset;
    public Func<string, bool>? IsAdded { get; set; }
    public bool CanChangeMembership { get; set; } = true;
    public PackLibraryView() => InitializeComponent();
    public void SetCatalog(IEnumerable<CatalogPack> packs)
    {
        CatalogCards.ItemsSource = packs;
        if (_selected is { } selected)
        {
            _selected = packs.FirstOrDefault(p => p.Entry.RelativePath == selected.Entry.RelativePath) ?? selected;
            DetailContent.DataContext = _selected;
        }
        RefreshMembership();
    }
    public void RefreshMembership()
    {
        var added = _selected is not null && IsAdded?.Invoke(_selected.Entry.RelativePath) == true;
        MembershipButton.Content = added ? "Убрать" : "Добавить";
        MembershipButton.Style = TryFindResource(added ? typeof(Button) : "PrimaryButtonStyle") as Style;
        MembershipButton.IsEnabled = CanChangeMembership;
        MembershipButton.ToolTip = CanChangeMembership ? null : "Дождитесь завершения игры, подготовки или передачи мира.";
    }
    public void ShowCatalog()
    {
        DetailScroll.Visibility = Visibility.Collapsed;
        LibraryTopBar.Visibility = Visibility.Visible;
        CatalogScroll.Visibility = Visibility.Visible;
        CatalogScroll.ScrollToVerticalOffset(_catalogOffset);
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
}
