# Contributing

Thanks for your interest in Simple Markdown Viewer! Bug reports, ideas, and pull requests are all welcome.

## Reporting Bugs and Requesting Features

Use the [issue templates](https://github.com/KrunchMuffin/SimpleMarkdownViewer/issues/new/choose). For bugs, the app version (Help > About), your OS version, and steps to reproduce make a big difference. If the app crashes, the bug template explains how to grab the error details from Windows Event Viewer.

Security problems should be reported privately; see [SECURITY.md](SECURITY.md).

## Making Changes

For anything bigger than a small fix, please open an issue first so we can agree on the approach before you spend time on it.

### Build and Run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build
dotnet run -- path/to/file.md
```

See the [README](README.md#building-from-source) for publishing and building the Windows installer.

### Project Layout

- `MainWindow.axaml` / `MainWindow.axaml.cs` - The window: tabs, editor, menus, file watching, settings
- `MarkdownRenderer.cs` - Markdown to HTML (Markdig, plus Mermaid and math preprocessing)
- `PreviewPage.cs` - Builds the preview page, including its Content-Security-Policy
- `Assets/preview/` - Preview styles and script (`preview.css`, `print.css`, `preview.js`)
- `Assets/libs/` - Bundled Mermaid, KaTeX, and highlight.js
- `UpdateChecker.cs` - Checks GitHub for newer releases
- `Program.cs` - Startup and single-instance handling

### Guidelines

- Keep pull requests focused on one change, and match the style of the surrounding code.
- There are no automated tests yet, so describe how you tested your change: which files you opened and what you checked. Screenshots help for anything visual.
- Anything that changes the preview must keep working under its Content-Security-Policy: scripts only run from `Assets/preview/preview.js` and the bundled libraries, never from the markdown itself.
- Windows is the primary platform. macOS and Linux fixes are very welcome, since they're currently untested.

Every pull request is built automatically on Windows; please make sure that build passes.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you agree to uphold it.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
