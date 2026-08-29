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
    [Fact]
    public void WhileOneIsHeld_AnotherCannotBeTaken()
    {
        using var first = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(first);

        var second = SingleInstanceGuard.TryAcquire();

        Assert.Null(second);
    }

    /// <summary>
    /// And it is a guard, not a lock nobody can ever take again: a launcher that
    /// closes hands it back, and the next one starts normally.
    /// </summary>
    [Fact]
    public void OnceItIsGivenBack_TheNextOneStarts()
    {
        var first = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(first);
        first.Dispose();

        using var second = SingleInstanceGuard.TryAcquire();

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
        using var running = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(running);
        using var asked = new ManualResetEventSlim(false);
        running.AnotherInstanceStarted += () => asked.Set();

        Assert.Null(SingleInstanceGuard.TryAcquire());
        SingleInstanceGuard.AskRunningInstanceToShowItself();

        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)), "the running launcher was never asked to show itself");
    }

    /// <summary>
    /// Asking when nobody is listening is what happens on the very first start,
    /// and it must be silent rather than an error.
    /// </summary>
    [Fact]
    public void AskingWithNobodyRunning_DoesNothing()
    {
        SingleInstanceGuard.AskRunningInstanceToShowItself();
    }
}
