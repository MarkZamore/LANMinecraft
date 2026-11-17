#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$boundary = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/agent-runs'))
$run = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\','/')
$name = [IO.Path]::GetFileName($run)
$legacy = $name -match '^native-desktop-host-tests-[0-9a-f]{32}$'
if ([IO.Path]::GetDirectoryName($run) -cne $boundary -or
    (!$legacy -and $name -notmatch '^n-[0-9a-f]{8}$')) { throw 'Not an isolated native fixture.' }
$record = Get-Content -LiteralPath (Join-Path $run 'RUN.json') -Raw | ConvertFrom-Json
if ($record.owner -cne 'NativePortableHostTests') { throw 'Unexpected fixture owner.' }
if (!$legacy -and ($record.fixtureId -cnotmatch '^[0-9a-f]{32}$' -or $name -cne ('n-'+$record.fixtureId.Substring(0,8)))) { throw 'Native fixture identity mismatch.' }
$ancestor = [IO.DirectoryInfo]::new($run)
while ($ancestor -and $ancestor.FullName.Length -ge $repository.Length) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fixture ancestor is a reparse entry.' }
    $ancestor = $ancestor.Parent
}
$prefix = $run + [IO.Path]::DirectorySeparatorChar
foreach ($process in Get-CimInstance Win32_Process) {
    if ($process.ExecutablePath -and $process.ExecutablePath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Owned fixture process still running: $($process.ProcessId)"
    }
}
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($run)
while ($pending.Count) {
    $current = $pending.Pop()
    if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Fixture contains a reparse entry.' }
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
        if ([IO.Path]::GetFileName($entry) -ceq '.git') { throw 'Fixture contains Git metadata.' }
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Fixture contains a reparse entry.' }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry) }
    }
}
$actions = [Collections.Generic.List[string]]::new()
$directories = if ($legacy) { @('payload','native-build','native-update-build','installation Я 日本語','inherited-temp') }
    else { @('payload','native-build','native-update-build','Minecraft','inherited-temp','held-minecraft','junction-target','unrelated cwd') }
foreach ($name in $directories) {
    $target = [IO.Path]::GetFullPath((Join-Path $run $name))
    if (!$target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Escaped cleanup target.' }
    if (!(Test-Path -LiteralPath $target -PathType Container)) { continue }
    foreach ($relative in @('host-build.json','native-build.log','Minecraft/Launcher/bootstrap.log','Launcher/bootstrap.log')) {
        $evidence = Join-Path $target $relative
        if (Test-Path -LiteralPath $evidence -PathType Leaf) {
            Copy-Item -LiteralPath $evidence -Destination (Join-Path $run ($name+'-'+[IO.Path]::GetFileName($evidence))) -Force
        }
    }
    Remove-Item -LiteralPath $target -Recurse -Force
    $actions.Add("Removed owned recoverable directory $name.")
}
if (!$legacy) {
    foreach ($file in [IO.Directory]::EnumerateFiles($run)) {
        $leaf = [IO.Path]::GetFileName($file)
        if ($leaf -cnotin @('LANMinecraft.exe','updater-probe.exe','Лаунчер с пробелом.exe','invalid-component-descriptor.exe','outside-copy.dll') -and
            $leaf -cnotmatch '^malformed-[0-9a-f]{32}\.exe$') { continue }
        Remove-Item -LiteralPath $file -Force
        $actions.Add("Removed owned generated file $leaf.")
    }
}
[IO.File]::WriteAllText((Join-Path $run 'cleanup.json'),($actions | ConvertTo-Json)+"`n",[Text.UTF8Encoding]::new($false))
Write-Output 'Native fixture bulk cleanup completed.'
