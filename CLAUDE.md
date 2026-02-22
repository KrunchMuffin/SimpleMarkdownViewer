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

- **MainWindow** (`MainWindow.axaml.cs`) - Contains all application logic in a single file:
  - Tab management (`TabState` class) - Each open file is a tab with its own file watcher
  - Split-view editor - AvaloniaEdit text editor (left) with live WebView preview (right), toggled via Ctrl+E
  - Settings persistence (`AppSettings` class) - Dark mode, preview line numbers, and recent files stored in `%LocalAppData%/SimpleMarkdownViewer/settings.json`
  - Custom CSS support - Optional `custom-dark.css` / `custom-light.css` in settings folder, injected after built-in styles
  - Markdown rendering - Uses Markdig with preprocessing for Mermaid diagrams and KaTeX math; optional source line numbers via AST walking
  - WebView integration - Renders HTML in WebView2 (Windows), WKWebView (macOS), or WebKitGTK (Linux)
  - Tab overflow - ScrollViewer with arrow buttons and dropdown picker for many open tabs
  - Context menus - Custom JS context menu in WebView preview; Avalonia context menu in editor with Format submenu; tab right-click with Close/Close Others/Close to Right/Close All
- **Program.cs** - Entry point with single-instance support via named mutex and named pipe IPC

### Rendering Pipeline

1. Markdown file is read (explicit UTF-8 encoding)
2. Preprocessed for Mermaid (`PreprocessMermaid`) and math (`PreprocessMath`)
3. Converted to HTML via Markdig (`ConvertMarkdownToHtml`)
4. Injected into HTML template (`GetTemplate`) with theme-aware styling
5. Written to temp file (UTF-8 with BOM) and loaded in WebView

### Key Dependencies

- **Avalonia 11.3.12** - Cross-platform UI framework
- **AvaloniaEdit** - Code editor control with TextMate syntax highlighting
- **WebView.Avalonia** - Cross-platform WebView wrapper
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
