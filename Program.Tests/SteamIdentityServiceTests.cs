using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// Binding a machine to a Steam account is the one migration that can silently
/// cost a player their quests, teams and homes, so every branch is pinned here:
/// what happens with and without a legacy UUID.json, when Steam is closed, when
/// a second account signs in, and when both histories exist.
/// </summary>
public sealed class SteamIdentityServiceTests : IDisposable
{
    private const ulong FirstAccount = 76561198000000001;
    private const ulong SecondAccount = 76561198000000002;
    private static readonly Guid LegacyUuid = new("06c83c9e-980b-47d5-b7be-23d2bb649068");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-identity-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AMachineWithHistory_KeepsItsUuidAndBacksTheFileUp()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        using var service = CreateService(paths, FirstAccount, "MarkZamore");

        var result = await service.EnsureBoundAsync(CancellationToken.None);

        Assert.True(result.Bound);
        Assert.True(result.Migrated);
        Assert.Equal(IdentityBindingSource.MigratedUuidJson, result.Source);
        Assert.Equal(LegacyUuid, service.Binding!.PlayerUuid);

        // UUID.json is untouched, and a copy of it exists for rollback.
        Assert.Equal(LegacyUuid, ReadLegacyIdentity(paths));
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(LegacyUuid, ReadLegacyIdentity(result.BackupPath!));
    }

    [Fact]
    public async Task BindingIsIdempotent_AndSurvivesARestart()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        using var first = CreateService(paths, FirstAccount, "MarkZamore");
        await first.EnsureBoundAsync(CancellationToken.None);
        var afterFirst = File.ReadAllBytes(paths.SteamIdentityFile);

        var again = await first.EnsureBoundAsync(CancellationToken.None);
        Assert.True(again.Bound);
        Assert.False(again.Migrated);
        Assert.Equal(afterFirst, File.ReadAllBytes(paths.SteamIdentityFile));

        // A fresh launcher process reads the same binding back.
        using var second = CreateService(paths, FirstAccount, "MarkZamore");
        var restarted = await second.EnsureBoundAsync(CancellationToken.None);
        Assert.True(restarted.Bound);
        Assert.False(restarted.Migrated);
        Assert.Equal(LegacyUuid, second.Binding!.PlayerUuid);
        Assert.Single(Directory.GetDirectories(paths.IdentityBackups));
    }

    [Fact]
    public async Task AFreshMachine_GetsTheUuidDerivedFromTheSteamAccount()
    {
        var paths = CreatePaths();
        using var service = CreateService(paths, FirstAccount, "anuvenn");

        var result = await service.EnsureBoundAsync(CancellationToken.None);

        Assert.True(result.Bound);
        Assert.False(result.Migrated);
        Assert.Equal(IdentityBindingSource.Derived, result.Source);
        Assert.True(SteamId64.TryFrom(FirstAccount, out var steamId));
        var derived = SteamIdentityDerivation.DeriveMinecraftUuid(steamId);
        Assert.Equal(derived, service.Binding!.PlayerUuid);

        // The legacy file is written once with the same value, so rolling back
        // to the previous launcher keeps this player's progress too.
        Assert.True(File.Exists(paths.IdentityFile));
        Assert.Equal(derived, ReadLegacyIdentity(paths));
    }

    [Fact]
    public async Task WithoutSteam_NothingIsWrittenAndThePlayerIsTold()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        using var service = new SteamIdentityService(paths, new FakeSteamUser());

        var result = await service.EnsureBoundAsync(CancellationToken.None);

        Assert.False(result.Bound);
        Assert.False(service.IsBound);
        Assert.Contains("Steam не запущен", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.SteamIdentityFile));
        Assert.Equal(LegacyUuid, ReadLegacyIdentity(paths));
        Assert.Throws<IdentityUnavailableException>(() => service.ResolveContext(new AppSettings()));
    }

    [Fact]
    public async Task ASecondSteamAccountOnTheSameMachine_GetsItsOwnDerivedProfile()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        using var first = CreateService(paths, FirstAccount, "MarkZamore");
        await first.EnsureBoundAsync(CancellationToken.None);

        using var second = CreateService(paths, SecondAccount, "anuvenn");
        var result = await second.EnsureBoundAsync(CancellationToken.None);

        Assert.True(result.Bound);
        Assert.Equal(IdentityBindingSource.Derived, result.Source);
        Assert.NotEqual(LegacyUuid, second.Binding!.PlayerUuid);

        var document = new SteamIdentityStore(paths).TryLoad();
        Assert.Equal(2, document!.Bindings.Count);
        Assert.Equal(
            [LegacyUuid, second.Binding.PlayerUuid],
            document.Bindings.Select(binding => binding.PlayerUuid));
    }

    [Theory]
    [InlineData(IdentityConflictDecision.KeepLegacy)]
    [InlineData(IdentityConflictDecision.UseDerived)]
    public async Task WhenBothHistoriesExist_ThePlayerChooses(IdentityConflictDecision decision)
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        Assert.True(SteamId64.TryFrom(FirstAccount, out var steamId));
        var derived = SteamIdentityDerivation.DeriveMinecraftUuid(steamId);
        WriteWorldProfile(paths, "Chebupeli", derived);
        var resolver = new ScriptedResolver(decision);
        using var service = CreateService(paths, FirstAccount, "MarkZamore", resolver);

        var result = await service.EnsureBoundAsync(CancellationToken.None);

        Assert.True(result.Bound);
        Assert.True(result.ConflictResolved);
        Assert.Equal(
            decision == IdentityConflictDecision.KeepLegacy ? LegacyUuid : derived,
            service.Binding!.PlayerUuid);
        Assert.Equal(decision, service.Binding.ConflictDecision);
        Assert.Equal(LegacyUuid, service.Binding.LegacyPlayerUuid);
        Assert.Contains("Chebupeli", resolver.LastConflict!.ConflictingWorlds);
        // Both profiles stay on disk whichever way the player chose.
        Assert.Equal(LegacyUuid, ReadLegacyIdentity(paths));
        Assert.True(File.Exists(Path.Combine(paths.Worlds, "Chebupeli", "playerdata", $"{derived:D}.dat")));
    }

    [Fact]
    public async Task CancellingTheConflict_LeavesTheMachineUnbound()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        Assert.True(SteamId64.TryFrom(FirstAccount, out var steamId));
        WriteWorldProfile(paths, "Chebupeli", SteamIdentityDerivation.DeriveMinecraftUuid(steamId));
        using var service = CreateService(
            paths, FirstAccount, "MarkZamore", new ScriptedResolver(IdentityConflictDecision.Cancel));

        var result = await service.EnsureBoundAsync(CancellationToken.None);

        Assert.False(result.Bound);
        Assert.False(File.Exists(paths.SteamIdentityFile));
        Assert.Equal(LegacyUuid, ReadLegacyIdentity(paths));
    }

    [Fact]
    public async Task ADamagedBindingFile_StopsTheLauncherInsteadOfRebinding()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        File.WriteAllText(paths.SteamIdentityFile, "{ this is not json");
        using var service = CreateService(paths, FirstAccount, "MarkZamore");

        await Assert.ThrowsAsync<IdentityUnavailableException>(
            () => service.EnsureBoundAsync(CancellationToken.None));
        Assert.Equal("{ this is not json", File.ReadAllText(paths.SteamIdentityFile));
    }

    [Fact]
    public async Task AFileFromAnotherSchema_IsNeverOverwritten()
    {
        var paths = CreatePaths();
        var future = """{"schemaVersion":99,"bindings":[]}""";
        File.WriteAllText(paths.SteamIdentityFile, future);
        using var service = CreateService(paths, FirstAccount, "MarkZamore");

        var failure = await Assert.ThrowsAsync<IdentityUnavailableException>(
            () => service.EnsureBoundAsync(CancellationToken.None));
        Assert.Contains("другой версией лаунчера", failure.Message, StringComparison.Ordinal);
        Assert.Equal(future, File.ReadAllText(paths.SteamIdentityFile));
    }

    [Fact]
    public async Task TheResolvedContext_CarriesBothIdentities()
    {
        var paths = CreatePaths();
        WriteLegacyIdentity(paths, LegacyUuid);
        using var service = CreateService(paths, FirstAccount, "MarkZamore");
        await service.EnsureBoundAsync(CancellationToken.None);

        var context = service.ResolveContext(new AppSettings { PlayerName = "MarkZamore" });

        Assert.Equal(LegacyUuid.ToString("D"), context.MinecraftUuid);
        Assert.Equal(LegacyUuid, context.PlayerUuid);
        Assert.Equal(FirstAccount, context.SteamId64.Value);
        Assert.Equal("MarkZamore", context.IdentityName);
        Assert.Equal(40, context.SessionAccessToken.Length);
        Assert.Equal(IdentityBindingSource.MigratedUuidJson, context.Source);
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    private static SteamIdentityService CreateService(
        AppPaths paths,
        ulong steamId64,
        string persona,
        IIdentityConflictResolver? resolver = null) =>
        new(paths, new FakeSteamUser(steamId64, persona), null, resolver);

    private static void WriteLegacyIdentity(AppPaths paths, Guid playerUuid) =>
        File.WriteAllText(paths.IdentityFile, JsonSerializer.Serialize(
            new PortableIdentity
            {
                SchemaVersion = 1,
                PlayerUuid = playerUuid,
                CreatedAtUtc = DateTimeOffset.UtcNow
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static Guid ReadLegacyIdentity(AppPaths paths) => ReadLegacyIdentity(paths.IdentityFile);

    private static Guid ReadLegacyIdentity(string path) =>
        JsonSerializer.Deserialize<PortableIdentity>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.PlayerUuid;

    private static void WriteWorldProfile(AppPaths paths, string worldName, Guid playerUuid)
    {
        var directory = Path.Combine(paths.Worlds, worldName, "playerdata");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, $"{playerUuid:D}.dat"), [1, 2, 3]);
    }

    private sealed class FakeSteamUser(ulong steamId64 = 0, string personaName = "") : ISteamUserSource
    {
        public bool TryGetLocalUser(out ulong id, out string persona)
        {
            id = steamId64;
            persona = personaName;
            return steamId64 != 0;
        }
    }

    private sealed class ScriptedResolver(IdentityConflictDecision decision) : IIdentityConflictResolver
    {
        public IdentityConflict? LastConflict { get; private set; }

        public Task<IdentityConflictDecision> ResolveAsync(IdentityConflict conflict, CancellationToken token)
        {
            LastConflict = conflict;
            return Task.FromResult(decision);
        }
    }
}
