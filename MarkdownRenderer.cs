using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;

namespace SimpleMarkdownViewer;

/// <summary>
/// Converts markdown to the HTML fragment shown in the preview. Mermaid fences
/// are protected from Markdig's typography transforms, and $...$ / $$...$$ math
/// is turned into placeholders the preview renders with KaTeX.
/// </summary>
internal sealed partial class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSmartyPants()
        .UseEmojiAndSmiley()
        .UseTaskLists()
        .UseDiagrams()
        .Build();

    // Ensure tables have a blank line before them (for Markdig compatibility)
    [GeneratedRegex(@"(\n[^\n\|]+)\n(\|[^\n]+\|)", RegexOptions.Multiline)]
    private static partial Regex TableWithoutBlankLine();

    [GeneratedRegex(@"(`+)([\s\S]*?)\1")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"@@MDVIEWER_CODE_SPAN_(\d+)@@")]
    private static partial Regex CodeSpanPlaceholder();

    [GeneratedRegex(@"(?<!\\)\$\$(.+?)(?<!\\)\$\$", RegexOptions.Singleline)]
    private static partial Regex DisplayMath();

    [GeneratedRegex(@"(?<![\\$])\$([^\s$\d](?:[^$\n]*?[^\s$])?)\$(?![$\d])")]
    private static partial Regex InlineMath();

    [GeneratedRegex(@"<!--MERMAID_PLACEHOLDER_(\d+)-->")]
    private static partial Regex MermaidPlaceholder();

    private static void AddLineNumbers(Markdig.Syntax.ContainerBlock container)
    {
        foreach (var block in container)
        {
            if (block is Markdig.Extensions.Tables.Table)
                continue;

            if (block.Line >= 0)
                block.GetAttributes().AddProperty("data-line", (block.Line + 1).ToString());

            if (block is Markdig.Syntax.ContainerBlock childContainer)
                AddLineNumbers(childContainer);
        }
    }

    private static string ExtractMermaidFences(string markdown, List<string> mermaidBlocks)
    {
        var result = new StringBuilder(markdown.Length);
        var position = 0;

        while (position < markdown.Length)
        {
            var lineStart = position;
            var lineEnd = FindLineEnd(markdown, lineStart);
            var nextLineStart = FindNextLineStart(markdown, lineEnd);
            var line = markdown.Substring(lineStart, lineEnd - lineStart);

            if (TryGetFenceLine(line, out var fenceChar, out var fenceLength, out var info)
                && info.StartsWith("mermaid", StringComparison.OrdinalIgnoreCase))
            {
                var codeStart = nextLineStart;
                var scanPosition = nextLineStart;

                while (scanPosition < markdown.Length)
                {
                    var closingLineStart = scanPosition;
                    var closingLineEnd = FindLineEnd(markdown, closingLineStart);
                    var closingNextLineStart = FindNextLineStart(markdown, closingLineEnd);
                    var closingLine = markdown.Substring(closingLineStart, closingLineEnd - closingLineStart);

                    if (IsClosingFenceLine(closingLine, fenceChar, fenceLength))
                    {
                        mermaidBlocks.Add(markdown.Substring(codeStart, closingLineStart - codeStart));
                        result.Append($"<!--MERMAID_PLACEHOLDER_{mermaidBlocks.Count - 1}-->");
                        position = closingNextLineStart;
                        goto ContinueScanning;
                    }

                    scanPosition = closingNextLineStart;
                }
            }

            result.Append(markdown, lineStart, nextLineStart - lineStart);
            position = nextLineStart;

        ContinueScanning:;
        }

        return result.ToString();
    }

    private static int FindLineEnd(string text, int start)
    {
        var index = start;
        while (index < text.Length && text[index] != '\r' && text[index] != '\n')
            index++;
        return index;
    }

    private static int FindNextLineStart(string text, int lineEnd)
    {
        if (lineEnd >= text.Length)
            return text.Length;

        if (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n')
            return lineEnd + 2;

        return lineEnd + 1;
    }

    private static bool TryGetFenceLine(string line, out char fenceChar, out int fenceLength, out string info)
    {
        fenceChar = '\0';
        fenceLength = 0;
        info = "";

        var index = 0;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
            index++;

        if (index >= line.Length || (line[index] != '`' && line[index] != '~'))
            return false;

        fenceChar = line[index];
        while (index < line.Length && line[index] == fenceChar)
        {
            fenceLength++;
            index++;
        }

        if (fenceLength < 3)
            return false;

        info = line.Substring(index).TrimStart();
        return true;
    }

    private static bool IsClosingFenceLine(string line, char fenceChar, int openingFenceLength)
    {
        var index = 0;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
            index++;

        var fenceLength = 0;
        while (index < line.Length && line[index] == fenceChar)
        {
            fenceLength++;
            index++;
        }

        if (fenceLength < openingFenceLength)
            return false;

        while (index < line.Length)
        {
            if (line[index] != ' ' && line[index] != '\t')
                return false;
            index++;
        }

        return true;
    }

    public string ToHtml(string markdown, bool includeLineNumbers)
    {
        // Extract Mermaid blocks BEFORE Markdig processing to protect from typography transforms
        var mermaidBlocks = new List<string>();
        markdown = ExtractMermaidFences(markdown, mermaidBlocks);

        markdown = PreprocessMarkdown(markdown);

        string htmlContent;
        if (includeLineNumbers)
        {
            var document = Markdown.Parse(markdown, _pipeline);
            // Inject source line numbers as data attributes on all blocks (needed for line numbers and scroll sync)
            AddLineNumbers(document);
            using var writer = new System.IO.StringWriter();
            var renderer = new Markdig.Renderers.HtmlRenderer(writer);
            _pipeline.Setup(renderer);
            renderer.Render(document);
            writer.Flush();
            htmlContent = writer.ToString();
        }
        else
        {
            htmlContent = Markdown.ToHtml(markdown, _pipeline);
        }

        // Restore Mermaid blocks AFTER Markdig processing
        if (mermaidBlocks.Count == 0)
            return htmlContent;

        return MermaidPlaceholder().Replace(htmlContent, m =>
            $"<div class=\"mermaid\">\n{WebUtility.HtmlEncode(mermaidBlocks[int.Parse(m.Groups[1].Value)])}</div>");
    }

    public static string WrapMermaidSource(string source)
    {
        return $"```mermaid\n{source.TrimEnd('\r', '\n')}\n```";
    }

    public static bool LooksLikeMarkdownDocument(string content)
    {
        var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith("```", StringComparison.Ordinal)
            || content.Contains("```mermaid", StringComparison.OrdinalIgnoreCase)
            || content.Contains("~~~mermaid", StringComparison.OrdinalIgnoreCase);
    }

    private static string PreprocessMarkdown(string markdown)
    {
        var result = new StringBuilder(markdown.Length);
        var segmentStart = 0;
        var position = 0;

        while (position < markdown.Length)
        {
            var lineStart = position;
            var lineEnd = FindLineEnd(markdown, lineStart);
            var nextLineStart = FindNextLineStart(markdown, lineEnd);
            var line = markdown.Substring(lineStart, lineEnd - lineStart);

            if (TryGetFenceLine(line, out var fenceChar, out var fenceLength, out _))
            {
                var scanPosition = nextLineStart;

                while (scanPosition < markdown.Length)
                {
                    var closingLineStart = scanPosition;
                    var closingLineEnd = FindLineEnd(markdown, closingLineStart);
                    var closingNextLineStart = FindNextLineStart(markdown, closingLineEnd);
                    var closingLine = markdown.Substring(closingLineStart, closingLineEnd - closingLineStart);

                    if (IsClosingFenceLine(closingLine, fenceChar, fenceLength))
                    {
                        if (lineStart > segmentStart)
                            result.Append(PreprocessMarkdownSegment(markdown.Substring(segmentStart, lineStart - segmentStart)));

                        result.Append(markdown, lineStart, closingNextLineStart - lineStart);
                        position = closingNextLineStart;
                        segmentStart = position;
                        goto ContinueScanning;
                    }

                    scanPosition = closingNextLineStart;
                }

                if (lineStart > segmentStart)
                    result.Append(PreprocessMarkdownSegment(markdown.Substring(segmentStart, lineStart - segmentStart)));

                result.Append(markdown, lineStart, markdown.Length - lineStart);
                return result.ToString();
            }

            position = nextLineStart;

        ContinueScanning:;
        }

        if (segmentStart < markdown.Length)
            result.Append(PreprocessMarkdownSegment(markdown.Substring(segmentStart)));

        return result.ToString();
    }

    private static string PreprocessMarkdownSegment(string markdown)
    {
        // Ensure tables have a blank line before them (for Markdig compatibility).
        markdown = TableWithoutBlankLine().Replace(markdown, "$1\n\n$2");

        return PreprocessMathSegment(markdown);
    }

    private static string PreprocessMathSegment(string markdown)
    {
        var codeSpans = new List<string>();
        markdown = CodeSpan().Replace(
            markdown,
            m =>
            {
                var index = codeSpans.Count;
                codeSpans.Add(m.Value);
                return $"@@MDVIEWER_CODE_SPAN_{index}@@";
            });

        markdown = DisplayMath().Replace(
            markdown,
            m =>
            {
                var math = WebUtility.HtmlEncode(m.Groups[1].Value.Trim());
                return $"<div class=\"math-display\" data-math=\"{math}\"></div>";
            });

        markdown = InlineMath().Replace(
            markdown,
            m =>
            {
                var math = WebUtility.HtmlEncode(m.Groups[1].Value.Trim());
                return $"<span class=\"math-inline\" data-math=\"{math}\"></span>";
            });

        if (codeSpans.Count == 0)
            return markdown;

        return CodeSpanPlaceholder().Replace(markdown, m => codeSpans[int.Parse(m.Groups[1].Value)]);
    }
}
