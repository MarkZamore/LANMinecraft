using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Saying when a world handover will be over, rather than when its current step
/// will be.
///
/// The two are far apart: copying a world aside runs at disk speed, sending it
/// runs at the relay's, and the bar that is measuring one of them says nothing
/// about the other. So the estimate is not a division of the bar - it is a
/// proportion of the whole, taken from the shape of past handovers and the time
/// spent so far.
/// </summary>
public sealed class TransferPacingTests
{
    private static readonly string[] SenderLabels = [.. TransferPacing.Sending];

    /// <summary>
    /// A measured handover teaches the pacing even when the timings begin with a
    /// step neither side names.
    /// </summary>
    /// <remarks>
    /// Blend used to pick the side out of whichever key the dictionary handed
    /// over first. A key that names no step of either pipeline made that lookup
    /// fail, and a failed lookup returns the pacing untouched - so the
    /// measurement was dropped and every estimate a player saw came from the
    /// numbers this shipped with rather than from their own transfers. The run
    /// knows which side it was; it says so now.
    /// </remarks>
    [Fact]
    public void TimingsThatStartWithAnUnknownStep_AreStillLearnedFrom()
    {
        var last = TransferPacing.Sending[^1];
        var timings = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Что-то ещё"] = 4,
            [TransferPacing.Sending[0]] = 30,
            [last] = 12
        };

        var told = new TransferPacing().Blend(timings, last);
        Assert.NotEqual(
            new TransferPacing().FractionDone([TransferPacing.Sending[0]], last, 0),
            told.FractionDone([TransferPacing.Sending[0]], last, 0));

        // And a caller with no stage to give reads every key rather than the
        // first, so one unrecognised name is no longer the whole answer.
        var guessed = new TransferPacing().Blend(timings);
        Assert.Equal(
            told.FractionDone([TransferPacing.Sending[0]], last, 0),
            guessed.FractionDone([TransferPacing.Sending[0]], last, 0));
    }

    /// <summary>Each side of a handover is recognised from any step of it.</summary>
    [Fact]
    public void AStepNamesTheSideItBelongsTo()
    {
        Assert.Same(TransferPacing.Sending, TransferPacing.PipelineFor("Отправка мира"));
        Assert.Same(TransferPacing.Receiving, TransferPacing.PipelineFor("Получение мира"));
        Assert.Null(TransferPacing.PipelineFor("Что-то ещё"));
    }

    /// <summary>
    /// A run through every step of one side has to arrive at exactly all of it -
    /// otherwise the estimate would never reach zero, or would reach it early.
    /// </summary>
    [Fact]
    public void WalkingTheWholePipeline_EndsAtOne()
    {
        var pacing = new TransferPacing();
        var done = new List<string>();
        double last = 0;
        foreach (var stage in TransferPacing.Sending)
        {
            var atStart = pacing.FractionDone(done, stage, 0);
            Assert.True(atStart >= last, $"{stage} went backwards: {atStart} after {last}");
            last = pacing.FractionDone(done, stage, 1);
            done.Add(stage);
        }
        Assert.Equal(1, last, 6);
    }

    /// <summary>
    /// The two sides are held apart. A sender only ever visits its own eight
    /// steps, so its fraction must not be diluted by the receiver's.
    /// </summary>
    [Fact]
    public void OneSideDoesNotCountAgainstTheOther()
    {
        var pacing = new TransferPacing();
        var sending = pacing.FractionDone(["Копирование мира"], "Подготовка профилей", 0);
        var receiving = pacing.FractionDone(["Копирование у отправителя"], "Профили у отправителя", 0);
        Assert.InRange(sending, 0.05, 0.15);
        Assert.InRange(receiving, 0.05, 0.15);
    }

    /// <summary>
    /// A finished handover moves the shape towards what it actually spent, and
    /// leaves the other side's steps alone: watching one end of a transfer must
    /// not erase what is known about the other.
    /// </summary>
    [Fact]
    public void AFinishedRun_MovesTheShapeAndLeavesTheOtherSideAlone()
    {
        var pacing = new TransferPacing();
        var before = pacing.WeightOf("Получение мира");

        // A run where sending took almost all of it, unlike the shipped guess.
        var blended = pacing.Blend(new Dictionary<string, double>
        {
            ["Копирование мира"] = 1,
            ["Подготовка профилей"] = 1,
            ["Сжатие мира"] = 1,
            ["Отправка мира"] = 200,
            ["Перенос исходного мира"] = 1,
            ["Распаковка у получателя"] = 1,
            ["Проверка у получателя"] = 1,
            ["Установка у получателя"] = 1
        });

        Assert.True(
            blended.WeightOf("Отправка мира") > pacing.WeightOf("Отправка мира"),
            "the step that actually took the time should weigh more afterwards");
        Assert.True(
            blended.WeightOf("Сжатие мира") < pacing.WeightOf("Сжатие мира"),
            "the step that turned out to be quick should weigh less");
        Assert.Equal(before, blended.WeightOf("Получение мира"));
    }

    /// <summary>
    /// A step nobody has ever timed still weighs something. A weight of zero
    /// would make a run that flies through it divide by nothing.
    /// </summary>
    [Fact]
    public void AStepThatTookNoTime_StillWeighsSomething()
    {
        var blended = new TransferPacing().Blend(
            SenderLabels.ToDictionary(stage => stage, stage => stage == "Отправка мира" ? 100d : 0d));

        Assert.All(SenderLabels, stage => Assert.True(blended.WeightOf(stage) > 0, stage));
    }

    /// <summary>Nothing is said for the first seconds; the division is all noise there.</summary>
    [Fact]
    public void EarlyOn_ItSaysNothing()
    {
        var clock = TimeSpan.Zero;
        var run = new TransferRun(new TransferPacing(), () => clock);

        Assert.Null(run.Advance("Копирование мира", 0, 1_000_000));
        clock = TimeSpan.FromSeconds(1);
        Assert.Null(run.Advance("Копирование мира", 500_000, 1_000_000));
    }

    /// <summary>
    /// The whole point: one step finished, and the answer covers the seven that
    /// have not started. Copying is about a twelfth of a handover, so a minute
    /// of it means roughly eleven more.
    /// </summary>
    [Fact]
    public void OneStepIn_TheAnswerCoversTheRest()
    {
        var clock = TimeSpan.Zero;
        var run = new TransferRun(new TransferPacing(), () => clock);
        run.Advance("Копирование мира", 0, 1_000_000);

        clock = TimeSpan.FromSeconds(60);
        var remaining = run.Advance("Копирование мира", 1_000_000, 1_000_000);

        Assert.NotNull(remaining);
        // Copying is 0.08 of the guessed shape: 60s of it puts the whole at 750s.
        Assert.InRange(remaining!.Value.TotalSeconds, 600, 800);
    }

    /// <summary>
    /// The answer only ever shrinks its way to the end: a step whose byte total
    /// arrives late must not undo the step before it.
    /// </summary>
    [Fact]
    public void TheAnswerNeverWalksBackwards()
    {
        var clock = TimeSpan.Zero;
        var run = new TransferRun(new TransferPacing(), () => clock);
        double previous = 0;

        // The estimate counts from the first step of the handover, so the
        // handover is started at zero: that puts its clock and this one on the
        // same origin, and elapsed + remaining is the whole again.
        run.Advance(TransferPacing.Sending[0], 0, 0);

        foreach (var stage in TransferPacing.Sending)
        {
            foreach (var current in new long[] { 0, 250, 500, 1000 })
            {
                clock += TimeSpan.FromSeconds(5);
                run.Advance(stage, current, current == 0 ? 0 : 1000);
                var elapsed = clock.TotalSeconds;
                var reached = run.Advance(stage, current, current == 0 ? 0 : 1000) is { } left
                    ? elapsed / (elapsed + left.TotalSeconds)
                    : 0;
                Assert.True(reached >= previous - 1e-9, $"{stage} at {current} went backwards");
                if (reached > 0) previous = reached;
            }
        }
    }

    /// <summary>
    /// Only a handover that reached its last step is worth learning from. One
    /// that was cancelled halfway says nothing about how long the whole takes.
    /// </summary>
    [Fact]
    public void OnlyARunThatReachedTheEndCountsAsFinished()
    {
        var clock = TimeSpan.Zero;
        var run = new TransferRun(new TransferPacing(), () => clock);
        run.Advance("Отправка мира", 1, 2);
        Assert.False(run.Completed);

        run.Advance(TransferPacing.Sending[^1], 1, 1);
        Assert.True(run.Completed);
    }

    /// <summary>
    /// Waiting for a friend to answer a dialog is not work, and it must not be
    /// read as any. The estimate counts from the first step of the handover, so
    /// the same copying gives the same answer whether the wait before it was a
    /// second or a minute.
    /// </summary>
    [Fact]
    public void TheWaitBeforeTheHandoverIsNotCountedAsWork()
    {
        var clock = TimeSpan.Zero;
        var patient = new TransferRun(new TransferPacing(), () => clock);
        // Two minutes of somebody deciding, then a minute of copying.
        patient.Advance("Проверка получателя", 0, 0);
        patient.Advance("Ожидание ответа получателя", 0, 0);
        clock = TimeSpan.FromSeconds(120);
        patient.Advance("Копирование мира", 0, 1_000_000);
        clock = TimeSpan.FromSeconds(180);
        var afterWaiting = patient.Advance("Копирование мира", 1_000_000, 1_000_000);

        var prompt = new TransferRun(new TransferPacing(), () => clock);
        clock = TimeSpan.Zero;
        prompt.Advance("Копирование мира", 0, 1_000_000);
        clock = TimeSpan.FromSeconds(60);
        var withoutWaiting = prompt.Advance("Копирование мира", 1_000_000, 1_000_000);

        Assert.NotNull(afterWaiting);
        Assert.NotNull(withoutWaiting);
        Assert.Equal(
            withoutWaiting!.Value.TotalSeconds,
            afterWaiting!.Value.TotalSeconds,
            1);
    }

    /// <summary>
    /// The four seconds of silence are four seconds of the handover, not four
    /// of waiting: a run that has just started copying says nothing yet, however
    /// long the launcher spent getting there.
    /// </summary>
    [Fact]
    public void TheSilenceAtTheStartIsMeasuredFromTheHandoverToo()
    {
        var clock = TimeSpan.Zero;
        var run = new TransferRun(new TransferPacing(), () => clock);
        run.Advance("Ожидание ответа получателя", 0, 0);
        clock = TimeSpan.FromMinutes(5);
        run.Advance("Копирование мира", 0, 1_000_000);
        clock = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);

        Assert.Null(run.Advance("Копирование мира", 900_000, 1_000_000));
    }

    /// <summary>What each step cost, including the one still on screen.</summary>
    [Fact]
    public void TimingsCloseOffTheStepStillRunning()
    {
        var clock = TimeSpan.Zero;
        var run = new TransferRun(new TransferPacing(), () => clock);
        run.Advance("Копирование мира", 0, 100);
        clock = TimeSpan.FromSeconds(30);
        run.Advance("Сжатие мира", 0, 100);
        clock = TimeSpan.FromSeconds(50);

        var timings = run.Timings();
        Assert.Equal(30, timings["Копирование мира"], 3);
        Assert.Equal(20, timings["Сжатие мира"], 3);
    }
}
