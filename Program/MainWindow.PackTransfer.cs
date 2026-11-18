using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Minecraft;

public partial class MainWindow
{
    private readonly ObservableCollection<ClientBuildViewModel> _personalPacks = [];
    private PackTransferService? _packTransfer;
    private bool PackTransferActive => _packTransfer?.IsActive == true;
    private bool TransferInProgress => _transferActive || _transfer?.IsOperationActive == true || PackTransferActive;

    private void InitializePackTransfer()
    {
        var paths = RequirePaths();
        var archive = new PackArchiveService(paths, IsPersonalPack,
            path => _catalog?.IsKnownCatalogPath(path) != false,
            prepareDescriptor: (path, token) => _autoManifest?.EnsureAsync(path, token) ?? Task.CompletedTask);
        _packTransfer = new PackTransferService(paths,
            _peerTransport ?? throw new InvalidOperationException("Steam transport unavailable."),
            archive, ConfirmPackTransferAsync, MayStartPackTransfer, _logger);
        _packTransfer.ProgressChanged += progress => PostToUi(() => ApplyPackTransferProgress(progress));
        _packTransfer.PackReceived += path => PostToUi(() => OnPackReceived(path));
        _packTransfer.IncomingFailed += message => PostToUi(() =>
        {
            if (!_shutdownStarted) NoticeDialog.Show(this, "Не удалось получить сборку", message);
        });
        _peerRouter?.Register(_packTransfer);
    }

    private bool IsPersonalPack(string path) => _catalog?.IsPersonalInstalled(path) == true;

    private bool MayStartPackTransfer() =>
        !Volatile.Read(ref _shutdownStarted) && !Volatile.Read(ref _removingPackFiles) &&
        !Volatile.Read(ref _playRequested) && !Volatile.Read(ref _busy) &&
        !Volatile.Read(ref _transferActive) && _transfer?.IsOperationActive != true &&
        _minecraft is { IsClientRunning: false, IsClientPreparing: false } && IsIdentityBound;

    private Task<bool> ConfirmPackTransferAsync(PackTransferOffer offer, CancellationToken token) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!MayStartPackTransfer()) return false;
            RefreshUi();
            if (!PackTransferConfirmationDialog.Ask(this, offer.SenderName, offer.PackName, offer.ArchiveBytes, token))
                return false;
            if (_libraryVisible) ShowLibrary(false);
            PackTransferExpander.IsExpanded = true;
            PackTransferProgressArea.BringIntoView();
            return true;
        }, System.Windows.Threading.DispatcherPriority.Normal, token).Task;

    private void OnPackReceived(string path)
    {
        if (_shutdownStarted || _settings is null || _settingsService is null) return;
        PackMembership.Add(_settings, path);
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn($"Received pack settings could not be saved: {ex.Message}");
        }
        RefreshBuilds();
        RefreshUi();
        SetState($"Сборка получена: {path}");
    }

    private void RefreshPersonalPacks()
    {
        if (PersonalPackComboBox is null || _paths is null || _catalog is null || !Directory.Exists(_paths.Packs)) return;
        var selected = (PersonalPackComboBox.SelectedItem as ClientBuildViewModel)?.RelativePath;
        var packs = Directory.EnumerateDirectories(_paths.Packs)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(pack => IsPersonalPack(pack.Name))
            .Select(pack => new ClientBuildViewModel
            {
                Name = _displayNames?.Resolve(pack.Name, pack.Name) ?? PackDisplayName.For(pack.Name, pack.Name),
                RelativePath = pack.Name,
                FullPath = pack.Path
            });
        var ordered = ListOrder.Builds(packs).ToArray();
        if (_personalPacks.Count == ordered.Length && _personalPacks.Zip(ordered).All(pair =>
                pair.First.RelativePath.Equals(pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Name.Equals(pair.Second.Name, StringComparison.Ordinal)))
            return;

        _personalPacks.Clear();
        foreach (var pack in ordered) _personalPacks.Add(pack);
        PersonalPackComboBox.SelectedItem = _personalPacks.FirstOrDefault(pack =>
            pack.RelativePath.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? _personalPacks.FirstOrDefault();
    }

    private void PackTransferSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackTransferButton is not null) RefreshPackTransferControls();
    }

    private void RefreshPackTransferControls()
    {
        if (PersonalPackComboBox is null) return;
        var pack = PersonalPackComboBox.SelectedItem as ClientBuildViewModel;
        var peer = PackRecipientComboBox.SelectedItem as PeerViewModel;
        PersonalPackPlaceholderText.Visibility = _personalPacks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PackRecipientPlaceholderText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PersonalPackComboBox.IsEnabled = !PackTransferActive && _personalPacks.Count > 1;
        PackRecipientComboBox.IsEnabled = !PackTransferActive && _peers.Count > 0;
        DeletePersonalPackButton.IsEnabled = pack is not null && CanDeletePackFiles(pack.RelativePath);
        PackTransferButton.IsEnabled = _packTransfer is not null && !PackTransferActive && MayStartPackTransfer() &&
            pack is not null && CanDeletePackFiles(pack.RelativePath) &&
            peer is { IsCompatible: true, IsMinecraftRunning: false, IsMinecraftPreparing: false };
        PackTransferProgressArea.IsEnabled = PackTransferActive || PackTransferButton.IsEnabled;
    }

    private async void PackTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStartPackTransfer() || _packTransfer is null || PackTransferActive ||
            PersonalPackComboBox.SelectedItem is not ClientBuildViewModel pack ||
            !IsPersonalPack(pack.RelativePath) || !CanDeletePackFiles(pack.RelativePath) ||
            PackRecipientComboBox.SelectedItem is not PeerViewModel
                { IsCompatible: true, IsMinecraftRunning: false, IsMinecraftPreparing: false } peer) return;

        var token = _lifetimeCts.Token;
        using var protection = _maintenance?.ProtectPack(pack.RelativePath);
        try
        {
            await _packTransfer.SendAsync(peer, pack.RelativePath, token);
            if (!_shutdownStarted) SetState($"Сборка передана: {pack.Name}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or OperationCanceledException)
        {
            _logger?.Warn($"Pack transfer failed: {ex.Message}");
            if (!_shutdownStarted) NoticeDialog.Show(this, "Не удалось передать сборку", ex.Message);
        }
        finally
        {
            if (!_shutdownStarted) RefreshUi();
        }
    }

    private void ApplyPackTransferProgress(PackTransferProgress progress)
    {
        if (_shutdownStarted) return;
        var active = progress.Phase != PackTransferPhase.Idle;
        var measurable = progress.Phase is PackTransferPhase.Sending or PackTransferPhase.Receiving;
        var fraction = Math.Clamp(progress.Fraction, 0d, 1d);
        SetProgressActivity(PackTransferProgressBar, active);
        PackTransferProgressBar.IsIndeterminate = active && !measurable && progress.Phase != PackTransferPhase.Completed;
        PackTransferProgressBar.Value = measurable ? fraction * 100 : progress.Phase == PackTransferPhase.Completed ? 100 : 0;
        PackTransferProgressText.Text = progress.Phase switch
        {
            PackTransferPhase.Preparing => "Упаковка сборки",
            PackTransferPhase.AwaitingConfirmation => "Ожидание подтверждения",
            PackTransferPhase.Sending => $"Отправка сборки {fraction:P0}",
            PackTransferPhase.Receiving => $"Получение сборки {fraction:P0}",
            PackTransferPhase.Installing => "Проверка и установка сборки",
            PackTransferPhase.Completed => "Передача завершена",
            _ => "В ожидании сборки"
        };
        RefreshUi();
    }
}
