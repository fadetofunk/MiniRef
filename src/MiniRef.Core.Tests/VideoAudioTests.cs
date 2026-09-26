using System.Text.Json.Nodes;
using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

/// <summary>A source video's own soundtrack (VideoRef.AudioUse): numbering, prompt sentence,
/// exporter wiring into the reference node's paired ref_video_audios slot, and import round-trip.
/// This is the audio half of continuing one clip from another.</summary>
public class VideoAudioTests
{
    private static string LoadTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video_minimax_h3_r2v.template.json"));

    private static SceneProject ProjectWithVoiceAndContinuationVideo(VideoAudioUse use) => new()
    {
        Subjects =
        [
            new Subject
            {
                Name = "Hero",
                Description = "a hooded courier",
                Pictures = [new PictureRef()],
                Audios = [new AudioRef { Description = "a low, steady voice" }]
            }
        ],
        SourceVideos = [new VideoRef { Description = "the previous clip", AudioUse = use }],
        TaskTypes = TaskType.VideoContinuation,
        Summary = "Continues the previous clip.",
        Shots = [new Shot { Text = "<Subject 1> keeps running, continuing from the end of <Video 1>." }]
    };

    [Fact]
    public void VideoSoundtracks_AreNumberedBeforeEverySubjectAudio()
    {
        var project = ProjectWithVoiceAndContinuationVideo(VideoAudioUse.Reference);
        project.SourceVideos.Add(new VideoRef { AudioUse = VideoAudioUse.None });
        project.SourceVideos.Add(new VideoRef { AudioUse = VideoAudioUse.Reuse });

        var numbering = ReferenceNumberer.Compute(project);

        // MiniMaxH3ReferenceToVideo emits a video's soundtrack ahead of the standalone audios, so the
        // soundtracks take <Audio 1..v> and the subject's voice shifts up behind them.
        Assert.Equal(1, numbering.AudioNumber(project.SourceVideos[0].Id));
        Assert.Equal(0, numbering.AudioNumber(project.SourceVideos[1].Id));   // not in use -> no number
        Assert.Equal(2, numbering.AudioNumber(project.SourceVideos[2].Id));
        Assert.Equal(3, numbering.AudioNumber(project.Subjects[0].Audios[0].Id));
    }

    [Fact]
    public void Compose_DescribesTheVideoSoundtrack_ReuseVersusReference()
    {
        var reuse = PromptComposer.Compose(ProjectWithVoiceAndContinuationVideo(VideoAudioUse.Reuse));
        Assert.Contains("<Audio 1> is the synchronized audio track of <Video 1> and is reused in the target video.", reuse);
        Assert.Contains("<Audio 2> is the voice-timbre reference for <Subject 1>", reuse);

        var reference = PromptComposer.Compose(ProjectWithVoiceAndContinuationVideo(VideoAudioUse.Reference));
        Assert.Contains("<Audio 1> is the synchronized audio track of <Video 1>; the target video's audio continues its audible characteristics.", reference);

        var none = PromptComposer.Compose(ProjectWithVoiceAndContinuationVideo(VideoAudioUse.None));
        Assert.DoesNotContain("synchronized audio track", none);
    }

    [Fact]
    public void Export_WiresTheVideoLoadersAudioOutput_IntoTheRefVideoAudioSlot()
    {
        var resultJson = ComfyWorkflowExporter.Export(LoadTemplate(), ProjectWithVoiceAndContinuationVideo(VideoAudioUse.Reference));
        var result = JsonNode.Parse(resultJson)!.AsObject();
        var nodes = result["nodes"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var links = result["links"]!.AsArray();

        var refNode = nodes.Single(n => n["type"]!.GetValue<string>() == "MiniMaxH3ReferenceToVideo");
        var refNodeId = (int)refNode["id"]!.GetValue<double>();
        var inputs = refNode["inputs"]!.AsArray();

        var audioSlotIndex = inputs.ToList().FindIndex(i => i!["name"]!.GetValue<string>() == "ref_video_audios.ref_video_audio_0");
        Assert.True(audioSlotIndex >= 0);
        var linkId = inputs[audioSlotIndex]!["link"];
        Assert.NotNull(linkId);

        var videoNode = nodes.Single(n => n["type"]!.GetValue<string>() == "VHS_LoadVideoPath");
        var videoNodeId = (int)videoNode["id"]!.GetValue<double>();

        // The link runs from the loader's third output ("audio", index 2) into that slot.
        var link = links.Select(l => l!.AsArray()).Single(l => l[0]!.GetValue<double>() == linkId!.GetValue<double>());
        Assert.Equal(videoNodeId, (int)link[1]!.GetValue<double>());
        Assert.Equal(2, (int)link[2]!.GetValue<double>());
        Assert.Equal(refNodeId, (int)link[3]!.GetValue<double>());
        Assert.Equal(audioSlotIndex, (int)link[4]!.GetValue<double>());
        Assert.Equal("AUDIO", link[5]!.GetValue<string>());

        // ...and the loader's own output records that link.
        var audioOutput = videoNode["outputs"]!.AsArray().Single(o => o!["name"]!.GetValue<string>() == "audio");
        Assert.Equal(linkId!.GetValue<double>(), audioOutput!["links"]!.AsArray().Single()!.GetValue<double>());
    }

    [Fact]
    public void Export_LeavesTheRefVideoAudioSlotUnwired_WhenTheVideosAudioIsNotUsed()
    {
        var resultJson = ComfyWorkflowExporter.Export(LoadTemplate(), ProjectWithVoiceAndContinuationVideo(VideoAudioUse.None));
        var refNode = JsonNode.Parse(resultJson)!["nodes"]!.AsArray().Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "MiniMaxH3ReferenceToVideo");

        var slot = refNode["inputs"]!.AsArray().SingleOrDefault(i => i!["name"]!.GetValue<string>() == "ref_video_audios.ref_video_audio_0");
        Assert.True(slot is null || slot["link"] is null);
    }

    [Theory]
    [InlineData(VideoAudioUse.Reuse)]
    [InlineData(VideoAudioUse.Reference)]
    public void Import_RoundTripsTheVideoSoundtrackSetting_ThroughAnExport(VideoAudioUse use)
    {
        var workflow = ComfyWorkflowExporter.Export(LoadTemplate(), ProjectWithVoiceAndContinuationVideo(use));

        var imported = ComfyWorkflowImporter.Import(workflow);

        Assert.Equal(use, Assert.Single(imported.SourceVideos).AudioUse);
        // The soundtrack sentence must not be mistaken for a subject voice reference.
        Assert.Single(imported.Subjects[0].Audios);
    }

    [Fact]
    public void ImportPromptText_ReadsAVideoSoundtrackSentence()
    {
        const string prompt = """
            subject_definitions: <Subject 1> is a courier in <Picture 1>. <Video 1> is the source video for the target video edit. <Audio 1> is the synchronized audio track of <Video 1> and is reused in the target video.

            detailed_description: [Shot 1] <Subject 1> keeps running.
            """;

        var imported = ComfyWorkflowImporter.ImportPromptText(prompt);

        Assert.Equal(VideoAudioUse.Reuse, Assert.Single(imported.SourceVideos).AudioUse);
        Assert.Empty(imported.Subjects[0].Audios);
    }
}
