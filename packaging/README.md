# Packaging

Templates and manifests used to package the app. The release workflow
(`.github/workflows/release.yml`) uses `macos/` and `linux/` directly; the
package manager manifests below are submitted to their repositories.

## winget (Windows)

Package ID `DABWorx.SimpleMarkdownViewer`, manifests in `winget/<version>/`.

**First submission** (once): copy the three files to a fork of
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) under
`manifests/d/DABWorx/SimpleMarkdownViewer/<version>/` and open a pull request, or let
[wingetcreate](https://github.com/microsoft/winget-create) do it:

```powershell
winget install wingetcreate
wingetcreate submit --token <github-pat> packaging\winget\1.6.0
```

Validate locally first with `winget validate --manifest packaging\winget\1.6.0`.
The pull request is checked automatically (the installer is run in a sandbox) and
then reviewed by a moderator; the first one takes a few days.

**Later versions** are automatic: when a release is published, `winget.yml` runs
`wingetcreate update` and opens the version-bump pull request. It needs the
`WINGET_TOKEN` repository secret, a classic personal access token with the
`public_repo` scope for the GitHub account that will own the pull request.

The in-app Install Update button upgrades outside winget. winget then sees the
newer version in Apps & Features and simply reports it as installed.

## Scoop (Windows)

`scoop/simplemarkdownviewer.json` installs the portable zip from the release and
updates itself: `checkver` reads the latest GitHub release and `autoupdate`
rewrites the URL and hash, so bucket bots keep it current without further work.

It lives in our own bucket, [KrunchMuffin/scoop-bucket](https://github.com/KrunchMuffin/scoop-bucket)
(`bucket/simplemarkdownviewer.json`), whose Excavator workflow runs `checkver -u` every
few hours and commits new versions. Users install with
`scoop bucket add dabworx https://github.com/KrunchMuffin/scoop-bucket` then
`scoop install dabworx/simplemarkdownviewer`. The copy here is the reference; edit the
bucket's copy for anything other than version and hash. The official Extras bucket
requires roughly 100 stars or 50 forks, so it's an option for later.

Test a manifest without a bucket: `scoop install .\packaging\scoop\simplemarkdownviewer.json`.
