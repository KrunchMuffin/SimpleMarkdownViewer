# Changelog

## 1.7.0 - 2026-10-31

### Added
- A portable Windows download (`SimpleMarkdownViewer-<version>-win-x64.zip`): unzip and run, no installer. Portable copies open the release page instead of installing updates in-app.
- Package manager manifests for winget and Scoop under `packaging/`, and a workflow that submits each published release to winget.

### Changed
- Updated Markdig to 1.5.1, which includes several markdown parsing fixes, Mermaid to 12.2.0 and KaTeX to 0.19.1. Existing diagrams keep their current look and layout. A diagram can opt in to the new Mermaid look or ELK layout through its own `config` block.
- On macOS, Mermaid diagrams now need macOS 14.4 or later.

## 1.6.0 - 2026-10-14

### Added
- On Windows, the update notice's Install Update button now downloads and installs the new version for you, then restarts the app, instead of opening the release page. The files you had open are reopened after the restart. The download is checked against the SHA-256 checksum published with the release before it runs, and you're asked to save any unsaved changes first. Windows asks for administrator permission to install. The portable build, macOS and Linux still open the release page.

### Fixed
- Opening a file by a relative path from the command line (for example `SimpleMarkdownViewer notes.md` in a terminal) crashed at startup. A relative path handed to an already-running instance is now resolved against that terminal's folder as well.

### Changed
- Faster startup. The app is precompiled for its platform at build time, the editor's syntax highlighting is loaded the first time you enter edit mode, and the Mermaid and KaTeX libraries are loaded only for documents that use diagrams or math. Switching tabs and changing the theme are quicker for the same reason, and the welcome page no longer flashes before a file opened at startup.
- Updated Avalonia to 12.1.4.

## 1.5.2 - 2026-09-27

### Added
- Downloads for macOS (Apple Silicon and Intel) and Linux (.deb and AppImage), built and tested automatically for each release. These are new, so please report anything that doesn't work.

### Fixed
- On Linux, the preview showed "The URL can't be shown" instead of the document.
- On macOS, the menu bar showed "Avalonia Application" instead of the app's name.

### Changed
- The README, website, contributing guide and bug report form now cover macOS and Linux.

## 1.5.1 - 2026-09-27

### Added
- Update notifications. At most once a day on startup, the app checks GitHub for a newer release and shows a notice with Download and Dismiss buttons. Nothing is downloaded or installed automatically, and no information about you or your files is sent. Use Help > Check for Updates to check any time, or turn automatic checks off with Help > Check for Updates Automatically.

### Changed
- Updated the README: edit mode, custom CSS, update checks, the full shortcut list, and corrected file-association instructions.

## 1.5.0 - 2026-09-27

### Security
- Markdown files can no longer run JavaScript in the preview. Raw HTML such as `<script>`, `onerror=` attributes and `javascript:` links is now blocked by a Content-Security-Policy, while ordinary HTML like `<details>` and `<img>` still renders.
- Mermaid diagrams render in strict mode (labels are escaped, click callbacks are ignored).
- Updated Avalonia to fix a high-severity advisory in a transitive dependency (Tmds.DBus, GHSA-xrw6-gwf8-vvr9).

### Fixed
- Fixed a crash when a file was changed externally while another program still had it locked.
- One external save no longer triggers several preview reloads, and saves made by writing a temp file and renaming it are now picked up.
- Clicking a web link in the preview opens it in your browser instead of replacing the document. In-page anchors scroll, and links to other markdown files open in a new tab.
- Relative images and links now resolve against the markdown file's folder.
- Typing right after entering edit mode (Ctrl+E) or creating a new file now goes into the editor without clicking it first.
- "New from Clipboard" no longer leaves a file in the temp folder each time; leftover preview files from crashes are cleaned up.
- The WebView2 browser profile is now stored in `%LocalAppData%\SimpleMarkdownViewer\WebView2Data` instead of next to the program, which standard users can't write to under Program Files.

### Changed
- The preview updates in place while you type or when the file changes, keeping your scroll position and skipping diagrams that didn't change.
- Print uses the native print dialog.
- Now built on .NET 10 and Avalonia 12, with Avalonia's official WebView control.
- Updated Markdig 1.4.0, Mermaid 11.17.2, KaTeX 0.18.9 and highlight.js 11.12.0.
- The installer removes files left over from previous versions when upgrading.

## 1.4.1 - 2026-05-25

### Fixed
- Fixed a UI freeze when opening or pasting large markdown documents with long pipe tables.
- Moved markdown preview rendering off the Avalonia UI thread so expensive documents do not block the app shell.
- Avoided preview line-number decoration inside large table internals, which could make table-heavy files sluggish.
- Replaced whole-document fenced-code preprocessing with a linear scanner to keep large documents predictable.
