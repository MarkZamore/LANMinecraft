using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length is not (3 or 5 or 6)) throw new ArgumentException(
    "Usage: <LANMinecraft.dll> <application-root> <pack-directory-name> [<pack-source-directory> <game-directory> [<client-jar>]]");
var assemblyPath = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var file = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
    return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Type Type(string name) => assembly.GetType("Minecraft." + name, throwOnError: true)!;
var appRoot = Path.GetFullPath(args[1]);
var packRoot = args.Length >= 5 ? Path.GetFullPath(args[3]) : Path.Combine(appRoot, "Minecraft", "Packs", args[2]);
var gameRoot = args.Length >= 5 ? Path.GetFullPath(args[4]) : Path.Combine(appRoot, "Minecraft", "Personal", "Instances", args[2]);
using var descriptorJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(packRoot, "portable-pack.json")));
var doc = descriptorJson.RootElement;
var loader = doc.GetProperty("loader");
var loaderKind = Enum.Parse(Type("PackLoaderKind"), loader.GetProperty("type").GetString()!, ignoreCase: true);
var loaderVersion = loader.GetProperty("version").GetString();
var loaderDescriptor = Activator.CreateInstance(Type("PackLoaderDescriptor"), loaderKind, loaderVersion);
var version = doc.GetProperty("minecraftVersion").GetString()!;
var descriptor = Activator.CreateInstance(Type("PackRuntimeDescriptor"), 1, version, loaderDescriptor,
    doc.TryGetProperty("clientJar", out var overrideClient) ? overrideClient.GetString() : "", "read-only-probe");
var shared = Path.Combine(appRoot, "Minecraft", "Launcher", "Shared");
var libraries = Path.Combine(shared, "libraries");
var isFabric = loaderKind.ToString() == "Fabric";
var client = args.Length == 6 ? Path.GetFullPath(args[5]) : isFabric
    ? Path.Combine(shared, "versions", version, version + ".jar")
    : Directory.EnumerateFiles(Path.Combine(libraries, "net", "minecraft", "client"), "client-*-srg.jar", SearchOption.AllDirectories)
        .Single(file => Path.GetFileName(file).StartsWith("client-" + version + "-", StringComparison.Ordinal));
if (!File.Exists(client)) throw new FileNotFoundException("The runtime client JAR was not found.", client);
var profile = isFabric ? $"fabric-loader-{loaderVersion}-{version}" : "read-only-probe";
var runtime = Activator.CreateInstance(Type("PreparedRuntime"), packRoot, profile, "java", client, descriptor)!;
Type("PreparedRuntime").GetProperty("LibrariesRoot")!.SetValue(runtime, libraries);
var paths = Activator.CreateInstance(Type("AppPaths"), appRoot)!;
var serviceType = Type("IdentityAdapterMappingService");
var service = Activator.CreateInstance(serviceType, paths)!;
const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
string? fullFailure = null;
try
{
    var names = serviceType.GetMethod("ReadRuntimeMappings", hidden)!.Invoke(null, [runtime])!;
    var tupleType = names.GetType();
    serviceType.GetMethod("BuildEverything", hidden)!.Invoke(service,
        [runtime, gameRoot, tupleType.GetField("Item2")!.GetValue(names), tupleType.GetField("Item1")!.GetValue(names)]);
}
catch (TargetInvocationException exception) { fullFailure = exception.InnerException?.ToString() ?? exception.ToString(); }
var configuration = serviceType.GetMethod("Build")!.Invoke(service, [runtime, gameRoot])!;
var handshake = Type("ForgeHandshakeMappingService").GetMethod("Build", hidden)!.Invoke(null, [runtime, paths]);
Console.WriteLine(JsonSerializer.Serialize(new { fullFailure, configuration, handshake }, new JsonSerializerOptions { WriteIndented = true }));
