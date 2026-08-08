using System.Text.RegularExpressions;

namespace MiniRef.Core.Services;

public enum ShotTagKind { Subject, Picture, Audio, Video }

/// <summary>One piece of a tokenized shot text: either a run of plain text (Kind is null) or a
/// single &lt;Subject N&gt;/&lt;Picture N&gt;/&lt;Audio N&gt;/&lt;Video N&gt; reference tag.
/// Text always holds the exact source substring, so re-concatenating every token's Text in order
/// reconstructs the original string.</summary>
public readonly record struct ShotTextToken(string Text, ShotTagKind? Kind, int Number);

/// <summary>Splits a shot's free-form narrative into plain-text and reference-tag pieces, so the
/// UI can render tags as chips without needing its own copy of the tag grammar. Deliberately only
/// recognizes the four reference tag kinds -- other bracketed syntax in shot text (the `&lt;d&gt;`
/// dialogue tag, on-screen text in quotes) is left as plain text.</summary>
public static partial class ShotTextTokenizer
{
    public static IEnumerable<ShotTextToken> Tokenize(string text)
    {
        var lastIndex = 0;
        foreach (Match match in TagRegex().Matches(text))
        {
            if (match.Index > lastIndex)
                yield return new ShotTextToken(text[lastIndex..match.Index], null, 0);

            var kind = Enum.Parse<ShotTagKind>(match.Groups["kind"].Value);
            yield return new ShotTextToken(match.Value, kind, int.Parse(match.Groups["n"].Value));

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
            yield return new ShotTextToken(text[lastIndex..], null, 0);
    }

    [GeneratedRegex(@"<(?<kind>Subject|Picture|Audio|Video) (?<n>\d+)>")]
    private static partial Regex TagRegex();
}
