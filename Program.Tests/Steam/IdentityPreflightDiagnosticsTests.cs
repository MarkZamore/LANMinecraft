namespace Minecraft.Tests;

public sealed class IdentityPreflightDiagnosticsTests
{
    [Fact]
    public void EmptyNativeFailure_PreservesWindowsStatusAndNamesTheMissingOutput()
    {
        var message = PortableIdentityAdapterService.FormatPreflightFailure(
            "fixture-client.jar", "example/LanList", unchecked((int)0xC0000005), "", "  ");

        Assert.Contains("fixture-client.jar::example/LanList", message, StringComparison.Ordinal);
        Assert.Contains("Exit code -1073741819 (0xC0000005)", message, StringComparison.Ordinal);
        Assert.Contains("no standard output or standard error", message, StringComparison.Ordinal);
    }

    [Fact]
    public void JavaRefusal_KeepsBoundedDiagnosticTailAndItsDistinctExitCode()
    {
        var message = PortableIdentityAdapterService.FormatPreflightFailure(
            "fixture-client.jar", "example/LanList", 1,
            new string('x', 3000), "java.lang.IllegalStateException: unsupported exact native member");

        Assert.Contains("Exit code 1 (0x00000001)", message, StringComparison.Ordinal);
        Assert.EndsWith("java.lang.IllegalStateException: unsupported exact native member", message, StringComparison.Ordinal);
        Assert.True(message.Length < 2300);
        Assert.DoesNotContain("no standard output", message, StringComparison.Ordinal);
    }
}
