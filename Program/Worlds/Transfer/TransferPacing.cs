namespace Minecraft;

/// <summary>
/// How a world handover spends its time, so the bar can say when all of it
/// will be over rather than only the phase in front of it.
///
/// Handing a world over is five passes across the same gigabytes - copy it
/// aside, compress it, send it, unpack it, hash it - at five speeds that have
/// nothing to do with each other. A disk that copies at 800 MB/s says nothing
/// about a Steam relay that sends at 5, so no amount of watching one phase
/// predicts the next.
///
/// What does carry over is the shape: the share of the whole that each phase
/// takes. Hold that shape and the arithmetic needs no speeds at all - if a
/// third of the work is done and it took a minute, there are two minutes left.
/// The shape is learned from the transfers this launcher has actually done and
/// starts from the numbers below, which are a guess and are meant to be
/// replaced by the first handover that finishes.
/// </summary>
internal sealed class TransferPacing
{
    /// <summary>
    /// What the bar is called at each step of sending a world. These are the
    /// labels <see cref="WorldTransferService"/> publishes, in the order they
    /// appear; the last one is how a run is known to have finished.
    /// </summary>
    public static readonly IReadOnlyList<string> Sending =
    [
        "Копирование мира",
        "Подготовка профилей",
        "Сжатие мира",
        "Отправка мира",
        "Перенос исходного мира",
        "Распаковка у получателя",
        "Проверка у получателя",
        "Установка у получателя"
    ];

    /// <summary>The same handover watched from the other end.</summary>
    public static readonly IReadOnlyList<string> Receiving =
    [
        "Копирование у отправителя",
        "Профили у отправителя",
        "Сжатие у отправителя",
        "Получение мира",
        "Распаковка мира",
        "Проверка мира",
        "Подготовка профилей",
        "Завершение у отправителя",
        "Установка мира"
    ];

    /// <summary>
    /// A first guess at the shape, for a launcher that has never finished a
    /// transfer: sending over a relay dominates, and the two passes over the
    /// world on either side of it cost about as much as each other.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> Guess =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Копирование мира"] = 0.08,
            ["Подготовка профилей"] = 0.01,
            ["Сжатие мира"] = 0.13,
            ["Отправка мира"] = 0.55,
            ["Перенос исходного мира"] = 0.01,
            ["Распаковка у получателя"] = 0.12,
            ["Проверка у получателя"] = 0.07,
            ["Установка у получателя"] = 0.03,

            ["Копирование у отправителя"] = 0.08,
            ["Профили у отправителя"] = 0.01,
            ["Сжатие у отправителя"] = 0.13,
            ["Получение мира"] = 0.55,
            ["Распаковка мира"] = 0.12,
            ["Проверка мира"] = 0.07,
            ["Завершение у отправителя"] = 0.01,
            ["Установка мира"] = 0.02
        };

    /// <summary>How much of the whole a step nobody has a number for is worth.</summary>
    private const double Unnamed = 0.01;

    /// <summary>
    /// How much the newest transfer is allowed to move the shape. Half: two
    /// handovers in a row over the same link settle it, and one unusual run -
    /// a paused download, a laptop that slept - cannot take it over.
    /// </summary>
    private const double NewWeight = 0.5;

    public TransferPacing() : this(Guess)
    {
    }

    public TransferPacing(IReadOnlyDictionary<string, double> weights)
    {
        Weights = new Dictionary<string, double>(
            weights.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value),
            StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, double> Weights { get; }

    /// <summary>Which of the two orders a run is following, from any step in it.</summary>
    public static IReadOnlyList<string>? PipelineFor(string stage) =>
        Sending.Contains(stage, StringComparer.Ordinal) ? Sending
        : Receiving.Contains(stage, StringComparer.Ordinal) ? Receiving
        : null;

    public double WeightOf(string stage) =>
        Weights.TryGetValue(stage, out var weight) ? weight : Unnamed;

    /// <summary>
    /// How much of the whole handover is behind us: every step already finished,
    /// plus how far the current one has got. A step whose byte total is not
    /// known yet counts as not started, so the answer only ever grows.
    /// </summary>
    public double FractionDone(
        IReadOnlyCollection<string> finished, string current, double currentProgress)
    {
        var pipeline = PipelineFor(current) ?? PipelineFor(finished.FirstOrDefault() ?? "");
        if (pipeline is null) return 0;

        var whole = pipeline.Sum(WeightOf);
        if (whole <= 0) return 0;

        var done = finished.Where(stage => pipeline.Contains(stage, StringComparer.Ordinal)).Sum(WeightOf);
        done += WeightOf(current) * Math.Clamp(currentProgress, 0, 1);
        return Math.Clamp(done / whole, 0, 1);
    }

    /// <summary>
    /// The shape after a finished transfer: what it actually spent, folded into
    /// what was believed before it. Steps the run never reached keep the shape
    /// they had, so watching one side of a handover does not erase the other.
    /// </summary>
    /// <param name="pipelineStage">
    /// A step of the side that was watched - the run's own last step, which
    /// <c>TransferRun.Completed</c> has already checked is the last step of a
    /// pipeline. This used to be guessed from whichever key a dictionary
    /// happened to hand over first, and a key that named no step of either side
    /// made the guess fail; a failed guess returns the pacing unchanged, so the
    /// measurement was thrown away and every estimate a player ever saw came
    /// from the numbers this shipped with rather than from their own transfers.
    /// </param>
    public TransferPacing Blend(
        IReadOnlyDictionary<string, double> observedSeconds, string pipelineStage = "")
    {
        ArgumentNullException.ThrowIfNull(observedSeconds);
        var total = observedSeconds.Values.Where(seconds => seconds > 0).Sum();
        if (total <= 0) return this;

        // Every key rather than the first, for a caller that has no stage to
        // give: one unrecognised name is then no longer the whole answer.
        var pipeline = PipelineFor(pipelineStage)
            ?? observedSeconds.Keys
                .Select(PipelineFor)
                .FirstOrDefault(found => found is not null);
        if (pipeline is null) return this;

        // Renormalising against this side's share keeps the other side's steps
        // comparable with the ones just measured.
        var share = pipeline.Sum(WeightOf);
        var blended = new Dictionary<string, double>(Weights, StringComparer.Ordinal);
        foreach (var stage in pipeline)
        {
            observedSeconds.TryGetValue(stage, out var seconds);
            var measured = Math.Max(seconds, 0) / total * share;
            blended[stage] = WeightOf(stage) * (1 - NewWeight) + measured * NewWeight;
            // A step that takes no measurable time still has to be worth
            // something, or a run that flies through it divides by nothing.
            if (blended[stage] < Unnamed) blended[stage] = Unnamed;
        }
        return new TransferPacing(blended);
    }
}
