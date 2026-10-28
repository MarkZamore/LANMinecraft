using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>Exercises the production cache decision with intact, freshly fingerprinted state.</summary>
public sealed class WindowTitleAdapterCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "window-title-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly Type ServiceType = typeof(PortableIdentityAdapterService);

    [Fact]
    public void TitleOnlyDegradedState_IsRetriedEvenWhenEveryFileAndFingerprintMatches()
    {
        var (service, runtime, gameDirectory, state) = CreateState();
        using (service)
        {
            Assert.True(IsReusable(service, runtime, gameDirectory, state));
            Properties(state)["enabled"] = "false";
            Properties(state)["identityHooksEnabled"] = "false";
            Fingerprint(state);
            Assert.False(IsReusable(service, runtime, gameDirectory, state));
        }
    }

    [Fact]
    public void VerifiedSkinsOnlyCore_RemainsReusable()
    {
        var (service, runtime, gameDirectory, state) = CreateState();
        using (service)
        {
            Properties(state)["identityHooksEnabled"] = "false";
            Fingerprint(state);
            Assert.True(IsReusable(service, runtime, gameDirectory, state));
        }
    }

    private (PortableIdentityAdapterService Service, PreparedRuntime Runtime, string GameDirectory, object State) CreateState()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var mapping = Path.Combine(paths.Launcher, "fixture-mappings.txt");
        var target = Path.Combine(paths.Launcher, "fixture-client.jar");
        File.WriteAllText(mapping, "unchanged mapping fixture");
        File.WriteAllText(target, "unchanged target fixture");
        var gameDirectory = Path.Combine(paths.Instances, "Fixture");
        var runtime = new PreparedRuntime(paths.Runtimes, "fixture", "unused-java.exe", target,
            new PackRuntimeDescriptor(1, "26.2", new(PackLoaderKind.NeoForge, "26.2.0.84"), "", "descriptor"));
        var stateType = ServiceType.GetNestedType("IdentityAdapterState", BindingFlags.NonPublic)!;
        var targetType = ServiceType.GetNestedType("IdentityAdapterTargetState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        Set(state, "SchemaVersion", ServiceType.GetField("AdapterCacheGeneration", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue());
        Set(state, "DescriptorHash", runtime.Descriptor.DescriptorHash);
        Set(state, "TargetRoot", gameDirectory);
        Set(state, "AdapterSha256", "current-adapter-hash");
        Set(state, "MappingPath", mapping);
        Set(state, "MappingSizeBytes", new FileInfo(mapping).Length);
        Set(state, "MappingLastWriteUtcTicks", File.GetLastWriteTimeUtc(mapping).Ticks);
        Set(state, "MappingSha256", Hash(mapping));
        Set(state, "Properties", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["windowTitleEnabled"] = "true",
            ["windowTitleText"] = "Fixture",
            ["windowTitleConfigSha256"] = "policy-fingerprint"
        });
        var targetState = Activator.CreateInstance(targetType, nonPublic: true)!;
        Set(targetState, "JarPath", target);
        Set(targetState, "ClassName", "net/minecraft/client/Minecraft");
        Set(targetState, "SizeBytes", new FileInfo(target).Length);
        Set(targetState, "LastWriteUtcTicks", File.GetLastWriteTimeUtc(target).Ticks);
        Set(targetState, "Sha256", Hash(target));
        var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))!;
        targets.Add(targetState);
        Set(state, "Targets", targets);
        Fingerprint(state);
        return (new PortableIdentityAdapterService(paths, new Logger(paths.LogFile)), runtime, gameDirectory, state);
    }

    private static bool IsReusable(PortableIdentityAdapterService service, PreparedRuntime runtime, string gameDirectory, object state) =>
        (bool)ServiceType.GetMethod("IsStateValid", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [state, runtime, gameDirectory, "current-adapter-hash"])!;
    private static Dictionary<string, string> Properties(object state) =>
        (Dictionary<string, string>)state.GetType().GetProperty("Properties")!.GetValue(state)!;
    private static void Fingerprint(object state) => Set(state, "ConfigurationSha256",
        ServiceType.GetMethod("ComputeConfigurationHash", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [state]));
    private static void Set(object instance, string property, object? value) => instance.GetType().GetProperty(property)!.SetValue(instance, value);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public void Dispose() => TempTree.Delete(_root);
}
