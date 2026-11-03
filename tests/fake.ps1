# Drives the real tool in a window against an organization that exists only in memory: load,
# select, type, tick, update, reconnect - and asks the fake org what it was asked and what was
# written. Nothing leaves the machine. Shots of every step, the dialogs the tool put up and its
# own log land in tests\.fake\<scenario>.
#
#   .\tests\fake.ps1                          # every scenario, against this checkout
#   .\tests\fake.ps1 -Scenario shift-select-loads-once
#   .\tests\fake.ps1 -Against master          # the same scenarios, against another branch's tool
#
# -Against builds that branch's tool in a worktree under tests\.against and swaps it in under a
# copy of the harness, so a scenario can be shown to fail before its fix and pass after it.
# Set USU_HARNESS_ONSCREEN=1 to watch the window instead of keeping it off screen.

param(
    [string]$Scenario,
    [string]$Against
)

$ErrorActionPreference = "Stop"

$tests = $PSScriptRoot
$repo = Split-Path -Parent $tests
$harness = Join-Path $tests "UserSettings.Harness"
$bin = Join-Path $harness "bin\Debug\net48"
$plugin = "MsCrmTools.UserSettingsUtility"

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * `
    -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "No MSBuild found. The tool is a .NET Framework project with packages.config." }

function Invoke-MSBuild([string[]]$arguments) {
    & $msbuild @arguments -v:q -nologo -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed: $arguments" }
}

Invoke-MSBuild @("$repo\$plugin.sln", "-t:Restore", "-p:RestorePackagesConfig=true")
Invoke-MSBuild @("$harness\UserSettings.Harness.csproj", "-restore")

$exe = Join-Path $bin "UserSettings.Harness.exe"
$out = Join-Path $tests ".fake"

if ($Against) {
    $slug = $Against -replace '[^A-Za-z0-9]+', '-'
    $root = Join-Path $tests ".against\$slug"
    $src = Join-Path $root "src"

    if (Test-Path $src) { git -C $repo worktree remove --force $src | Out-Null }
    git -C $repo worktree add --detach $src $Against | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not check out $Against" }

    Invoke-MSBuild @("$src\$plugin.sln", "-t:Restore", "-p:RestorePackagesConfig=true")
    Invoke-MSBuild @("$src\$plugin\$plugin.csproj", "-p:Configuration=Release")

    $swapped = Join-Path $root "bin"
    if (Test-Path $swapped) { Remove-Item $swapped -Recurse -Force }
    Copy-Item $bin $swapped -Recurse
    Copy-Item "$src\$plugin\bin\Release\$plugin.dll" $swapped -Force

    $exe = Join-Path $swapped "UserSettings.Harness.exe"
    $out = Join-Path $root "shots"
    Write-Host "Against $Against ($(git -C $src rev-parse --short HEAD))"
}

$arguments = @("fake", $out)
if ($Scenario) { $arguments += $Scenario }

& $exe @arguments
$code = $LASTEXITCODE

if ($Against) { git -C $repo worktree remove --force $src | Out-Null }
exit $code
