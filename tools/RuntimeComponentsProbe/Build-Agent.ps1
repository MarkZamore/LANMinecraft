[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$run = [IO.DirectoryInfo]::new($output)
while ($run -and -not (Test-Path -LiteralPath (Join-Path $run.FullName 'RUN.json'))) { $run = $run.Parent }
if (-not $run -or $run.Parent.Name -ne 'agent-runs') { throw 'Use an owned agent-runs fixture.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$classes = Join-Path $output 'classes'
New-Item -ItemType Directory -Path $classes -Force | Out-Null
$javac = Join-Path $env:JAVA_HOME 'bin/javac.exe'
$jar = Join-Path $env:JAVA_HOME 'bin/jar.exe'
& $javac --release 17 -d $classes (Join-Path $PSScriptRoot 'RuntimeComponentsQaAgent.java')
if ($LASTEXITCODE -ne 0) { throw 'QA observer compilation failed.' }
$manifest = Join-Path $output 'MANIFEST.MF'
[IO.File]::WriteAllText($manifest,"Manifest-Version: 1.0`nPremain-Class: RuntimeComponentsQaAgent`n`n",[Text.UTF8Encoding]::new($false))
$archive = Join-Path $output 'runtime-components-qa-agent.jar'
& $jar --create --file $archive --manifest $manifest -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'QA observer packaging failed.' }
Write-Output $archive
