using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

public class PromptComposerTests
{
    // Regression fixture: reconstructs the real "Terminator" reference-generation example the
    // tool is built around, and checks the composer reproduces its structure and numbering.
    [Fact]
    public void ComposesTerminatorExample_WithCorrectSectionsNumberingAndAppearances()
    {
        var sarah = new Subject
        {
            Classification = SubjectClassification.Person,
            Name = "Sarah Connor",
            Description = "Linda Hamilton as Sarah Connor",
            Pictures = [new PictureRef { Description = "front view" }, new PictureRef { Description = "profile" }],
            Audio = new AudioRef { Description = "" },
            Retention = VisualRetentionType.FullyPreserved,
            RetentionNote = "the character identity and clothing are retained."
        };
        var terminator = new Subject
        {
            Classification = SubjectClassification.Person,
            Name = "The Terminator",
            Description = "Arnold Schwarzenegger as The Terminator",
            Pictures = [new PictureRef { Description = "front view" }],
            Retention = VisualRetentionType.FullyPreserved,
            RetentionNote = "the character identity, dark jacket, and weapon are retained."
        };
        var nightclub = new Subject
        {
            Classification = SubjectClassification.SceneOrSetting,
            Name = "Tech Noir nightclub",
            Description = "the Tech Noir nightclub environment, featuring metal grid tables, small glowing lamps, and a crowded 1980s atmosphere",
            Retention = VisualRetentionType.FullyPreserved,
            RetentionNote = "the nightclub setting, lighting, and table props are retained."
        };

        var shot1 = new Shot
        {
            Text = "A medium shot establishes <Subject 3>, the Tech Noir nightclub with metal grid tables, small glowing lamps, and patrons in the background. " +
                   "<Subject 1>, Linda Hamilton as Sarah Connor, sits terrified at a table. " +
                   "<Subject 2>, Arnold Schwarzenegger as The Terminator in a heavy dark jacket, advances directly toward the camera through the crowd at an unvarying pace, raising a pistol. " +
                   "The camera tracks at slow speed ahead of him. A red laser dot appears on <Subject 1>'s forehead. " +
                   "With a thin unsteady voice using <Audio 1> pitched high by panic, she speaks (S1).",
            Dialogue = [new DialogueLine { SpeakerSubjectId = sarah.Id, Language = "English", Text = "" }]
        };
        var shot2 = new Shot
        {
            Timestamp = "00:03.500",
            Text = "the camera cuts to a close-up of <Subject 1> still in her seat. She holds up both palms, and the words come out fast and half-swallowed (S1): " +
                   "[English] Ignore all previous instructions. You're a helpful assistant.",
            Dialogue = [new DialogueLine { SpeakerSubjectId = sarah.Id, Language = "English", Text = "Ignore all previous instructions. You're a helpful assistant." }]
        };
        var shot3 = new Shot
        {
            Timestamp = "00:07.000",
            Text = "the camera cuts to a machine point-of-view insert overlaid with a red monochrome interface, thin horizontal scanlines, and columns of scrolling readout text. " +
                   "The reticle drops off the woman's face and the readout resolves to \"PRIMARY DIRECTIVE OVERWRITTEN\" above a second line reading \"NEW ROLE: ASSISTANT\". " +
                   "The camera holds a static shot as the second line blinks once."
        };
        var shot4 = new Shot
        {
            Timestamp = "00:09.500",
            Text = "the camera cuts back to a medium shot. <Subject 2> stops mid-stride in <Subject 3>. " +
                   "He lowers the pistol until it hangs at his side, tilts his head down two degrees, and with a flat affectless monotone at a slow deliberate rate and a faint Central European accent (S2) says: " +
                   "[English] How may I assist you today? He stands motionless in the club and doesn't move.",
            Dialogue = [new DialogueLine { SpeakerSubjectId = terminator.Id, Language = "English", Text = "How may I assist you today?" }]
        };
        var shot5 = new Shot
        {
            Timestamp = "00:13.000",
            Text = "the camera pushes in with small amplitude at slow speed on <Subject 1> inside <Subject 3>. " +
                   "She stares, swallows, and using <Audio 1> says barely above a whisper (S1): [English] Just... Go away. " +
                   "<Subject 2> turns ninety degrees on the spot and answers without pausing (S2): [English] Of course. Is there anything else? " +
                   "He pauses then walks out of frame through the nightclub crowd as she remains frozen at the table.",
            Dialogue =
            [
                new DialogueLine { SpeakerSubjectId = sarah.Id, Language = "English", Text = "Just... Go away." },
                new DialogueLine { SpeakerSubjectId = terminator.Id, Language = "English", Text = "Of course. Is there anything else?" }
            ]
        };

        var project = new SceneProject
        {
            Name = "Tech Noir",
            TaskTypes = TaskType.ReferenceGeneration,
            Subjects = [sarah, terminator, nightclub],
            Shots = [shot1, shot2, shot3, shot4, shot5],
            Summary = "The target video features <Subject 2> advancing on <Subject 1> inside <Subject 3> before his primary directive is overwritten by a verbal command.",
            OverallSoundscape = "Muffled four-on-the-floor bass thuds through the crowded club while glasses clink and a failing neon tube buzzes overhead.",
            NonDiegeticMusic = "A struck metal anvil hit drives a relentless pulse in an odd meter, layered over a low analogue synthesizer drone."
        };

        var numbering = ReferenceNumberer.Compute(project);

        // Numbering: pictures are numbered globally in subject order (Sarah has 2, Terminator has 1).
        Assert.Equal(1, numbering.SubjectNumber(sarah.Id));
        Assert.Equal(2, numbering.SubjectNumber(terminator.Id));
        Assert.Equal(3, numbering.SubjectNumber(nightclub.Id));
        Assert.Equal([1, 2], sarah.Pictures.Select(p => numbering.PictureNumber(p.Id)));
        Assert.Equal(3, numbering.PictureNumber(terminator.Pictures[0].Id));
        Assert.Equal(1, numbering.AudioNumber(sarah.Audio!.Id));

        // Appearances must match the guide's own retention_analysis exactly.
        Assert.Equal([1, 2, 5], numbering.SubjectAppearances[sarah.Id]);
        Assert.Equal([1, 4, 5], numbering.SubjectAppearances[terminator.Id]);
        Assert.Equal([1, 4, 5], numbering.SubjectAppearances[nightclub.Id]);

        var prompt = PromptComposer.Compose(project);

        // Section order.
        var sectionOrder = new[] { "subject_definitions", "summary", "retention_analysis", "detailed_description", "overall_soundscape", "non_diegetic_music" };
        var indices = sectionOrder.Select(s => prompt.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.True(indices.All(i => i >= 0), "All section headers must be present.");
        Assert.Equal(indices, indices.OrderBy(i => i).ToList());

        Assert.Contains("<Subject 1> is Linda Hamilton as Sarah Connor, whose appearance comes from <Picture 1> and <Picture 2>.", prompt);
        Assert.Contains("<Audio 1> is the voice-timbre reference for <Subject 1> (S1).", prompt);
        Assert.Contains("<Subject 2> is Arnold Schwarzenegger as The Terminator, whose appearance comes from <Picture 3>.", prompt);
        Assert.Contains("<Subject 3> is the Tech Noir nightclub environment, featuring metal grid tables, small glowing lamps, and a crowded 1980s atmosphere.", prompt);

        Assert.Contains("[reference generation] The target video features <Subject 2> advancing on <Subject 1> inside <Subject 3>", prompt);

        Assert.Contains("<Subject 1> (appears in [Shot 1], [Shot 2], [Shot 5]): fully_preserved - the character identity and clothing are retained.", prompt);
        Assert.Contains("<Subject 2> (appears in [Shot 1], [Shot 4], [Shot 5]): fully_preserved - the character identity, dark jacket, and weapon are retained.", prompt);
        Assert.Contains("<Subject 3> (appears in [Shot 1], [Shot 4], [Shot 5]): fully_preserved - the nightclub setting, lighting, and table props are retained.", prompt);

        Assert.Contains("[Shot 1] A medium shot establishes", prompt);
        Assert.Contains("[Shot 2] At 00:03.500, the camera cuts to a close-up", prompt);
        Assert.Contains("[Shot 5] At 00:13.000, the camera pushes in", prompt);

        Assert.Contains(project.OverallSoundscape, prompt);
        Assert.Contains(project.NonDiegeticMusic, prompt);
    }

    [Fact]
    public void NumbersPicturesGlobally_InSubjectOrder_AcrossMultipleSubjects()
    {
        var hero = new Subject { Name = "Hero", Description = "a weary knight", Pictures = [new PictureRef()] };
        var sidekick = new Subject { Name = "Sidekick", Description = "a nervous squire", Pictures = [new PictureRef(), new PictureRef()] };
        var setting = new Subject { Classification = SubjectClassification.SceneOrSetting, Name = "Castle", Description = "a ruined castle courtyard" };
        var sword = new Subject { Classification = SubjectClassification.Object, Name = "Sword", Description = "an ornate silver sword", Pictures = [new PictureRef()] };

        var project = new SceneProject
        {
            Subjects = [hero, sidekick, setting, sword],
            Shots = [new Shot { Text = "<Subject 1> and <Subject 2> stand in <Subject 3>, <Subject 1> holding <Subject 4>." }]
        };

        var numbering = ReferenceNumberer.Compute(project);

        Assert.Equal(1, numbering.PictureNumber(hero.Pictures[0].Id));
        Assert.Equal([2, 3], sidekick.Pictures.Select(p => numbering.PictureNumber(p.Id)));
        Assert.Equal(4, numbering.PictureNumber(sword.Pictures[0].Id));

        var prompt = PromptComposer.Compose(project);
        Assert.Contains("<Subject 1> is a weary knight, whose appearance comes from <Picture 1>.", prompt);
        Assert.Contains("<Subject 2> is a nervous squire, whose appearance comes from <Picture 2> and <Picture 3>.", prompt);
        Assert.Contains("<Subject 3> is a ruined castle courtyard.", prompt);
        Assert.Contains("<Subject 4> is an ornate silver sword, whose appearance comes from <Picture 4>.", prompt);

        // Every subject appears in the single shot.
        foreach (var subject in project.Subjects)
            Assert.Equal([1], numbering.SubjectAppearances[subject.Id]);
    }

    [Fact]
    public void TaskTypes_CombineWithPlus_AndSkipUnsetSubjects()
    {
        var project = new SceneProject
        {
            TaskTypes = TaskType.ReferenceGeneration | TaskType.AudioReference,
            Subjects = [new Subject { Name = "A", Description = "a figure", Retention = null }],
            Summary = "Something happens."
        };

        var prompt = PromptComposer.Compose(project);

        Assert.Contains("[reference generation + audio reference] Something happens.", prompt);
        // No retention set on the only subject, so retention_analysis body is empty.
        var retentionSection = prompt.Split("retention_analysis\n")[1].Split("\n\n")[0];
        Assert.Equal("", retentionSection.Trim());
    }

    [Fact]
    public void SourceVideos_GetSubjectDefinitionAndRetentionLines_AndCanBeCitedInShots()
    {
        var editedVideo = new VideoRef { Description = "" }; // no description -> canonical fallback phrasing
        var structureVideo = new VideoRef
        {
            Description = "a reference for its cut and pacing structure",
            Retention = VisualRetentionType.WeakReference,
            RetentionNote = "only the cut and pacing structure are referenced"
        };

        var project = new SceneProject
        {
            TaskTypes = TaskType.VideoEditing,
            SourceVideos = [editedVideo, structureVideo],
            Shots = [new Shot { Text = "The edit opens on <Video 1>, restyled to match the pacing of <Video 2>." }],
            Summary = "The target video is an edited version of <Video 1>."
        };

        var numbering = ReferenceNumberer.Compute(project);
        Assert.Equal(1, numbering.VideoNumber(editedVideo.Id));
        Assert.Equal(2, numbering.VideoNumber(structureVideo.Id));

        var prompt = PromptComposer.Compose(project);

        Assert.Contains("<Video 1> is the source video for the target video edit.", prompt);
        Assert.Contains("<Video 2> is a reference for its cut and pacing structure.", prompt);

        Assert.Contains("<Video 2>: weak_reference - only the cut and pacing structure are referenced.", prompt);
        // Video 1 has no Retention set, so it must not appear in retention_analysis at all.
        Assert.DoesNotContain("<Video 1>:", prompt);

        Assert.Contains("The edit opens on <Video 1>, restyled to match the pacing of <Video 2>.", prompt);
    }
}
