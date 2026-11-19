using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// One launcher at a time. Two copies share a settings file, an instance folder
/// and a Steam connection, and the second one to write wins - so the second copy
/// must not start at all, and the icon that started it must bring the first one
/// forward instead.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    // A name of this test run's own: a real launcher is usually running on the
    // machine these are written on, and it holds the launcher's own name.
    private readonly string _scope = "." + Guid.NewGuid().ToString("N");

    [Fact]
    public void WhileOneIsHeld_AnotherCannotBeTaken()
    {
        using var first = SingleInstanceGuard.TryAcquire(_scope);
        Assert.NotNull(first);

        var second = SingleInstanceGuard.TryAcquire(_scope);

        Assert.Null(second);
    }

    /// <summary>
    /// And it is a guard, not a lock nobody can ever take again: a launcher that
    /// closes hands it back, and the next one starts normally.
    /// </summary>
    [Fact]
    public void OnceItIsGivenBack_TheNextOneStarts()
    {
        var first = SingleInstanceGuard.TryAcquire(_scope);
        Assert.NotNull(first);
        first.Dispose();

        using var second = SingleInstanceGuard.TryAcquire(_scope);

        Assert.NotNull(second);
    }

    /// <summary>
    /// The second copy's whole job before it exits: ask the running one to show
    /// itself. The request has to arrive, or pressing the icon would look like
    /// nothing happened at all.
    /// </summary>
    [Fact]
    public void TheSecondCopy_AsksTheRunningOneToComeForward()
    {
        using var running = SingleInstanceGuard.TryAcquire(_scope);
        Assert.NotNull(running);
        using var asked = new ManualResetEventSlim(false);
        running.AnotherInstanceStarted += () => asked.Set();

        Assert.Null(SingleInstanceGuard.TryAcquire(_scope));
        SingleInstanceGuard.AskRunningInstanceToShowItself(_scope);

        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)), "the running launcher was never asked to show itself");
    }

    /// <summary>
    /// Asking when nobody is listening is what happens on the very first start,
    /// and it must be silent rather than an error.
    /// </summary>
    [Fact]
    public void AskingWithNobodyRunning_DoesNothing()
    {
        SingleInstanceGuard.AskRunningInstanceToShowItself(_scope);
    }
}
