using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minecraft;

/// <summary>One Steam account and the Minecraft UUID its progress lives under.</summary>
public sealed class SteamIdentityBinding
{
    public string SteamId64 { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public Guid PlayerUuid { get; set; }
    public IdentityBindingSource Source { get; set; }
    public DateTimeOffset BoundAtUtc { get; set; }

    /// <summary>The UUID.json value this binding inherited, kept for rollback.</summary>
    public Guid? LegacyPlayerUuid { get; set; }

    /// <summary>Where UUID.json was copied before the first write, if it was.</summary>
    public string? LegacyBackupPath { get; set; }

    /// <summary>Recorded only when the player had to choose between two histories.</summary>
    public IdentityConflictDecision? ConflictDecision { get; set; }
}

public sealed class SteamIdentityDocument
{
    public int SchemaVersion { get; set; } = SteamIdentityStore.CurrentSchemaVersion;
    public List<SteamIdentityBinding> Bindings { get; set; } = [];
}

/// <summary>
/// Owns Minecraft/Personal/steam-identity.json: which Steam account plays as
/// which Minecraft UUID. Reads are strict - an unknown schema or damaged JSON
/// stops the launcher rather than silently rebinding a player to a fresh UUID -
/// and UUID.json is only ever read and backed up, never modified.
/// </summary>
public sealed class SteamIdentityStore(AppPaths paths, Logger? logger = null)
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath => paths.SteamIdentityFile;

    /// <summary>Returns null when no binding file exists yet.</summary>
    public SteamIdentityDocument? TryLoad()
    {
        if (!File.Exists(FilePath)) return null;

        SteamIdentityDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SteamIdentityDocument>(
                File.ReadAllText(FilePath),
                JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IdentityUnavailableException(
                "Файл Minecraft\\Personal\\steam-identity.json повреждён или недоступен. " +
                "Восстановите его из папки Backups или удалите, чтобы создать привязку заново.",
                ex);
        }

        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new IdentityUnavailableException(
                $"Файл Minecraft\\Personal\\steam-identity.json создан другой версией лаунчера " +
                $"(схема {document?.SchemaVersion ?? 0}, ожидается {CurrentSchemaVersion}). " +
                "Обновите лаунчер или удалите этот файл.");
        }

        foreach (var binding in document.Bindings)
        {
            if (!SteamId64.TryNormalize(binding.SteamId64, out var canonical) ||
                binding.PlayerUuid == Guid.Empty)
            {
                throw new IdentityUnavailableException(
                    "В Minecraft\\Personal\\steam-identity.json найдена повреждённая запись привязки.");
            }
            binding.SteamId64 = canonical;
        }

        if (document.Bindings.Select(binding => binding.SteamId64).Distinct(StringComparer.Ordinal).Count() !=
            document.Bindings.Count ||
            document.Bindings.Select(binding => binding.PlayerUuid).Distinct().Count() != document.Bindings.Count)
        {
            throw new IdentityUnavailableException(
                "В Minecraft\\Personal\\steam-identity.json есть повторяющиеся привязки.");
        }

        return document;
    }

    /// <summary>Writes atomically and verifies the result; a no-op when nothing changed.</summary>
    public void Save(SteamIdentityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.SchemaVersion = CurrentSchemaVersion;
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (File.Exists(FilePath) &&
            string.Equals(File.ReadAllText(FilePath), json, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        AtomicFile.WriteAllText(FilePath, json);
        if (TryLoad() is null)
        {
            throw new IdentityUnavailableException(
                "Привязку к Steam не удалось сохранить: файл не читается после записи.");
        }
    }

    /// <summary>
    /// Copies UUID.json into Personal/Backups/Identity before the launcher
    /// first writes a binding derived from it. Returns null when there is
    /// nothing to back up.
    /// </summary>
    public string? BackUpLegacyIdentityFile()
    {
        if (!File.Exists(paths.IdentityFile)) return null;

        Directory.CreateDirectory(paths.IdentityBackups);
        var directory = Path.Combine(
            paths.IdentityBackups,
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var backup = Path.Combine(directory, Path.GetFileName(paths.IdentityFile));
        if (File.Exists(backup)) return backup;

        File.Copy(paths.IdentityFile, backup);
        if (!HashesEqual(paths.IdentityFile, backup))
        {
            throw new IdentityUnavailableException(
                "Резервная копия Minecraft\\Personal\\UUID.json не совпала с оригиналом.");
        }

        logger?.Info($"UUID.json backed up to {backup} before binding it to a Steam account.");
        return backup;
    }

    private static bool HashesEqual(string left, string right)
    {
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
    }
}
