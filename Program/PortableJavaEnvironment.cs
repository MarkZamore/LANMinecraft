using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Minecraft;

/// <summary>The portable profile shared by the game and its Java helpers.</summary>
internal static class PortableJavaEnvironment
{
    public static void ConfigureEnvironment(
        IDictionary<string, string?> environment,
        string temporaryDirectory,
        string homeDirectory)
    {
        environment["TEMP"] = temporaryDirectory;
        environment["TMP"] = temporaryDirectory;
        environment["USERPROFILE"] = homeDirectory;
        environment["HOME"] = homeDirectory;
        environment["APPDATA"] = Path.Combine(homeDirectory, "AppData", "Roaming");
        environment["LOCALAPPDATA"] = Path.Combine(homeDirectory, "AppData", "Local");
    }

    /// <summary>Gives a short-lived Java check or installer its own temporary
    /// profile. Nothing here changes the launcher's or Windows' environment.</summary>
    internal static ToolScope CreateToolScope(ProcessStartInfo startInfo, string parentDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var parent = Path.GetFullPath(parentDirectory);
        var root = Path.Combine(parent, "java-tool-" + Guid.NewGuid().ToString("N"));
        var scope = new ToolScope(root, parent, startInfo);
        try
        {
            var temporary = Path.Combine(root, "Temp");
            var home = Path.Combine(root, "Home");
            Directory.CreateDirectory(temporary);
            Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming"));
            Directory.CreateDirectory(Path.Combine(home, "AppData", "Local"));
            ConfigureEnvironment(startInfo.Environment, temporary, home);
            startInfo.ArgumentList.Add($"-Djava.io.tmpdir={temporary}");
            startInfo.ArgumentList.Add($"-Duser.home={home}");
            if (string.IsNullOrEmpty(startInfo.WorkingDirectory)) startInfo.WorkingDirectory = root;
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>Observes pipe failures after cancellation without waiting for a
    /// child that could not be stopped to close its inherited pipe handles.</summary>
    internal static void ObserveOutputCompletion(params Task[] outputs)
    {
        foreach (var output in outputs)
        {
            _ = output.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal static bool HasRetainedToolScope(string parentDirectory)
    {
        try
        {
            return Directory.Exists(parentDirectory) &&
                   Directory.EnumerateDirectories(parentDirectory, "java-tool-*", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable tree is not evidence that its helper has stopped.
            return true;
        }
    }

    internal sealed class ToolScope(string root, string parent, ProcessStartInfo startInfo) : IDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
        private Process? _process;
        private bool _preserve;
        private bool _disposed;

        internal string RootDirectory => root;

        public Process Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null) throw new InvalidOperationException("The Java helper was already started.");
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Java helper process could not be started.");
            return _process;
        }

        public bool Stop()
        {
            if (!RequestStop()) return false;
            try
            {
                if (_process is null || _process.WaitForExit((int)StopTimeout.TotalMilliseconds)) return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
            _preserve = true;
            return false;
        }

        public async Task<bool> StopAsync()
        {
            if (!RequestStop()) return false;
            if (_process is null) return true;
            // This token belongs to cleanup. The caller's token is normally
            // cancelled already, so using it would skip the exit wait again.
            using var timeout = new CancellationTokenSource(StopTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception)
            {
                _preserve = true;
                return false;
            }
        }

        private bool RequestStop()
        {
            try
            {
                if (_process is not null && !_process.HasExited) _process.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
            {
                // A failed whole-tree kill can leave a child behind even if
                // the parent exits. Keep its profile until startup recovery.
                _preserve = true;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited) _preserve = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    _preserve = true;
                }
                finally
                {
                    _process.Dispose();
                }
            }
            if (_preserve) return;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A Java image can remain mapped briefly after exit. Startup's
                // portable Temp cleanup recovers a directory left behind here.
            }
        }
    }
}
