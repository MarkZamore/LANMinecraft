using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Minecraft;

/// <summary>Immutable pre-start boundary; reading a failure never includes the previous session's bytes.</summary>
internal sealed class SessionGameLog
{
    private const long MaximumBaselineBytes = 16 * 1024 * 1024;
    private readonly string _path;
    private readonly bool _captured;
    private readonly ulong? _identity;
    private readonly long _length;
    private readonly byte[] _hash = [];
    private readonly bool _partialLine;

    public SessionGameLog(string gameDirectory)
    {
        _path = Path.Combine(gameDirectory, "logs", "latest.log");
        try
        {
            using var stream = Open();
            _identity = Identity(stream);
            _length = stream.Length;
            if (_length > MaximumBaselineBytes) { _captured = true; return; }
            _hash = HashPrefix(stream, _length);
            if (_length > 0) { stream.Position = _length - 1; _partialLine = stream.ReadByte() != '\n'; }
            _captured = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _captured = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unknown boundary is not permission to report somebody else's log.
        }
    }

    public string Describe()
    {
        if (!_captured) return "";
        try
        {
            using var stream = Open();
            long start = 0;
            bool skipPartial = false;
            if (_identity is not null && Identity(stream) == _identity && stream.Length >= _length)
            {
                // Do not scan huge old logs during launch or guess their append boundary.
                // Rotated/replaced and shorter truncated files still have a known fresh start.
                if (_hash.Length == 0) return "";
                if (HashPrefix(stream, _length).AsSpan().SequenceEqual(_hash))
                {
                    start = _length;
                    skipPartial = _partialLine;
                }
            }
            // Otherwise log4j rotated/replaced or truncated/rewrote the file.
            // Keep the read bounded even when a mod prints a multi-megabyte line.
            var tailStart = Math.Max(start, stream.Length - 64 * 1024);
            skipPartial |= tailStart > start;
            stream.Position = tailStart;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (skipPartial) _ = reader.ReadLine();
            var text = reader.ReadToEnd();
            var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).TakeLast(30);
            var details = string.Join(Environment.NewLine, lines);
            return details.Length == 0 ? "" : Environment.NewLine + details;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private FileStream Open() => new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] HashPrefix(FileStream stream, long length)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        while (length > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read == 0) throw new IOException("Log changed while capturing its session boundary.");
            hash.AppendData(buffer, 0, read);
            length -= read;
        }
        return hash.GetHashAndReset();
    }

    private static ulong Identity(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Could not identify the session log.");
        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
