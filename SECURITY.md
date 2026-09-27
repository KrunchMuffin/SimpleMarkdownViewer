# Security Policy

## Supported Versions

Only the [latest release](https://github.com/KrunchMuffin/SimpleMarkdownViewer/releases/latest) receives security fixes. Please update before reporting, and use **Help > Check for Updates** in the app to see whether you're current.

## Reporting a Vulnerability

Please **don't** open a public issue for security problems. Instead, [report it privately on GitHub](https://github.com/KrunchMuffin/SimpleMarkdownViewer/security/advisories/new). Only the maintainer can see the report.

Helpful details to include:

- The app version (Help > About) and your operating system
- What an attacker could do, and what they need (for example, "the victim opens a crafted `.md` file")
- Steps or a sample file that reproduce it

This is a small project maintained in spare time, so responses are best-effort. You'll get an acknowledgement once the report has been read, and credit in the release notes if you'd like it.

## Scope

Examples of what's in scope:

- A markdown or Mermaid file that runs script in the preview, reads local files, or triggers app commands
- Links in a document that launch programs or open files without the user clicking them
- Problems with the update check or the installer

Issues in third-party components (Avalonia, WebView2, Markdig, Mermaid, KaTeX, highlight.js) are best reported to those projects, but let us know too if they affect this app.
