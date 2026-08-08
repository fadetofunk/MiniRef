using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

/// <summary>Builds and serializes the FlowDocument a shot's RichTextBox displays: reference tags
/// render as colored InlineUIContainer chips (subject/picture/audio/video each get their own
/// color) while everything else stays as plain Run/LineBreak inlines. The chip's raw tag text
/// (e.g. "&lt;Subject 2&gt;") is stashed on the chip Border's Tag property so <see cref="Serialize"/>
/// can round-trip back to the plain-text format PromptComposer and ReferenceNumberer consume --
/// the FlowDocument is purely a view over Shot.Text, never the source of truth.</summary>
public static class ShotRichTextBuilder
{
    private static readonly Brush SubjectBrush = Freeze(0x3B, 0x6E, 0xA5);
    private static readonly Brush PictureBrush = Freeze(0x3E, 0x8E, 0x7E);
    private static readonly Brush AudioBrush = Freeze(0x7B, 0x5E, 0xA7);
    private static readonly Brush VideoBrush = Freeze(0xB5, 0x76, 0x2B);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static FlowDocument BuildDocument(string text, IReadOnlyList<Subject> subjects, IReadOnlyList<VideoRef> videos)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };

        foreach (var token in ShotTextTokenizer.Tokenize(text))
        {
            if (token.Kind is null)
                AppendPlainText(paragraph, token.Text);
            else
                paragraph.Inlines.Add(new InlineUIContainer(BuildChip(token, subjects, videos)));
        }

        return new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }

    /// <summary>Inserts <paramref name="text"/> (which may itself mix plain text and reference
    /// tags, e.g. a dialogue line starting with "&lt;Subject 1&gt;") at <paramref name="at"/>,
    /// chipifying any tags it contains. Returns a TextPointer immediately after the inserted
    /// content, so callers can place the caret there.</summary>
    public static TextPointer InsertAt(TextPointer at, string text, IReadOnlyList<Subject> subjects, IReadOnlyList<VideoRef> videos)
    {
        var pointer = at;
        foreach (var token in ShotTextTokenizer.Tokenize(text))
        {
            pointer = token.Kind is null
                ? InsertPlainText(pointer, token.Text)
                : new InlineUIContainer(BuildChip(token, subjects, videos), pointer).ElementEnd;
        }
        return pointer;
    }

    /// <summary>Walks the live FlowDocument back into the plain-text tag format, reading each
    /// chip's stashed raw tag text rather than its (possibly stale, e.g. after a rename) display
    /// label.</summary>
    public static string Serialize(FlowDocument document)
    {
        var sb = new StringBuilder();
        var firstParagraph = true;

        foreach (var block in document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;

            // Our own edits keep everything in one Paragraph and use LineBreak for newlines, but
            // pasting multi-line plain text makes WPF split it into separate Paragraph blocks
            // instead -- join those back with '\n' so the round-trip to Shot.Text still matches.
            if (!firstParagraph) sb.Append('\n');
            firstParagraph = false;

            AppendInlines(sb, paragraph.Inlines);
        }

        return sb.ToString();
    }

    private static void AppendInlines(StringBuilder sb, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    sb.Append(run.Text);
                    break;
                case LineBreak:
                    sb.Append('\n');
                    break;
                case InlineUIContainer { Child: Border { Tag: string rawTag } }:
                    sb.Append(rawTag);
                    break;
                case Span span:
                    AppendInlines(sb, span.Inlines);
                    break;
            }
        }
    }

    private static void AppendPlainText(Paragraph paragraph, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
                paragraph.Inlines.Add(new Run(lines[i]));
            if (i < lines.Length - 1)
                paragraph.Inlines.Add(new LineBreak());
        }
    }

    private static TextPointer InsertPlainText(TextPointer at, string text)
    {
        var pointer = at;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
                pointer = new Run(lines[i], pointer).ElementEnd;
            if (i < lines.Length - 1)
                pointer = new LineBreak(pointer).ElementEnd;
        }
        return pointer;
    }

    private static Border BuildChip(ShotTextToken token, IReadOnlyList<Subject> subjects, IReadOnlyList<VideoRef> videos)
    {
        var (label, background) = token.Kind switch
        {
            ShotTagKind.Subject => (ResolveSubjectLabel(token.Number, subjects), SubjectBrush),
            ShotTagKind.Picture => ($"Picture {token.Number}", PictureBrush),
            ShotTagKind.Audio => ($"Audio {token.Number}", AudioBrush),
            ShotTagKind.Video => (ResolveVideoLabel(token.Number, videos), VideoBrush),
            _ => (token.Text, Brushes.Gray)
        };

        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(1, 0, 1, 0),
            Tag = token.Text,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    // Subject/video numbers are assigned by ReferenceNumberer strictly from list order (1-based),
    // so the Nth entry is a direct index lookup -- no need to recompute the number dictionaries
    // just to resolve a single label.
    private static string ResolveSubjectLabel(int number, IReadOnlyList<Subject> subjects)
    {
        if (number >= 1 && number <= subjects.Count && subjects[number - 1].Name is { Length: > 0 } name)
            return name;
        return $"Subject {number}";
    }

    private static string ResolveVideoLabel(int number, IReadOnlyList<VideoRef> videos)
    {
        if (number >= 1 && number <= videos.Count && videos[number - 1].Description is { Length: > 0 } description)
            return description;
        return $"Video {number}";
    }
}
