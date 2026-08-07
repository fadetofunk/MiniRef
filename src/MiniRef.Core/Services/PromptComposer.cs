using System.Text;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Turns a SceneProject into the final six-section MiniMax H3 reference prompt text.</summary>
public static class PromptComposer
{
    public static string Compose(SceneProject project)
    {
        var numbering = ReferenceNumberer.Compute(project);

        // summary, retention_analysis, overall_soundscape, and non_diegetic_music are legitimately
        // empty in a well-formed project (e.g. no summary written yet, or no subject has a retention
        // value set, or the scene has no notable ambience/music) -- unlike subject_definitions and
        // detailed_description, which are always expected to have content. Rather than emit a header
        // the model has nothing to act on (or, for summary, just a floating "[task type]" tag with
        // nothing after it -- which risks being read as something to vocalize instead of metadata),
        // they're dropped entirely when blank. summary is checked against the raw field rather than
        // the composed content, since the "[task type] " prefix means the composed text is never
        // actually empty on its own.
        var retentionAnalysis = BuildRetentionAnalysis(project, numbering);
        var overallSoundscape = project.OverallSoundscape.Trim();
        var nonDiegeticMusic = project.NonDiegeticMusic.Trim();

        var sections = new (string Name, string Content)[]
        {
            ("subject_definitions", BuildSubjectDefinitions(project, numbering)),
            ("summary", BuildSummary(project)),
            ("retention_analysis", retentionAnalysis),
            ("detailed_description", BuildDetailedDescription(project, numbering)),
            ("overall_soundscape", overallSoundscape),
            ("non_diegetic_music", nonDiegeticMusic)
        }.Where(s => s.Name switch
        {
            "summary" => project.Summary.Trim().Length > 0,
            "retention_analysis" or "overall_soundscape" or "non_diegetic_music" => s.Content.Length > 0,
            _ => true
        }).ToArray();

        var sb = new StringBuilder();
        for (var i = 0; i < sections.Length; i++)
        {
            sb.Append(sections[i].Name).Append('\n');
            sb.Append(sections[i].Content).Append('\n');
            if (i < sections.Length - 1)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildSubjectDefinitions(SceneProject project, ReferenceNumbering numbering)
    {
        var sentences = new List<string>();

        foreach (var subject in project.Subjects)
        {
            var n = numbering.SubjectNumber(subject.Id);
            var description = NormalizeSentence(subject.Description);

            if (subject.Pictures.Count > 0)
            {
                var pictureTags = subject.Pictures
                    .Select(p => ReferenceNumberer.PictureTag(numbering.PictureNumber(p.Id)))
                    .ToList();
                description = $"{TrimTrailingPeriod(description)}, whose appearance comes from {JoinWithAnd(pictureTags)}.";
            }

            sentences.Add($"{ReferenceNumberer.SubjectTag(n)} is {description}");

            if (subject.Audio is { } audio)
            {
                var audioTag = ReferenceNumberer.AudioTag(numbering.AudioNumber(audio.Id));
                var subjectTag = ReferenceNumberer.SubjectTag(n);
                var speakerTag = ReferenceNumberer.SpeakerTag(n);
                var note = string.IsNullOrWhiteSpace(audio.Description) ? "" : $", {audio.Description.Trim()}";
                sentences.Add($"{audioTag} is the voice-timbre reference for {subjectTag} {speakerTag}{note}.");
            }
        }

        // Per the guide, <Video N> is reserved for whole-video relationships (an edit source, a
        // continuation point, or structural/temporal reference) -- distinct from a Subject whose
        // appearance merely comes from a video. Falls back to the guide's own canonical phrasing
        // ("<Video 1> is the source video for the target video edit.") when no description is given.
        foreach (var video in project.SourceVideos)
        {
            var n = numbering.VideoNumber(video.Id);
            var description = string.IsNullOrWhiteSpace(video.Description)
                ? "the source video for the target video edit."
                : NormalizeSentence(video.Description);
            sentences.Add($"{ReferenceNumberer.VideoTag(n)} is {description}");
        }

        return string.Join(" ", sentences);
    }

    private static string BuildSummary(SceneProject project)
    {
        var types = project.TaskTypes.Split().Select(t => t.ToPromptToken()).ToList();
        var prefix = types.Count > 0 ? $"[{string.Join(" + ", types)}] " : "";
        return $"{prefix}{project.Summary.Trim()}";
    }

    private static string BuildRetentionAnalysis(SceneProject project, ReferenceNumbering numbering)
    {
        var lines = new List<string>();

        foreach (var subject in project.Subjects)
        {
            if (subject.Retention is null)
                continue;

            var n = numbering.SubjectNumber(subject.Id);
            var shots = numbering.SubjectAppearances.TryGetValue(subject.Id, out var s) ? s : [];
            var shotList = string.Join(", ", shots.Select(ReferenceNumberer.ShotLabel));
            var note = string.IsNullOrWhiteSpace(subject.RetentionNote) ? "" : $" - {subject.RetentionNote.Trim()}";

            lines.Add($"{ReferenceNumberer.SubjectTag(n)} (appears in {shotList}): {subject.Retention.Value.ToPromptToken()}{note}.");

            if (subject.Audio is { Retention: { } audioRetention } audio)
            {
                var audioNote = string.IsNullOrWhiteSpace(audio.RetentionNote) ? "" : $" - {audio.RetentionNote.Trim()}";
                lines.Add($"{ReferenceNumberer.AudioTag(numbering.AudioNumber(audio.Id))}: {audioRetention.ToPromptToken()}{audioNote}.");
            }
        }

        foreach (var video in project.SourceVideos)
        {
            if (video.Retention is null)
                continue;

            var note = string.IsNullOrWhiteSpace(video.RetentionNote) ? "" : $" - {video.RetentionNote.Trim()}";
            lines.Add($"{ReferenceNumberer.VideoTag(numbering.VideoNumber(video.Id))}: {video.Retention.Value.ToPromptToken()}{note}.");
        }

        return string.Join(" ", lines);
    }

    private static string BuildDetailedDescription(SceneProject project, ReferenceNumbering numbering)
    {
        var sb = new StringBuilder();

        if (project.VisualStyle is { } style)
            sb.Append($"The target video is a {style.ToPromptToken()} scene. ");

        for (var i = 0; i < project.Shots.Count; i++)
        {
            var shot = project.Shots[i];
            var n = numbering.ShotNumber(shot.Id);

            sb.Append(ReferenceNumberer.ShotLabel(n)).Append(' ');
            if (!string.IsNullOrWhiteSpace(shot.Timestamp))
                sb.Append($"At {shot.Timestamp.Trim()}, ");

            sb.Append(shot.Text.Trim());
            sb.Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    private static string NormalizeSentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? trimmed : TrimTrailingPeriod(trimmed) + ".";
    }

    private static string TrimTrailingPeriod(string text) => text.EndsWith('.') ? text[..^1] : text;

    private static string JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
    };
}
