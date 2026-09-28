# Contributing

Thanks for your interest in Simple Markdown Viewer! Bug reports, ideas, and pull requests are all welcome.

## Reporting Bugs and Requesting Features

Use the [issue templates](https://github.com/KrunchMuffin/SimpleMarkdownViewer/issues/new/choose). For bugs, the app version (Help > About), your OS version, and steps to reproduce make a big difference. If the app crashes, the bug template explains how to grab the error details on each platform.

Security problems should be reported privately; see [SECURITY.md](SECURITY.md).

## Making Changes

For anything bigger than a small fix, please open an issue first so we can agree on the approach before you spend time on it.

### Build and Run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build
dotnet run -- path/to/file.md
```

See the [README](README.md#building-from-source) for publishing, and [Releasing](#releasing) below for installers and packages. On Linux you also need WebKitGTK 4.1 (`sudo apt install libwebkit2gtk-4.1-0` on Ubuntu and Debian).

### Project Layout

- `MainWindow.axaml` / `MainWindow.axaml.cs` - The window: tabs, editor, menus, file watching, settings
- `MarkdownRenderer.cs` - Markdown to HTML (Markdig, plus Mermaid and math preprocessing)
- `PreviewPage.cs` - Builds the preview page, including its Content-Security-Policy
- `Assets/preview/` - Preview styles and script (`preview.css`, `print.css`, `preview.js`)
- `Assets/libs/` - Bundled Mermaid, KaTeX, and highlight.js
- `UpdateChecker.cs` - Checks GitHub for newer releases
- `Program.cs` - Startup and single-instance handling
- `packaging/` - macOS `Info.plist`, and the Linux `.desktop` file and AppImage launcher
- `.github/workflows/` - `build.yml` builds every push and pull request; `release.yml` builds the downloads for a release

### Guidelines

- Keep pull requests focused on one change, and match the style of the surrounding code.
- There are no automated tests yet, so describe how you tested your change: which files you opened and what you checked. Screenshots help for anything visual.
- Anything that changes the preview must keep working under its Content-Security-Policy: scripts only run from `Assets/preview/preview.js` and the bundled libraries, never from the markdown itself.
- The preview talks to the app through `sendToHost` in `preview.js`, which uses the WebView's message channel. Don't navigate to `app://` URLs directly: WebKitGTK on Linux shows an error page instead of letting the app cancel them.
- Windows gets the most use. macOS and Linux are checked automatically at release time (the app must start and render a document), but real-world fixes for them are very welcome.

Every pull request is built automatically on Windows; please make sure that build passes.

## Releasing

For maintainers. Releases are built by [`release.yml`](.github/workflows/release.yml):

1. Update the version in `SimpleMarkdownViewer.csproj` (Version, AssemblyVersion, FileVersion), `installer.iss` (MyAppVersion), the About dialog text in `MainWindow.axaml.cs`, and the installer file name in `build-installer.bat`.
2. In `CHANGELOG.md`, rename `## Unreleased` to `## X.Y.Z - YYYY-MM-DD`. The release notes are taken from this section.
3. Commit, then push a tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.
4. The workflow checks that the tag matches the project versions, builds the Windows installer, the macOS disk images (Apple Silicon and Intel) and the Linux .deb and AppImage, and starts the macOS and Linux builds to make sure they render a document. Screenshots of that check are saved with the run.
5. It creates a **draft** release with the changelog notes, a download table and SHA-256 checksums. Review it on the [Releases](https://github.com/KrunchMuffin/SimpleMarkdownViewer/releases) page and publish it. Publishing is what makes the update notice appear in the app.

To try the workflow without releasing, run it from the Actions tab and leave the tag empty. The downloads are kept as artifacts of that run.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you agree to uphold it.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
