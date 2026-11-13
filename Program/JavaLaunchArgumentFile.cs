using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Minecraft;

/// <summary>Keep long Java launches within the Windows CreateProcessW limit.</summary>
internal sealed class JavaLaunchArgumentFile : IDisposable
{
    private const int WindowsCommandLimit = 32_767; // Including the terminating null.
    private const string FilePrefix = "java-launch-";
    private readonly string _path;
    private readonly Logger? _logger;

    private JavaLaunchArgumentFile(string path, Logger? logger)
    {
        _path = path;
        _logger = logger;
    }

    internal static JavaLaunchArgumentFile? Prepare(AppPaths paths, ProcessStartInfo start,
        IReadOnlyList<string> arguments, int javaMajor, int mainClassIndex, Logger? logger = null)
    {
        if (!OperatingSystem.IsWindows() || WindowsLength(start) <= WindowsCommandLimit) return null;
        if (javaMajor < 9)
            throw new InvalidOperationException("Команда запуска Java 8 слишком длинная для Windows. Переместите лаунчер в папку с более коротким путём.");
        if (mainClassIndex < 0 || mainClassIndex >= arguments.Count)
            throw new ArgumentOutOfRangeException(nameof(mainClassIndex));

        // Keep any explicit JDK expansion controls in the normal invocation.
        // Java does not expand @files nested inside an argument file.
        var fileCount = mainClassIndex + 1;
        for (var index = 0; index < mainClassIndex; index++)
        {
            var argument = arguments[index];
            if (argument == "--disable-@files" || argument.StartsWith('@'))
            {
                fileCount = index;
                break;
            }
        }

        // Startup housekeeping empties Personal/Temp even if a Java child
        // launched by the previous process is still initializing.
        var directory = ArgumentDirectory(paths);
        CheckOrdinaryDirectory(directory);
        var file = Path.Combine(directory, FilePrefix + Guid.NewGuid().ToString("N") + ".args");
        var fileArgument = "@" + file;
        var trailing = arguments.Skip(fileCount).ToArray();
        var shortened = new ProcessStartInfo(start.FileName);
        shortened.ArgumentList.Add(fileArgument);
        foreach (var argument in trailing) shortened.ArgumentList.Add(argument);
        if (fileCount <= 0 || WindowsLength(shortened) > WindowsCommandLimit)
            throw new InvalidOperationException("Команда запуска Java остаётся слишком длинной для Windows. Переместите лаунчер в папку с более коротким путём.");
        // Windows OpenJDK parses its launcher input using the system ANSI code
        // page before -Dfile.encoding takes effect. Use the same byte encoding
        // as a direct invocation, including paths with representable Unicode.
        var codePage = (int)GetACP();
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        byte[] contents;
        try
        {
            _ = encoding.GetByteCount(file);
            var serialized = Serialize(arguments.Take(fileCount));
            contents = encoding.GetBytes(serialized);
            // Java tokenizes the bytes itself. A multibyte code page can use
            // the ASCII backslash byte as the second byte of a character.
            if (contents.Count(value => value == (byte)'\\') != serialized.Count(value => value == '\\'))
                throw new EncoderFallbackException("The Windows code page cannot represent a Java argument file losslessly.");
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidOperationException("Длинная команда Java содержит символы, которые не поддерживает системная кодировка Windows.", error);
        }

        Directory.CreateDirectory(directory);
        var created = false;
        try
        {
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(contents);
            }
            start.Arguments = "";
            start.ArgumentList.Clear();
            foreach (var argument in shortened.ArgumentList) start.ArgumentList.Add(argument);
            return new JavaLaunchArgumentFile(file, logger);
        }
        catch
        {
            if (created) Delete(file, logger);
            throw;
        }
    }

    internal static long WindowsLength(ProcessStartInfo start)
    {
        var file = start.FileName.AsSpan().Trim();
        long length = file.Length + (file.StartsWith("\"") && file.EndsWith("\"") ? 1L : 3L);
        if (start.ArgumentList.Count == 0)
            return length + (start.Arguments.Length == 0 ? 0 : 1L + start.Arguments.Length);
        foreach (var argument in start.ArgumentList)
            length += 1 + WindowsArgumentLength(argument);
        return length;
    }

    private static long WindowsArgumentLength(string argument)
    {
        if (argument.Length != 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return argument.Length;
        // ProcessStartInfo's Windows quoting doubles slashes before a quote
        // and at the end of a quoted token. Count the actual UTF-16 units.
        long length = argument.Length + 2L;
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"') length += slashes + 1L;
            slashes = 0;
        }
        return length + slashes;
    }

    private static string Serialize(IEnumerable<string> arguments)
    {
        var contents = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (argument.Contains('\0') || argument.Contains('\u001A'))
                throw new InvalidDataException("A Java argument contains an unsupported control character.");
            contents.Append('"');
            foreach (var character in argument)
            {
                switch (character)
                {
                    case '\\': contents.Append("\\\\"); break;
                    case '"': contents.Append("\\\""); break;
                    case '\t': contents.Append("\\t"); break;
                    case '\r': contents.Append("\\r"); break;
                    case '\n': contents.Append("\\n"); break;
                    case '\f': contents.Append("\\f"); break;
                    default: contents.Append(character); break;
                }
            }
            contents.Append("\"\n");
        }
        return contents.ToString();
    }

    private static void CheckOrdinaryDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null ||
                (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Java argument file directory cannot be a reparse point.");
        }
    }

    private static string ArgumentDirectory(AppPaths paths) => Path.Combine(paths.Launcher, "LaunchArguments");

    internal static void CleanupStale(AppPaths paths)
    {
        var directory = ArgumentDirectory(paths);
        var cutoff = DateTime.UtcNow.AddDays(-1);
        try
        {
            CheckOrdinaryDirectory(directory);
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*.args"))
            {
                var file = new FileInfo(path);
                var name = Path.GetFileNameWithoutExtension(file.Name);
                if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[FilePrefix.Length..], "N", out _) ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LastWriteTimeUtc >= cutoff)
                    continue;
                Delete(path, null);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leave unreadable generated files for the next startup.
        }
    }

    public void Dispose() => Delete(_path, _logger);

    private static void Delete(string path, Logger? logger)
    {
        try
        {
            CheckOrdinaryDirectory(Path.GetDirectoryName(path)!);
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.Warn("Could not remove the Java argument file: " + error.GetType().Name + ".");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
