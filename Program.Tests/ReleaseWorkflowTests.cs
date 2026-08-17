using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Minecraft.Tests;

/// <summary>
/// The release is published by one workflow, and every step of it talks to the
/// same API. A GitHub incident once ended a release twice over - a single 503
/// on a call nobody had wrapped - so these keep the retry rule in place and
/// prove it retries the right things.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    /// <summary>gh is called through the helper, never on its own.</summary>
    [Fact]
    public void ReleaseWorkflow_CallsGhOnlyThroughTheRetryHelper()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        var bare = workflow
            .Split('\n')
            .Select((line, index) => (Text: line.TrimEnd('\r'), Number: index + 1))
            .Where(line => !line.Text.TrimStart().StartsWith('#'))
            .Where(line => Regex.IsMatch(line.Text, @"(?<![\w-])gh\s+(release|api|auth|run|workflow)\b"))
            .Where(line => !line.Text.Contains("Invoke-Gh", StringComparison.Ordinal))
            .Select(line => $"line {line.Number}: {line.Text.Trim()}")
            .ToArray();

        Assert.Empty(bare);
        Assert.Contains("Invoke-Gh", workflow, StringComparison.Ordinal);
    }

    /// <summary>Every step that calls gh dot-sources the helper first.</summary>
    [Fact]
    public void EveryStepThatCallsGh_LoadsTheRetryHelper()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        var steps = workflow.Split("      - name:", StringSplitOptions.RemoveEmptyEntries);

        var missing = steps
            .Where(step => step.Contains("Invoke-Gh ", StringComparison.Ordinal))
            .Where(step => !step.Contains("GhRetry.ps1", StringComparison.Ordinal))
            .Select(step => step.Split('\n')[0].Trim())
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// GitHub answering 503 is weather, not an answer: the call is made again.
    /// </summary>
    [Theory]
    [InlineData("non-200 OK status code: 503 Service Unavailable", 2, 0, 3)]
    [InlineData("HTTP 503: No server is currently available", 1, 0, 2)]
    [InlineData("HTTP 502: Bad Gateway", 99, 1, 6)]
    [InlineData("error connecting to api.github.com: connection reset by peer", 1, 0, 2)]
    public void ATransientFailure_IsTriedAgain(string message, int failures, int expectedExit, int expectedCalls)
    {
        var result = RunHelper(message, failures);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(expectedCalls, result.Calls);
        if (expectedExit == 0) Assert.Contains("ok", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 404 is an answer several steps are built on - "is there a release with
    /// this tag?" - so it comes back at once, unretried.
    /// </summary>
    [Theory]
    [InlineData("release not found (HTTP 404)")]
    [InlineData("gh: Not Found (HTTP 404)")]
    [InlineData("unknown command \"nonesuch\" for \"gh release\"")]
    public void AnAnswerFromGitHub_IsNotRetried(string message)
    {
        var result = RunHelper(message, failures: 99);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Calls);
    }

    /// <summary>
    /// Runs the helper with a stub gh first on PATH, and reports what happened:
    /// the exit code the caller would read, how many times gh was run, and what
    /// came back on standard output.
    /// </summary>
    private static (int ExitCode, int Calls, string Output) RunHelper(string stderrMessage, int failures)
    {
        var helper = FindRepositoryFile(".github", "scripts", "GhRetry.ps1");
        var shell = FindShell();
        var sandbox = Directory.CreateTempSubdirectory("gh-retry-test");
        try
        {
            var log = Path.Combine(sandbox.FullName, "calls.log");
            File.WriteAllText(log, string.Empty);
            // A stub that fails the first few times and then succeeds, counting
            // its own calls: one line per call, so the count is the line count.
            // Everything it runs is named by full path - a Git Bash on PATH
            // offers a `find` that walks the whole disk instead of counting lines.
            File.WriteAllText(Path.Combine(sandbox.FullName, "gh.cmd"), """
                @echo off
                >>"%GH_STUB_LOG%" echo call
                for /f %%A in ('%SystemRoot%\System32\find.exe /c /v "" ^< "%GH_STUB_LOG%"') do set COUNT=%%A
                if %COUNT% LEQ %GH_STUB_FAILURES% (
                  echo %GH_STUB_MESSAGE% 1>&2
                  exit /b 1
                )
                echo ok
                exit /b 0
                """);

            var script =
                $". '{helper}'{Environment.NewLine}" +
                $"$out = Invoke-Gh release view latest --json tagName{Environment.NewLine}" +
                $"Write-Output \"EXIT:$LASTEXITCODE\"{Environment.NewLine}" +
                $"Write-Output \"OUT:$out\"";
            var startInfo = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            startInfo.Environment["PATH"] = sandbox.FullName + Path.PathSeparator + startInfo.Environment["PATH"];
            startInfo.Environment["GH_RETRY_DELAYS"] = "0,0,0,0,0";
            startInfo.Environment["GH_STUB_LOG"] = log;
            startInfo.Environment["GH_STUB_FAILURES"] = failures.ToString();
            startInfo.Environment["GH_STUB_MESSAGE"] = stderrMessage;

            using var process = Process.Start(startInfo)!;
            // Both pipes are drained while the shell runs. PowerShell 5.1 is
            // loud about a native command that writes to stderr, and a full
            // stderr buffer would stop it before it ever reached its last line.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "the helper did not finish in two minutes");
            var output = standardOutput.GetAwaiter().GetResult();
            standardError.GetAwaiter().GetResult();

            var exit = Regex.Match(output, @"EXIT:(-?\d+)");
            Assert.True(exit.Success, $"no exit code in the helper output: {output}");
            var calls = File.ReadAllLines(log).Count(line => line.Trim().Length > 0);
            return (int.Parse(exit.Groups[1].Value), calls, output);
        }
        finally
        {
            try { sandbox.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>PowerShell 7 if the machine has it - the workflow runs pwsh.</summary>
    private static string FindShell()
    {
        foreach (var candidate in new[] { "pwsh.exe", "pwsh", "powershell.exe" })
        {
            var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Where(directory => directory.Length > 0)
                .Select(directory => Path.Combine(directory, candidate))
                .FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }

        throw new InvalidOperationException("No PowerShell was found to run the release helper with.");
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
