using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace Minecraft;

/// <summary>Local assets prepared and leased by the native bootstrap before WPF starts.</summary>
internal static class LauncherAssets
{
    private static readonly ConditionalWeakTable<BootstrapLaunchContext, Lazy<BitmapImage>> Backgrounds = new();
    internal static string BackgroundPath => BootstrapLaunchContext.Current.Resolve("assets", "Background.png");
    internal static string GameIconsPath => BootstrapLaunchContext.Current.Resolve("assets", "MinecraftWindowIconAssets.jar");
    internal static string SteamNativePath => BootstrapLaunchContext.Current.Resolve("steam-native", "steam_api64.dll");
    internal static Stream OpenGameIcons() => File.Open(GameIconsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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
