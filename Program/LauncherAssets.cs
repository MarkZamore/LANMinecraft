using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace Minecraft;

/// <summary>Local assets prepared and leased by the native bootstrap before WPF starts.</summary>
internal static class LauncherAssets
{
    private static readonly ConditionalWeakTable<BootstrapLaunchContext, Lazy<BitmapImage>> Backgrounds = new();
    private static readonly ConditionalWeakTable<BootstrapLaunchContext, System.Collections.Concurrent.ConcurrentDictionary<string, Geometry>> Icons = new();
    internal static string BackgroundPath => BootstrapLaunchContext.Current.Resolve("assets", "Background.png");
    internal static string GameIconsPath => BootstrapLaunchContext.Current.Resolve("assets", "MinecraftWindowIconAssets.jar");
    internal static string SteamNativePath => BootstrapLaunchContext.Current.Resolve("steam-native", "steam_api64.dll");
    internal static Stream OpenGameIcons() => File.Open(GameIconsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static Stream? OpenLibrarySeed(string storageId)
    {
        var context = BootstrapLaunchContext.Current;
        var name = LibrarySeedPath(storageId);
        return context.ContainsFile("assets", name)
            ? File.Open(context.Resolve("assets", name), FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    internal static bool HasLibrarySeed(string storageId) =>
        BootstrapLaunchContext.Current.ContainsFile("assets", LibrarySeedPath(storageId));

    private static string LibrarySeedPath(string storageId)
    {
        if (storageId is not { Length: > 0 and <= 80 } ||
            storageId.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '-' or '_')))
            throw new InvalidDataException("Invalid library preview identifier.");
        return $"LibrarySeed/{storageId}.zip";
    }

    internal static Geometry Icon(string icon)
    {
        if (icon is not ("back" or "chevron" or "trash" or "requirements-dot"))
            throw new InvalidDataException("Unknown launcher SVG icon: " + icon);
        var context = BootstrapLaunchContext.Current;
        return Icons.GetValue(context, static _ => new(StringComparer.Ordinal))
            .GetOrAdd(icon, name => LoadIcon(context, name));
    }

    private static Geometry LoadIcon(BootstrapLaunchContext context, string name)
    {
        // Colours and stroke widths are supplied by the WPF template. Only one
        // bounded path from the pinned, editable SVG profile can become geometry.
        var path = context.Resolve("assets", $"Icons/{name}.svg");
        using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var xml = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8192
        });
        var contents = XDocument.Load(xml);
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = contents.Root;
        var children = root?.Elements().ToArray();
        if (root?.Name != svg + "svg" || children is not { Length: 1 } ||
            children[0].Name != svg + "path" || children[0].HasElements ||
            root.Attribute("transform") is not null || children[0].Attribute("transform") is not null ||
            (string?)children[0].Attribute("id") != name ||
            (string?)children[0].Attribute("d") is not { Length: > 0 and <= 2048 } data)
            throw new InvalidDataException("Invalid launcher SVG icon: " + name);
        var geometry = Geometry.Parse(data);
        if (geometry.Bounds.IsEmpty) throw new InvalidDataException("Empty launcher SVG icon: " + name);
        geometry.Freeze();
        return geometry;
    }
    internal static BitmapImage Background() => Backgrounds.GetValue(BootstrapLaunchContext.Current,
        context => new Lazy<BitmapImage>(() => LoadBackground(context))).Value;
    private static BitmapImage LoadBackground(BootstrapLaunchContext context)
    {
        using var stream = File.Open(context.Resolve("assets", "Background.png"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}

/// <summary>Resolves the verified image while App.xaml is loaded, before any window appears.</summary>
public sealed class LauncherBackgroundExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => LauncherAssets.Background();
}

/// <summary>Loads editable launcher SVG path data from the verified desktop assets.</summary>
[MarkupExtensionReturnType(typeof(Geometry))]
public sealed class LauncherSvgExtension(string icon) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => LauncherAssets.Icon(icon);
}
