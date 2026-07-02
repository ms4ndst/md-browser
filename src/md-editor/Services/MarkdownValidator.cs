using System;
using System.Collections.Generic;
using System.IO;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdEditor.Services;

public enum IssueSeverity { Info, Warning, Error }

public sealed record ValidationIssue(int Line, int Column, IssueSeverity Severity, string Message)
{
    public string Glyph => Severity switch
    {
        IssueSeverity.Error => "●",
        IssueSeverity.Warning => "▲",
        _ => "ⓘ"
    };
}

public static class MarkdownValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(
        string markdown,
        string? sourceFilePath,
        MarkdownPipeline pipeline)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(markdown)) return issues;

        MarkdownDocument doc;
        try
        {
            doc = Markdown.Parse(markdown, pipeline);
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue(1, 1, IssueSeverity.Error, $"Parse failed: {ex.Message}"));
            return issues;
        }

        var sourceDir = !string.IsNullOrEmpty(sourceFilePath)
            ? Path.GetDirectoryName(sourceFilePath)
            : null;

        int prevHeadingLevel = 0;
        foreach (var block in doc.Descendants<HeadingBlock>())
        {
            if (prevHeadingLevel > 0 && block.Level > prevHeadingLevel + 1)
            {
                issues.Add(new ValidationIssue(
                    block.Line + 1, block.Column + 1,
                    IssueSeverity.Warning,
                    $"Heading level jumps from H{prevHeadingLevel} to H{block.Level}"));
            }
            prevHeadingLevel = block.Level;

            var text = block.Inline is null ? string.Empty : InlineToString(block.Inline);
            if (string.IsNullOrWhiteSpace(text))
            {
                issues.Add(new ValidationIssue(
                    block.Line + 1, block.Column + 1,
                    IssueSeverity.Warning,
                    "Empty heading"));
            }
        }

        foreach (var fenced in doc.Descendants<FencedCodeBlock>())
        {
            if (string.IsNullOrWhiteSpace(fenced.Info))
            {
                issues.Add(new ValidationIssue(
                    fenced.Line + 1, fenced.Column + 1,
                    IssueSeverity.Info,
                    "Fenced code block has no language hint"));
            }
        }

        foreach (var link in doc.Descendants<LinkInline>())
        {
            var url = link.Url ?? string.Empty;
            if (string.IsNullOrEmpty(url)) continue;

            // data: URIs - check for stripped/truncated payload before treating them as valid.
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (TryDescribeBrokenDataUri(url, out var reason))
                {
                    var label = link.IsImage ? "Image" : "Link";
                    issues.Add(new ValidationIssue(
                        link.Line + 1, link.Column + 1,
                        IssueSeverity.Error,
                        $"{label} has a broken data URI: {reason}"));
                }
                continue;
            }

            if (IsAbsoluteOrAnchor(url)) continue;
            if (sourceDir is null) continue;

            var resolved = TryResolveLocalPath(url, sourceDir);
            if (resolved is not null && !File.Exists(resolved))
            {
                var label = link.IsImage ? "Image" : "Link";
                issues.Add(new ValidationIssue(
                    link.Line + 1, link.Column + 1,
                    IssueSeverity.Error,
                    $"{label} target not found: {url}"));
            }
        }

        return issues;
    }

    /// <summary>
    /// Detect data URIs whose payload has been stripped or truncated.
    /// Returns true and a short reason when the URI is unrenderable.
    /// </summary>
    private static bool TryDescribeBrokenDataUri(string url, out string reason)
    {
        var commaIdx = url.IndexOf(',');
        if (commaIdx < 0)
        {
            // e.g. "data:image/png;base64..." with no comma + no payload at all
            reason = "no payload separator (missing comma)";
            return true;
        }

        var header = url[..commaIdx];
        var payload = url[(commaIdx + 1)..];

        // Ellipsis placeholders inserted by lossy conversion tools.
        if (payload.Contains("...") || header.Contains("..."))
        {
            reason = "payload was replaced with '...' placeholder";
            return true;
        }

        // Anything below ~32 chars is too short to be a real image even at 1x1.
        // A 1x1 PNG base64 is ~88 chars; SVG inline data is typically longer.
        if (payload.Length < 32)
        {
            reason = $"payload is only {payload.Length} chars (too short to be a real image)";
            return true;
        }

        // Explicit "stripped" markers some tools leave behind.
        if (payload.Equals("stripped", StringComparison.OrdinalIgnoreCase)
            || payload.Equals("removed", StringComparison.OrdinalIgnoreCase)
            || payload.Equals("redacted", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"payload is literal '{payload}'";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string InlineToString(Markdig.Syntax.Inlines.ContainerInline container)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            if (inline is LiteralInline lit) sb.Append(lit.Content.ToString());
            else if (inline is ContainerInline cc) sb.Append(InlineToString(cc));
        }
        return sb.ToString();
    }

    private static bool IsAbsoluteOrAnchor(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("#");

    private static string? TryResolveLocalPath(string url, string sourceDir)
    {
        var hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];
        var query = url.IndexOf('?');
        if (query >= 0) url = url[..query];
        if (string.IsNullOrEmpty(url)) return null;

        try
        {
            return Path.GetFullPath(Path.Combine(sourceDir, url.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }
    }
}
