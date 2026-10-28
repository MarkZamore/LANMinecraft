using System.Reflection;
using System.Diagnostics;
using Minecraft;

namespace Minecraft.Tests;

public sealed class IdentityAdapterGuestMovementHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "movement-helper-" + Guid.NewGuid().ToString("N"));
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task TransformerAndPreflight_EnforceExclusiveMovementSites()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "tools", "Test-GuestMovement.ps1")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? @"C:\Tools\jdk-25.0.2";
        Assert.True(File.Exists(Path.Combine(javaHome, "bin", "javac.exe")), "Guest movement bytecode regression needs JAVA_HOME pointing to a JDK.");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(repository.FullName, "tools", "Test-GuestMovement.ps1"), "-JavaHome", javaHome,
                     "-OutputDirectory", _root }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var diagnostic = await output + Environment.NewLine + await error;
        Assert.True(process.ExitCode == 0, diagnostic);
        Assert.Contains("PASS guest movement:", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(Z)Z", true)]
    [InlineData("()Z", false)]
    public void MovementHelper_IsMappedOnlyWithItsExactNativeDescriptor(string descriptor, bool expected)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "mappings.txt");
        File.WriteAllText(file, $$"""
            tsrg2 obf mojmap
            listener net/minecraft/server/network/ServerGamePacketListenerImpl
            	a (Lplayer;)V handleMovePlayer
            	a (Lvehicle;)V handleMoveVehicle
            	b ()Z isSingleplayerOwner
            	c {{descriptor}} shouldCheckPlayerMovement
            	d (DDDFFZZ)V handlePlayerPositionChange
            	e (Lteleport;)V handleAcceptTeleportPacket
            player net/minecraft/network/protocol/game/ServerboundMovePlayerPacket
            vehicle net/minecraft/network/protocol/game/ServerboundMoveVehiclePacket
            teleport net/minecraft/network/protocol/game/ServerboundAcceptTeleportationPacket
            """);
        var service = typeof(IdentityAdapterMappingService);
        var names = service.GetNestedType("RuntimeNames", BindingFlags.NonPublic)!;
        var mapping = names.GetMethod("Read", Hidden)!.Invoke(null, [file, null]);
        var properties = new Dictionary<string, string>();
        Assert.True((bool)service.GetMethod("AddGuestMovementProperties", Hidden)!.Invoke(null, [mapping, properties])!);
        Assert.Equal(expected, properties.ContainsKey("movementCheckHelperMethods"));
        if (expected)
        {
            Assert.Equal("shouldCheckPlayerMovement,c", properties["movementCheckHelperMethods"]);
            Assert.Equal("(Z)Z", properties["movementCheckHelperDescriptors"]);
            Assert.Equal("handlePlayerPositionChange,d", properties["movementPositionMethods"]);
            Assert.Equal("(DDDFFZZ)V", properties["movementPositionDescriptors"]);
            Assert.Equal("handleAcceptTeleportPacket,e", properties["movementTeleportAckMethods"]);
            Assert.Equal("(Lnet/minecraft/network/protocol/game/ServerboundAcceptTeleportationPacket;)V,(Lteleport;)V",
                properties["movementTeleportAckDescriptors"]);
        }
    }
}
