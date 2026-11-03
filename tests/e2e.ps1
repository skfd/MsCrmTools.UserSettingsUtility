# Drives the real tool against a real Dataverse, changing exactly one user's settings: the
# signed-in user's own paging limit and work start time. It narrows the list to that user, refuses
# to press Update with anyone else ticked, asks the org what was written, and puts the original
# values back whatever happened. Shots, dialogs and the tool's log land in tests\.e2e.
#
#   .\tests\e2e.ps1
#   .\tests\e2e.ps1 -TokenCache <path>    # sign in borrowed from somewhere else
#
# The sign in is borrowed, never performed: by default it is the token cache xrm-icons' e2e.ps1
# keeps beside it (a copy of the xtb.ps1 sandbox's), so this runs with nobody at the keyboard.
# The environment comes from the active pac auth profile; -Environment and -Username aim it
# elsewhere, somewhere that cache has signed into.

param(
    [string]$Environment,
    [string]$Username,
    [string]$TokenCache = (Join-Path $env:USERPROFILE "Code\xrm-icons\tests\.e2e\token-cache")
)

$ErrorActionPreference = "Stop"

$tests = $PSScriptRoot
$repo = Split-Path -Parent $tests
$harness = Join-Path $tests "UserSettings.Harness"
$out = Join-Path $tests ".e2e"

if (-not (Test-Path $TokenCache)) {
    throw "No token cache at $TokenCache. Run xrm-icons' tests\e2e.ps1 once (it borrows the xtb.ps1 sandbox's sign in), or pass -TokenCache."
}

if (-not $Environment -or -not $Username) {
    $who = pac org who --json | ConvertFrom-Json
    if (-not $who -or -not $who.OrgUrl) { throw "pac has no active organization. Pass -Environment and -Username." }
    if (-not $Environment) { $Environment = $who.OrgUrl.TrimEnd('/') }
    if (-not $Username) { $Username = $who.UserEmail }
}

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * `
    -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe" | Select-Object -First 1
& $msbuild "$repo\MsCrmTools.UserSettingsUtility.sln" -t:Restore -p:RestorePackagesConfig=true -v:q -nologo -clp:ErrorsOnly
& $msbuild "$harness\UserSettings.Harness.csproj" -restore -v:q -nologo -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# A copy, so a refresh this run makes cannot spoil the cache it was borrowed from
New-Item -ItemType Directory -Force -Path $out | Out-Null
$cache = Join-Path $out "token-cache"
Copy-Item $TokenCache $cache -Force

Write-Host "Against $Environment as $Username"
& (Join-Path $harness "bin\Debug\net48\UserSettings.Harness.exe") org $Environment $Username $cache $out
exit $LASTEXITCODE
