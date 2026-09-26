using System.Text.RegularExpressions;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Assigns stable reference numbers from list order and figures out which
/// shots each subject appears in, so nobody has to track `&lt;Subject N&gt;` numbers by hand.</summary>
public static partial class ReferenceNumberer
{
    public static string SubjectTag(int n) => $"<Subject {n}>";
    public static string PictureTag(int n) => $"<Picture {n}>";
    public static string AudioTag(int n) => $"<Audio {n}>";
    public static string VideoTag(int n) => $"<Video {n}>";
    public static string ShotLabel(int n) => $"[Shot {n}]";
    public static string SpeakerTag(int subjectNumber) => $"(S{subjectNumber})";

    /// <summary>Assigns &lt;Subject N&gt;/&lt;Picture N&gt;/&lt;Audio N&gt; numbers from subject
    /// list order alone (subject audios start at <paramref name="firstAudioNumber"/>, which is one past
    /// <see cref="CountVideoAudios"/> because video soundtracks are numbered first). Used both by <see cref="Compute"/> and directly by the UI to label
    /// insert-tag chip buttons, so the numbers shown while writing always match the composed output.</summary>
    public static (
        IReadOnlyDictionary<Guid, int> SubjectNumbers,
        IReadOnlyDictionary<Guid, int> PictureNumbers,
        IReadOnlyDictionary<Guid, int> AudioNumbers) NumberSubjects(IReadOnlyList<Subject> subjects, int firstAudioNumber = 1)
    {
        var subjectNumbers = new Dictionary<Guid, int>();
        var pictureNumbers = new Dictionary<Guid, int>();
        var audioNumbers = new Dictionary<Guid, int>();

        var pictureCounter = 0;
        var audioCounter = firstAudioNumber - 1;
        for (var i = 0; i < subjects.Count; i++)
        {
            var subject = subjects[i];
            subjectNumbers[subject.Id] = i + 1;

            foreach (var picture in subject.Pictures)
                pictureNumbers[picture.Id] = ++pictureCounter;

            foreach (var audio in subject.Audios)
                audioNumbers[audio.Id] = ++audioCounter;
        }

        return (subjectNumbers, pictureNumbers, audioNumbers);
    }

    /// <summary>Assigns &lt;Video N&gt; numbers from source-video list order alone. Same rationale
    /// as <see cref="NumberSubjects"/> -- shared by <see cref="Compute"/> and the UI's video chips.</summary>
    public static IReadOnlyDictionary<Guid, int> NumberVideos(IReadOnlyList<VideoRef> videos)
    {
        var videoNumbers = new Dictionary<Guid, int>();
        for (var i = 0; i < videos.Count; i++)
            videoNumbers[videos[i].Id] = i + 1;
        return videoNumbers;
    }

    /// <summary>How many source videos have their own soundtrack in use
    /// (<see cref="VideoRef.AudioUse"/> not None) -- each takes one &lt;Audio N&gt; number.</summary>
    public static int CountVideoAudios(IEnumerable<VideoRef> videos) => videos.Count(v => v.AudioUse != VideoAudioUse.None);

    /// <summary>Assigns an &lt;Audio N&gt; number to each source video whose own soundtrack is in use,
    /// in video-list order starting at 1, keyed by the VideoRef's Id. Merged into
    /// <see cref="ReferenceNumbering.AudioNumbers"/> by <see cref="Compute"/>, so the existing
    /// tag-renumbering follows them.
    ///
    /// These come BEFORE every subject audio: MiniMaxH3ReferenceToVideo emits a reference video's
    /// soundtrack as its own audio item ahead of that video, and standalone reference audios after all
    /// videos (confirmed against the node's source), so the soundtrack is &lt;Audio 1&gt; and the first
    /// subject voice is &lt;Audio 2&gt; -- which is why <see cref="NumberSubjects"/> takes an offset.</summary>
    public static IReadOnlyDictionary<Guid, int> NumberVideoAudios(IReadOnlyList<VideoRef> videos)
    {
        var counter = 0;
        var numbers = new Dictionary<Guid, int>();
        foreach (var video in videos)
        {
            if (video.AudioUse != VideoAudioUse.None)
                numbers[video.Id] = ++counter;
        }
        return numbers;
    }

    /// <summary>Assigns speaker "(Sx)" IDs by the order subjects first actually speak, across
    /// shots in list order and each shot's Dialogue in insertion order. Per the guide: "Assign
    /// (Sx) once according to the order of actual vocal events in the target video" -- the ID
    /// "comes from the target video's global speaker order and is not independently assigned or
    /// renumbered" from &lt;Subject N&gt;, so a subject speaking third overall gets (S3) even if
    /// they're &lt;Subject 1&gt;, and a subject who never has a recorded line isn't assigned one
    /// at all (there's no vocal event to order by). Used both by <see cref="Compute"/> and
    /// directly by the UI's dialogue-insert helper, so the ID shown while writing always matches
    /// the composed output.</summary>
    public static IReadOnlyDictionary<Guid, int> NumberSpeakers(IReadOnlyList<Shot> shots)
    {
        var speakerNumbers = new Dictionary<Guid, int>();
        foreach (var shot in shots)
        {
            foreach (var line in shot.Dialogue)
            {
                if (!speakerNumbers.ContainsKey(line.SpeakerSubjectId))
                    speakerNumbers[line.SpeakerSubjectId] = speakerNumbers.Count + 1;
            }
        }
        return speakerNumbers;
    }

    public static ReferenceNumbering Compute(SceneProject project)
    {
        var (subjectNumbers, pictureNumbers, subjectAudioNumbers) =
            NumberSubjects(project.Subjects, CountVideoAudios(project.SourceVideos) + 1);
        var videoNumbers = NumberVideos(project.SourceVideos);

        var audioNumbers = new Dictionary<Guid, int>(subjectAudioNumbers);
        foreach (var (videoId, number) in NumberVideoAudios(project.SourceVideos))
            audioNumbers[videoId] = number;
        var speakerNumbers = NumberSpeakers(project.Shots);
        var shotNumbers = new Dictionary<Guid, int>();

        for (var i = 0; i < project.Shots.Count; i++)
            shotNumbers[project.Shots[i].Id] = i + 1;

        var appearances = ComputeSubjectAppearances(project, subjectNumbers, shotNumbers);

        return new ReferenceNumbering
        {
            SubjectNumbers = subjectNumbers,
            PictureNumbers = pictureNumbers,
            AudioNumbers = audioNumbers,
            VideoNumbers = videoNumbers,
            SpeakerNumbers = speakerNumbers,
            ShotNumbers = shotNumbers,
            SubjectAppearances = appearances
        };
    }

    /// <summary>Rewrites every &lt;Subject N&gt;/&lt;Picture N&gt;/&lt;Audio N&gt;/&lt;Video N&gt;
    /// tag in the project's shot text, summary, and ambient-sound fields to follow along when a
    /// list mutation (a deletion, a reorder) shifts what number a surviving reference now has --
    /// e.g. removing &lt;Picture 5&gt; renumbers every "&lt;Picture 6&gt;" elsewhere in the project
    /// down to "&lt;Picture 5&gt;", instead of silently leaving it pointing at a picture that no
    /// longer exists at that number. Compare <paramref name="before"/>/<paramref name="after"/>
    /// snapshots taken immediately around the mutation (e.g. both from <see cref="Compute"/>) --
    /// only numbers that actually changed for a still-surviving Id get rewritten; a removed
    /// reference's own tag is left as dangling text for the user to notice and clean up, same as
    /// today, since there's no sensible number to rewrite it to.</summary>
    public static void RewriteTagsAfterRenumbering(SceneProject project, ReferenceNumbering before, ReferenceNumbering after) =>
        RewriteTagsAfterRenumbering(project, [before], [after]);

    /// <summary>Segment-aware form: <paramref name="before"/>/<paramref name="after"/> hold one
    /// snapshot per segment (index 0 is the project's own, index i is <c>ForSegment(i)</c>), each
    /// applied to only that segment's text. The cast is shared, so a deleted or reordered
    /// subject/picture/audio shifts numbers in every segment's prompt -- but each segment has its
    /// own &lt;Video&gt; and video-soundtrack &lt;Audio&gt; numbering, which is why the maps can't be
    /// computed once and reused across segments.</summary>
    public static void RewriteTagsAfterRenumbering(
        SceneProject project, IReadOnlyList<ReferenceNumbering> before, IReadOnlyList<ReferenceNumbering> after)
    {
        for (var i = 0; i < before.Count && i < after.Count; i++)
        {
            var subjectMap = BuildRenumberMap(before[i].SubjectNumbers, after[i].SubjectNumbers);
            var pictureMap = BuildRenumberMap(before[i].PictureNumbers, after[i].PictureNumbers);
            var audioMap = BuildRenumberMap(before[i].AudioNumbers, after[i].AudioNumbers);
            var videoMap = BuildRenumberMap(before[i].VideoNumbers, after[i].VideoNumbers);

            if (subjectMap.Count == 0 && pictureMap.Count == 0 && audioMap.Count == 0 && videoMap.Count == 0)
                continue;

            if (i == 0)
            {
                foreach (var shot in project.Shots)
                    shot.Text = RewriteTags(shot.Text, subjectMap, pictureMap, audioMap, videoMap);

                project.Summary = RewriteTags(project.Summary, subjectMap, pictureMap, audioMap, videoMap);
                project.OverallSoundscape = RewriteTags(project.OverallSoundscape, subjectMap, pictureMap, audioMap, videoMap);
                project.NonDiegeticMusic = RewriteTags(project.NonDiegeticMusic, subjectMap, pictureMap, audioMap, videoMap);
            }
            else if (i - 1 < project.Continuations.Count)
            {
                var segment = project.Continuations[i - 1];
                foreach (var shot in segment.Shots)
                    shot.Text = RewriteTags(shot.Text, subjectMap, pictureMap, audioMap, videoMap);

                segment.Summary = RewriteTags(segment.Summary, subjectMap, pictureMap, audioMap, videoMap);
                segment.OverallSoundscape = RewriteTags(segment.OverallSoundscape, subjectMap, pictureMap, audioMap, videoMap);
                segment.NonDiegeticMusic = RewriteTags(segment.NonDiegeticMusic, subjectMap, pictureMap, audioMap, videoMap);
            }
        }
    }

    /// <summary>Snapshots the numbering of every segment (the project's own, then each
    /// continuation's view) -- the input <see cref="RewriteTagsAfterRenumbering(SceneProject, IReadOnlyList{ReferenceNumbering}, IReadOnlyList{ReferenceNumbering})"/>
    /// wants, taken immediately before and after a list mutation.</summary>
    public static IReadOnlyList<ReferenceNumbering> ComputeAllSegments(SceneProject project) =>
        Enumerable.Range(0, project.SegmentCount).Select(i => Compute(project.ForSegment(i))).ToList();

    /// <summary>Old number -> new number, for every Id present in both snapshots whose number
    /// actually changed. An Id missing from <paramref name="after"/> (it was just removed) or
    /// unchanged contributes nothing -- there's nothing to rewrite it to.</summary>
    private static Dictionary<int, int> BuildRenumberMap(IReadOnlyDictionary<Guid, int> before, IReadOnlyDictionary<Guid, int> after)
    {
        var map = new Dictionary<int, int>();
        foreach (var (id, oldNumber) in before)
        {
            if (after.TryGetValue(id, out var newNumber) && newNumber != oldNumber)
                map[oldNumber] = newNumber;
        }
        return map;
    }

    private static string RewriteTags(
        string text, IReadOnlyDictionary<int, int> subjectMap, IReadOnlyDictionary<int, int> pictureMap,
        IReadOnlyDictionary<int, int> audioMap, IReadOnlyDictionary<int, int> videoMap)
    {
        if (string.IsNullOrEmpty(text)) return text;

        return AnyTagRegex().Replace(text, match =>
        {
            var map = match.Groups["kind"].Value switch
            {
                "Subject" => subjectMap,
                "Picture" => pictureMap,
                "Audio" => audioMap,
                "Video" => videoMap,
                _ => null
            };
            if (map is null) return match.Value;

            var oldNumber = int.Parse(match.Groups["n"].Value);
            return map.TryGetValue(oldNumber, out var newNumber) ? $"<{match.Groups["kind"].Value} {newNumber}>" : match.Value;
        });
    }

    [GeneratedRegex(@"<(?<kind>Subject|Picture|Audio|Video) (?<n>\d+)>")]
    private static partial Regex AnyTagRegex();

    private static Dictionary<Guid, List<int>> ComputeSubjectAppearances(
        SceneProject project,
        IReadOnlyDictionary<Guid, int> subjectNumbers,
        IReadOnlyDictionary<Guid, int> shotNumbers)
    {
        var result = project.Subjects.ToDictionary(s => s.Id, _ => new List<int>());

        foreach (var shot in project.Shots)
        {
            var shotNumber = shotNumbers[shot.Id];
            var seenInThisShot = new HashSet<Guid>();

            foreach (Match match in SubjectTagRegex().Matches(shot.Text))
            {
                var n = int.Parse(match.Groups["n"].Value);
                var subject = project.Subjects.FirstOrDefault(s => subjectNumbers[s.Id] == n);
                if (subject is not null)
                    seenInThisShot.Add(subject.Id);
            }

            foreach (var line in shot.Dialogue)
                seenInThisShot.Add(line.SpeakerSubjectId);

            foreach (var subjectId in seenInThisShot)
            {
                if (result.TryGetValue(subjectId, out var shots) && !shots.Contains(shotNumber))
                    shots.Add(shotNumber);
            }
        }

        foreach (var shots in result.Values)
            shots.Sort();

        return result;
    }

    [GeneratedRegex(@"<Subject (?<n>\d+)>")]
    private static partial Regex SubjectTagRegex();
}
