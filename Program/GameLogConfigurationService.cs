using System.IO;
using System.Reflection;
using System.Text;

namespace Minecraft;

/// <summary>
/// Decides how much the game is allowed to log.
///
/// Minecraft's stock configuration writes a full DEBUG copy of everything to
/// <c>logs/debug.log</c>. In a modded pack that is tens of megabytes per
/// session - one chatty mod alone produced 15 MB of a 35 MB file here - and it
/// is only ever read when something has already gone wrong, by which point
/// latest.log and the crash report say the same thing. The launcher writes its
/// own log4j configuration into the instance and points the game at it.
///
/// A pack that ships its own <c>log4j2.xml</c> keeps it: whoever wrote it knew
/// what they wanted from their logs.
///
/// A mod loader may reconfigure log4j after this file is read, so the cleanup
/// that removes debug.log on the next launch is the guarantee; this is the
/// optimisation that keeps it from being written in the first place.
/// </summary>
public sealed class GameLogConfigurationService(Logger? logger = null)
{
    internal const string ConfigurationFileName = "log4j2.xml";
    private const string ResourceName = "Minecraft.GameLogConfiguration.xml";

    /// <summary>Where the file lives inside an instance, next to the other configs.</summary>
    internal static string GetConfigurationPath(string gameDirectory) =>
        Path.Combine(gameDirectory, "config", ConfigurationFileName);

    /// <summary>
    /// Writes the configuration and returns the JVM argument that selects it,
    /// or null when the pack owns its logging or the file cannot be written -
    /// in which case the game simply logs the way it always did.
    /// </summary>
    public string? PrepareArgument(string gameDirectory, string packDirectory)
    {
        try
        {
            if (PackOwnsItsLogging(packDirectory))
            {
                logger?.Info("The pack ships its own log4j2.xml; game logging is left as the pack configured it.");
                return null;
            }

            var path = GetConfigurationPath(gameDirectory);
            var content = ReadEmbeddedConfiguration();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) ||
                !string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            {
                AtomicFile.WriteAllText(path, content);
                logger?.Info($"Game logging configuration written to {path}.");
            }

            // Relative to the game directory, which is the JVM's working
            // directory, so the argument does not carry the player's user name.
            return $"-Dlog4j.configurationFile=config/{ConfigurationFileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger?.Warn($"Game logging configuration could not be written: {ex.Message}");
            return null;
        }
    }

    private static bool PackOwnsItsLogging(string packDirectory) =>
        File.Exists(Path.Combine(packDirectory, ConfigurationFileName)) ||
        File.Exists(Path.Combine(packDirectory, "config", ConfigurationFileName));

    private static string ReadEmbeddedConfiguration()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource is missing: {ResourceName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
