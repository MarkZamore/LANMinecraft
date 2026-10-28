using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Minecraft;

namespace Minecraft.Tests;

public sealed class IdentityAdapterTransportCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "identity-transport-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly Type ServiceType = typeof(PortableIdentityAdapterService);
    private static readonly SteamTransportBuild Pin = new("0.3.4", "transport.jar", 3,
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        34013, [PackLoaderKind.NeoForge], [26, 3], [26, 4]);

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void CurrentTransport_ReusesVerifiedSkinCoreWithoutChangingIdentityProperties()
    {
        using var fixture = CreateFixture();
        var before = fixture.Properties.ToArray();
        Assert.True(IsReusable(fixture));
        Assert.Equal(before, fixture.Properties.ToArray());
    }

    [Fact]
    public void SameLengthAndMtimeTransportChange_InvalidatesNativeGuardProof()
    {
        using var fixture = CreateFixture();
        Assert.True(IsReusable(fixture));
        var timestamp = File.GetLastWriteTimeUtc(fixture.TransportPath);
        var size = new FileInfo(fixture.TransportPath).Length;
        File.WriteAllText(fixture.TransportPath, "abd");
        File.SetLastWriteTimeUtc(fixture.TransportPath, timestamp);
        Assert.Equal(size, new FileInfo(fixture.TransportPath).Length);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.TransportPath));
        Assert.False(IsReusable(fixture));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("cacheId")]
    [InlineData("fileName")]
    [InlineData("expectedHash")]
    [InlineData("expectedSize")]
    public void CatalogSelectionChange_InvalidatesEvenWhenAdapterAndNativeTargetsAreUnchanged(string changed)
    {
        using var fixture = CreateFixture();
        Assert.True(IsReusable(fixture));
        fixture.CurrentBuild = changed switch
        {
            "version" => Pin with { Version = "0.3.5" },
            "cacheId" => Pin with { CacheFileId = Pin.CacheFileId + 1 },
            "fileName" => Pin with { FileName = "renamed.jar" },
            "expectedHash" => Pin with { Sha256 = new string('b', 64) },
            "expectedSize" => Pin with { SizeBytes = Pin.SizeBytes + 1 },
            _ => throw new ArgumentException(changed)
        };
        if (changed == "fileName")
            File.Copy(fixture.TransportPath, Path.Combine(Path.GetDirectoryName(fixture.TransportPath)!, "renamed.jar"));
        Assert.False(IsReusable(fixture));
    }

    [Fact]
    public void MissingAndPresentTransitions_InvalidateAndKeepIndependentIdentityHooks()
    {
        using var fixture = CreateFixture();
        File.Delete(fixture.TransportPath);
        Assert.False(IsReusable(fixture));
        fixture.Properties["lanRedirectNativeInitEnabled"] = "false";
        RecordCurrentFingerprint(fixture);
        Assert.True(IsReusable(fixture));
        Assert.Equal("false", fixture.Properties["identityHooksEnabled"]);
        Assert.Equal("com/mojang/authlib/GameProfile", fixture.Properties["gameProfileClasses"]);

        File.WriteAllText(fixture.TransportPath, "abc");
        Assert.False(IsReusable(fixture));
        fixture.Properties["lanRedirectNativeInitEnabled"] = "true";
        RecordCurrentFingerprint(fixture);
        Assert.True(IsReusable(fixture));
        Assert.Equal("false", fixture.Properties["identityHooksEnabled"]);
        Assert.Equal("getPackedTextures", fixture.Properties["skinReaderMethods"]);
    }

    [Fact]
    public void UnknownOldStateWithoutManagedFingerprint_IsRejected()
    {
        using var fixture = CreateFixture();
        fixture.Properties.Remove(PortableIdentityAdapterService.ManagedTransportFingerprintProperty);
        RehashState(fixture.State);
        Assert.False(IsReusable(fixture));
    }

    [Fact]
    public void CatalogGuardDisableAndEnableTransitions_InvalidateCachedProof()
    {
        using var fixture = CreateFixture();
        fixture.CurrentBuild = Pin with { Version = "0.3.5" };
        Assert.False(IsReusable(fixture));
        fixture.Properties["lanRedirectNativeInitEnabled"] = "false";
        RecordCurrentFingerprint(fixture);
        Assert.True(IsReusable(fixture));
        fixture.CurrentBuild = Pin;
        Assert.False(IsReusable(fixture));
        Assert.Equal("false", fixture.Properties["identityHooksEnabled"]);
        fixture.Properties["lanRedirectNativeInitEnabled"] = "true";
        RecordCurrentFingerprint(fixture);
        Assert.True(IsReusable(fixture));
    }

    [Fact]
    public void FingerprintContainsNoAbsoluteLocation_AndHandlesNoSelection()
    {
        using var fixture = CreateFixture();
        var other = Path.Combine(_root, "other-location");
        Directory.CreateDirectory(Path.Combine(other, "mods"));
        File.Copy(fixture.TransportPath, Path.Combine(other, "mods", Pin.FileName));
        var current = PortableIdentityAdapterService.ComputeManagedTransportFingerprint(Pin, fixture.GameDirectory);
        Assert.Equal(current, PortableIdentityAdapterService.ComputeManagedTransportFingerprint(Pin, other));
        Assert.Matches("^[a-f0-9]{64}$", current);
        Assert.NotEqual(current, PortableIdentityAdapterService.ComputeManagedTransportFingerprint(null, other));
        fixture.CurrentBuild = null;
        Assert.False(IsReusable(fixture));
        fixture.Properties["lanRedirectNativeInitEnabled"] = "false";
        RecordCurrentFingerprint(fixture);
        Assert.True(IsReusable(fixture));
    }

    private Fixture CreateFixture()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var mapping = Path.Combine(paths.Launcher, "fixture-mappings.txt");
        var target = Path.Combine(paths.Launcher, "fixture-client.jar");
        File.WriteAllText(mapping, "unchanged native mappings");
        File.WriteAllText(target, "unchanged native target");
        var game = Path.Combine(paths.Instances, "Fixture");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        var transport = Path.Combine(game, "mods", Pin.FileName);
        File.WriteAllText(transport, "abc");
        var runtime = new PreparedRuntime(paths.Runtimes, "fixture", "unused-java.exe", target,
            new PackRuntimeDescriptor(1, "26.3", new(PackLoaderKind.NeoForge, "26.3.0.99"), "", "descriptor"));
        var stateType = ServiceType.GetNestedType("IdentityAdapterState", BindingFlags.NonPublic)!;
        var targetType = ServiceType.GetNestedType("IdentityAdapterTargetState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        Set(state, "SchemaVersion", ServiceType.GetField("AdapterCacheGeneration", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue());
        Set(state, "DescriptorHash", runtime.Descriptor.DescriptorHash);
        Set(state, "TargetRoot", game);
        Set(state, "AdapterSha256", "unchanged-adapter-hash");
        Set(state, "MappingPath", mapping);
        Set(state, "MappingSizeBytes", new FileInfo(mapping).Length);
        Set(state, "MappingLastWriteUtcTicks", File.GetLastWriteTimeUtc(mapping).Ticks);
        Set(state, "MappingSha256", Hash(mapping));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["identityHooksEnabled"] = "false", ["gameProfileClasses"] = "com/mojang/authlib/GameProfile",
            ["skinReaderMethods"] = "getPackedTextures", ["lanRedirectNativeInitEnabled"] = "true"
        };
        Set(state, "Properties", properties);
        var native = Activator.CreateInstance(targetType, nonPublic: true)!;
        Set(native, "JarPath", target);
        Set(native, "ClassName", "net/minecraft/client/Minecraft");
        Set(native, "SizeBytes", new FileInfo(target).Length);
        Set(native, "LastWriteUtcTicks", File.GetLastWriteTimeUtc(target).Ticks);
        Set(native, "Sha256", Hash(target));
        var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))!;
        targets.Add(native);
        Set(state, "Targets", targets);
        var fixture = new Fixture(runtime, game, transport, state, properties);
        fixture.Service = new PortableIdentityAdapterService(paths, new Logger(paths.LogFile), _ => fixture.CurrentBuild);
        RecordCurrentFingerprint(fixture);
        return fixture;
    }

    private static bool IsReusable(Fixture fixture) => (bool)ServiceType
        .GetMethod("IsStateValid", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(fixture.Service, [fixture.State, fixture.Runtime, fixture.GameDirectory, "unchanged-adapter-hash"])!;
    private static void RecordCurrentFingerprint(Fixture fixture)
    {
        fixture.Properties[PortableIdentityAdapterService.ManagedTransportFingerprintProperty] =
            PortableIdentityAdapterService.ComputeManagedTransportFingerprint(fixture.CurrentBuild, fixture.GameDirectory);
        RehashState(fixture.State);
    }
    private static void RehashState(object state) => Set(state, "ConfigurationSha256",
        ServiceType.GetMethod("ComputeConfigurationHash", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [state]));
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed class Fixture(PreparedRuntime runtime, string gameDirectory, string transportPath, object state,
        Dictionary<string, string> properties) : IDisposable
    {
        internal SteamTransportBuild? CurrentBuild { get; set; } = Pin;
        internal PortableIdentityAdapterService Service { get; set; } = null!;
        internal PreparedRuntime Runtime { get; } = runtime;
        internal string GameDirectory { get; } = gameDirectory;
        internal string TransportPath { get; } = transportPath;
        internal object State { get; } = state;
        internal Dictionary<string, string> Properties { get; } = properties;
        public void Dispose() => Service.Dispose();
    }
}
