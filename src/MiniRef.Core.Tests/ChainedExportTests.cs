using System.Text.Json.Nodes;
using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

/// <summary>A multi-segment project exports as ONE ComfyUI workflow that renders every clip back to
/// back: the per-clip sampling stack is duplicated per segment, each continuation is fed the previous
/// stack's decoded frames and audio in-graph, and the loaders and cast are shared.</summary>
public class ChainedExportTests
{
    private static string LoadTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video_minimax_h3_r2v.template.json"));

    private static SceneProject ThreeSegmentProject(VideoAudioUse audioUse = VideoAudioUse.Reference)
    {
        var hero = new Subject
        {
            Name = "Hero",
            Description = "a hooded courier",
            Pictures = [new PictureRef { Description = "front" }, new PictureRef { Description = "profile" }],
            Audios = [new AudioRef { Description = "a low, steady voice" }]
        };

        var project = new SceneProject
        {
            Subjects = [hero],
            Summary = "Part one summary.",
            Shots = [new Shot { Text = "<Subject 1> sprints across a rooftop." }],
            DurationSeconds = 12,
            Continuations =
            [
                new SceneSegment { Summary = "Part two summary.", DurationSeconds = 10, Shots = [new Shot { Text = "<Subject 1> leaps the gap." }] },
                new SceneSegment { Summary = "Part three summary.", DurationSeconds = 8, Shots = [new Shot { Text = "<Subject 1> lands and rolls." }] }
            ]
        };
        foreach (var segment in project.Continuations)
            segment.PreviousVideo.AudioUse = audioUse;
        return project;
    }

    // ---- small graph helpers over the exported JSON ----

    private sealed record Graph(List<JsonObject> Nodes, List<JsonArray> Links, JsonObject Root)
    {
        public List<JsonObject> OfType(string type) => Nodes.Where(n => n["type"]!.GetValue<string>() == type).ToList();
        public JsonObject Node(int id) => Nodes.Single(n => (int)n["id"]!.GetValue<double>() == id);
        public static int Id(JsonNode? n) => (int)n!["id"]!.GetValue<double>();

        /// <summary>The (origin node, origin slot) feeding the named input of a node, or null if unlinked.</summary>
        public (JsonObject Origin, int Slot)? Feeder(JsonObject node, string inputName)
        {
            var input = node["inputs"]!.AsArray().SingleOrDefault(i => i!["name"]!.GetValue<string>() == inputName);
            if (input?["link"] is null) return null;
            var linkId = input["link"]!.GetValue<double>();
            var link = Links.Single(l => l[0]!.GetValue<double>() == linkId);
            return (Node((int)link[1]!.GetValue<double>()), (int)link[2]!.GetValue<double>());
        }
    }

    private static Graph Export(SceneProject project)
    {
        var root = JsonNode.Parse(ComfyWorkflowExporter.Export(LoadTemplate(), project))!.AsObject();
        return new Graph(
            root["nodes"]!.AsArray().Select(n => n!.AsObject()).ToList(),
            root["links"]!.AsArray().Select(l => l!.AsArray()).ToList(),
            root);
    }

    /// <summary>Follows an input back through any tail-trim nodes (ImageFromBatch / TrimAudioDuration) to the
    /// node that really produced it, e.g. the previous clip's VAEDecode.</summary>
    private static JsonObject Source(Graph g, JsonObject node, string inputName)
    {
        var origin = g.Feeder(node, inputName)!.Value.Origin;
        while (origin["type"]!.GetValue<string>() is "ImageFromBatch" or "TrimAudioDuration")
            origin = g.Feeder(origin, origin["type"]!.GetValue<string>() == "ImageFromBatch" ? "image" : "audio")!.Value.Origin;
        return origin;
    }

    /// <summary>The reference nodes in segment order (the clones are appended after the original).</summary>
    private static List<JsonObject> RefNodes(Graph g) => g.OfType("MiniMaxH3ReferenceToVideo");

    [Fact]
    public void Export_DuplicatesThePerClipStackOncePerSegment_AndSharesEverythingElse()
    {
        var g = Export(ThreeSegmentProject());

        foreach (var perClip in new[]
                 {
                     "MiniMaxH3ReferenceToVideo", "SamplerCustomAdvanced", "BasicGuider", "RandomNoise", "VAEDecode",
                     "VAEDecodeAudio", "CreateVideo", "SaveVideo", "PrimitiveStringMultiline", "PrimitiveFloat", "ComfyMathExpression"
                 })
            Assert.Equal(3, g.OfType(perClip).Count);

        // Loaders, sampler/scheduler settings, resolution, and the cast are shared -- not tripled.
        Assert.Single(g.OfType("UNETLoader"));
        Assert.Single(g.OfType("CLIPLoader"));
        Assert.Equal(2, g.OfType("VAELoader").Count);
        Assert.Single(g.OfType("KSamplerSelect"));
        Assert.Single(g.OfType("BasicScheduler"));
        Assert.Single(g.OfType("ResolutionSelector"));
        Assert.Equal(2, g.OfType("LoadImage").Count);   // the hero's two pictures, once
        Assert.Single(g.OfType("LoadAudio"));
    }

    [Fact]
    public void EverySegment_ReferencesTheSameSharedPictureAndVoiceLoaders_InTheSameOrder()
    {
        var g = Export(ThreeSegmentProject());
        var refs = RefNodes(g);

        foreach (var name in new[] { "ref_images.ref_image_0", "ref_images.ref_image_1", "ref_audios.ref_audio_0" })
        {
            var first = g.Feeder(refs[0], name);
            Assert.NotNull(first);
            foreach (var later in refs.Skip(1))
                Assert.Equal(Graph.Id(first!.Value.Origin), Graph.Id(g.Feeder(later, name)!.Value.Origin));
        }
    }

    [Fact]
    public void EachContinuation_TakesThePreviousClipsDecodedFramesAndAudio_InGraph()
    {
        var g = Export(ThreeSegmentProject());
        var refs = RefNodes(g);
        var decodes = g.OfType("VAEDecode");
        var audioDecodes = g.OfType("VAEDecodeAudio");

        // Segment 1 has no source video; segments 2 and 3 are fed by the clip before them.
        Assert.Null(g.Feeder(refs[0], "ref_videos.ref_video_0"));

        // (Through the tail trims, which the default settings add.)
        Assert.Equal(Graph.Id(decodes[0]), Graph.Id(Source(g, refs[1], "ref_videos.ref_video_0")));
        Assert.Equal(Graph.Id(audioDecodes[0]), Graph.Id(Source(g, refs[1], "ref_video_audios.ref_video_audio_0")));
        Assert.Equal(Graph.Id(decodes[1]), Graph.Id(Source(g, refs[2], "ref_videos.ref_video_0")));
        Assert.Equal(Graph.Id(audioDecodes[1]), Graph.Id(Source(g, refs[2], "ref_video_audios.ref_video_audio_0")));

        // No file loaders were created for the chained clips.
        Assert.Empty(g.OfType("VHS_LoadVideoPath"));
    }

    [Fact]
    public void AContinuationWhoseAudioIsNotUsed_GetsNoPairedAudioLink()
    {
        var g = Export(ThreeSegmentProject(VideoAudioUse.None));
        var refs = RefNodes(g);

        Assert.NotNull(g.Feeder(refs[1], "ref_videos.ref_video_0"));
        Assert.Null(g.Feeder(refs[1], "ref_video_audios.ref_video_audio_0"));
    }

    [Fact]
    public void SegmentOnesOwnSourceVideos_AreNotCarriedIntoTheContinuations()
    {
        var project = ThreeSegmentProject();
        project.SourceVideos.Add(new VideoRef { Description = "an establishing clip", AudioUse = VideoAudioUse.Reuse });

        var g = Export(project);
        var refs = RefNodes(g);
        var vhs = Assert.Single(g.OfType("VHS_LoadVideoPath"));

        // Segment 1 reads the VHS loader (video and audio)...
        Assert.Equal(Graph.Id(vhs), Graph.Id(g.Feeder(refs[0], "ref_videos.ref_video_0")!.Value.Origin));
        Assert.Equal(Graph.Id(vhs), Graph.Id(g.Feeder(refs[0], "ref_video_audios.ref_video_audio_0")!.Value.Origin));

        // ...but the continuations read only the previous clip, and the loader feeds nothing else.
        foreach (var later in refs.Skip(1))
            Assert.Equal("VAEDecode", Source(g, later, "ref_videos.ref_video_0")["type"]!.GetValue<string>());
        Assert.Equal(2, vhs["outputs"]!.AsArray().Sum(o => o!["links"]?.AsArray().Count ?? 0));
    }

    [Fact]
    public void EachSegment_GetsItsOwnPromptDurationAndOutputFile()
    {
        var g = Export(ThreeSegmentProject());
        var refs = RefNodes(g);

        string PromptOf(JsonObject refNode) =>
            g.Feeder(refNode, "prompt")!.Value.Origin["widgets_values"]!.AsArray()[0]!.GetValue<string>();

        var first = PromptOf(refs[0]);
        var second = PromptOf(refs[1]);
        var third = PromptOf(refs[2]);
        Assert.Contains("Part one summary.", first);
        Assert.DoesNotContain("<Video 1>", first);
        Assert.Contains("Part two summary.", second);
        Assert.Contains("[reference generation + video continuation + audio reference]", second);
        Assert.Contains("<Video 1> is the source video, which the target video continues from the end of.", second);
        Assert.Contains("Part three summary.", third);
        Assert.Contains("<Audio 1> is the synchronized audio track of <Video 1>", third);

        // Each ref node's length comes from its own duration stack (12s / 10s / 8s), not a shared one.
        var durations = refs.Select(r =>
        {
            var length = g.Feeder(r, "length")!.Value.Origin;                 // ComfyMathExpression
            return g.Feeder(length, "values.a")!.Value.Origin["widgets_values"]!.AsArray()[0]!.GetValue<double>();
        }).ToList();
        Assert.Equal([12.0, 10.0, 8.0], durations);

        var prefixes = g.OfType("SaveVideo").Select(n => n["widgets_values"]!.AsArray()[0]!.GetValue<string>()).ToList();
        Assert.Equal(3, prefixes.Distinct().Count());
        Assert.EndsWith("_part1", prefixes[0]);
        Assert.EndsWith("_part2", prefixes[1]);
        Assert.EndsWith("_part3", prefixes[2]);
    }

    [Fact]
    public void ChainedGraph_HasConsistentLinkBookkeeping_AndEveryClipStackIsFullyWired()
    {
        var g = Export(ThreeSegmentProject());

        // Unique ids, and last_*_id covers everything.
        var nodeIds = g.Nodes.Select(Graph.Id).ToList();
        Assert.Equal(nodeIds.Count, nodeIds.Distinct().Count());
        var linkIds = g.Links.Select(l => (int)l[0]!.GetValue<double>()).ToList();
        Assert.Equal(linkIds.Count, linkIds.Distinct().Count());
        Assert.True(g.Root["last_node_id"]!.GetValue<double>() >= nodeIds.Max());
        Assert.True(g.Root["last_link_id"]!.GetValue<double>() >= linkIds.Max());

        foreach (var link in g.Links)
        {
            var id = link[0]!.GetValue<double>();
            var origin = g.Node((int)link[1]!.GetValue<double>());
            var target = g.Node((int)link[3]!.GetValue<double>());

            // The origin's output records the link, and the target's input points back at it.
            var recorded = origin["outputs"]!.AsArray()[(int)link[2]!.GetValue<double>()]!["links"]!.AsArray();
            Assert.Contains(recorded, l => l!.GetValue<double>() == id);
            Assert.Equal(id, target["inputs"]!.AsArray()[(int)link[4]!.GetValue<double>()]!["link"]!.GetValue<double>());
        }

        // Every per-clip node has as many wired inputs as its segment-1 original -- the ref nodes
        // differ only by the extra video/audio pair a continuation gets.
        foreach (var type in new[] { "SamplerCustomAdvanced", "BasicGuider", "VAEDecode", "VAEDecodeAudio", "CreateVideo", "SaveVideo" })
        {
            var counts = g.OfType(type).Select(n => n["inputs"]!.AsArray().Count(i => i!["link"] is not null)).Distinct().ToList();
            Assert.Single(counts);
        }
    }

    [Fact]
    public void ImportingAChainedExport_RebuildsEverySegment_SoEachPromptComposesIdentically()
    {
        var project = ThreeSegmentProject();
        project.Continuations[0].Shots[0].Text = "<Subject 1> (S1) says, <d>[English] Go, go!</d> and leaps the gap.";
        project.Continuations[0].Summary = "Part two summary, with <Subject 1> mid-air.";
        project.Continuations[0].PreviousVideo.Retention = VisualRetentionType.PartiallyPreserved;
        project.Continuations[0].PreviousVideo.RetentionNote = "the ending pose and motion carry over";
        project.Continuations[1].PreviousVideo.AudioUse = VideoAudioUse.None;
        project.Continuations[1].OverallSoundscape = "A distant siren fades in.";

        var imported = ComfyWorkflowImporter.Import(ComfyWorkflowExporter.Export(LoadTemplate(), project));

        Assert.Equal(3, imported.SegmentCount);
        Assert.Single(imported.Subjects);                                // the cast is shared, not tripled
        Assert.Equal(2, imported.Subjects[0].Pictures.Count);

        var second = imported.Continuations[0];
        Assert.Equal(10, second.DurationSeconds);
        Assert.Equal("Part two summary, with <Subject 1> mid-air.", second.Summary);
        Assert.Equal(TaskType.ReferenceGeneration, second.TaskTypes);    // implied continuation/audio types aren't stored
        Assert.Equal(VideoAudioUse.Reference, second.PreviousVideo.AudioUse);
        Assert.Equal(VisualRetentionType.PartiallyPreserved, second.PreviousVideo.Retention);
        Assert.Equal("the ending pose and motion carry over", second.PreviousVideo.RetentionNote);
        var line = Assert.Single(Assert.Single(second.Shots).Dialogue);   // dialogue is structured again
        Assert.Equal("Go, go!", line.Text);
        Assert.Equal(imported.Subjects[0].Id, line.SpeakerSubjectId);

        var third = imported.Continuations[1];
        Assert.Equal(8, third.DurationSeconds);
        Assert.Equal(VideoAudioUse.None, third.PreviousVideo.AudioUse);
        Assert.Equal("A distant siren fades in.", third.OverallSoundscape);

        for (var i = 0; i < 3; i++)
            Assert.Equal(PromptComposer.ComposeSegment(project, i), PromptComposer.ComposeSegment(imported, i));
    }

    [Theory]
    [InlineData(12.0, 294)]
    [InlineData(10.0, 243)]
    [InlineData(8.0, 192)]
    [InlineData(0.1, 5)]
    public void ClipFrames_MirrorsTheTemplatesLengthMath(double seconds, int expectedFrames)
    {
        Assert.Equal(expectedFrames, ClipFrames.ForSeconds(seconds));
        Assert.Equal(5, ClipFrames.ForSeconds(seconds) % 17);   // always a valid 17k + 5
    }

    [Theory]
    [InlineData(73, 73)]
    [InlineData(100, 90)]
    [InlineData(48, 39)]
    [InlineData(4, 5)]
    public void ClipFrames_LargestValidAtMost_SnapsDown(int requested, int expected) =>
        Assert.Equal(expected, ClipFrames.LargestValidAtMost(requested));

    [Theory]
    [InlineData(72, 73)]    // 3 s
    [InlineData(48, 56)]    // 2 s: 56 is 8 away, 39 is 9
    [InlineData(24, 22)]    // 1 s
    [InlineData(96, 90)]    // 4 s
    public void ClipFrames_NearestValid_SnapsToTheClosestValidLength(int requested, int expected) =>
        Assert.Equal(expected, ClipFrames.NearestValid(requested));

    private static List<JsonObject> TrimNodesFor(Graph g, string type, int segmentNumber) =>
        g.OfType(type).Where(n => n["title"]!.GetValue<string>().EndsWith($"{ComfyWorkflowExporter.SegmentTitleMarker}{segmentNumber}")).ToList();

    [Fact]
    public void ByDefault_AContinuationGetsOnlyTheLastThreeSecondsOfThePreviousClip()
    {
        var g = Export(ThreeSegmentProject());
        var refs = RefNodes(g);

        // 3 s = 72 frames, which snaps to the nearest valid length (17k + 5): 73, i.e. ~3.04 s.
        const int expected = 73;

        var frameTrim = Assert.Single(TrimNodesFor(g, "ImageFromBatch", 2));
        Assert.Equal([-expected, expected], frameTrim["widgets_values"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray());

        // The reference node reads the trim, and the trim reads the previous clip's decoded frames.
        Assert.Equal(Graph.Id(frameTrim), Graph.Id(g.Feeder(refs[1], "ref_videos.ref_video_0")!.Value.Origin));
        Assert.Equal("VAEDecode", g.Feeder(frameTrim, "image")!.Value.Origin["type"]!.GetValue<string>());

        // The soundtrack is cut to the same length from the same end so picture and sound stay in step.
        var audioTrim = Assert.Single(TrimNodesFor(g, "TrimAudioDuration", 2));
        var audioWidgets = audioTrim["widgets_values"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        Assert.Equal(-expected / 24.0, audioWidgets[0], 6);
        Assert.Equal(expected / 24.0, audioWidgets[1], 6);
        Assert.Equal(Graph.Id(audioTrim), Graph.Id(g.Feeder(refs[1], "ref_video_audios.ref_video_audio_0")!.Value.Origin));
        Assert.Equal("VAEDecodeAudio", g.Feeder(audioTrim, "audio")!.Value.Origin["type"]!.GetValue<string>());
    }

    [Fact]
    public void AWholeClipRequest_NeedsNoTrimWhenTheNewClipIsAtLeastAsLong()
    {
        var project = ThreeSegmentProject();
        project.Continuations[0].DurationSeconds = 12;
        project.Continuations[1].DurationSeconds = 12;
        foreach (var segment in project.Continuations)
            segment.PreviousVideo.UseLastSeconds = 0;     // "the whole previous clip"

        var g = Export(project);

        Assert.Empty(g.OfType("ImageFromBatch"));
        Assert.Empty(g.OfType("TrimAudioDuration"));
        Assert.Equal("VAEDecode", g.Feeder(RefNodes(g)[1], "ref_videos.ref_video_0")!.Value.Origin["type"]!.GetValue<string>());
    }

    [Fact]
    public void AShorterNewClip_StillGetsTheENDOfThePreviousClip_NotItsBeginning()
    {
        // The reference node keeps a too-long reference video's FIRST frames, so handing it all of a 12 s
        // clip for a 10 s one would silently drop the ending. We must cut the tail ourselves.
        var project = ThreeSegmentProject();                    // 12s -> 10s -> 8s
        foreach (var segment in project.Continuations)
            segment.PreviousVideo.UseLastSeconds = 0;

        var g = Export(project);

        var second = Assert.Single(TrimNodesFor(g, "ImageFromBatch", 2))["widgets_values"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        Assert.Equal([-243, 243], second);                      // last 243 frames (the 10 s clip's length), not the first

        var third = Assert.Single(TrimNodesFor(g, "ImageFromBatch", 3))["widgets_values"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        Assert.Equal([-192, 192], third);
    }

    [Fact]
    public void ATailLongerThanThePreviousClip_MeansTheWholeClip()
    {
        var project = ThreeSegmentProject();
        project.Continuations[0].DurationSeconds = 12;
        project.Continuations[0].PreviousVideo.UseLastSeconds = 30;

        var g = Export(project);

        Assert.Empty(TrimNodesFor(g, "ImageFromBatch", 2));
    }

    [Fact]
    public void WhenThePreviousClipsAudioIsNotUsed_OnlyTheFramesAreTrimmed()
    {
        var g = Export(ThreeSegmentProject(VideoAudioUse.None));

        Assert.NotEmpty(g.OfType("ImageFromBatch"));
        Assert.Empty(g.OfType("TrimAudioDuration"));
    }

    [Theory]
    [InlineData(3.0, 3.0)]   // 3 s -> 73 frames (3.04 s) once snapped to a valid length
    [InlineData(0.0, 0.0)]   // whole clip: no trim node, so it reads back as 0
    public void TheTailSetting_RoundTripsThroughAnExportAndImport(double useLastSeconds, double expectedAfterImport)
    {
        var project = ThreeSegmentProject();
        project.Continuations[0].DurationSeconds = 12;
        project.Continuations[0].PreviousVideo.UseLastSeconds = useLastSeconds;

        var imported = ComfyWorkflowImporter.Import(ComfyWorkflowExporter.Export(LoadTemplate(), project));

        Assert.Equal(expectedAfterImport, imported.Continuations[0].PreviousVideo.UseLastSeconds, 1);
    }

    [Fact]
    public void ASingleSegmentProject_ExportsExactlyAsBefore()
    {
        var project = ThreeSegmentProject();
        project.Continuations.Clear();

        var g = Export(project);

        Assert.Single(g.OfType("MiniMaxH3ReferenceToVideo"));
        Assert.Single(g.OfType("SaveVideo"));
        Assert.Equal("%date:yyyy-MM-dd%/ComfyUI", g.OfType("SaveVideo")[0]["widgets_values"]!.AsArray()[0]!.GetValue<string>());
    }
}
