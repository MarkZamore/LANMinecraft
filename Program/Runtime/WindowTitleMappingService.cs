using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Minecraft;

/// <summary>
/// Verifies the native title factory independently of optional UUID/LAN hooks.
/// Named clients need no mappings; remapped clients use their own mapping files.
/// </summary>
internal static class WindowTitleMappingService
{
    internal const string MinecraftClass = "net/minecraft/client/Minecraft";
    internal const string TitleDescriptor = "()Ljava/lang/String;";
    private const int MaximumClassBytes = 8 * 1024 * 1024;

    internal static IdentityAdapterConfiguration Build(PreparedRuntime runtime, WindowTitleSettings settings)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["windowTitleEnabled"] = "false" };
        if (!settings.Enabled) return new IdentityAdapterConfiguration(string.Empty, properties, []);
        var paths = CandidateJars(runtime).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
        foreach (var path in paths)
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(MinecraftClass + ".class");
            if (entry is null) continue;
            var method = ReadTitleMethod(entry, MinecraftClass, ["createTitle", "m_91270_"]);
            return Configuration(path, MinecraftClass, MinecraftClass, method);
        }
        if (paths.Length != 0 && IdentityAdapterMappingService.FindMappedMethod(
                runtime, MinecraftClass, "createTitle", TitleDescriptor) is { } mapped)
        {
            var classes = new[] { mapped.RuntimeClass, mapped.SourceClass }.Distinct(StringComparer.Ordinal).ToArray();
            var methods = new[] { mapped.RuntimeMethod, mapped.SourceMethod }.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var path in paths)
            {
                using var archive = ZipFile.OpenRead(path);
                foreach (var name in classes)
                {
                    if (archive.GetEntry(name + ".class") is not { } entry) continue;
                    // The runtime uses intermediary/SRG while the pinned source
                    // JAR can still be obfuscated. Verify the actual source class
                    // and factory; preflight transforms that exact native target.
                    _ = ReadTitleMethod(entry, name, methods);
                    return Configuration(path, name, string.Join(",", classes), string.Join(",", methods));
                }
            }
        }
        throw new NotSupportedException($"No verified window title method for Minecraft {runtime.Descriptor.MinecraftVersion} " +
            $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}.");

        IdentityAdapterConfiguration Configuration(string path, string sourceClass, string aliases, string methods)
        {
            properties["windowTitleEnabled"] = "true";
            properties["windowTitleText"] = settings.Text;
            properties["windowTitleClasses"] = aliases;
            properties["createTitleMethods"] = methods;
            properties["createTitleDescriptors"] = TitleDescriptor;
            return new IdentityAdapterConfiguration(string.Empty, properties,
                [new IdentityAdapterTarget(Path.GetFullPath(path), sourceClass)]);
        }
    }

    private static IEnumerable<string> CandidateJars(PreparedRuntime runtime)
    {
        // 26.2's FML startup loads this exact loader-version output. It does not
        // advertise that Minecraft jar as an ordinary profile library.
        var loader = runtime.Descriptor.Loader;
        if (loader.Type == PackLoaderKind.NeoForge && !string.IsNullOrWhiteSpace(loader.Version))
        {
            yield return Path.Combine(runtime.LibrariesRoot, "net", "neoforged", "minecraft-client-patched",
                loader.Version, $"minecraft-client-patched-{loader.Version}.jar");
            // Older NeoForge stores its patched client beside the loader itself.
            yield return Path.Combine(runtime.LibrariesRoot, "net", "neoforged", "neoforge",
                loader.Version, $"neoforge-{loader.Version}-client.jar");
        }
        if (loader.Type == PackLoaderKind.Forge && !string.IsNullOrWhiteSpace(loader.Version))
        {
            var version = runtime.Descriptor.MinecraftVersion + "-" + loader.Version;
            // This is the actual Forge-patched launch artifact, not the vanilla
            // SRG intermediate that its installer used to produce it.
            yield return Path.Combine(runtime.LibrariesRoot, "net", "minecraftforge", "forge",
                version, $"forge-{version}-client.jar");
        }
        var sharedRoot = Path.GetDirectoryName(Path.GetFullPath(runtime.LibrariesRoot));
        foreach (var root in new[] { runtime.RuntimeRoot, sharedRoot }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            yield return Path.Combine(root, "versions", runtime.ProfileId, runtime.ProfileId + ".jar");
        if (loader.Type == PackLoaderKind.Forge)
        {
            // Forge 1.20.1 loads Minecraft's SRG client. Other Minecrafts in the
            // shared store have the same class name and must not be considered.
            var clients = Path.Combine(runtime.LibrariesRoot, "net", "minecraft", "client");
            if (Directory.Exists(clients))
            {
                foreach (var path in Directory.EnumerateFiles(clients, "*-srg.jar", SearchOption.AllDirectories)
                    .Where(path => IdentityAdapterMappingService.IsFiledUnderVersion(path, runtime.Descriptor.MinecraftVersion))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    yield return path;
            }
        }
        // Explicitly prepared client only, never an arbitrary shared-store jar.
        yield return runtime.ClientJarPath;
    }

    private static string ReadTitleMethod(ZipArchiveEntry entry, string className, string[] methods)
    {
        if (entry.Length is <= 0 or > MaximumClassBytes)
            throw new InvalidDataException("Minecraft title class exceeds the supported size.");
        using var stream = entry.Open();
        var bytes = new byte[(int)entry.Length];
        stream.ReadExactly(bytes);
        var reader = new ClassFileReader(bytes);
        return reader.ReadTitleMethod(className, methods);
    }

    /// <summary>A bounded class-header/method-table read, not a new mapping database.</summary>
    private ref struct ClassFileReader(byte[] data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        internal string ReadTitleMethod(string className, string[] supportedMethods)
        {
            if (U4() != 0xCAFEBABE) throw InvalidClass();
            Skip(4); // minor and major versions; ASM preflight verifies runtime support.
            var count = U2();
            var utf8 = new string?[count];
            var classes = new ushort[count];
            for (var index = 1; index < count; index++)
            {
                switch (U1())
                {
                    case 1:
                        var length = U2();
                        Require(length);
                        // Java uses modified UTF-8, but the class and method names
                        // inspected here are ASCII. Unrelated constants stay opaque.
                        utf8[index] = Encoding.UTF8.GetString(_data.Slice(_offset, length));
                        Skip(length);
                        break;
                    case 7: classes[index] = U2(); break;
                    case 8: case 16: case 19: case 20: Skip(2); break;
                    case 3: case 4: case 9: case 10: case 11: case 12: case 17: case 18: Skip(4); break;
                    case 5: case 6: Skip(8); index++; break;
                    case 15: Skip(3); break;
                    default: throw InvalidClass();
                }
            }
            _ = U2(); // access flags
            var thisClass = U2();
            if (thisClass >= count || classes[thisClass] >= count || utf8[classes[thisClass]] != className)
                throw InvalidClass();
            _ = U2(); // superclass
            Skip(U2() * 2); // interfaces
            var fields = U2();
            for (var index = 0; index < fields; index++) { Skip(6); SkipAttributes(); }
            var methods = U2();
            string? title = null;
            for (var index = 0; index < methods; index++)
            {
                var flags = U2();
                var name = U2();
                var descriptor = U2();
                if (name >= count || descriptor >= count) throw InvalidClass();
                var attributes = U2();
                var hasCode = false;
                for (var attribute = 0; attribute < attributes; attribute++)
                {
                    var type = U2();
                    if (type >= count) throw InvalidClass();
                    hasCode |= utf8[type] == "Code";
                    SkipLength(U4());
                }
                if (!supportedMethods.Contains(utf8[name], StringComparer.Ordinal) || utf8[descriptor] != TitleDescriptor ||
                    (flags & (0x0008 | 0x0100 | 0x0400)) != 0 || !hasCode) continue;
                if (title is not null) throw new InvalidDataException("Minecraft has ambiguous window title methods.");
                title = utf8[name];
            }
            return title ?? throw new NotSupportedException("Minecraft has no supported instance createTitle() String method.");
        }

        private void SkipAttributes()
        {
            var count = U2();
            for (var index = 0; index < count; index++) { Skip(2); SkipLength(U4()); }
        }
        private void SkipLength(uint length)
        {
            if (length > int.MaxValue) throw InvalidClass();
            Skip((int)length);
        }
        private byte U1() { Require(1); return _data[_offset++]; }
        private ushort U2() { Require(2); var value = BinaryPrimitives.ReadUInt16BigEndian(_data[_offset..]); _offset += 2; return value; }
        private uint U4() { Require(4); var value = BinaryPrimitives.ReadUInt32BigEndian(_data[_offset..]); _offset += 4; return value; }
        private void Skip(int count) { Require(count); _offset += count; }
        private void Require(int count)
        {
            if (count < 0 || count > _data.Length - _offset) throw InvalidClass();
        }
        private static InvalidDataException InvalidClass() => new("Invalid Minecraft window title class file.");
    }
}
