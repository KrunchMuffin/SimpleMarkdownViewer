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
build-installer.bat
```

## Architecture

This is a single-window Avalonia UI desktop application for viewing markdown files.

### Core Components

- **MainWindow** (`MainWindow.axaml.cs`) - Contains all application logic in a single file:
  - Tab management (`TabState` class) - Each open file is a tab with its own file watcher
  - Settings persistence (`AppSettings` class) - Dark mode preference and recent files stored in `%LocalAppData%/SimpleMarkdownViewer/settings.json`
  - Markdown rendering - Uses Markdig with preprocessing for Mermaid diagrams and KaTeX math
  - WebView integration - Renders HTML in WebView2 (Windows), WKWebView (macOS), or WebKitGTK (Linux)

### Rendering Pipeline

1. Markdown file is read
2. Preprocessed for Mermaid (`PreprocessMermaid`) and math (`PreprocessMath`)
3. Converted to HTML via Markdig
4. Injected into HTML template (`GetTemplate`) with theme-aware styling
5. Written to temp file and loaded in WebView

### Key Dependencies

- **Avalonia 11.2.1** - Cross-platform UI framework
- **WebView.Avalonia** - Cross-platform WebView wrapper
- **Markdig** - Markdown parsing with advanced extensions
- Client-side: highlight.js, Mermaid, KaTeX (loaded from CDN)

## Platform Requirements

- **Windows**: WebView2 runtime (pre-installed on Windows 10/11)
- **macOS**: Uses built-in WKWebView
- **Linux**: Requires WebKitGTK (`libwebkit2gtk-4.1`)
