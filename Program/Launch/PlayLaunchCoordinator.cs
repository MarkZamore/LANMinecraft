namespace Minecraft;

internal delegate Task StartPreparedClient(AppSettings settings, IProgress<RuntimePreparationProgress>? progress,
    LocalAiDownloadPlan localAi, string packHash, CancellationToken token);

/// <summary>Prepares the selected pack and hands its verified state to the game process.</summary>
internal sealed class PlayLaunchCoordinator(
    AppPaths paths,
    SettingsService settingsStore,
    PackPreparationDownloadService downloads,
    PackAutoManifestService autoManifest,
    PackHashService hashes,
    ControlsPresetService controlsPreset,
    Logger logger,
    StartPreparedClient startClient)
{
    private readonly MinimapResetService _minimapReset = new(logger);

    internal async Task LaunchAsync(string relativePath, AppSettings settings, Task launcherDataRefresh,
        IProgress<RuntimePreparationProgress>? progress, Action<string> reportState,
        Func<string, Task> showPreparedPack, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.Equals(settings.ClientRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            settings.ClientRelativePath = relativePath;
            settingsStore.Save(settings);
        }

        // Finish the selected pack's prior metadata write before reserving the
        // same directory for sync, verification and game preparation.
        await launcherDataRefresh.WaitAsync(token);
        using var access = await PackAccess.AcquireAsync(paths, relativePath, token);
        PackPreparationDownloads prepared;
        try
        {
            token.ThrowIfCancellationRequested();
            if (PackAccess.IsRunning(paths, [relativePath], logger))
                throw new InvalidOperationException("Закройте уже запущенную игру этой сборки.");
            prepared = await downloads.PrepareAsync(relativePath, progress, token, access);
        }
        catch when (!token.IsCancellationRequested)
        {
            ReportPreparationFailure(progress);
            throw;
        }
        token.ThrowIfCancellationRequested();
        if (prepared.SyncResult.Warning is { } warning)
        {
            logger.Warn(warning);
            reportState(warning);
        }

        // Explicit descriptors are kept. For a local folder of JARs, infer the runtime once.
        await autoManifest.EnsureAsync(relativePath, token);
        var packDirectory = paths.CombineUnderPacks(relativePath);
        var packHash = await hashes.CalculateAsync(packDirectory, token);
        token.ThrowIfCancellationRequested();
        if (packHash == "missing")
            throw new InvalidOperationException($"Pack validation failed: ./Minecraft/Packs/{relativePath} is missing.");

        if (PackManifestService.Load(packDirectory).NativeRuntime is null)
        {
            var instance = paths.CombineUnderInstances(relativePath);
            var removed = controlsPreset.RemoveDuplicateMappings(instance);
            if (removed > 0) reportState($"Repaired the controls file: {removed} repeated line(s)");
            _minimapReset.Apply(packDirectory, instance);
        }

        token.ThrowIfCancellationRequested();
        await showPreparedPack(packHash).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        // RAM and skin remain editable during downloads. Keep the current settings,
        // but do not pass a different pack's settings with this hash and download plan.
        if (!string.Equals(settings.ClientRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбранная сборка изменилась. Нажмите «Играть» ещё раз.");
        settingsStore.Save(settings);

        reportState("Starting client");
        token.ThrowIfCancellationRequested();
        try
        {
            await startClient(settings, progress, prepared.LocalAi, packHash, token);
        }
        catch when (!token.IsCancellationRequested)
        {
            ReportPreparationFailure(progress);
            throw;
        }
        reportState("Minecraft");
    }

    private static void ReportPreparationFailure(IProgress<RuntimePreparationProgress>? progress) =>
        progress?.Report(new(RuntimePreparationStage.Failed, "Не удалось подготовить сборку"));
}
