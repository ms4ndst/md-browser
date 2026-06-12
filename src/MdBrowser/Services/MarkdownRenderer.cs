using System;
using System.IO;
using System.Net;
using Markdig;

namespace MdBrowser.Services;

public sealed class MarkdownRenderer
{
    /// <summary>
    /// Virtual host that resolves to the *source* file's directory.
    /// Used as the base href so relative resources (images, css, fonts) in the
    /// markdown work without us inlining them.
    /// </summary>
    public const string VirtualHost = "mdbrowser.local";

    /// <summary>
    /// Virtual host that resolves to a per-user temp directory where the rendered
    /// preview HTML is written before we Navigate() to it.
    /// Bypasses WebView2's 2 MB NavigateToString limit for files with many inline
    /// base64 images.
    /// </summary>
    public const string PreviewVirtualHost = "mdbrowser-preview.local";

    /// <summary>Stable file name used for every rendered preview.</summary>
    public const string PreviewFileName = "preview.html";

    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseTaskLists()
            .UseEmojiAndSmiley()
            .UseAutoLinks()
            .UseFootnotes()
            .UseGenericAttributes()
            .UseBootstrap()
            .Build();
    }

    public string Render(string markdown, string? sourceFilePath, CatppuccinFlavor flavor)
    {
        var html = Markdown.ToHtml(markdown ?? string.Empty, _pipeline);
        // Relative resources resolve against the virtual host when a source file is set,
        // otherwise against about:blank (no relative resources to load anyway).
        var baseHref = !string.IsNullOrEmpty(sourceFilePath)
            ? $"https://{VirtualHost}/"
            : "about:blank";

        return BuildDocument(html, baseHref, sourceFilePath, flavor);
    }

    public string RenderEmpty(string title, string subtitle, CatppuccinFlavor flavor)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeSubtitle = WebUtility.HtmlEncode(subtitle);
        var body = $"""
            <div class="empty-state">
              <div class="empty-icon">📄</div>
              <h1>{safeTitle}</h1>
              <p>{safeSubtitle}</p>
            </div>
            """;
        return BuildDocument(body, "about:blank", null, flavor);
    }

    private static HtmlPalette PaletteFor(CatppuccinFlavor f) => f switch
    {
        CatppuccinFlavor.Latte => new HtmlPalette
        {
            Base = "#eff1f5", Mantle = "#e6e9ef", Crust = "#dce0e8",
            Surface0 = "#ccd0da", Surface1 = "#bcc0cc", Surface2 = "#acb0be",
            Overlay0 = "#9ca0b0", Overlay1 = "#8c8fa1", Overlay2 = "#7c7f93",
            Subtext0 = "#6c6f85", Subtext1 = "#5c5f77", Text = "#4c4f69",
            Lavender = "#7287fd", Blue = "#1e66f5", Sapphire = "#209fb5", Sky = "#04a5e5",
            Teal = "#179299", Green = "#40a02b", Yellow = "#df8e1d", Peach = "#fe640b",
            Maroon = "#e64553", Red = "#d20f39", Mauve = "#8839ef", Pink = "#ea76cb"
        },
        CatppuccinFlavor.Frappe => new HtmlPalette
        {
            Base = "#303446", Mantle = "#292c3c", Crust = "#232634",
            Surface0 = "#414559", Surface1 = "#51576d", Surface2 = "#626880",
            Overlay0 = "#737994", Overlay1 = "#838ba7", Overlay2 = "#949cbb",
            Subtext0 = "#a5adce", Subtext1 = "#b5bfe2", Text = "#c6d0f5",
            Lavender = "#babbf1", Blue = "#8caaee", Sapphire = "#85c1dc", Sky = "#99d1db",
            Teal = "#81c8be", Green = "#a6d189", Yellow = "#e5c890", Peach = "#ef9f76",
            Maroon = "#ea999c", Red = "#e78284", Mauve = "#ca9ee6", Pink = "#f4b8e4"
        },
        CatppuccinFlavor.Macchiato => new HtmlPalette
        {
            Base = "#24273a", Mantle = "#1e2030", Crust = "#181926",
            Surface0 = "#363a4f", Surface1 = "#494d64", Surface2 = "#5b6078",
            Overlay0 = "#6e738d", Overlay1 = "#8087a2", Overlay2 = "#939ab7",
            Subtext0 = "#a5adcb", Subtext1 = "#b8c0e0", Text = "#cad3f5",
            Lavender = "#b7bdf8", Blue = "#8aadf4", Sapphire = "#7dc4e4", Sky = "#91d7e3",
            Teal = "#8bd5ca", Green = "#a6da95", Yellow = "#eed49f", Peach = "#f5a97f",
            Maroon = "#ee99a0", Red = "#ed8796", Mauve = "#c6a0f6", Pink = "#f5bde6"
        },
        _ => new HtmlPalette
        {
            Base = "#1e1e2e", Mantle = "#181825", Crust = "#11111b",
            Surface0 = "#313244", Surface1 = "#45475a", Surface2 = "#585b70",
            Overlay0 = "#6c7086", Overlay1 = "#7f849c", Overlay2 = "#9399b2",
            Subtext0 = "#a6adc8", Subtext1 = "#bac2de", Text = "#cdd6f4",
            Lavender = "#b4befe", Blue = "#89b4fa", Sapphire = "#74c7ec", Sky = "#89dceb",
            Teal = "#94e2d5", Green = "#a6e3a1", Yellow = "#f9e2af", Peach = "#fab387",
            Maroon = "#eba0ac", Red = "#f38ba8", Mauve = "#cba6f7", Pink = "#f5c2e7"
        }
    };

    private static string BuildDocument(string innerHtml, string baseHrefRaw, string? sourceFilePath, CatppuccinFlavor flavor)
    {
        var p = PaletteFor(flavor);
        var baseHref = WebUtility.HtmlEncode(baseHrefRaw);
        var titleSrc = sourceFilePath is null ? "Markdown Viewer" : Path.GetFileName(sourceFilePath);
        var safeTitle = WebUtility.HtmlEncode(titleSrc);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta http-equiv="Content-Security-Policy"
                    content="default-src 'none'; img-src data: blob: file: https: http:; style-src 'unsafe-inline'; font-src 'self' data:; connect-src 'none'; object-src 'none'; frame-src 'none'; script-src 'none';">
              <meta name="referrer" content="no-referrer">
              <base href="{{baseHref}}">
              <title>{{safeTitle}}</title>
              <style>
                :root {
                  --ctp-base:     {{p.Base}};
                  --ctp-mantle:   {{p.Mantle}};
                  --ctp-crust:    {{p.Crust}};
                  --ctp-surface0: {{p.Surface0}};
                  --ctp-surface1: {{p.Surface1}};
                  --ctp-surface2: {{p.Surface2}};
                  --ctp-overlay0: {{p.Overlay0}};
                  --ctp-overlay1: {{p.Overlay1}};
                  --ctp-overlay2: {{p.Overlay2}};
                  --ctp-subtext0: {{p.Subtext0}};
                  --ctp-subtext1: {{p.Subtext1}};
                  --ctp-text:     {{p.Text}};
                  --ctp-lavender: {{p.Lavender}};
                  --ctp-blue:     {{p.Blue}};
                  --ctp-sapphire: {{p.Sapphire}};
                  --ctp-sky:      {{p.Sky}};
                  --ctp-teal:     {{p.Teal}};
                  --ctp-green:    {{p.Green}};
                  --ctp-yellow:   {{p.Yellow}};
                  --ctp-peach:    {{p.Peach}};
                  --ctp-maroon:   {{p.Maroon}};
                  --ctp-red:      {{p.Red}};
                  --ctp-mauve:    {{p.Mauve}};
                  --ctp-pink:     {{p.Pink}};
                }
                * { box-sizing: border-box; }
                html, body {
                  margin: 0; padding: 0;
                  background: var(--ctp-base);
                  color: var(--ctp-text);
                  font-family: 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif;
                  font-size: 15px; line-height: 1.65;
                }
                body { padding: 32px 48px 64px; max-width: 900px; margin: 0 auto; }
                ::-webkit-scrollbar { width: 12px; height: 12px; }
                ::-webkit-scrollbar-track { background: var(--ctp-mantle); }
                ::-webkit-scrollbar-thumb {
                  background: var(--ctp-surface1); border-radius: 6px;
                  border: 3px solid var(--ctp-mantle);
                }
                ::-webkit-scrollbar-thumb:hover { background: var(--ctp-surface2); }
                h1, h2, h3, h4, h5, h6 {
                  color: var(--ctp-text); font-weight: 600;
                  margin-top: 1.6em; margin-bottom: 0.5em; line-height: 1.25;
                }
                h1 {
                  font-size: 2em; padding-bottom: .3em;
                  border-bottom: 1px solid var(--ctp-surface1);
                  color: var(--ctp-mauve);
                }
                h2 {
                  font-size: 1.5em; padding-bottom: .25em;
                  border-bottom: 1px solid var(--ctp-surface0);
                  color: var(--ctp-lavender);
                }
                h3 { font-size: 1.25em; color: var(--ctp-blue); }
                h4 { font-size: 1em; color: var(--ctp-sapphire); }
                h5 { font-size: 0.95em; color: var(--ctp-teal); }
                h6 { font-size: 0.9em; color: var(--ctp-subtext0); }
                p { margin: 0 0 1em; color: var(--ctp-text); }
                strong { color: var(--ctp-text); font-weight: 600; }
                em { color: var(--ctp-subtext1); }
                a {
                  color: var(--ctp-blue); text-decoration: none;
                  border-bottom: 1px dotted var(--ctp-overlay0);
                }
                a:hover { color: var(--ctp-sky); border-bottom: 1px solid var(--ctp-sky); }
                ul, ol { padding-left: 1.6em; }
                li { margin: 0.25em 0; }
                li::marker { color: var(--ctp-mauve); }
                blockquote {
                  border-left: 4px solid var(--ctp-mauve);
                  background: var(--ctp-mantle); margin: 1em 0;
                  padding: 0.6em 1em; color: var(--ctp-subtext1);
                  border-radius: 0 6px 6px 0;
                }
                blockquote p:last-child { margin-bottom: 0; }
                code {
                  font-family: 'Cascadia Code', 'JetBrains Mono', 'Consolas', monospace;
                  font-size: 0.92em;
                  background: var(--ctp-surface0); color: var(--ctp-peach);
                  padding: 0.15em 0.4em; border-radius: 4px;
                }
                pre {
                  background: var(--ctp-mantle);
                  border: 1px solid var(--ctp-surface0);
                  border-radius: 8px; padding: 14px 18px;
                  overflow-x: auto; line-height: 1.5;
                }
                pre code { background: transparent; color: var(--ctp-text); padding: 0; font-size: 0.9em; }
                hr { height: 1px; border: 0; background: var(--ctp-surface1); margin: 2em 0; }
                table {
                  border-collapse: collapse; width: 100%; margin: 1em 0;
                  background: var(--ctp-mantle);
                  border-radius: 6px; overflow: hidden;
                }
                th, td {
                  padding: 8px 14px; text-align: left;
                  border-bottom: 1px solid var(--ctp-surface0);
                }
                th { background: var(--ctp-surface0); color: var(--ctp-lavender); font-weight: 600; }
                tr:last-child td { border-bottom: none; }
                tbody tr:hover { background: var(--ctp-surface0); }
                img { max-width: 100%; height: auto; border-radius: 6px; }
                input[type=checkbox] { accent-color: var(--ctp-mauve); margin-right: 4px; }
                kbd {
                  background: var(--ctp-surface1); color: var(--ctp-text);
                  border: 1px solid var(--ctp-surface2);
                  border-radius: 4px; padding: 1px 6px;
                  font-family: 'Cascadia Code', monospace; font-size: 0.85em;
                }
                .empty-state { text-align: center; padding: 80px 20px; color: var(--ctp-subtext0); }
                .empty-state h1 {
                  color: var(--ctp-mauve); border: none;
                  font-size: 1.5em; margin-bottom: 0.4em;
                }
                .empty-state p { color: var(--ctp-subtext0); }
                .empty-icon { font-size: 64px; margin-bottom: 16px; opacity: 0.6; }
              </style>
            </head>
            <body>
            {{innerHtml}}
            </body>
            </html>
            """;
    }

    private sealed class HtmlPalette
    {
        public string Base = "", Mantle = "", Crust = "";
        public string Surface0 = "", Surface1 = "", Surface2 = "";
        public string Overlay0 = "", Overlay1 = "", Overlay2 = "";
        public string Subtext0 = "", Subtext1 = "", Text = "";
        public string Lavender = "", Blue = "", Sapphire = "", Sky = "";
        public string Teal = "", Green = "", Yellow = "", Peach = "";
        public string Maroon = "", Red = "", Mauve = "", Pink = "";
    }
}
