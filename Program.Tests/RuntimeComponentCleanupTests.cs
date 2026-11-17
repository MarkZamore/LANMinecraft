using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class RuntimeComponentCleanupTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "component-cleanup-" + Guid.NewGuid().ToString("N")));
    private static readonly DateTime Started = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    public RuntimeComponentCleanupTests() => _paths.Ensure();

    [Fact]
    public void SessionReferencesBelongToTheOriginalProcessInstance()
    {
        var hash = new string('a', 64);
        WriteSession(new ClientPresenceService.ClientSession(42, Started, "Pack")
        {
            ComponentHashes = [hash], ComponentRevision = "components-1"
        });
        var presence = new ClientPresenceService(_paths);

        Assert.Contains(hash, presence.ReadComponentReferences(_ => ("javaw", Started))!);
        Assert.Empty(presence.ReadComponentReferences(_ => ("javaw", Started.AddMinutes(1)))!);
    }

    [Fact]
    public void AnOlderLiveSessionDefersComponentDeletion()
    {
        WriteSession(new ClientPresenceService.ClientSession(42, Started, "Pack"));
        var presence = new ClientPresenceService(_paths);

        Assert.Null(presence.ReadComponentReferences(_ => ("javaw", Started)));
        Assert.Empty(presence.ReadComponentReferences(_ => null)!);
    }

    [Fact]
    public void MalformedSessionEvidenceSurvivesDiscoveryAndDefersCleanup()
    {
        var file = SessionPath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ unfinished");
        var presence = new ClientPresenceService(_paths);

        Assert.Empty(presence.ReadLiveSessions(_ => null));
        Assert.True(File.Exists(file));
        Assert.Null(presence.ReadComponentReferences(_ => null));
    }

    [Fact]
    public void RememberPersistsComponentReferencesForTheNextLauncher()
    {
        using var process = Process.GetCurrentProcess();
        var hash = new string('b', 64);
        var presence = new ClientPresenceService(_paths);
        presence.Remember(process, "Pack", process.MainModule!.FileName, [hash], "components-2");

        var restored = Assert.Single(presence.ReadLiveSessions());
        Assert.Equal([hash], restored.ComponentHashes);
        Assert.Equal("components-2", restored.ComponentRevision);
        Assert.Contains(hash, presence.ReadComponentReferences()!);
    }

    [Fact]
    public void UnreferencedOwnedObjectIsDeleted()
    {
        var (payload, marker) = CreateObject();

        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });

        Assert.False(File.Exists(payload));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void APreparationHandleProtectsBothPayloadAndOwnershipMarker()
    {
        var (payload, marker) = CreateObject();
        using var preparation = File.OpenRead(payload);

        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void DurableGameReferenceProtectsTheObjectAfterPreparationEnds()
    {
        var (payload, marker) = CreateObject();
        var hash = Path.GetFileName(Path.GetDirectoryName(payload)!);
        using var process = Process.GetCurrentProcess();
        var presence = new ClientPresenceService(_paths);
        presence.Remember(process, "Pack", process.MainModule!.FileName, [hash], "components-1");

        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });
        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(marker));

        presence.Forget(process.Id);
        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });
        Assert.False(File.Exists(payload));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void AbandonedPartIsRemovedWhileFreshPartAndUnknownFilesStay()
    {
        var (payload, marker) = CreateObject();
        var directory = Path.GetDirectoryName(payload)!;
        var expired = payload + "." + Guid.NewGuid().ToString("N") + ".part";
        var fresh = payload + "." + Guid.NewGuid().ToString("N") + ".part";
        var unknown = Path.Combine(directory, "unrecognised.txt");
        File.WriteAllText(expired, "partial");
        File.WriteAllText(fresh, "in progress");
        File.WriteAllText(unknown, "unowned");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-2));

        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void UnreadableSelectionProtectsAnOtherwiseUnreferencedObject()
    {
        var (payload, marker) = CreateObject();
        var selections = Path.Combine(_paths.Launcher, "Components", "packs", new string('c', 64));
        Directory.CreateDirectory(selections);
        File.WriteAllText(Path.Combine(selections, "selection.json"), "{ unfinished");

        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void ARecordedJavaArchiveCanBeCollectedAfterItsCatalogPinDisappears()
    {
        byte[] bytes = [17, 0, 1];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var pin = new JavaRuntimePin(17, "temurin-17.0.0-test", "17.0.0", "temurin-17.0.0-test",
            "historical-jdk.zip", "jdk/", [new Uri("https://example.test/historical-jdk.zip")],
            bytes.Length, hash, 1024, false);
        var directory = Path.Combine(JavaRuntimeStorage.ArchiveParent(_paths), pin.RuntimeId.Replace('+', '_'));
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, pin.ArchiveFileName);
        File.WriteAllBytes(archive, bytes);
        JavaRuntimeStorage.RecordArchiveOwnership(_paths, archive, pin);

        var candidate = Assert.Single(JavaRuntimeStorage.ReadUnusedDirectories(_paths));
        Assert.Equal(pin.RuntimeId, candidate.RuntimeId);
        Assert.False(candidate.IsInstall);
        JavaRuntimeStorage.SweepUnusedSuperseded(_paths, null, static () => { });
        Assert.False(Directory.Exists(directory));
    }

    private (string Payload, string Marker) CreateObject()
    {
        byte[] bytes = [8, 21, 25];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(_paths.Launcher, "Components", "objects", hash);
        Directory.CreateDirectory(directory);
        var payload = Path.Combine(directory, "fixture.jar");
        File.WriteAllBytes(payload, bytes);
        var marker = Path.Combine(directory, "artifact.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { id = "fixture", fileName = "fixture.jar", sha256 = hash, sizeBytes = bytes.Length }));
        return (payload, marker);
    }

    private void WriteSession(ClientPresenceService.ClientSession session)
    {
        var file = SessionPath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(session));
    }

    private string SessionPath() => Path.Combine(_paths.Personal, "ClientSessions", "42.json");

    public void Dispose() => TempTree.Delete(_paths.Root);
}
