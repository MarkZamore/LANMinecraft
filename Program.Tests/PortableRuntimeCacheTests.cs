using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Minecraft.Tests;

public sealed class PortableRuntimeCacheTests
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ActiveHostsAndTheTwoNewestInactiveGenerationsSurviveRepeatedCleanup()
    {
        using var fixture = new RuntimeFixture();
        var generations = Enumerable.Range(1, 7).Select(index => fixture.Owned(index, Epoch.AddDays(index))).ToArray();
        using (File.Open(fixture.Lease(generations[0]), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (File.Open(fixture.Lease(generations[6]), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PortableRuntimeCache.Cleanup(fixture.Paths);
            PortableRuntimeCache.Cleanup(fixture.Paths);

            foreach (var index in new[] { 0, 4, 5, 6 }) AssertGeneration(generations[index]);
            foreach (var index in new[] { 1, 2, 3 }) Assert.False(Directory.Exists(generations[index]));
            Assert.Equal(4, Directory.EnumerateDirectories(fixture.Runtime).Count());
            foreach (var generation in generations) AssertOwnedMarker(fixture.Lease(generation), Path.GetFileName(generation));
        }

        PortableRuntimeCache.Cleanup(fixture.Paths);
        Assert.Equal(2, Directory.EnumerateDirectories(fixture.Runtime).Count());
        AssertGeneration(generations[5]);
        AssertGeneration(generations[6]);
        Assert.False(Directory.Exists(generations[0]));
        Assert.False(Directory.Exists(generations[4]));
        foreach (var generation in generations) AssertOwnedMarker(fixture.Lease(generation), Path.GetFileName(generation));
    }

    [Fact]
    public void UnknownNamesAndInvalidOrMissingOwnershipMarkersAreNeverDeleted()
    {
        using var fixture = new RuntimeFixture();
        var unknown = new List<string>
        {
            fixture.Generation(Hash(10), Epoch, null),
            fixture.Generation(Hash(11), Epoch, Encoding.ASCII.GetBytes("LMHOST00\n" + Hash(11) + "\n")),
            fixture.Generation(Hash(12), Epoch, Marker(Hash(13))),
            fixture.Generation(Hash(14), Epoch, Encoding.ASCII.GetBytes("LMHOST01\r\n" + Hash(14) + "\r\n")),
            fixture.Generation(Hash(15), Epoch, Encoding.UTF8.GetPreamble().Concat(Marker(Hash(15))).ToArray()),
            fixture.Generation(Hash(16), Epoch, Marker(Hash(16)).Append((byte)' ').ToArray()),
            fixture.Generation(new string('a', 64), Epoch, Marker(new string('A', 64))),
            fixture.Generation(new string('B', 64), Epoch, Marker(new string('B', 64))),
            fixture.Generation(new string('g', 64), Epoch, Marker(new string('g', 64))),
            fixture.Generation(new string('c', 63), Epoch, Marker(new string('c', 63))),
            fixture.Generation("foreign-runtime", Epoch, Marker("foreign-runtime"))
        };
        var snapshots = unknown.ToDictionary(path => path, Snapshot);
        var markerSnapshots = unknown.Select(fixture.Lease).Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        var standalone = Path.Combine(fixture.Runtime, Hash(30));
        File.WriteAllText(standalone, "A standalone file is not an extracted generation.");
        File.WriteAllBytes(standalone + ".lease", Marker(Hash(30)));
        var notes = Path.Combine(fixture.Runtime, "notes.txt");
        File.WriteAllText(notes, "Foreign runtime cache notes.");
        var oldOwned = fixture.Owned(40, Epoch.AddDays(1));
        var recent = fixture.Owned(41, Epoch.AddDays(2));
        var newest = fixture.Owned(42, Epoch.AddDays(3));

        PortableRuntimeCache.Cleanup(fixture.Paths);

        Assert.False(Directory.Exists(oldOwned));
        AssertGeneration(recent); AssertGeneration(newest);
        foreach (var path in unknown) AssertSnapshot(path, snapshots[path]);
        foreach (var marker in markerSnapshots) Assert.Equal(marker.Value, File.ReadAllBytes(marker.Key));
        Assert.Equal("A standalone file is not an extracted generation.", File.ReadAllText(standalone));
        AssertOwnedMarker(standalone + ".lease", Hash(30));
        Assert.Equal("Foreign runtime cache notes.", File.ReadAllText(notes));
    }

    [Fact]
    public void AReparseLauncherParentNeverSweepsAnExternalRuntimeTree()
    {
        using var fixture = new RuntimeFixture();
        var external = Path.Combine(fixture.Root, "ExternalLauncher");
        var externalRuntime = Path.Combine(external, "Runtime");
        var generations = Enumerable.Range(1, 4)
            .Select(index => fixture.Generation(Hash(index), Epoch.AddDays(index), Marker(Hash(index)), externalRuntime)).ToArray();
        var sentinel = Path.Combine(external, "outside-runtime.txt");
        File.WriteAllText(sentinel, "The linked parent is outside this installation.");
        Directory.Delete(fixture.Paths.Launcher, recursive: false);
        fixture.Junction(fixture.Paths.Launcher, external);

        PortableRuntimeCache.Cleanup(fixture.Paths);

        Assert.True((File.GetAttributes(fixture.Paths.Launcher) & FileAttributes.ReparsePoint) != 0);
        foreach (var generation in generations)
        {
            AssertGeneration(generation);
            AssertOwnedMarker(fixture.Lease(generation), Path.GetFileName(generation));
        }
        Assert.Equal("The linked parent is outside this installation.", File.ReadAllText(sentinel));
    }

    [Fact]
    public void AReparseDescendantPreservesTheWholeCandidateAndItsExternalSentinel()
    {
        using var fixture = new RuntimeFixture();
        var linked = fixture.Owned(1, Epoch);
        var ordinaryOld = fixture.Owned(2, Epoch.AddDays(1));
        var recent = fixture.Owned(3, Epoch.AddDays(2));
        var newest = fixture.Owned(4, Epoch.AddDays(3));
        var snapshot = Snapshot(linked);
        var external = Path.Combine(fixture.Root, "ExternalPayload");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "Never traverse a generation's junction.");
        var junction = Path.Combine(linked, "linked-payload");
        fixture.Junction(junction, external);
        // Creating the child link updates its parent; keep this owned candidate
        // demonstrably older than the two retained generations.
        Directory.SetLastWriteTimeUtc(linked, Epoch);

        PortableRuntimeCache.Cleanup(fixture.Paths);

        Assert.True(Directory.Exists(linked));
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        foreach (var pair in snapshot) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(linked, pair.Key)));
        Assert.Equal("Never traverse a generation's junction.", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(ordinaryOld));
        AssertGeneration(recent); AssertGeneration(newest);
        AssertOwnedMarker(fixture.Lease(linked), Path.GetFileName(linked));
    }

    [Fact]
    public void ALockedPayloadPreservesTheWholeGenerationUntilTheHandleIsReleased()
    {
        using var fixture = new RuntimeFixture();
        var locked = fixture.Owned(1, Epoch);
        var ordinaryOld = fixture.Owned(2, Epoch.AddDays(1));
        var recent = fixture.Owned(3, Epoch.AddDays(2));
        var newest = fixture.Owned(4, Epoch.AddDays(3));
        var snapshot = Snapshot(locked);
        // Readers may verify the bytes, but an exclusive cleaner probe and
        // deletion remain blocked just as they are for an in-use payload.
        using (File.Open(Path.Combine(locked, "payload", "host.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PortableRuntimeCache.Cleanup(fixture.Paths);

            AssertSnapshot(locked, snapshot);
            Assert.False(Directory.Exists(ordinaryOld));
            AssertGeneration(recent); AssertGeneration(newest);
            AssertOwnedMarker(fixture.Lease(locked), Path.GetFileName(locked));
        }
        PortableRuntimeCache.Cleanup(fixture.Paths);
        Assert.False(Directory.Exists(locked));
        AssertGeneration(recent); AssertGeneration(newest);
        AssertOwnedMarker(fixture.Lease(locked), Path.GetFileName(locked));
    }

    [Fact]
    public void AReadOnlyPayloadIsNotPartiallyDeletedAndCanBePrunedAfterItBecomesWritable()
    {
        using var fixture = new RuntimeFixture();
        var readOnly = fixture.Owned(1, Epoch);
        var ordinaryOld = fixture.Owned(2, Epoch.AddDays(1));
        var recent = fixture.Owned(3, Epoch.AddDays(2));
        var newest = fixture.Owned(4, Epoch.AddDays(3));
        var payload = Path.Combine(readOnly, "payload", "host.dll");
        fixture.MakeReadOnly(payload);
        var snapshot = Snapshot(readOnly);

        PortableRuntimeCache.Cleanup(fixture.Paths);

        AssertSnapshot(readOnly, snapshot);
        Assert.True((File.GetAttributes(payload) & FileAttributes.ReadOnly) != 0);
        Assert.False(Directory.Exists(ordinaryOld));
        AssertGeneration(recent); AssertGeneration(newest);
        File.SetAttributes(payload, FileAttributes.Normal);
        PortableRuntimeCache.Cleanup(fixture.Paths);
        Assert.False(Directory.Exists(readOnly));
        AssertGeneration(recent); AssertGeneration(newest);
        AssertOwnedMarker(fixture.Lease(readOnly), Path.GetFileName(readOnly));
    }

    private static string Hash(int number) => number.ToString("x64", CultureInfo.InvariantCulture);
    private static byte[] Marker(string hash) => Encoding.ASCII.GetBytes("LMHOST01\n" + hash + "\n");

    private static void AssertOwnedMarker(string path, string hash)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(74, bytes.Length);
        Assert.Equal(Marker(hash), bytes);
    }

    private static void AssertGeneration(string path)
    {
        Assert.True(Directory.Exists(path));
        Assert.Equal("Synthetic extraction " + Path.GetFileName(path), File.ReadAllText(Path.Combine(path, "a-unlocked.txt")));
        Assert.Equal("Synthetic runtime payload; never executed.", File.ReadAllText(Path.Combine(path, "payload", "host.dll")));
    }

    private static Dictionary<string, byte[]> Snapshot(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertSnapshot(string path, Dictionary<string, byte[]> expected)
    {
        Assert.True(Directory.Exists(path));
        var actual = Snapshot(path);
        Assert.Equal(expected.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), actual.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string artifacts = Path.Combine(RepositoryRoot(), "artifacts", nameof(PortableRuntimeCacheTests));
        private readonly List<string> junctions = [];
        private readonly List<string> readOnlyFiles = [];
        public string Root { get; }
        public AppPaths Paths { get; }
        public string Runtime => Path.Combine(Paths.Launcher, "Runtime");
        public RuntimeFixture()
        {
            Root = Path.Combine(artifacts, Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Path.Combine(Root, "Installation"));
            Directory.CreateDirectory(Paths.Launcher);
        }

        public string Owned(int number, DateTime stamp) => Generation(Hash(number), stamp, Marker(Hash(number)));
        public string Lease(string generation) => generation + ".lease";
        public string Generation(string name, DateTime stamp, byte[]? ownership, string? runtime = null)
        {
            var path = Path.Combine(runtime ?? Runtime, name);
            RequireOwned(path);
            Directory.CreateDirectory(Path.Combine(path, "payload"));
            File.WriteAllText(Path.Combine(path, "a-unlocked.txt"), "Synthetic extraction " + name);
            File.WriteAllText(Path.Combine(path, "payload", "host.dll"), "Synthetic runtime payload; never executed.");
            if (ownership is not null) File.WriteAllBytes(Lease(path), ownership);
            Directory.SetLastWriteTimeUtc(path, stamp);
            return path;
        }

        public void MakeReadOnly(string path)
        {
            RequireOwned(path);
            readOnlyFiles.Add(path);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }

        public void Junction(string path, string target)
        {
            RequireOwned(path); RequireOwned(target);
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            junctions.Add(path);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", LongPath(path), LongPath(target) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(); process.WaitForExit();
                Assert.Fail("The fixture junction helper did not exit.");
            }
            Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
            Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        }

        public void Dispose()
        {
            // Never let a generic recursive fixture cleaner decide whether to
            // traverse a real junction. Remove only the link, without recursion,
            // and retain the fixture if that conservative unlink fails.
            foreach (var junction in junctions.OrderByDescending(path => path.Length))
            {
                try
                {
                    RequireOwned(junction);
                    var link = new DirectoryInfo(junction);
                    if (link.LinkTarget is not null) Directory.Delete(junction, recursive: false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            }
            foreach (var file in readOnlyFiles)
            {
                try { if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            }
            if (!IsUnder(Root, artifacts)) return;
            TempTree.Delete(Root);
        }

        private void RequireOwned(string path)
        {
            if (!IsUnder(path, Root)) throw new InvalidOperationException("The fixture path escaped its own artifacts directory.");
        }
        private static bool IsUnder(string path, string parent)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string LongPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("The repository root was not found for the artifacts fixture.");
        }
    }
}
