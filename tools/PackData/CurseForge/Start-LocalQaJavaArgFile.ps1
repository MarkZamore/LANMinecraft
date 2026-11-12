[CmdletBinding()]
param([Parameter(Mandatory)][hashtable] $LocalQaArguments)

$ErrorActionPreference = 'Stop'
if (!$LocalQaArguments.WithSteam -or !$LocalQaArguments.NativeQaAgent) {
    throw 'The Java argument-file wrapper requires the native Steam QA agent.'
}

$runner = Join-Path $PSScriptRoot 'Start-LocalQa.ps1'
$source = [IO.File]::ReadAllText($runner)
$start = '    $start = $process.StartInfo'
$cleanup = '    if ($null -ne $process) { $process.Dispose() }'
$flag = 'javaArgumentFileUsed = $false'
foreach ($marker in @($start, $cleanup, $flag)) {
    if ($source.Split($marker, [StringSplitOptions]::None).Length -ne 2) {
        throw 'The local QA runner changed. Review its argument-file wrapper.'
    }
}

$arguments = @'
    if ($runtime.Descriptor.JavaMajorVersion -lt 9) {
        throw 'This QA-only argument file requires Java 9 or newer.'
    }
    $argFile = Join-Path $qaTemp 'start-java.args'
    $processType = $assembly.GetType('Minecraft.MinecraftProcessService', $true)
    $complete = $processType.GetMethod('CompleteJvmArguments', $buildFlags)
    $packOptions = [Minecraft.PackJvmArgumentsService]::new()
    $tokens = $complete.Invoke($null, [object[]]@($start, $profile, $game, $packOptions, $null))
    $javaArgs = foreach ($arg in $tokens) {
        if ($arg -match '[^\u0001-\u007F]' -or $arg.Contains([char]0)) {
            throw 'The QA argument-file wrapper requires ASCII arguments.'
        }
        '"' + $arg.Replace('\', '\\').Replace('"', '\"').Replace("`t", '\t').Replace("`r", '\r').Replace("`n", '\n').Replace([string][char]12, '\f') + '"'
    }
    [IO.File]::WriteAllLines($argFile, [string[]]$javaArgs, [Text.Encoding]::ASCII)
    $start.Arguments = ''
    $start.ArgumentList.Clear()
    $start.ArgumentList.Add('@' + $argFile)
'@

$revised = $source.Replace($start, $start + [Environment]::NewLine + $arguments)
$revised = $revised.Replace($cleanup, '    if ($argFile) { [IO.File]::Delete($argFile) }' + [Environment]::NewLine + $cleanup)
$revised = $revised.Replace($flag, 'javaArgumentFileUsed = $true')
$parserTokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($revised, $runner, [ref]$parserTokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
& $ast.GetScriptBlock() @LocalQaArguments
