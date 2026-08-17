using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A launcher closed while the game plays leaves the game behind; the next one
/// has to recognise it rather than offer a second client over the first. The
/// note on disk carries a process id, and ids are reused, so what makes it
/// trustworthy is the moment the process started.
/// </summary>
public sealed class ClientPresenceTests
{
    private static readonly DateTime Started = new(2026, 8, 18, 3, 45, 12, DateTimeKind.Utc);

    [Fact]
    public void TheSameProcess_IsRecognised()
    {
        var session = new ClientPresenceService.ClientSession(4242, Started, "LL8");

        Assert.True(ClientPresenceService.IsStillRunning(session, ("javaw", Started)));
    }

    /// <summary>Windows hands the id to someone else once the game is gone.</summary>
    [Fact]
    public void AReusedId_IsNotTheGame()
    {
        var session = new ClientPresenceService.ClientSession(4242, Started, "LL8");

        Assert.False(ClientPresenceService.IsStillRunning(session, ("javaw", Started.AddMinutes(20))));
        Assert.False(ClientPresenceService.IsStillRunning(session, ("notepad", Started)));
        Assert.False(ClientPresenceService.IsStillRunning(session, null));
    }

    /// <summary>
    /// Process start times keep more precision than a round trip through JSON,
    /// so the comparison has a second of slack - and no more.
    /// </summary>
    [Fact]
    public void ASecondOfSlack_IsAllowed()
    {
        var session = new ClientPresenceService.ClientSession(7, Started, "LL8");

        Assert.True(ClientPresenceService.IsStillRunning(session, ("java", Started.AddMilliseconds(900))));
        Assert.False(ClientPresenceService.IsStillRunning(session, ("java", Started.AddSeconds(5))));
    }
}
