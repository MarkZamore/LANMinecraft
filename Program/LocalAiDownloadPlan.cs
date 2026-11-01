using System.Collections.Frozen;

namespace Minecraft;

/// <summary>A frozen pack-owned recipe and the verified cache inventory used by the shared download budget.</summary>
public sealed class LocalAiDownloadPlan
{
    internal LocalAiDownloadPlan(LocalAiRequirements? requirements, LocalAiRecipe? recipe,
        IReadOnlyList<LocalAiDownloadArtifact> artifacts)
    {
        Requirements = requirements;
        Recipe = recipe;
        Artifacts = artifacts;
        PendingArtifacts = artifacts.Where(file => !file.Cached).ToFrozenDictionary(file => file.Id, file => file.SizeBytes);
    }

    internal LocalAiRequirements? Requirements { get; }
    internal LocalAiRecipe? Recipe { get; }
    internal IReadOnlyList<LocalAiDownloadArtifact> Artifacts { get; }
    public IReadOnlyDictionary<string, long> PendingArtifacts { get; }
}

internal sealed record LocalAiDownloadArtifact(string Id, Uri Uri, long SizeBytes, string Sha256, string RelativePath, bool Cached);
