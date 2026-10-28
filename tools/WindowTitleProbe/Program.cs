using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

// Read-only snapshot of current production helper results. Do not instantiate
// PackRuntimeService: preparing a runtime would repair/download installation data.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var packs = new List<string>();
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("Every option requires a value.");
    var (key, value) = (args[index], args[index + 1]);
    if (key == "--pack") packs.Add(value);
    else if (key is "--install-root" or "--output") options.Add(key, value);
    else throw new ArgumentException("Unknown snapshot option: " + key);
}
if (!options.TryGetValue("--install-root", out var installArgument) ||
    !options.TryGetValue("--output", out var outputArgument))
    throw new ArgumentException("Usage: --install-root <Minecraft data directory> --output <new artifacts directory> [--pack <name>] ...");
if (packs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != packs.Count)
    throw new ArgumentException("Pack names must be unique.");
var install = Path.GetFullPath(installArgument);
var output = Path.GetFullPath(outputArgument);
if (!Directory.Exists(Path.Combine(install, "Launcher")) || !Directory.Exists(Path.Combine(install, "Packs")))
    throw new DirectoryNotFoundException("The install root must contain Launcher and Packs.");
if (packs.Count == 0)
    packs.AddRange(Directory.EnumerateDirectories(Path.Combine(install, "Packs"))
        .Where(PackManifestService.HasManifest).Select(path => Path.GetFileName(path)!)
        .Order(StringComparer.OrdinalIgnoreCase));
if (packs.Count == 0) throw new InvalidDataException("No installed pack manifests were found.");
if (!output.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("artifacts", StringComparer.OrdinalIgnoreCase) ||
    IsWithin(output, install) || Directory.Exists(output) || File.Exists(output))
    throw new IOException("Use a new artifacts output directory outside the Minecraft data directory.");
var assembly = typeof(PackManifestService).Assembly;
var settingsType = assembly.GetType("Minecraft.WindowTitleSettings", throwOnError: true)!;
var mappingType = assembly.GetType("Minecraft.WindowTitleMappingService", throwOnError: true)!;
var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var nativeTargets = new List<object>();
var snapshots = new List<object>();
var states = new List<object>();
foreach (var pack in packs)
{
    if (pack is "." or ".." || Path.GetFileName(pack) != pack || pack.IndexOfAny(['/', '\\']) >= 0)
        throw new ArgumentException("A pack must be one directory name.");
    var packDirectory = Path.Combine(install, "Packs", pack);
    var manifestPath = Path.Combine(packDirectory, PackManifestService.ManifestFileName);
    var runtimeRoot = Path.Combine(install, "Launcher", "Runtimes", pack);
    var runtimeStatePath = Path.Combine(runtimeRoot, ".portable-runtime.json");
    inputs.Add(manifestPath, Hash(manifestPath));
    inputs.Add(runtimeStatePath, Hash(runtimeStatePath));
    var descriptor = PackManifestService.Load(packDirectory);
    using var runtimeState = JsonDocument.Parse(File.ReadAllText(runtimeStatePath));
    var root = runtimeState.RootElement;
    if (!string.Equals(root.GetProperty("descriptorHash").GetString(), descriptor.DescriptorHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The installed runtime cache does not match the current pack manifest: " + pack);
    var profileId = root.GetProperty("profileId").GetString() ?? throw new InvalidDataException("Missing runtime profile.");
    if (profileId is "." or ".." || Path.GetFileName(profileId) != profileId || profileId.IndexOfAny(['/', '\\']) >= 0)
        throw new InvalidDataException("The cached profile must be one installed version identifier.");
    var launcher = Path.Combine(install, "Launcher");
    var clientRelative = root.GetProperty("clientJarRelativePath").GetString() ?? throw new InvalidDataException("Missing runtime client path.");
    var client = Path.GetFullPath(Path.Combine(launcher, clientRelative.Replace('/', Path.DirectorySeparatorChar)));
    if (Path.IsPathRooted(clientRelative) || !IsWithin(client, launcher) || !File.Exists(client))
        throw new InvalidDataException("The recorded runtime client is not an installed launcher file.");
    var runtime = new PreparedRuntime(runtimeRoot, profileId, string.Empty, client, descriptor)
    {
        LibrariesRoot = Path.Combine(launcher, "Shared", "libraries")
    };
    var gameDirectory = Path.Combine(install, "Personal", "Instances", pack);
    var settings = Invoke(settingsType, "Load", packDirectory, runtime, gameDirectory);
    var enabled = Property<bool>(settings, "Enabled");
    if (!enabled) throw new InvalidDataException("The global title producer is disabled for an installed pack: " + pack);
    if (!string.Equals(Property<string>(settings, "Text"), pack, StringComparison.Ordinal))
        throw new InvalidDataException("The global title does not match the pack directory name: " + pack);
    var configuration = Invoke(mappingType, "Build", runtime, settings);
    var properties = Property<IReadOnlyDictionary<string, string>>(configuration, "Properties");
    var actualTargets = ReadTargets(configuration);
    var target = actualTargets.Single();
    nativeTargets.Add(new
    {
        name = pack, minecraftVersion = descriptor.MinecraftVersion, loaderVersion = descriptor.Loader.Version,
        jarPath = target.JarPath, className = target.ClassName, methodName = properties["createTitleMethods"],
        enabled, title = pack, negativeControl = false
    });
    if (nativeTargets.Count == 1)
        nativeTargets.Add(new
        {
            name = pack + " [disabled control]", minecraftVersion = descriptor.MinecraftVersion, loaderVersion = descriptor.Loader.Version,
            jarPath = target.JarPath, className = target.ClassName, methodName = properties["createTitleMethods"],
            enabled = false, title = pack, negativeControl = true
        });
    states.Add(new { targetRoot = gameDirectory, properties, targets = actualTargets.Select(t => new { jarPath = t.JarPath, className = t.ClassName }).ToArray() });
    snapshots.Add(new
    {
        pack, descriptor.MinecraftVersion, descriptor.Loader, profileId, enabled, expectedTitle = pack,
        resolvedTitle = Property<string>(settings, "Text"), settingsSha256 = Property<string>(settings, "ContentSha256"),
        runtimeStateSha256 = inputs[runtimeStatePath], manifestSha256 = inputs[manifestPath]
    });
}
foreach (var input in inputs)
    if (input.Value.Length == 0 ? File.Exists(input.Key) : Hash(input.Key) != input.Value)
        throw new IOException("A snapshot input changed during observation: " + input.Key);
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
File.WriteAllText(Path.Combine(output, "targets.json"), JsonSerializer.Serialize(nativeTargets, json));
for (var index = 0; index < states.Count; index++)
    File.WriteAllText(Path.Combine(output, "state-" + index + ".json"), JsonSerializer.Serialize(states[index], json));
File.WriteAllText(Path.Combine(output, "snapshot.json"), JsonSerializer.Serialize(new
{
    schemaVersion = 1, observedAtUtc = DateTimeOffset.UtcNow, launcherAssemblySha256 = Hash(assembly.Location),
    runtimePrepared = false, gameStarted = false, networkInitialized = false, packs = snapshots
}, json));
Console.WriteLine("WINDOW_TITLE_HELPER_SNAPSHOT_PASS packs=" + packs.Count);
Console.WriteLine(Path.Combine(output, "targets.json"));

static object Invoke(Type type, string name, params object[] arguments) =>
    type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
static T Property<T>(object instance, string name) => (T)instance.GetType()
    .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;
static (string JarPath, string ClassName)[] ReadTargets(object configuration) =>
    Property<IEnumerable>(configuration, "Targets").Cast<object>()
        .Select(target => (Property<string>(target, "JarPath"), Property<string>(target, "ClassName"))).ToArray();
static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
static bool IsWithin(string path, string directory) => Path.GetFullPath(path).StartsWith(
    Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
    StringComparison.OrdinalIgnoreCase);
