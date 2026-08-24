using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Keeps the shape of a world handover between runs of the launcher.
///
/// It is a cache and behaves like one: an unreadable or missing file is the
/// built-in guess, and a failed write costs the next transfer a rougher
/// estimate and nothing else. Nothing here is worth interrupting a transfer
/// for, so nothing here throws.
/// </summary>
public sealed class TransferPacingStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;

    public TransferPacingStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = Path.Combine(paths.Personal, "transfer-pacing.json");
    }

    internal TransferPacing Load()
    {
        try
        {
            if (!File.Exists(_file)) return new TransferPacing();
            var weights = JsonSerializer.Deserialize<Dictionary<string, double>>(
                File.ReadAllText(_file), JsonOptions);
            // An empty or nonsense file is no better than none.
            if (weights is null || weights.Count == 0) return new TransferPacing();
            return new TransferPacing(weights);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TransferPacing();
        }
    }

    internal void Save(TransferPacing pacing)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(pacing.Weights, JsonOptions));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }
}
