using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace MdEditor.Services;

public static class MarkdownHighlighting
{
    public static IHighlightingDefinition Build(CatppuccinFlavor flavor)
    {
        var p = HexPaletteFor(flavor);

        // Authored as inline XSHD so per-flavor recoloring is one string interpolation.
        var xshd = $@"<?xml version=""1.0""?>
<SyntaxDefinition name=""Markdown""
                  extensions="".md;.markdown;.mdown;.mkd;.mkdn""
                  xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">

  <Color name=""Heading""    foreground=""{p.Mauve}""    fontWeight=""bold""/>
  <Color name=""Bold""       foreground=""{p.Text}""     fontWeight=""bold""/>
  <Color name=""Italic""     foreground=""{p.Subtext1}"" fontStyle=""italic""/>
  <Color name=""Strike""     foreground=""{p.Overlay0}""/>
  <Color name=""InlineCode"" foreground=""{p.Peach}""/>
  <Color name=""CodeFence""  foreground=""{p.Green}""/>
  <Color name=""LinkText""   foreground=""{p.Blue}""/>
  <Color name=""LinkUrl""    foreground=""{p.Sapphire}""  fontStyle=""italic""/>
  <Color name=""Quote""      foreground=""{p.Subtext0}""  fontStyle=""italic""/>
  <Color name=""List""       foreground=""{p.Yellow}""    fontWeight=""bold""/>
  <Color name=""Hr""         foreground=""{p.Overlay0}""/>
  <Color name=""Html""       foreground=""{p.Maroon}""/>
  <Color name=""Task""       foreground=""{p.Teal}""/>

  <RuleSet>
    <!-- Fenced code blocks (multiline) -->
    <Span color=""CodeFence"" multiline=""true"">
      <Begin>^\s*```.*$</Begin>
      <End>^\s*```\s*$</End>
    </Span>

    <!-- ATX headings (whole line) -->
    <Rule color=""Heading"">^\#{{1,6}}\s.*$</Rule>

    <!-- Setext underlines for h1/h2 -->
    <Rule color=""Heading"">^=+\s*$</Rule>
    <Rule color=""Heading"">^-{{3,}}\s*$</Rule>

    <!-- Horizontal rule -->
    <Rule color=""Hr"">^\s*([-*_]\s*){{3,}}\s*$</Rule>

    <!-- Blockquote line -->
    <Rule color=""Quote"">^\s*&gt;.*$</Rule>

    <!-- Task list items -->
    <Rule color=""Task"">^\s*[-*+]\s\[[ xX]\]</Rule>

    <!-- List bullets at line start -->
    <Rule color=""List"">^\s*[-*+]\s</Rule>
    <Rule color=""List"">^\s*\d+\.\s</Rule>

    <!-- Image / link text -->
    <Rule color=""LinkText"">!?\[[^\]\n]*\]</Rule>
    <!-- Link URL in parens -->
    <Rule color=""LinkUrl"">\([^\s)]+\)</Rule>
    <!-- Reference-style links -->
    <Rule color=""LinkUrl"">^\s*\[[^\]\n]+\]:\s*\S.*$</Rule>

    <!-- Inline code -->
    <Span color=""InlineCode""><Begin>``</Begin><End>``</End></Span>
    <Span color=""InlineCode""><Begin>`</Begin><End>`</End></Span>

    <!-- Bold, italic, strikethrough -->
    <Span color=""Bold""><Begin>\*\*</Begin><End>\*\*</End></Span>
    <Span color=""Bold""><Begin>__</Begin><End>__</End></Span>
    <Rule color=""Italic"">(?&lt;!\*)\*[^*\n]+\*(?!\*)</Rule>
    <Rule color=""Italic"">(?&lt;!_)_[^_\n]+_(?!_)</Rule>
    <Span color=""Strike""><Begin>~~</Begin><End>~~</End></Span>

    <!-- HTML tags -->
    <Rule color=""Html"">&lt;/?[a-zA-Z][^&gt;]*&gt;</Rule>
  </RuleSet>
</SyntaxDefinition>";

        using var sr = new StringReader(xshd);
        using var xr = XmlReader.Create(sr);
        return HighlightingLoader.Load(xr, HighlightingManager.Instance);
    }

    private static Palette HexPaletteFor(CatppuccinFlavor f) => f switch
    {
        CatppuccinFlavor.Latte => new Palette
        {
            Text = "#4c4f69", Subtext1 = "#5c5f77", Subtext0 = "#6c6f85",
            Overlay0 = "#9ca0b0", Mauve = "#8839ef", Blue = "#1e66f5",
            Sapphire = "#209fb5", Teal = "#179299", Green = "#40a02b",
            Yellow = "#df8e1d", Peach = "#fe640b", Maroon = "#e64553"
        },
        CatppuccinFlavor.Frappe => new Palette
        {
            Text = "#c6d0f5", Subtext1 = "#b5bfe2", Subtext0 = "#a5adce",
            Overlay0 = "#737994", Mauve = "#ca9ee6", Blue = "#8caaee",
            Sapphire = "#85c1dc", Teal = "#81c8be", Green = "#a6d189",
            Yellow = "#e5c890", Peach = "#ef9f76", Maroon = "#ea999c"
        },
        CatppuccinFlavor.Macchiato => new Palette
        {
            Text = "#cad3f5", Subtext1 = "#b8c0e0", Subtext0 = "#a5adcb",
            Overlay0 = "#6e738d", Mauve = "#c6a0f6", Blue = "#8aadf4",
            Sapphire = "#7dc4e4", Teal = "#8bd5ca", Green = "#a6da95",
            Yellow = "#eed49f", Peach = "#f5a97f", Maroon = "#ee99a0"
        },
        _ => new Palette
        {
            Text = "#cdd6f4", Subtext1 = "#bac2de", Subtext0 = "#a6adc8",
            Overlay0 = "#6c7086", Mauve = "#cba6f7", Blue = "#89b4fa",
            Sapphire = "#74c7ec", Teal = "#94e2d5", Green = "#a6e3a1",
            Yellow = "#f9e2af", Peach = "#fab387", Maroon = "#eba0ac"
        }
    };

    private sealed class Palette
    {
        public string Text = "", Subtext1 = "", Subtext0 = "", Overlay0 = "";
        public string Mauve = "", Blue = "", Sapphire = "", Teal = "", Green = "";
        public string Yellow = "", Peach = "", Maroon = "";
    }
}
