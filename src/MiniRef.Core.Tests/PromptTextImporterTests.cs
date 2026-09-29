using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

/// <summary>Covers ComfyWorkflowImporter.ImportPromptText -- building a project straight from a
/// pasted six-section MiniMax H3 prompt -- and the structured-dialogue extraction that both import
/// paths now share, which is what lets a Character Pod's voice reference wire itself into an
/// imported prompt's speaking turns.</summary>
public class PromptTextImporterTests
{
    // The exact prompt shape the feature is built around: guide-style "name:" one-line headers, a
    // subject sentence that names its <Picture 1> inline, and two spoken <d> lines in one shot.
    private const string GeowomanPrompt = """
        subject_definitions:
        <Subject 1> is the superheroine Geowoman in <Picture 1>, a 18-year-old woman with a confident, composed bearing; her full appearance costume design, hair, facial features, and build is defined by the reference image and must be preserved exactly as shown.

        summary:
        [reference generation] The target video is a 12-second, 16:9 talking-head recording of <Subject 1>, Geowoman, who delivers a single two-sentence line to capture clean voice samples.

        retention_analysis:
        <Subject 1> (appears in [Shot 1]): fully_preserved - Geowoman's costume, hair, facial features, build, and overall superheroine presentation as established in <Picture 1> are retained unchanged throughout the entire 12-second shot.

        detailed_description:
        The target video uses a clean, realistic studio-recording style with even, soft frontal key lighting and a neutral dark-grey backdrop, optimized for clear voice capture.

        [Shot 1] A static medium close-up frames <Subject 1>, Geowoman, from the chest up, centered in the 16:9 frame against a plain dark-grey background. Her superheroine costume, hair, and facial features match <Picture 1> precisely. After roughly one second of silence, <Subject 1> (S1) begins speaking. She says, <d>[English] The pleasure of Busby's company is what I most enjoy.</d> A brief natural pause follows. She continues, <d>[English] He put a tack on Miss Yancy's chair, when she called him a horrible boy.</d> As she finishes, her expression softens into a small satisfied smile.

        overall_soundscape:
        A quiet, low room tone from a small studio continues beneath the dialogue for the full twelve seconds.

        non_diegetic_music:
        N/A
        """;

    [Fact]
    public void ImportPromptText_BuildsSubjectPictureSlotAndSceneFieldsFromTheProseSections()
    {
        var imported = ComfyWorkflowImporter.ImportPromptText(GeowomanPrompt);

        var subject = Assert.Single(imported.Subjects);
        Assert.Contains("superheroine Geowoman", subject.Description);
        Assert.Equal(VisualRetentionType.FullyPreserved, subject.Retention);
        Assert.Contains("retained unchanged", subject.RetentionNote);

        // Every <Picture N> the prose names becomes a real, file-less slot ready for a pod to fill.
        var picture = Assert.Single(subject.Pictures);
        Assert.Null(picture.FilePath);
        Assert.Empty(subject.Audios);

        Assert.True(imported.TaskTypes.HasFlag(TaskType.ReferenceGeneration));
        Assert.StartsWith("The target video is a 12-second", imported.Summary);
        Assert.StartsWith("A quiet, low room tone", imported.OverallSoundscape);
        Assert.Equal("N/A", imported.NonDiegeticMusic);

        var shot = Assert.Single(imported.Shots);
        Assert.Equal("", shot.Timestamp);
        Assert.Contains("<d>[English] The pleasure of Busby's company", shot.Text);
    }

    [Fact]
    public void ImportPromptText_ExtractsSpokenLinesAsStructuredDialogueAttributedToTheNearestSubject()
    {
        var imported = ComfyWorkflowImporter.ImportPromptText(GeowomanPrompt);

        var shot = Assert.Single(imported.Shots);
        Assert.Equal(2, shot.Dialogue.Count);

        Assert.All(shot.Dialogue, line => Assert.Equal(imported.Subjects[0].Id, line.SpeakerSubjectId));
        Assert.All(shot.Dialogue, line => Assert.Equal("English", line.Language));
        Assert.Equal("The pleasure of Busby's company is what I most enjoy.", shot.Dialogue[0].Text);
        Assert.Equal("He put a tack on Miss Yancy's chair, when she called him a horrible boy.", shot.Dialogue[1].Text);

        // The <d> runs themselves stay in the narrative where the model expects them.
        Assert.Contains("She says, <d>[English] The pleasure", shot.Text);
    }

    [Fact]
    public void ImportedSpeakingSubject_GetsAVoiceTimbreSentence_AsSoonAsAnAudioReferenceIsAdded()
    {
        var imported = ComfyWorkflowImporter.ImportPromptText(GeowomanPrompt);

        // Straight after import: the subject speaks but has no voice, so nothing is claimed.
        Assert.DoesNotContain("voice-timbre reference", PromptComposer.Compose(imported));

        // Dropping in a pod's voice reference is all it takes -- the sentence and the (S1) ID
        // that the imported dialogue established compose on their own.
        imported.Subjects[0].Audios.Add(new AudioRef { Description = "a warm, measured contralto" });

        Assert.Contains(
            "<Audio 1> is the voice-timbre reference for <Subject 1> (S1), a warm, measured contralto.",
            PromptComposer.Compose(imported));
    }

    [Fact]
    public void ImportPromptText_SequencesSpeakerIdsByOrderOfFirstSpokenLine_NotSubjectNumber()
    {
        const string prompt = """
            subject_definitions: <Subject 1> is the hero in <Picture 1>. <Subject 2> is the villain in <Picture 2>.

            detailed_description: [Shot 1] <Subject 2> (S1) says, <d>[English] It's over.</d> Then <Subject 1> (S2) says, <d>[English] Not quite.</d>
            """;

        var imported = ComfyWorkflowImporter.ImportPromptText(prompt);
        imported.Subjects[0].Audios.Add(new AudioRef());   // hero  -- <Subject 1>, speaks second
        imported.Subjects[1].Audios.Add(new AudioRef());   // villain -- <Subject 2>, speaks first

        var composed = PromptComposer.Compose(imported);

        Assert.Contains("voice-timbre reference for <Subject 2> (S1)", composed);
        Assert.Contains("voice-timbre reference for <Subject 1> (S2)", composed);
    }

    [Fact]
    public void ImportPromptText_CreatesEmptyPictureAndAudioSlotsFromCanonicalSubjectDefinitions()
    {
        const string prompt = """
            subject_definitions: <Subject 1> is a singer, whose appearance comes from <Picture 1> and <Picture 2>. <Audio 1> is the voice-timbre reference for <Subject 1> (S1).

            detailed_description: [Shot 1] <Subject 1> performs on a small stage.
            """;

        var imported = ComfyWorkflowImporter.ImportPromptText(prompt);

        var subject = Assert.Single(imported.Subjects);
        Assert.Equal(2, subject.Pictures.Count);
        Assert.All(subject.Pictures, p => Assert.Null(p.FilePath));
        var audio = Assert.Single(subject.Audios);
        Assert.Null(audio.FilePath);
    }

    [Fact]
    public void ImportPromptText_Throws_OnBlankInput()
    {
        Assert.Throws<InvalidDataException>(() => ComfyWorkflowImporter.ImportPromptText("   "));
    }

    [Fact]
    public void ImportPromptText_HandlesCrlfLineEndings_AsPastedFromAWindowsTextBox()
    {
        // A WPF TextBox hands back "\r\n". The section-header and paragraph regexes key on "\n\n";
        // without normalization every header past the first is missed and the whole prompt lands
        // in subject_definitions -- i.e. the entire text gets dumped into the appearance boxes.
        var prompt =
            "subject_definitions:\r\n" +
            "<Subject 1> is the hero in <Picture 1>.\r\n" +
            "<Subject 2> is the villain in <Picture 2>.\r\n" +
            "\r\n" +
            "summary:\r\n" +
            "[reference generation] A rooftop standoff at dusk.\r\n" +
            "\r\n" +
            "detailed_description:\r\n" +
            "[Shot 1] <Subject 1> steps onto the roof.\r\n" +
            "\r\n" +
            "At approximately 00:03.000, <Subject 2> (S1) says, <d>[English] Far enough.</d>\r\n" +
            "\r\n" +
            "overall_soundscape:\r\n" +
            "Wind over the rooftop and distant traffic.\r\n";

        var imported = ComfyWorkflowImporter.ImportPromptText(prompt);

        Assert.Equal(2, imported.Subjects.Count);
        Assert.Equal("the hero in <Picture 1>", imported.Subjects[0].Description);
        Assert.Equal("the villain in <Picture 2>", imported.Subjects[1].Description);
        Assert.StartsWith("A rooftop standoff at dusk", imported.Summary);
        Assert.Equal("Wind over the rooftop and distant traffic.", imported.OverallSoundscape);

        Assert.Equal(2, imported.Shots.Count);
        Assert.Equal("00:03.000", imported.Shots[1].Timestamp);
        Assert.Single(imported.Shots[1].Dialogue);
        Assert.Equal(imported.Subjects[1].Id, imported.Shots[1].Dialogue[0].SpeakerSubjectId);
    }

    [Fact]
    public void ImportPromptText_SplitsASingleShotLabelIntoOneShotPerTimedBeat()
    {
        // One [Shot 1] label, five blank-line-separated paragraphs, each a distinct timed beat --
        // the "single continuous shot" shape a prompt sometimes uses. MiniRef wants one Shot each.
        const string prompt = """
            subject_definitions: <Subject 1> is the hero in <Picture 1>. <Subject 2> is the villain in <Picture 2>.

            detailed_description:
            The target video uses a dark, cinematic live-action style.

            [Shot 1] A medium-wide vertical shot opens on a dim lair as <Subject 1> hangs from a chain over a vat of acid, while <Subject 2> watches from wall-mounted screens.

            At approximately 00:04.000, on every screen <Subject 2> (S1) speaks, <d>[English] You never stood a chance.</d> <Subject 1> does not react.

            Immediately after the line ends, at approximately 00:06.000, <Subject 2> (S1) breaks into a triumphant cackle.

            At approximately 00:08.500, <Subject 1>'s lower body breaks the acid surface and she sinks under.

            From 00:09.000 to 00:12.000, the frame holds on the now-empty chain swaying above the churning surface.
            """;

        var imported = ComfyWorkflowImporter.ImportPromptText(prompt);

        Assert.Equal(5, imported.Shots.Count);

        Assert.Equal("", imported.Shots[0].Timestamp);
        Assert.StartsWith("A medium-wide vertical shot opens", imported.Shots[0].Text);

        Assert.Equal("00:04.000", imported.Shots[1].Timestamp);
        Assert.StartsWith("on every screen <Subject 2> (S1) speaks", imported.Shots[1].Text);
        Assert.Single(imported.Shots[1].Dialogue);
        Assert.Equal(imported.Subjects[1].Id, imported.Shots[1].Dialogue[0].SpeakerSubjectId);

        // A beat whose timestamp isn't at the very start of the sentence still becomes its own
        // shot; the clause is left in place rather than guessed at.
        Assert.Equal("", imported.Shots[2].Timestamp);
        Assert.Contains("00:06.000", imported.Shots[2].Text);

        Assert.Equal("00:08.500", imported.Shots[3].Timestamp);
        Assert.Equal("00:09.000", imported.Shots[4].Timestamp);   // range keeps its start time
        Assert.StartsWith("the frame holds on the now-empty chain", imported.Shots[4].Text);
    }

    [Fact]
    public void ImportPromptText_KeepsAMultiParagraphShotWholeWhenNoParagraphIsTimestamped()
    {
        const string prompt = """
            subject_definitions: <Subject 1> is the hero in <Picture 1>.

            detailed_description:
            [Shot 1] <Subject 1> steps into the alley and looks around.

            The rain picks up. She pulls her collar tight and keeps walking.
            """;

        var imported = ComfyWorkflowImporter.ImportPromptText(prompt);

        var shot = Assert.Single(imported.Shots);
        Assert.Contains("steps into the alley", shot.Text);
        Assert.Contains("The rain picks up", shot.Text);
    }

    [Fact]
    public void ImportPromptText_StillFindsEverySection_WhenBlankLinesBetweenThemAreLost()
    {
        // Regression: a paste out of a browser/chat UI can collapse the blank line between sections
        // down to a single line break (this is the shape that reproduced it -- built by literally
        // doing that collapse below, rather than retyping a pre-collapsed prompt, so the test fails
        // the same way the bug did if the fix ever regresses). Previously this made every section past
        // subject_definitions disappear into it, and every <Subject N> mention anywhere in the rest of
        // the prompt -- shot text, retention notes -- spawned a duplicate "Subject" entry, some of
        // which stole a <Picture N>'s ownership away from the real subject that should hold it.
        const string wellFormed = """
            subject_definitions:
            <Subject 1> is the superheroine as depicted in <Picture 1>, with her exact costume, hair, and every visual characteristic preserved.
            <Subject 2> is the villain as depicted in <Picture 2>, with her exact costume, hair, and every visual characteristic preserved.

            summary:
            [reference generation] The superheroine and the villain, <Subject 1> and <Subject 2>, fight atop a scaffold.

            retention_analysis:
            <Subject 1> (appears in [Shot 1]): fully_preserved - kept as shown in <Picture 1>.
            <Subject 2> (appears in [Shot 1]): fully_preserved - kept as shown in <Picture 2>.

            detailed_description:
            [Shot 1] <Subject 1>, exactly as shown in <Picture 1>, stumbles backward. <Subject 2>, exactly as shown in <Picture 2>, advances and drives a punch into <Subject 1>'s midsection.

            overall_soundscape:
            A low thud punctuates the punch.
            """;
        var collapsed = wellFormed.Replace("\n\n", "\n");

        var imported = ComfyWorkflowImporter.ImportPromptText(collapsed);

        Assert.Equal(2, imported.Subjects.Count);
        Assert.Single(imported.Subjects[0].Pictures);
        Assert.Single(imported.Subjects[1].Pictures);
        Assert.Equal(VisualRetentionType.FullyPreserved, imported.Subjects[0].Retention);
        Assert.Equal(VisualRetentionType.FullyPreserved, imported.Subjects[1].Retention);
        Assert.True(imported.TaskTypes.HasFlag(TaskType.ReferenceGeneration));
        Assert.Contains("fight atop a scaffold", imported.Summary);
        Assert.Equal("A low thud punctuates the punch.", imported.OverallSoundscape);
        Assert.Single(imported.Shots);
    }

    [Fact]
    public void Import_ExtractsStructuredDialogueFromAWorkflowShotToo_NotJustPastedText()
    {
        // The "both import paths" decision: a ComfyUI workflow's shot text gets the same
        // <d>-to-DialogueLine treatment, with the multi-lingual language tag preserved.
        const string workflow = """
            {
              "nodes": [
                {
                  "id": 1,
                  "type": "PrimitiveStringMultiline",
                  "widgets_values": ["subject_definitions: <Subject 1> is a patron in <Picture 1>. <Subject 2> is a bouncer in <Picture 2>.\n\ndetailed_description: [Shot 1] <Subject 2> (S1) says, <d>[English] You're not on the list.</d> <Subject 1> (S2) says, <d>[Spanish] Then add me.</d>"]
                }
              ],
              "links": []
            }
            """;

        var imported = ComfyWorkflowImporter.Import(workflow);

        var shot = Assert.Single(imported.Shots);
        Assert.Equal(2, shot.Dialogue.Count);
        Assert.Equal(imported.Subjects[1].Id, shot.Dialogue[0].SpeakerSubjectId);
        Assert.Equal("You're not on the list.", shot.Dialogue[0].Text);
        Assert.Equal(imported.Subjects[0].Id, shot.Dialogue[1].SpeakerSubjectId);
        Assert.Equal("Spanish", shot.Dialogue[1].Language);
        Assert.Equal("Then add me.", shot.Dialogue[1].Text);
    }
}
