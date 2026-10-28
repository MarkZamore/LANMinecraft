using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>Infers a pinned runtime from mod metadata without executing mod code.</summary>
public sealed class PackAutoManifestService
{
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly IAutoRuntimeCatalog _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PackAutoManifestService(AppPaths paths, Logger logger, HttpClient httpClient)
        : this(paths, logger, new LoaderVersionResolver(httpClient, logger, paths)) { }

    internal PackAutoManifestService(AppPaths paths, Logger logger, IAutoRuntimeCatalog catalog)
    { _paths = paths; _logger = logger; _catalog = catalog; }

    public async Task<bool> EnsureAsync(string packRelativePath, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await EnsureCoreAsync(packRelativePath, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnsureCoreAsync(string packRelativePath, CancellationToken token)
    {
        var directory = _paths.CombineUnderPacks(packRelativePath);
        var manifestPath = Path.Combine(directory, PackManifestService.ManifestFileName);
        // Explicit descriptors and remotely managed packs always own their runtime.
        if (File.Exists(manifestPath) || File.Exists(Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName))) return false;
        var inspected = await Task.Run(() => (Snapshot: Snapshot(directory, token),
            Jars: ModRuntimeMetadata.Read(directory).ToArray()), token).ConfigureAwait(false);
        var before = inspected.Snapshot;
        var unknown = inspected.Jars.Where(jar => jar.Options.Count == 0 && !jar.IsNeutralLibrary).Select(jar => jar.FileName).ToArray();
        if (unknown.Length != 0)
            throw new InvalidDataException("Не удалось подтвердить платформу JAR: " + string.Join(", ", unknown) +
                ". Неизвестный JAR нельзя считать совместимой библиотекой; укажите portable-pack.json.");
        var jars = inspected.Jars.Where(jar => jar.Options.Count > 0).ToArray();
        if (jars.Length == 0) throw new InvalidDataException("В папке mods нет модов с достаточными данными о Minecraft и загрузчике. Добавьте portable-pack.json с точными версиями.");
        // Quilt's Fabric compatibility is considered only when a mod actually
        // declares Quilt. A Fabric-only folder keeps its unambiguous platform.
        var quiltDeclared = jars.Any(jar => jar.Options.Any(option => option.Loader == PackLoaderKind.Quilt));
        var families = jars.SelectMany(jar => jar.Options.Select(option => option.Loader)).Distinct()
            .Where(loader => jars.All(jar => OptionsFor(jar, loader, quiltDeclared).Any())).ToArray();
        if (families.Length == 0) throw new InvalidDataException("Моды требуют несовместимые загрузчики. Автоподбор не будет смешивать Forge, NeoForge, Fabric и Quilt.");
        families = families.Where(loader => jars.Any(jar => OptionsFor(jar, loader, quiltDeclared).Any(option => option.MinecraftRanges.Count > 0))).ToArray();
        if (families.Length == 0)
            throw new InvalidDataException("Метаданные модов не задают версию Minecraft. Добавьте portable-pack.json с точной версией.");
        IReadOnlyList<string> versions;
        try { versions = await _catalog.MinecraftVersionsAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        { throw new InvalidDataException("Не удалось получить официальный каталог Minecraft для модов (" + string.Join(", ", families) + "). Проверьте интернет.", ex); }
        var hadMinecraft = false;
        var hadSteamCandidate = false;
        var unsupportedGames = new List<string>();
        var missingFml = false;
        foreach (var minecraft in versions.Distinct(StringComparer.Ordinal).OrderByDescending(version => version, VersionOrder.Instance))
        {
            token.ThrowIfCancellationRequested();
            var matchingFamilies = families.Where(loader => jars.All(jar => OptionsFor(jar, loader, quiltDeclared).Any(option =>
                AcceptsAll(option.MinecraftRanges, minecraft)))).ToArray();
            if (matchingFamilies.Length == 0) continue;
            hadMinecraft = true;
            matchingFamilies = matchingFamilies.Where(loader => SteamPlayPolicy.IsSupported(new PackRuntimeDescriptor(
                PackManifestService.CurrentSchemaVersion, minecraft, new PackLoaderDescriptor(loader, null), "", "auto"))).ToArray();
            if (matchingFamilies.Length == 0) { unsupportedGames.Add(minecraft); continue; }
            hadSteamCandidate = true;
            var matches = new List<(PackLoaderKind Loader, string Version)>();
            foreach (var loader in matchingFamilies)
            {
                IReadOnlyList<string> releases;
                try { releases = await _catalog.LoaderVersionsAsync(loader, minecraft, token).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { continue; }
                catch (HttpRequestException ex)
                { throw new InvalidDataException("Не удалось проверить " + loader + " для Minecraft " + minecraft + ". Проверьте интернет.", ex); }
                foreach (var release in releases.OrderByDescending(version => LoaderVersionResolver.ComparisonVersion(loader, minecraft, version), VersionOrder.Instance))
                {
                    var comparison = LoaderVersionResolver.ComparisonVersion(loader, minecraft, release);
                    var options = jars.Select(jar => OptionsFor(jar, loader, quiltDeclared).Where(option =>
                        AcceptsAll(option.MinecraftRanges, minecraft) && (IsFabricOnQuilt(option, loader) ||
                            AcceptsAll(option.LoaderRanges, comparison))).ToArray()).ToArray();
                    if (options.Any(choices => choices.Length == 0)) continue;
                    bool NeedsFabricAlias(ModRuntimeOption option) => IsFabricOnQuilt(option, loader) || option.FabricApiRanges.Count > 0;
                    var nativeOptions = options.Select(choices => choices.Where(option => !NeedsFabricAlias(option)).ToArray()).ToArray();
                    if (nativeOptions.All(choices => choices.Length > 0) && nativeOptions.Any(choices => choices.Any(option => option.MinecraftRanges.Count > 0)))
                    {
                        // A complete native-Quilt assignment already satisfies
                        // every JAR, so no Fabric alias is needed to prove it.
                        options = nativeOptions;
                    }
                    else if (options.Any(choices => choices.Any(NeedsFabricAlias)))
                    {
                        string? fabricApi;
                        try { fabricApi = await _catalog.FabricApiVersionAsync(minecraft, release, token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
                        { throw new InvalidDataException("Не удалось подтвердить версию fabricloader, предоставляемую Quilt " + release + ". portable-pack.json не создан.", ex); }
                        if (fabricApi is null)
                            throw new InvalidDataException("Quilt " + release + " не подтверждает версию fabricloader для этих модов. Укажите portable-pack.json.");
                        options = options.Select(choices => choices.Where(option => AcceptsAll(option.FabricApiRanges, fabricApi) &&
                            (!IsFabricOnQuilt(option, loader) || AcceptsAll(option.LoaderRanges, fabricApi))).ToArray()).ToArray();
                        if (options.Any(choices => choices.Length == 0)) continue;
                    }
                    if (options.Any(choices => choices.Any(option => option.FmlRanges.Count > 0)))
                    {
                        string? fml;
                        try { fml = await _catalog.FmlVersionAsync(loader, minecraft, release, token).ConfigureAwait(false); }
                        catch (HttpRequestException ex)
                        { throw new InvalidDataException("Не удалось проверить версию FML для " + loader + " " + release + ".", ex); }
                        if (fml is null) missingFml = true;
                        options = options.Select((choices, index) => choices.Where(option => option.FmlRanges.Count == 0 ||
                            (fml is not null && AcceptsFml(loader, option, fml, jars[index].FileName))).ToArray()).ToArray();
                    }
                    if (options.Any(choices => choices.Length == 0) || !options.Any(choices => choices.Any(option => option.MinecraftRanges.Count > 0))) continue;
                    matches.Add((loader, release));
                    break;
                }
            }
            if (matches.Count == 0) continue;
            if (matches.Count > 1)
                throw new InvalidDataException("Моды совместимы с несколькими загрузчиками на Minecraft " + minecraft + ": " +
                    string.Join(", ", matches.Select(match => match.Loader)) + ". Укажите выбранный загрузчик в portable-pack.json.");
            var selected = matches[0];
            var descriptor = new PackRuntimeDescriptor(PackManifestService.CurrentSchemaVersion, minecraft,
                new PackLoaderDescriptor(selected.Loader, selected.Version), "", "auto");
            if (!SteamPlayPolicy.IsSupported(descriptor))
                throw new InvalidDataException("Для " + selected.Loader + " на Minecraft " + minecraft +
                    " нет совместимого опубликованного Steam-транспорта. Версия игры не будет понижена автоматически.");
            if (before != await Task.Run(() => Snapshot(directory, token), token).ConfigureAwait(false))
                throw new InvalidDataException("Файлы модов изменились во время автоподбора. Повторите запуск сборки.");
            if (File.Exists(Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName))) return false;
            token.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(new { schemaVersion = PackManifestService.CurrentSchemaVersion,
                minecraftVersion = minecraft, loader = new { type = selected.Loader.ToString().ToLowerInvariant(), version = selected.Version } },
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            var temporary = Path.Combine(directory, ".portable-auto-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                AtomicFile.WriteAllText(temporary, json);
                try { File.Move(temporary, manifestPath, overwrite: false); }
                catch (IOException) when (File.Exists(manifestPath)) { return false; }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _logger.Info(packRelativePath + ": Minecraft " + minecraft + ", " + selected.Loader + " " + selected.Version + "; metadata constraints verified against official release catalogs.");
            return true;
        }
        throw new InvalidDataException(!hadMinecraft
            ? "Для всех модов нет общего стабильного выпуска Minecraft в официальном каталоге. portable-pack.json не создан."
            : !hadSteamCandidate ? "Для Minecraft " + string.Join(", ", unsupportedGames.Take(5)) +
                " нет опубликованного Steam-транспорта для загрузчика этих модов. portable-pack.json не создан."
            : missingFml ? "Не удалось подтвердить совместимую версию FML по официальным данным загрузчика. Укажите точные версии в portable-pack.json."
            : "Для ограничений всех модов нет общего стабильного загрузчика Minecraft. portable-pack.json не создан.");
    }

    private static bool AcceptsAll(IReadOnlyList<string> ranges, string version) => ranges.All(range => StrictVersionRange.Accepts(range, version));

    private static IEnumerable<ModRuntimeOption> OptionsFor(ModRuntimeJar jar, PackLoaderKind loader, bool quiltDeclared) =>
        // Quilt reads its own metadata first and never scans fabric.mod.json
        // after finding a native Quilt mod in the same JAR.
        jar.Options.Where(option => option.Loader == loader || (quiltDeclared && IsFabricOnQuilt(option, loader) &&
            !jar.Options.Any(native => native.Loader == PackLoaderKind.Quilt)));

    private static bool IsFabricOnQuilt(ModRuntimeOption option, PackLoaderKind loader) =>
        loader == PackLoaderKind.Quilt && option.Loader == PackLoaderKind.Fabric;

    private static bool AcceptsFml(PackLoaderKind loader, ModRuntimeOption option, string evidence, string fileName)
    {
        if (loader == PackLoaderKind.NeoForge)
        {
            if (option.FmlLanguage != "lowcodefml") return AcceptsAll(option.FmlRanges, evidence);
            // FML 12's native language lookup ignores this deprecated name
            // before it checks any version range; it has no separate provider.
            var major = VersionOrder.Parse(evidence)[0];
            if (major >= 12) return true;
            // FML 4 has an ordinary lowcode provider in the same versioned
            // module as javafml, and checks the declared range normally.
            if (major == 4) return AcceptsAll(option.FmlRanges, evidence);
            throw new InvalidDataException("Нельзя подтвердить старый API lowcodefml для " + fileName + ". Укажите portable-pack.json.");
        }
        // Forge's modular provider reports only the Forge major, for both
        // javafml and lowcodefml. Do not invent a patch-level API version.
        return option.FmlRanges.All(range => AcceptsFmlMajor(range, evidence, fileName));
    }

    private static bool AcceptsFmlMajor(string range, string evidence, string fileName)
    {
        // A platform artifact coordinate proves its language API family, not
        // every runtime classloader's patch-level package version. Only accept
        // predicates whose answer is identical throughout the proven major line.
        var major = VersionOrder.Parse(evidence)[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Forge 47's native javafml and lowcodefml providers both use the
        // union-root directory fallback and report exactly "47", not 47.x.
        // This family was verified on official 47.3.7 and 47.4.0 artifacts.
        if (major == "47") return StrictVersionRange.Accepts(range, major);
        // Each interval/OR branch must itself describe whole major families.
        // This avoids sampling two points and overlooking a hole inside an OR.
        if (!WholeMajorPredicate(range))
            throw new InvalidDataException("Официальных данных недостаточно для проверки точечного ограничения JavaFML " + range + " в " + fileName +
                ". Автоподбор поддерживает целые ветки API, например [47,48) или [4,5); укажите portable-pack.json.");
        return StrictVersionRange.Accepts(range, major);
    }

    private static bool WholeMajorPredicate(string range)
    {
        const string boundary = @"^\d+(?:\.0+)*$";
        foreach (var branch in range.Split("||", StringSplitOptions.TrimEntries))
        {
            if (branch.Length == 0) return false;
            if (branch[0] is '[' or '(')
            {
                var intervals = Regex.Matches(branch, @"([\[(])([^\])]*)([\])])");
                if (intervals.Count == 0) return false;
                foreach (Match interval in intervals)
                {
                    var bounds = interval.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries);
                    if (bounds.Length != 2 || (bounds[0].Length > 0 && (interval.Groups[1].Value != "[" || !Regex.IsMatch(bounds[0], boundary))) ||
                        (bounds[1].Length > 0 && (interval.Groups[3].Value != ")" || !Regex.IsMatch(bounds[1], boundary)))) return false;
                }
            }
            else
            {
                var index = 0;
                while (index < branch.Length)
                {
                    var term = Regex.Match(branch[index..], @"^\s*(?:(?:>=|<)\s*\d+(?:\.0+)*|\d+\.[xX*]|[~^]\d+|[xX*])\s*(?:,\s*)?");
                    if (!term.Success || term.Length == 0) return false;
                    index += term.Length;
                }
            }
        }
        return true;
    }

    private string Snapshot(string directory, CancellationToken token)
    {
        _paths.EnsureUnderRoot(directory);
        var mods = Path.Combine(directory, "mods");
        for (var path = mods; !string.Equals(path, _paths.Root, StringComparison.OrdinalIgnoreCase); path = Path.GetDirectoryName(path)!)
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Автоподбор не читает моды через ссылки на другие папки.");
        if (!Directory.Exists(mods)) return "none";
        var result = new StringBuilder();
        foreach (var jar in Directory.EnumerateFiles(mods, "*.jar").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(jar) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Мод не должен быть ссылкой: " + Path.GetFileName(jar));
            using var input = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.Read);
            result.Append(Path.GetFileName(jar)).Append('|').Append(Convert.ToHexString(SHA256.HashData(input))).Append('\n');
        }
        return result.ToString();
    }
}
