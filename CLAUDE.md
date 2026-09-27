# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build
dotnet build

# Run
dotnet run

# Publish (Windows)
dotnet publish -c Release -r win-x64 --self-contained -o publish

# Publish (macOS Intel)
dotnet publish -c Release -r osx-x64 --self-contained -o publish

# Publish (macOS Apple Silicon)
dotnet publish -c Release -r osx-arm64 --self-contained -o publish

# Publish (Linux)
dotnet publish -c Release -r linux-x64 --self-contained -o publish

# Build Windows installer (requires Inno Setup 6)
# NOTE: build-installer.bat does NOT work from bash. Run steps manually:
rm -rf publish
"C:/Program Files/dotnet/dotnet.exe" publish -c Release -r win-x64 --self-contained -o publish
"G:/Program Files (x86)/Inno Setup 6/ISCC.exe" installer.iss
```

## Architecture

This is a single-window Avalonia UI desktop application for viewing and editing markdown files with split-view live preview.

### Core Components

- **MainWindow** (`MainWindow.axaml.cs`) - UI and application logic:
  - Tab management (`TabState` class) - Each open file is a tab with its own file watcher
  - Split-view editor - AvaloniaEdit text editor (left) with live WebView preview (right), toggled via Ctrl+E
  - Settings persistence (`AppSettings` class) - Dark mode, preview line numbers, recent files, and update-check state stored in `%LocalAppData%/SimpleMarkdownViewer/settings.json`
  - Custom CSS support - Optional `custom-dark.css` / `custom-light.css` in settings folder, injected after built-in styles
  - WebView integration - `NativeWebView` renders in WebView2 (Windows), WKWebView (macOS), or WebKitGTK (Linux); WebView2 profile lives in `%LocalAppData%/SimpleMarkdownViewer/WebView2Data`
  - Preview updates - Same-tab re-renders swap content in place via `mdviewer.setContent()` (keeps scroll, reuses unchanged diagrams); tab/theme/line-number/custom-CSS changes do a full page load
  - Host commands - Preview JS sends `app://<command>?token=<nonce>` navigations (`toggle-edit`, `open-link`, `ready`); the host rejects any without the page's CSP nonce
  - Tab overflow - ScrollViewer with arrow buttons and dropdown picker for many open tabs
  - Context menus - Custom JS context menu in WebView preview; Avalonia context menu in editor with Format submenu; tab right-click with Close/Close Others/Close to Right/Close All
- **MarkdownRenderer** (`MarkdownRenderer.cs`) - Markdig pipeline plus preprocessing for Mermaid fences and KaTeX math; optional source line numbers via AST walking
- **PreviewPage** (`PreviewPage.cs`) - Builds the preview page shell: Content-Security-Policy (nonce-only scripts), `<base href>` to the file's folder, theme/line-number attributes, custom CSS
- **Preview assets** (`Assets/preview/`) - `preview.css` (theme colors as CSS variables), `print.css`, `preview.js` (Mermaid/hljs/KaTeX rendering, diagram fullscreen, context menu, link routing)
- **UpdateChecker** (`UpdateChecker.cs`) - Reads the latest release from the GitHub releases API; MainWindow checks at most once a day on startup (result cached in settings), shows a dismissible notice bar, and offers Help > Check for Updates plus an on/off toggle
- **Program.cs** - Entry point with single-instance support via named mutex and named pipe IPC

### Rendering Pipeline

1. Markdown file is read (explicit UTF-8 encoding)
2. Mermaid fences extracted and math preprocessed (`MarkdownRenderer`)
3. Converted to an HTML fragment via Markdig (`MarkdownRenderer.ToHtml`) and cached per tab
4. If the WebView already shows this tab's page, the fragment is swapped in by script; otherwise
   `PreviewPage.Build` wraps it in the page shell, which is written to a temp file (UTF-8 with BOM) and loaded

### Key Dependencies

- **Avalonia 11.3.12** - Cross-platform UI framework
- **AvaloniaEdit** - Code editor control with TextMate syntax highlighting
- **Avalonia.Controls.WebView** - Official cross-platform `NativeWebView` control
- **Markdig** - Markdown parsing with advanced extensions
- Client-side: highlight.js, Mermaid, KaTeX (loaded from CDN)

## Version Bump Checklist

All four locations must be updated together:
- `SimpleMarkdownViewer.csproj` — Version, AssemblyVersion, FileVersion
- `installer.iss` — MyAppVersion (#define)
- `MainWindow.axaml.cs` — About dialog "Version X.Y.Z" text
- `build-installer.bat` — echo line referencing installer filename

## Project Info

- Publisher: DAB Worx Inc. (https://dabworx.com)
- Repo: https://github.com/KrunchMuffin/SimpleMarkdownViewer

## Platform Requirements

- **Windows**: WebView2 runtime (pre-installed on Windows 10/11)
- **macOS**: Uses built-in WKWebView
- **Linux**: Requires WebKitGTK (`libwebkit2gtk-4.1`)
