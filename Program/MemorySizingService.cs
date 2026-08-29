using System.Runtime.InteropServices;

namespace Minecraft;

/// <summary>
/// How much memory the game is allowed, and how that number is divided.
///
/// The number a player sets is the Java heap, and nothing is taken out of it:
/// it goes to <c>-Xmx</c> as it stands, which is the number the game prints on
/// F3 and in a crash report. The room beside the heap - class data, compiled
/// code, thread stacks, and above all the buffers Sodium hands the graphics
/// driver, which no Java setting bounds - is real and still has to come from
/// somewhere, so it is kept out of the largest number the field will accept
/// rather than out of the number in it.
///
/// The ceiling is the machine and nothing else: everything installed, less
/// what is left to the operating system. A 32 GB computer offers 28, whichever
/// build is selected and whether or not it has ever been played, which is what
/// every other Minecraft launcher offers and the only ceiling that does not
/// move under a player between sessions. What the pack holds beside its heap is
/// said rather than deducted - the field's own text names it, the log names it,
/// and the launcher still says so before a launch that cannot work.
///
/// Every rule here reads a <see cref="PackMemoryProfile"/> rather than a
/// constant, because the same launcher runs vanilla on an old version and packs
/// heavier than Limitless 8 on new ones, and those do not need the same room to
/// a factor of eight.
///
/// The machine has a say in one thing beside its installed memory: the card.
/// What does not fit in video memory the driver keeps in system memory instead,
/// and that copy is the game's, so a pack on an eight gigabyte card holds room
/// beside its heap that the same pack on a sixteen does not. A
/// <see cref="VideoMemoryProfile"/> nobody could read is charged nothing.
///
/// All of that is estimate, and estimate is what a launcher does before it has
/// watched anything. Once a pack has been played through on a machine there is
/// a <see cref="MeasuredMemoryProfile"/> for the pair, and it wins: it is the
/// room the game was seen holding rather than the room a model says it should,
/// and it already contains the card, the drivers and this Windows, so nothing
/// is added to it for any of them. Limitless 8 on a 24 GB budget was estimated
/// at 12 GB beside its heap - eight for the pack, four for an eight gigabyte
/// card - and left with a 12 GB heap it filled to 11.5. Measured at 7533 MB, it
/// keeps 9 and plays in 15.
/// </summary>
public static class MemorySizingService
{
    /// <summary>The smallest heap worth starting with, and the floor of the field.</summary>
    public const int MinHeapGb = 2;
    /// <summary>The largest heap any arithmetic here may produce.</summary>
    public const int MaxHeapGb = 128;
    /// <summary>
    /// Past this a larger heap buys garbage-collection pauses rather than
    /// comfort, so it bounds what the launcher suggests - not what a player may
    /// type.
    /// </summary>
    /// <remarks>
    /// It was sixteen, and sixteen was written down when the only thing anyone
    /// had seen a large heap do was pause. Then Limitless 8 was watched at the
    /// number this file recommends for it - 1128 mods, which the per-mod rule
    /// below turns into a 12 GB heap - and that heap was not large, it was
    /// full: spark reported 11.5 GB of the 12 in use, AllTheLeaks warned at
    /// 95%, and the full collections that came of it ran for 2.2 seconds. The
    /// pauses the old ceiling was protecting anyone from are what a heap this
    /// tight produces, not what a roomy one does.
    ///
    /// So sixteen is only thirty per cent above the pack the whole model was
    /// calibrated on, and the next pack up would meet the ceiling rather than
    /// its own arithmetic. Twenty is where a heap stops being about this game:
    /// a 32 GB machine may be asked for 24 GB altogether and this pack holds
    /// eight or nine of them beside the heap, so nobody reaches twenty by
    /// accident - it binds from 48 GB installed upwards, on packs half again
    /// the size of the largest one on record. G1 is asked for 32 MB regions and
    /// a 40 ms pause goal here, which is a collector sized for exactly that.
    /// </remarks>
    public const int MaxRecommendedHeapGb = 20;

    private const double BytesPerGb = 1024d * 1024d * 1024d;
    private const double BytesPerMb = 1024d * 1024d;

    /// <summary>
    /// How much of a machine a pack nobody has weighed is offered. Two thirds
    /// is generous on purpose: the pack the first press of Play downloads is a
    /// large one, and the first launch that can see it replaces this with its
    /// own number anyway.
    /// </summary>
    private const double UnseenPackShareOfMachine = 2d / 3d;

    // The pack-weight model, in megabytes. Calibrated against the one pack that
    // has been measured: Limitless 8, 1128 mods and 1.9 GB of jars, held almost
    // eight gigabytes outside a twelve gigabyte heap. The terms are what that
    // memory is made of, so the numbers carry to a pack of another shape: a
    // base every client pays, a per-mod cost (classes, mixins, threads), a
    // share of the jar bytes (class data and the models inside them), and a
    // share of the texture the pack ships loose.
    //
    // The per-mod numbers look oddly precise because they are the old ones
    // divided by 1.279. They were fitted when a "mod" meant a file in the mods
    // folder, and a mod is not a file: mods carry other mods inside themselves,
    // and Limitless 8's 882 files are 1128 mods. Dividing by its own ratio
    // leaves that pack's answers exactly where they were measured, and lets
    // every other pack be counted the way the loader counts it. All The Fabric 3
    // is 95 files and 287 mods, and counting files under-charged it by 1373 MB
    // of the 1129 it went over its budget by.
    private const int NativeBaseMb = 1024;
    private const int OlderMinecraftNativeBaseMb = 768;
    private const double NativePerModMb = 3.909;
    private const double NativePerJarMegabyte = 1.2;
    private const double NativePerAssetMegabyte = 0.5;

    // And what the heap itself wants, which grows with the number of mods
    // rather than with their size: registries, block states, chunk data and the
    // entities of every mod that adds any.
    private const int HeapBaseMb = 2048;
    private const int OlderMinecraftHeapBaseMb = 1024;
    private const double HeapPerModMb = 9.383;

    // And what the pack hands the card. A modern client keeps a couple of
    // gigabytes of atlases and buffers there before a single mod is added;
    // every mod brings its own textures, models and entity skins; and a
    // resource or shader pack is texture almost entirely, which the card holds
    // uncompressed and mipmapped - several times the bytes it takes on disk.
    // Limitless 8 comes to eleven and a half gigabytes: a 16 GB card holds it,
    // an 8 GB card holds two thirds of it and the driver keeps the rest in
    // system memory.
    private const int VideoBaseMb = 2048;
    private const int OlderMinecraftVideoBaseMb = 512;
    private const double VideoPerModMb = 7.819;
    private const double VideoPerAssetMegabyte = 12;
    /// <summary>
    /// The most a small card is charged. Past this it is not short of room, it
    /// is the wrong card for the pack: the driver starts evicting rather than
    /// mirroring, and charging the whole shortfall would leave a two gigabyte
    /// heap on a machine that could still play something lighter.
    /// </summary>
    private const int MaxVideoSpillGb = 4;

    /// <summary>How much over a measurement its reserve is set; see <see cref="MeasuredReserveGb"/>.</summary>
    private const double MeasuredMargin = 1.1;

    public static int GetWholeGameAllowanceGb()
    {
        try
        {
            return GetWholeGameAllowanceGb(GetTotalPhysicalMemoryBytes());
        }
        catch
        {
            return MaxHeapGb;
        }
    }

    /// <summary>
    /// The largest heap this machine offers: everything installed, less what is
    /// left to the operating system. Nothing about the pack, the card or any
    /// session is in it, which is the point - the number a player may type is a
    /// fact about their computer and does not move under them.
    /// </summary>
    /// <remarks>
    /// It used to be this less the room the pack holds outside its heap, which
    /// made the ceiling a different number for every build and, worse, a number
    /// that changed after an evening of play: the first measurement of a pack
    /// replaced the estimate and the field silently gained or lost gigabytes.
    /// A player who set a number and came back to a smaller one has no way to
    /// tell that from the launcher taking something away.
    ///
    /// So this is what every other Minecraft launcher offers - the machine,
    /// less the operating system - and the room beside the heap is said rather
    /// than enforced: the field's own text names it, the log names it, and the
    /// launcher still warns before a launch that cannot work. Whether to spend
    /// the last gigabytes on a heap is the player's to decide, and they are the
    /// only one who knows what else is running.
    /// </remarks>
    public static int GetAllowedHeapGb()
    {
        try
        {
            return GetAllowedHeapGb(GetTotalPhysicalMemoryBytes());
        }
        catch
        {
            return MaxHeapGb;
        }
    }

    /// <summary>The same ceiling, for a machine of a stated size.</summary>
    public static int GetAllowedHeapGb(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Floor(totalPhysicalMemoryBytes / BytesPerGb);
        return Math.Clamp(installedGb - GetOperatingSystemReserveGb(installedGb), MinHeapGb, MaxHeapGb);
    }

    /// <summary>
    /// What is kept for Windows and whatever else the player has open. An
    /// eighth of the machine, and never less than three gigabytes.
    /// </summary>
    /// <remarks>
    /// Three is the floor because Windows counts what the hardware took for
    /// itself: a laptop sold with eight gigabytes reports seven and a half,
    /// which rounds down to seven, and a fourth gigabyte held back there would
    /// leave less than a pack of fifty mods needs. An eighth is what grows: 32
    /// GB keeps four and offers 28, 64 keeps eight and offers 56. The old rule
    /// held back a quarter, which is a reasonable share of a machine to leave
    /// idle and an unreasonable one to refuse a player who asked for it.
    /// </remarks>
    public static int GetOperatingSystemReserveGb(int installedGb) => Math.Max(3, installedGb / 8);

    public static int GetRecommendedMemoryGb(
        PackMemoryProfile pack,
        VideoMemoryProfile video = default,
        MeasuredMemoryProfile measured = default)
    {
        try
        {
            return GetRecommendedMemoryGb(pack, GetTotalPhysicalMemoryBytes(), video, measured);
        }
        catch
        {
            return MaxRecommendedHeapGb / 2;
        }
    }

    /// <summary>
    /// Installed memory in whole gigabytes, or zero where it could not be read.
    /// Part of the key a measurement is filed under: the same pack on a machine
    /// with half the memory is a machine that pages, not one that holds less.
    /// </summary>
    public static int GetInstalledMemoryGb()
    {
        try
        {
            return (int)Math.Floor(GetTotalPhysicalMemoryBytes() / BytesPerGb);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Installed physical memory, straight from Win32. This used to come from
    /// Microsoft.VisualBasic.Devices.ComputerInfo, which only resolved because
    /// NAudio dragged in the Windows Forms assemblies; the numbers and every
    /// sizing rule below are unchanged.
    /// </summary>
    private static ulong GetTotalPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("Installed memory could not be read.");
        }
        return status.TotalPhysical;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// Everything the pack needs beside its heap. No Java setting bounds the
    /// last of it, so this is not a limit the launcher can impose - it is room
    /// it keeps out of the largest heap it will offer, so that the machine has
    /// somewhere to put it while the number the player sets stays theirs.
    /// </summary>
    /// <remarks>
    /// The whole-game number is consulted in one case only: a pack the launcher
    /// has not been able to look at, and has not watched either. Then it keeps
    /// to the rule it used when it could not tell packs apart at all - half of
    /// what the machine may be asked for, never more than eight - rather than
    /// guessing at a weight.
    /// </remarks>
    public static int GetNativeReserveGb(
        PackMemoryProfile pack,
        int wholeGameGb,
        VideoMemoryProfile video = default,
        MeasuredMemoryProfile measured = default) =>
        pack.IsKnown || measured.IsKnown ? GetNativeReserveGb(pack, video, measured)
        : Math.Clamp(wholeGameGb / 2, 2, 8);

    /// <summary>The same room, for a pack that has been weighed or watched.</summary>
    public static int GetNativeReserveGb(
        PackMemoryProfile pack,
        VideoMemoryProfile video = default,
        MeasuredMemoryProfile measured = default)
    {
        if (!pack.IsKnown && !measured.IsKnown) return GetNativeReserveGb(pack, MaxHeapGb, video);

        // A pack that can no longer be weighed - the folder gone or unreadable -
        // still has its sessions, and for it the ceiling is the whole answer.
        var megabytes = pack.IsKnown
            ? (pack.IsModernMinecraft ? NativeBaseMb : OlderMinecraftNativeBaseMb) +
              (double)pack.ModCount * NativePerModMb +
              pack.ModBytes / BytesPerMb * NativePerJarMegabyte +
              pack.AssetBytes / BytesPerMb * NativePerAssetMegabyte +
              (double)GetVideoSpillGb(pack, video) * 1024d
            : measured.AtMostMb;
        // Rounded up: a reserve set too low is spent by the game anyway - over
        // the budget, and into the memory the machine kept for itself.
        return Math.Clamp(
            (int)Math.Ceiling(HeldWithinWhatWasSeen(megabytes, measured) / 1024d),
            1,
            MaxHeapGb - MinHeapGb);
    }

    /// <summary>
    /// The estimate, held inside what the sessions actually prove: never below
    /// the room one of them was seen holding, never above the room the largest
    /// of them could possibly have held.
    /// </summary>
    /// <remarks>
    /// The measurement used to be the answer outright, and that was one number
    /// too few. A session gives two: everything the process asked to commit,
    /// and everything it had resident. What sits beside the heap is somewhere
    /// between them, and where exactly depends on how full the heap got - which
    /// is the one quantity here the launcher set rather than watched.
    ///
    /// The margin belongs to the floor alone. A floor is what one evening
    /// happened to hold and the next may exceed; a ceiling is already the
    /// largest commit ever seen on this machine, and a tenth on top of that is
    /// a tenth of a gigabyte-rounded reserve given away for nothing. On an
    /// eight gigabyte card the difference is a whole gigabyte of heap: the
    /// estimate of 12 GB, held under a ceiling of 10846 MB, comes to 11 - and
    /// with the margin it came back to 12, which is the number the player had
    /// before any of this was measured.
    ///
    /// Reading the ceiling as the answer is what cut a 24 GB budget to an 8 GB
    /// heap on a machine with a sixteen gigabyte card: the driver's mirror of
    /// that card is committed whether or not anything is in it, so the game was
    /// charged 14 GB beside its heap while holding 3. The estimate is a model
    /// and wrong in its own way - on the pack it was fitted to it was over by
    /// four gigabytes - so neither number is trusted alone. The estimate
    /// answers, and the pair says how far it is allowed to be wrong.
    /// </remarks>
    private static double HeldWithinWhatWasSeen(double estimateMb, MeasuredMemoryProfile measured) =>
        measured.IsKnown
            ? Math.Clamp(estimateMb, measured.AtLeastMb * MeasuredMargin, measured.AtMostMb)
            : estimateMb;


    /// <summary>
    /// What this pack hands the card and the card cannot hold, which the driver
    /// keeps in system memory instead. It is charged to the game like the rest
    /// of the room beside the heap, because that is where it is spent: two
    /// laptops with the same processor, the same installed memory and the same
    /// pack were not the same machine to a heap, and the one with the smaller
    /// card was the one whose full collections ran for two seconds.
    /// </summary>
    /// <remarks>
    /// A card that could not be read costs nothing, and so does a pack that has
    /// not been weighed: there is nothing to compare it with, and a guess here
    /// would take heap away from someone the launcher knows nothing about.
    /// </remarks>
    public static int GetVideoSpillGb(PackMemoryProfile pack, VideoMemoryProfile video)
    {
        if (!pack.IsKnown || !video.IsKnown) return 0;

        var wantedMb =
            (pack.IsModernMinecraft ? VideoBaseMb : OlderMinecraftVideoBaseMb) +
            (double)pack.ModCount * VideoPerModMb +
            pack.AssetBytes / BytesPerMb * VideoPerAssetMegabyte;
        var shortfallMb = wantedMb - (double)video.DedicatedGb * 1024;
        if (shortfallMb <= 0) return 0;

        return Math.Min(MaxVideoSpillGb, (int)Math.Ceiling(shortfallMb / 1024d));
    }

    /// <summary>
    /// The heap a whole-game budget was leaving this pack. Nothing asks this on
    /// the way to a launch any more - the field is the heap now - but a settings
    /// file written while the number meant the whole of the game is read through
    /// it once, so that the carried-across number is the heap that file was
    /// already producing and nobody's game changes size.
    /// </summary>
    public static int GetHeapForBudgetGb(
        PackMemoryProfile pack,
        int budgetGb,
        VideoMemoryProfile video = default,
        MeasuredMemoryProfile measured = default) =>
        Math.Max(MinHeapGb, budgetGb - GetNativeReserveGb(pack, budgetGb, video, measured));

    /// <summary>
    /// The heap this pack wants: enough to open a world and keep playing in it.
    /// Mods pay for it one by one - each brings its registries, its block states
    /// and its entities - while the size of the jars hardly matters here, which
    /// is why the bytes are absent.
    /// </summary>
    public static int GetRecommendedHeapGb(PackMemoryProfile pack)
    {
        if (!pack.IsKnown) return MaxRecommendedHeapGb / 2;

        var megabytes =
            (pack.IsModernMinecraft ? HeapBaseMb : OlderMinecraftHeapBaseMb) +
            (double)pack.ModCount * HeapPerModMb;
        return Math.Clamp(
            (int)Math.Round(megabytes / 1024d, MidpointRounding.AwayFromZero),
            MinHeapGb,
            MaxRecommendedHeapGb);
    }


    /// <summary>
    /// What the launcher suggests for a pack on this machine: the heap the pack
    /// wants - and never more heap than the machine can leave it once the room
    /// beside the heap is accounted for, whatever the pack would like.
    /// </summary>
    public static int GetRecommendedMemoryGb(
        PackMemoryProfile pack,
        ulong totalPhysicalMemoryBytes,
        VideoMemoryProfile video = default,
        MeasuredMemoryProfile measured = default)
    {
        var wanted = pack.IsKnown || measured.IsKnown
            ? GetRecommendedHeapGb(pack)
            : GetRecommendedHeapForAnUnseenPack(totalPhysicalMemoryBytes);
        // Suggested, not permitted: the suggestion still knows what the pack
        // holds beside its heap and still fits inside the machine, while the
        // ceiling above lets a player overrule both.
        var room = GetWholeGameAllowanceGb(totalPhysicalMemoryBytes) -
                   GetNativeReserveGb(pack, GetWholeGameAllowanceGb(totalPhysicalMemoryBytes), video, measured);
        return Math.Clamp(wanted, MinHeapGb, Math.Max(MinHeapGb, Math.Min(room, GetAllowedHeapGb(totalPhysicalMemoryBytes))));
    }

    /// <summary>
    /// Before any pack is installed there is nothing to weigh, and the launcher
    /// answers as it did when it weighed nothing at all: generously, because the
    /// pack it downloads on the first press of Play is a large one. The first
    /// launch that can see a pack replaces this with that pack's own number.
    /// </summary>
    /// <remarks>
    /// Arithmetic, not a table of sizes. The steps this replaces answered every
    /// machine between twelve and sixteen gigabytes with the same number and
    /// then jumped four at the boundary, which is a shape no machine has: two
    /// laptops a gigabyte apart were offered four gigabytes apart, and the one
    /// just under a step was offered less than a smaller machine's share of
    /// itself. A fraction of the machine, rounded to a whole gigabyte, moves
    /// with the machine instead.
    ///
    /// Two thirds of the machine is still what an unseen pack is offered
    /// altogether; what it is offered as heap is that, less the room such a pack
    /// is assumed to hold outside one. The number this hands back is therefore
    /// the same heap the old whole-game suggestion was already producing.
    /// </remarks>
    private static int GetRecommendedHeapForAnUnseenPack(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = totalPhysicalMemoryBytes / BytesPerGb;
        var wholeGame = (int)Math.Round(installedGb * UnseenPackShareOfMachine, MidpointRounding.AwayFromZero);
        // And no further than the point where more stops being worth having:
        // past this a larger heap buys longer collections rather than more
        // comfort.
        wholeGame = Math.Max(MinHeapGb, wholeGame);
        return Math.Clamp(
            wholeGame - GetNativeReserveGb(PackMemoryProfile.Unknown, wholeGame),
            MinHeapGb,
            MaxRecommendedHeapGb);
    }

    /// <summary>
    /// A number held to what this machine will leave this pack. The field in the
    /// window has always refused more than the machine can spare; the file did
    /// not, so a number written into it by hand went straight to <c>-Xmx</c> and
    /// the two disagreed about the same setting.
    /// </summary>
    public static int ClampHeapGb(int value) => Math.Clamp(value, MinHeapGb, GetAllowedHeapGb());

    /// <summary>The same, for a machine of a stated size.</summary>
    public static int ClampHeapGb(int value, ulong totalPhysicalMemoryBytes) =>
        Math.Clamp(value, MinHeapGb, GetAllowedHeapGb(totalPhysicalMemoryBytes));

    /// <summary>
    /// What the launcher considers a comfortable size for the whole game - the
    /// heap and everything beside it - with a quarter of the machine left idle.
    /// Nothing is refused on the strength of it any more: it bounds what the
    /// launcher SUGGESTS, and the field's own ceiling is the machine less the
    /// operating system.
    /// </summary>
    /// <remarks>
    /// The floor under that quarter is three gigabytes, not four. Four was too
    /// much of a small machine to hold back: Windows counts what the hardware
    /// has taken for itself, so a laptop sold with eight gigabytes reports
    /// seven and a half, which rounds down to seven and left four - half the
    /// machine - unofferable. A pack of fifty mods needs a four gigabyte budget
    /// to keep the promise its number makes, and that machine could not be
    /// offered one. Three still leaves an idle Windows its room; below eight
    /// gigabytes installed there is no arrangement that leaves everybody happy,
    /// and the one that lets the game start is the better of them.
    /// </remarks>
    public static int GetWholeGameAllowanceGb(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Floor(totalPhysicalMemoryBytes / BytesPerGb);
        var reserved = Math.Max(3, installedGb / 4);
        return Math.Clamp(installedGb - reserved, MinHeapGb, MaxHeapGb);
    }
}
