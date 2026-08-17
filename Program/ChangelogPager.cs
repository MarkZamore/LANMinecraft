namespace Minecraft;

/// <summary>
/// Hands out the version history a page at a time, newest first.
///
/// The list only grows - a release a day means a thousand entries in three
/// years - and a panel that builds every one of them on startup pays for
/// history nobody scrolled to. Fifty are enough to fill the column; the next
/// fifty arrive when the reader reaches the end, and the scrollbar shrinks as
/// they do, which is the honest signal that there is more behind it.
/// </summary>
internal sealed class ChangelogPager
{
    /// <summary>Entries per page; a little more than the column can show at once.</summary>
    public const int PageSize = 50;

    private readonly IReadOnlyList<ChangelogEntry> _all;

    public ChangelogPager(IReadOnlyList<ChangelogEntry> all, int pageSize = PageSize)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _all = all;
        Size = pageSize;
    }

    /// <summary>How many entries one page holds.</summary>
    public int Size { get; }

    /// <summary>How many have been handed out so far.</summary>
    public int Shown { get; private set; }

    /// <summary>True while the history has entries nobody has been given yet.</summary>
    public bool HasMore => Shown < _all.Count;

    /// <summary>The whole history, newest first, however much of it is shown.</summary>
    public int Total => _all.Count;

    /// <summary>The next page, or nothing when the end has been reached.</summary>
    public IReadOnlyList<ChangelogEntry> Next()
    {
        if (!HasMore) return [];
        var page = _all.Skip(Shown).Take(Size).ToList();
        Shown += page.Count;
        return page;
    }
}
