using System.Text.Json;
using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

/// <summary>Continuation segments: a chain of clips sharing one cast, each prompt composed over that
/// shared cast with the previous clip as its &lt;Video 1&gt;.</summary>
public class SceneSegmentTests
{
    private static SceneProject TwoSegmentProject()
    {
        var hero = new Subject
        {
            Name = "Hero",
            Description = "a hooded courier",
            Pictures = [new PictureRef { Description = "front" }],
            Audios = [new AudioRef { Description = "a low, steady voice" }]
        };
        var rival = new Subject
        {
            Name = "Rival",
            Description = "a masked pursuer",
            Pictures = [new PictureRef { Description = "front" }]
        };

        return new SceneProject
        {
            Name = "Chase",
            Subjects = [hero, rival],
            TaskTypes = TaskType.ReferenceGeneration,
            Summary = "The chase begins.",
            Shots = [new Shot { Text = "<Subject 1> sprints past <Subject 2> across a rooftop." }],
            DurationSeconds = 12,
            Continuations =
            [
                new SceneSegment
                {
                    Summary = "The chase continues.",
                    Shots = [new Shot { Text = "<Subject 1> leaps the gap while <Subject 2> follows, <Picture 2> in frame." }]
                }
            ]
        };
    }

    [Fact]
    public void ForSegment_ZeroIsTheProjectItself_AndAContinuationSharesTheCast()
    {
        var project = TwoSegmentProject();

        Assert.Equal(2, project.SegmentCount);
        Assert.Same(project, project.ForSegment(0));

        var view = project.ForSegment(1);
        Assert.Same(project.Subjects, view.Subjects);                       // reference pictures are set once
        Assert.Same(project.Continuations[0].Shots, view.Shots);
        Assert.Equal("The chase continues.", view.Summary);
        Assert.Same(project.Continuations[0].PreviousVideo, Assert.Single(view.SourceVideos));
    }

    [Fact]
    public void ForSegment_AddsVideoContinuationAndTheMatchingAudioTaskType()
    {
        var project = TwoSegmentProject();

        var referenceAudio = project.ForSegment(1).TaskTypes;
        Assert.True(referenceAudio.HasFlag(TaskType.ReferenceGeneration));
        Assert.True(referenceAudio.HasFlag(TaskType.VideoContinuation));
        Assert.True(referenceAudio.HasFlag(TaskType.AudioReference));

        project.Continuations[0].PreviousVideo.AudioUse = VideoAudioUse.Reuse;
        Assert.True(project.ForSegment(1).TaskTypes.HasFlag(TaskType.AudioReuse));

        project.Continuations[0].PreviousVideo.AudioUse = VideoAudioUse.None;
        var noAudio = project.ForSegment(1).TaskTypes;
        Assert.True(noAudio.HasFlag(TaskType.VideoContinuation));
        Assert.False(noAudio.HasFlag(TaskType.AudioReuse) || noAudio.HasFlag(TaskType.AudioReference));
    }

    [Fact]
    public void ForSegment_Throws_ForASegmentThatDoesNotExist()
    {
        var project = TwoSegmentProject();
        Assert.Throws<ArgumentOutOfRangeException>(() => project.ForSegment(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.ForSegment(-1));
    }

    [Fact]
    public void ComposeSegment_GivesEachSegmentItsOwnPrompt_OverTheSamePictureNumbers()
    {
        var project = TwoSegmentProject();

        var first = PromptComposer.ComposeSegment(project, 0);
        var second = PromptComposer.ComposeSegment(project, 1);

        Assert.Contains("[reference generation]", first);
        Assert.DoesNotContain("<Video 1>", first);

        Assert.Contains("[reference generation + video continuation + audio reference]", second);
        Assert.Contains("<Video 1> is the source video, which the target video continues from the end of.", second);
        // Hero's voice is <Audio 1>; the previous clip's soundtrack follows it as <Audio 2>.
        Assert.Contains("<Audio 2> is the synchronized audio track of <Video 1>", second);
        Assert.Contains("The chase continues.", second);
        Assert.DoesNotContain("The chase begins.", second);

        // Same cast, same numbers in both prompts.
        Assert.Contains("<Subject 2> is a masked pursuer, whose appearance comes from <Picture 2>.", first);
        Assert.Contains("<Subject 2> is a masked pursuer, whose appearance comes from <Picture 2>.", second);
    }

    [Fact]
    public void RemovingASubject_RewritesTheSharedTagsInEverySegment_ButLeavesEachSegmentsVideoTagAlone()
    {
        var project = TwoSegmentProject();
        var segment = project.Continuations[0];
        segment.Shots.Add(new Shot { Text = "Continuing from <Video 1>, with <Audio 2> carrying on and <Picture 2> still framed." });

        var before = ReferenceNumberer.ComputeAllSegments(project);
        project.Subjects.RemoveAt(0);   // Hero (subject 1, picture 1, voice audio 1) goes away
        var after = ReferenceNumberer.ComputeAllSegments(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        // Rival slides from <Subject 2>/<Picture 2> to <Subject 1>/<Picture 1> in the continuation...
        // (Hero's own <Subject 1> tag is left dangling for the author to notice, as elsewhere.)
        Assert.Equal("<Subject 1> leaps the gap while <Subject 1> follows, <Picture 1> in frame.", segment.Shots[0].Text);
        Assert.Contains("<Picture 1> still framed", segment.Shots[1].Text);

        // ...the previous clip's soundtrack slides from <Audio 2> to <Audio 1> now that the voice audio
        // before it is gone, and <Video 1> -- unique to this segment -- is untouched.
        Assert.Contains("with <Audio 1> carrying on", segment.Shots[1].Text);
        Assert.Contains("Continuing from <Video 1>", segment.Shots[1].Text);

        // Segment 1's own text follows the same renumbering.
        Assert.Equal("<Subject 1> sprints past <Subject 1> across a rooftop.", project.Shots[0].Text);
    }

    [Fact]
    public void ProjectsSavedBeforeSegmentsExisted_LoadWithNoContinuations()
    {
        const string legacy = """{ "Name": "Old", "Summary": "A single clip.", "DurationSeconds": 8 }""";

        var project = JsonSerializer.Deserialize<SceneProject>(legacy, ProjectStore.Options)!;

        Assert.Equal(1, project.SegmentCount);
        Assert.Empty(project.Continuations);
        Assert.Equal("A single clip.", project.Summary);
    }

    [Fact]
    public void Continuations_SurviveASaveAndReload_IncludingThePreviousVideoSettings()
    {
        var project = TwoSegmentProject();
        project.Continuations[0].DurationSeconds = 10;
        project.Continuations[0].PreviousVideo.AudioUse = VideoAudioUse.Reuse;
        project.Continuations[0].PreviousVideo.Retention = VisualRetentionType.PartiallyPreserved;

        var json = JsonSerializer.Serialize(project, ProjectStore.Options);
        var reloaded = JsonSerializer.Deserialize<SceneProject>(json, ProjectStore.Options)!;

        var segment = Assert.Single(reloaded.Continuations);
        Assert.Equal(10, segment.DurationSeconds);
        Assert.Equal("The chase continues.", segment.Summary);
        Assert.True(segment.PreviousVideo.FromPreviousSegment);
        Assert.Equal(VideoAudioUse.Reuse, segment.PreviousVideo.AudioUse);
        Assert.Equal(VisualRetentionType.PartiallyPreserved, segment.PreviousVideo.Retention);
        Assert.Single(segment.VideoList);
        Assert.DoesNotContain("SegmentCount", json);
        Assert.DoesNotContain("VideoList", json);
    }

    [Fact]
    public void ReferenceLimits_FlagASegmentThatTheSharedCastPlusThePreviousClipPushesOverTheTotal()
    {
        var project = new SceneProject
        {
            Subjects =
            [
                new Subject
                {
                    Pictures = [.. Enumerable.Range(0, 9).Select(_ => new PictureRef())],
                    Audios = [new AudioRef(), new AudioRef()]
                }
            ],
            Continuations = [new SceneSegment()]
        };

        // Segment 1: 9 pictures + 2 audios = 11 files, fine.
        Assert.Empty(ReferenceLimits.Check(project.ForSegment(0)));

        // Segment 2 adds the previous clip and its soundtrack: 9 + 2 + 1 + 1 = 13 > 12.
        var problems = ReferenceLimits.Check(project.ForSegment(1));
        Assert.Contains(problems, p => p.Contains("13 reference files in total"));
        Assert.DoesNotContain(problems, p => p.Contains("pictures"));
    }
}
