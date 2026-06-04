using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace MdBrowser.Services;

public sealed class MarkdownCompletionData : ICompletionData
{
    private readonly string _insertText;
    private readonly int? _caretOffsetIntoInsertion;

    public MarkdownCompletionData(string label, string insertText, string description, int? caretOffset = null, double priority = 0)
    {
        Text = label;
        _insertText = insertText;
        Description = description;
        _caretOffsetIntoInsertion = caretOffset;
        Priority = priority;
    }

    public ImageSource? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description { get; }
    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, _insertText);
        if (_caretOffsetIntoInsertion.HasValue)
        {
            var target = completionSegment.Offset + _caretOffsetIntoInsertion.Value;
            target = Math.Min(target, textArea.Document.TextLength);
            textArea.Caret.Offset = target;
        }
    }
}

public static class MarkdownCompletionProvider
{
    private static readonly string[] CodeLanguages =
    {
        "csharp", "javascript", "typescript", "python", "bash", "powershell",
        "json", "yaml", "xml", "html", "css", "sql", "rust", "go", "java",
        "kotlin", "swift", "ruby", "php", "c", "cpp", "diff", "ini", "toml",
        "markdown", "dockerfile", "makefile", "lua"
    };

    public static IEnumerable<ICompletionData> Snippets()
    {
        // Format: (label, insertText, description, caretOffset)
        yield return new MarkdownCompletionData(
            "# Heading 1",      "# ",              "ATX heading level 1",     caretOffset: 2);
        yield return new MarkdownCompletionData(
            "## Heading 2",     "## ",             "ATX heading level 2",     caretOffset: 3);
        yield return new MarkdownCompletionData(
            "### Heading 3",    "### ",            "ATX heading level 3",     caretOffset: 4);
        yield return new MarkdownCompletionData(
            "#### Heading 4",   "#### ",           "ATX heading level 4",     caretOffset: 5);

        yield return new MarkdownCompletionData(
            "**bold**",         "**bold**",        "Bold text",               caretOffset: 2);
        yield return new MarkdownCompletionData(
            "*italic*",         "*italic*",        "Italic text",             caretOffset: 1);
        yield return new MarkdownCompletionData(
            "~~strike~~",       "~~strike~~",      "Strikethrough",           caretOffset: 2);
        yield return new MarkdownCompletionData(
            "`code`",           "`code`",          "Inline code",             caretOffset: 1);

        yield return new MarkdownCompletionData(
            "[link](url)",      "[text](https://)", "Inline link",            caretOffset: 1);
        yield return new MarkdownCompletionData(
            "![image](path)",   "![alt](image.png)", "Image",                 caretOffset: 2);
        yield return new MarkdownCompletionData(
            "[ref][id]",        "[text][id]",      "Reference-style link",    caretOffset: 1);
        yield return new MarkdownCompletionData(
            "[id]: url",        "[id]: https://",  "Reference definition",    caretOffset: 1);

        yield return new MarkdownCompletionData(
            "> quote",          "> ",              "Blockquote",              caretOffset: 2);
        yield return new MarkdownCompletionData(
            "- list item",      "- ",              "Bulleted list",           caretOffset: 2);
        yield return new MarkdownCompletionData(
            "1. numbered",      "1. ",             "Numbered list",           caretOffset: 3);
        yield return new MarkdownCompletionData(
            "- [ ] task",       "- [ ] ",          "Task list item",          caretOffset: 6);
        yield return new MarkdownCompletionData(
            "- [x] done",       "- [x] ",          "Completed task",          caretOffset: 6);

        yield return new MarkdownCompletionData(
            "horizontal rule",  "\n---\n",          "Horizontal rule",         caretOffset: 5);

        yield return new MarkdownCompletionData(
            "table",
            "| Col 1 | Col 2 |\n| --- | --- |\n|  |  |\n",
            "3-row table skeleton",
            caretOffset: 36);

        // Fenced code per language
        foreach (var lang in CodeLanguages)
        {
            var snippet = $"```{lang}\n\n```";
            yield return new MarkdownCompletionData(
                $"code fence: {lang}",
                snippet,
                $"Fenced code block ({lang})",
                caretOffset: lang.Length + 4);
        }
    }

    public static IEnumerable<ICompletionData> CodeFenceLanguages()
    {
        foreach (var lang in CodeLanguages)
        {
            yield return new MarkdownCompletionData(
                lang,
                lang + "\n\n```",
                $"Code fence language: {lang}",
                caretOffset: lang.Length + 1);
        }
    }

    public static IEnumerable<ICompletionData> FilterByPrefix(IEnumerable<ICompletionData> all, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return all;
        return all.Where(d => d.Text.Contains(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
