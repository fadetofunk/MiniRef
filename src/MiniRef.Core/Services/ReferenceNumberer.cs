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
    /// list order alone. Used both by <see cref="Compute"/> and directly by the UI to label
    /// insert-tag chip buttons, so the numbers shown while writing always match the composed output.</summary>
    public static (
        IReadOnlyDictionary<Guid, int> SubjectNumbers,
        IReadOnlyDictionary<Guid, int> PictureNumbers,
        IReadOnlyDictionary<Guid, int> AudioNumbers) NumberSubjects(IReadOnlyList<Subject> subjects)
    {
        var subjectNumbers = new Dictionary<Guid, int>();
        var pictureNumbers = new Dictionary<Guid, int>();
        var audioNumbers = new Dictionary<Guid, int>();

        var pictureCounter = 0;
        var audioCounter = 0;
        for (var i = 0; i < subjects.Count; i++)
        {
            var subject = subjects[i];
            subjectNumbers[subject.Id] = i + 1;

            foreach (var picture in subject.Pictures)
                pictureNumbers[picture.Id] = ++pictureCounter;

            if (subject.Audio is { } audio)
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

    public static ReferenceNumbering Compute(SceneProject project)
    {
        var (subjectNumbers, pictureNumbers, audioNumbers) = NumberSubjects(project.Subjects);
        var videoNumbers = NumberVideos(project.SourceVideos);
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
            ShotNumbers = shotNumbers,
            SubjectAppearances = appearances
        };
    }

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
