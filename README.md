# Simple Markdown Viewer

A lightweight, fast markdown viewer for Windows built with Avalonia UI and WebView2.

![Screenshot](screenshot.png)

## Features

- **Live Reload** - Automatically refreshes when the file changes
- **Tabs** - Open multiple markdown files at once
- **Dark/Light Mode** - Toggle with persisted preference
- **Syntax Highlighting** - Code blocks with highlight.js
- **Mermaid Diagrams** - Flowcharts, sequence diagrams, etc.
- **KaTeX Math** - LaTeX math rendering
- **Drag & Drop** - Drop markdown files onto the window
- **Clipboard Paste** - Create a preview from clipboard content (Ctrl+Shift+V)
- **Recent Files** - Quick access to recently opened files
- **Print/PDF** - Print or save as PDF via system dialog
- **File Association** - Register as default .md viewer

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+O | Open file |
| Ctrl+Shift+V | New from clipboard |
| Ctrl+W | Close tab |
| Ctrl+Shift+C | Copy markdown to clipboard |
| Ctrl+P | Print |
| F5 | Refresh |
| F12 | Dev tools |

## Requirements

- Windows 10/11
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (usually pre-installed on Windows 10/11)

## Building from Source

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build

```bash
git clone https://github.com/yourusername/SimpleMarkdownViewer.git
cd SimpleMarkdownViewer
dotnet build
```

### Run

```bash
dotnet run
```

### Publish (self-contained exe)

```bash
dotnet publish -c Release -r win-x64 --self-contained -o publish
```

## File Association

To register as the default handler for `.md` files:

1. Run `register-file-association.reg` as administrator
2. Right-click any `.md` file → Open with → Choose another app → Select SimpleMarkdownViewer

To unregister, run `unregister-file-association.reg`.

## Tech Stack

- [Avalonia UI](https://avaloniaui.net/) - Cross-platform .NET UI framework
- [WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) - Chromium-based web view
- [Markdig](https://github.com/xoofx/markdig) - Markdown parser
- [highlight.js](https://highlightjs.org/) - Syntax highlighting
- [Mermaid](https://mermaid.js.org/) - Diagrams
- [KaTeX](https://katex.org/) - Math rendering

## Download

Grab the latest installer from [Releases](https://github.com/yourusername/SimpleMarkdownViewer/releases).

Or build it yourself (see below).

## Building the Installer

To create the installer yourself:

1. Install [Inno Setup 6](https://jrsoftware.org/isdl.php)
2. Run `build-installer.bat`
3. Installer will be in the `installer` folder

## License

MIT License - see [LICENSE](LICENSE) file.
