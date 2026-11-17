using System.Runtime.CompilerServices;

namespace Minecraft.Tests;

/// <summary>Uses the external packages checked by Prepare-DesktopComponentTests.ps1.</summary>
internal static class DesktopComponentTestFiles
{
    [ModuleInitializer]
    internal static void Initialize() => BootstrapLaunchContext.InitializeTestComponentsFromEnvironment();
}
