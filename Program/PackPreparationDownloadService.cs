using System.IO;
using System.Security.Cryptography;

namespace Minecraft;

internal sealed record PackPreparationDownloads(PackSyncResult SyncResult, LocalAiDownloadPlan LocalAi);

/// <summary>One download budget for a frozen pack update and its declared external artifacts.</summary>
internal sealed class PackPreparationDownloadService(AppPaths paths, PortablePackSyncService sync, LocalAiProvisioningService localAi)
{
    internal PackPreparationDownloadService(AppPaths paths, PortablePackSyncService sync)
        : this(paths, sync, new LocalAiProvisioningService(paths)) { }

    internal async Task<PackPreparationDownloads> PrepareAsync(string relativePath,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        using var plan = await sync.PrepareDownloadPlanAsync(relativePath, progress, token).ConfigureAwait(false);
        var ai = await localAi.PlanDownloadsAsync(plan.MetadataDirectory, plan.ExpectedFileSha256, token).ConfigureAwait(false);
        var downloads = new PackDownloadProgress(progress, plan.PendingBytes, ai.PendingArtifacts);
        var result = await sync.SyncAsync(plan, downloads.PackProgress, token).ConfigureAwait(false);
        if (result.Outcome == PackSyncOutcome.OfflineFallback && plan.ImmediateResult is null)
        {
            // Failed remote transfers do not authorize the remote recipe for
            // the installed version. Re-plan from the coherent local files.
            progress?.Report(new(RuntimePreparationStage.Checking, "Проверка сборки"));
            var directory = paths.CombineUnderPacks(relativePath);
            ai = await localAi.PlanDownloadsAsync(directory, file =>
            {
                var path = LocalAiFiles.Under(directory, file);
                if (!File.Exists(path)) return null;
                using var input = File.OpenRead(path);
                return Convert.ToHexStringLower(SHA256.HashData(input));
            }, token).ConfigureAwait(false);
            downloads = new PackDownloadProgress(progress, 0, ai.PendingArtifacts);
        }
        downloads.MarkPackComplete();
        await localAi.AcquireDownloadsAsync(plan.PackDirectory, ai,
            downloads.ArtifactProgress, downloads.MarkArtifactComplete, token).ConfigureAwait(false);
        return new(result, ai);
    }
}
