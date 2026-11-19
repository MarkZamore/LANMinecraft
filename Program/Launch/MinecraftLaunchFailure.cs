using System.ComponentModel;

namespace Minecraft;

/// <summary>Technical diagnostics remain in Message; the UI maps the failure type to a short explanation.</summary>
internal sealed class MinecraftMemoryAllocationException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

internal static class MinecraftLaunchFailure
{
    internal const string MemoryHeading = "Недостаточно оперативной памяти";
    internal const string MemoryDetail =
        "Возможно, предыдущий сеанс игры ещё не завершился или память занята другими приложениями. " +
        "Попробуйте запустить игру ещё раз.";

    internal static bool IsNativeMemoryError(int errorCode) => errorCode is 8 or 14 or 1455;

    internal static bool NamesNativeMemoryFailure(string console) =>
        (console.Contains("os::commit_memory(", StringComparison.OrdinalIgnoreCase) &&
         console.Contains("failed", StringComparison.OrdinalIgnoreCase)) ||
        (console.Contains("Native memory allocation (", StringComparison.OrdinalIgnoreCase) &&
         console.Contains("failed", StringComparison.OrdinalIgnoreCase)) ||
        console.Contains("There is insufficient memory for the Java Runtime Environment to continue", StringComparison.OrdinalIgnoreCase) ||
        console.Contains("DOS error/errno=1455", StringComparison.OrdinalIgnoreCase) ||
        console.Contains("Could not reserve enough space for", StringComparison.OrdinalIgnoreCase);

    internal static Exception FromProcessExit(int exitCode, string console, string logTail)
    {
        var diagnostics = $"Minecraft exited during startup with code {exitCode}." + console + logTail;
        // The old latest.log may belong to a different run. It is retained for
        // the technical report, but cannot decide what happened to this JVM.
        return NamesNativeMemoryFailure(console)
            ? new MinecraftMemoryAllocationException(diagnostics)
            : new InvalidOperationException(diagnostics);
    }

    internal static MinecraftMemoryAllocationException FromProcessStart(Win32Exception error) =>
        new($"Minecraft process could not be started (Windows error {error.NativeErrorCode}): {error.Message}", error);

    internal static (string Heading, string Detail) ForUser(Exception error) =>
        error is MinecraftMemoryAllocationException
            ? (MemoryHeading, MemoryDetail)
            : ("Игра не запустилась", error.Message);

    internal static void Present(Exception error, Action<string> log, Action<string, string> show)
    {
        log(error.ToString());
        var message = ForUser(error);
        show(message.Heading, message.Detail);
    }
}
