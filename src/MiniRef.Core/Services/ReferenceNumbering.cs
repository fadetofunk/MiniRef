using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>The `&lt;Subject N&gt;` / `&lt;Picture N&gt;` / `&lt;Audio N&gt;` / `&lt;Video N&gt;`
/// numbers assigned to a project's entities, purely from list order.</summary>
public class ReferenceNumbering
{
    public required IReadOnlyDictionary<Guid, int> SubjectNumbers { get; init; }
    public required IReadOnlyDictionary<Guid, int> PictureNumbers { get; init; }
    public required IReadOnlyDictionary<Guid, int> AudioNumbers { get; init; }
    public required IReadOnlyDictionary<Guid, int> VideoNumbers { get; init; }
    public required IReadOnlyDictionary<Guid, int> ShotNumbers { get; init; }

    /// <summary>Subject.Id -> ordered, de-duplicated shot numbers the subject appears in,
    /// derived from &lt;Subject N&gt; occurrences in shot text plus dialogue speaker assignments.</summary>
    public required IReadOnlyDictionary<Guid, List<int>> SubjectAppearances { get; init; }

    public int SubjectNumber(Guid id) => SubjectNumbers.TryGetValue(id, out var n) ? n : 0;
    public int PictureNumber(Guid id) => PictureNumbers.TryGetValue(id, out var n) ? n : 0;
    public int AudioNumber(Guid id) => AudioNumbers.TryGetValue(id, out var n) ? n : 0;
    public int VideoNumber(Guid id) => VideoNumbers.TryGetValue(id, out var n) ? n : 0;
    public int ShotNumber(Guid id) => ShotNumbers.TryGetValue(id, out var n) ? n : 0;
}
