using System;
using System.IO;
using System.Net;
using System.Text;

namespace SimpleMarkdownViewer;

/// <summary>
/// Builds the HTML page shell the WebView loads. Styles and behavior live in
/// Assets/preview (preview.css, print.css, preview.js); this class only wires
/// in per-render settings and the rendered markdown.
/// </summary>
internal static class PreviewPage
{
    private static readonly string AssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

    public sealed record Options(
        bool IsDarkMode,
        bool ShowLineNumbers,
        bool IsEditMode,
        string? BaseDirectory,
        string CustomCss,
        string Nonce);

    /// <summary>
    /// Only scripts carrying the page nonce may run, so script tags, event-handler
    /// attributes, and javascript: URLs in markdown are inert. Styles stay open
    /// (custom CSS, Mermaid, and KaTeX all use inline styles, and custom CSS may
    /// import web fonts) and images or media may come from anywhere, as markdown
    /// expects.
    /// </summary>
    private static string ContentSecurityPolicy(string nonce) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src file: https: 'unsafe-inline'; " +
        "img-src * data: blob: file:; media-src * data: blob: file:; font-src file: https: data:; " +
        "frame-src https:; connect-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    public static string CreateNonce() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));

    public static string Build(Options options, string contentHtml)
    {
        var theme = options.IsDarkMode ? "dark" : "light";
        var mermaidTheme = options.IsDarkMode ? "dark" : "default";
        var hljsTheme = options.IsDarkMode ? "github-dark" : "github";
        var rootClass = options.ShowLineNumbers ? " class=\"line-numbers\"" : "";

        var html = new StringBuilder(contentHtml.Length + 4096);
        html.Append($"""
            <!DOCTYPE html>
            <html lang="en" data-theme="{theme}" data-mermaid-theme="{mermaidTheme}"{rootClass}>
            <head>
                <meta charset="UTF-8">

            """);

        // Resolve relative links and images against the markdown file's folder,
        // not the temp folder the page is loaded from. It precedes the CSP, whose
        // base-uri 'none' then rejects any <base> smuggled in through markdown.
        if (options.BaseDirectory != null)
        {
            var baseUri = new Uri(Path.TrimEndingDirectorySeparator(options.BaseDirectory) + Path.DirectorySeparatorChar).AbsoluteUri;
            html.Append($"    <base href=\"{WebUtility.HtmlEncode(baseUri)}\">\n");
        }

        var nonce = options.Nonce;
        html.Append($"""
                <meta http-equiv="Content-Security-Policy" content="{ContentSecurityPolicy(nonce)}">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <link rel="stylesheet" href="{AssetUrl("preview/preview.css")}">
                <link rel="stylesheet" href="{AssetUrl($"libs/hljs/styles/{hljsTheme}.min.css")}">
                <link rel="stylesheet" href="{AssetUrl("libs/katex/katex.min.css")}">
            {options.CustomCss}
                <link rel="stylesheet" href="{AssetUrl("preview/print.css")}">

                <script nonce="{nonce}" src="{AssetUrl("libs/mermaid.min.js")}"></script>
                <script nonce="{nonce}" src="{AssetUrl("libs/hljs/highlight.min.js")}"></script>
                <script nonce="{nonce}" src="{AssetUrl("libs/hljs/languages/sql.min.js")}"></script>
                <script nonce="{nonce}" src="{AssetUrl("libs/hljs/languages/powershell.min.js")}"></script>
                <script nonce="{nonce}" src="{AssetUrl("libs/hljs/languages/csharp.min.js")}"></script>
                <script nonce="{nonce}" src="{AssetUrl("libs/katex/katex.min.js")}"></script>
            </head>
            <body>
                <article id="content" class="markdown-body">

            """);

        html.Append(contentHtml);

        html.Append($"""

                </article>

                <!-- Fullscreen overlay and controls -->
                <div id="fullscreenOverlay" class="fullscreen-overlay"></div>
                <div id="fullscreenControls" class="fullscreen-controls">
                    <span class="fullscreen-controls-title">Diagram Viewer</span>
                    <div class="fullscreen-controls-buttons">
                        <button data-action="zoom-in">Zoom +</button>
                        <button data-action="zoom-out">Zoom -</button>
                        <button data-action="reset">Reset</button>
                        <button data-action="fit">Fit</button>
                        <button data-action="close">✕ Close (Esc)</button>
                    </div>
                </div>

                <!-- Custom context menu -->
                <div id="ctxMenu" class="ctx-menu">
                    <div class="ctx-menu-item" id="ctxEdit">{(options.IsEditMode ? "Exit Edit Mode" : "Edit")}<span class="shortcut">Ctrl+E</span></div>
                    <div class="ctx-menu-sep"></div>
                    <div class="ctx-menu-item" id="ctxCopy">Copy<span class="shortcut">Ctrl+C</span></div>
                    <div class="ctx-menu-item" id="ctxSelectAll">Select All<span class="shortcut">Ctrl+A</span></div>
                </div>

                <script nonce="{nonce}" src="{AssetUrl("preview/preview.js")}"></script>
            </body>
            </html>
            """);

        return html.ToString();
    }

    private static string AssetUrl(string relativePath)
    {
        return new Uri(Path.Combine(AssetsDir, relativePath)).AbsoluteUri;
    }
}
