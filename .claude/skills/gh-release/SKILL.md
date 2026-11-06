---
name: gh-release
description: Project deviations from the global gh-release skill, for ad hoc builds of this fork
---

# gh-release (this project)

This repo is a fork, and upstream publishes the tool to the XrmToolBox store. Releases here are
ad hoc builds for local install, not versions.

- **Target the fork on every call:** `gh ... -R skfd/MsCrmTools.UserSettingsUtility`. Bare `gh`
  resolves to the `upstream` remote.
- **Build from `e2e`,** which merges every fix branch. Check each one is an ancestor of it first.
  `master` doesn't have them all. Upstream PRs come later, as one series.
- **Build with MSBuild, not `dotnet publish`:** .NET Framework with packages.config. The restore
  and build commands are in `tests/fake.ps1`. The artifact is `bin\Release\MsCrmTools.UserSettingsUtility.dll`,
  the only file the nuspec ships, plus a zip of it.
- **Don't bump the version.** It is upstream's date version in `Properties/AssemblyInfo.cs`, with
  no `-dev` scheme, so skip the version steps. The build reports the same version as the store
  release, and the notes must say so.
- **Tag `adhoc-YYYY-MM-DD`,** created with `--target <sha> --prerelease --latest=false` so it can't
  collide with an upstream tag or show as latest.
- **Test the shipped DLL:** copy it over the plugin in a copy of the harness's bin and run
  `UserSettings.Harness.exe fake <out>`. `tests/e2e.ps1` writes to a real org, so run it only when asked.
- **Stage assets outside the repo.** `tests/` holds a sign-in token cache and real-org logs.
- **Say in the notes to run `Unblock-File`** on the download before copying it to
  `%APPDATA%\MscrmTools\XrmToolBox\Plugins`.
