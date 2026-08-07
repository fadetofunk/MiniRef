using System.Text.Json.Nodes;
using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

public class ComfyWorkflowExporterTests
{
    private static string LoadTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video_minimax_h3_r2v.template.json"));

    private static SceneProject BuildProjectWithFourPicturesAndOneAudio()
    {
        var sarah = new Subject
        {
            Name = "Sarah Connor",
            Description = "a weary survivor",
            Pictures = [new PictureRef { Description = "front view" }, new PictureRef { Description = "profile" }],
            Audio = new AudioRef { Description = "" }
        };
        var terminator = new Subject
        {
            Name = "The Terminator",
            Description = "a relentless cyborg",
            Pictures = [new PictureRef { Description = "front view" }]
        };
        var nightclub = new Subject
        {
            Classification = SubjectClassification.SceneOrSetting,
            Name = "Tech Noir",
            Description = "a crowded 1980s nightclub",
            Pictures = [new PictureRef { Description = "wide shot" }]
        };

        return new SceneProject
        {
            Subjects = [sarah, terminator, nightclub],
            Shots = [new Shot { Text = "<Subject 1> and <Subject 2> face off in <Subject 3>." }],
            Summary = "A confrontation unfolds."
        };
    }

    [Fact]
    public void Export_WiresAllPicturesAndAudio_AndStripsDemoContent()
    {
        var template = LoadTemplate();
        var project = BuildProjectWithFourPicturesAndOneAudio();

        var resultJson = ComfyWorkflowExporter.Export(template, project);
        var result = JsonNode.Parse(resultJson)!.AsObject();
        var nodes = result["nodes"]!.AsArray();
        var links = result["links"]!.AsArray();

        var refNode = nodes.Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "MiniMaxH3ReferenceToVideo");
        var refNodeId = (int)refNode["id"]!.GetValue<double>();
        var inputs = refNode["inputs"]!.AsArray();

        // 4 pictures requested -> slots 0..2 (built into the template) plus a freshly-created slot 3.
        for (var i = 0; i < 4; i++)
        {
            var slot = inputs.Single(inp => inp!["name"]!.GetValue<string>() == $"ref_images.ref_image_{i}");
            Assert.NotNull(slot!["link"]);
        }

        // Only 1 audio ref -> just slot 0.
        var audioSlot = inputs.Single(inp => inp!["name"]!.GetValue<string>() == "ref_audios.ref_audio_0");
        Assert.NotNull(audioSlot!["link"]);

        var loadImageNodes = nodes.Select(n => n!.AsObject())
            .Where(n => n["type"]!.GetValue<string>() == "LoadImage")
            .ToList();
        Assert.Equal(4, loadImageNodes.Count);

        var loadAudioNodes = nodes.Select(n => n!.AsObject())
            .Where(n => n["type"]!.GetValue<string>() == "LoadAudio")
            .ToList();
        Assert.Single(loadAudioNodes);

        // Demo reference images from the template must be gone, not just unlinked.
        var demoFilenames = loadImageNodes
            .Select(n => n["widgets_values"]!.AsArray()[0]!.GetValue<string>())
            .ToList();
        Assert.DoesNotContain("red_superboy_on_city_roof.png", demoFilenames);
        Assert.DoesNotContain("mecha_dragon_lightning.png", demoFilenames);

        // Titles identify which character/tag each LoadImage node corresponds to.
        var titles = loadImageNodes.Select(n => n["title"]!.GetValue<string>()).ToList();
        Assert.Contains(titles, t => t.Contains("<Picture 1>") && t.Contains("Sarah Connor"));
        Assert.Contains(titles, t => t.Contains("<Picture 3>") && t.Contains("The Terminator"));
        Assert.Contains(titles, t => t.Contains("<Picture 4>") && t.Contains("Tech Noir"));

        // Every LoadImage/LoadAudio node's output link actually resolves to the reference node.
        foreach (var node in loadImageNodes.Concat(loadAudioNodes))
        {
            var nodeId = (int)node["id"]!.GetValue<double>();
            var outputLinkId = node["outputs"]!.AsArray()[0]!["links"]!.AsArray()[0]!.GetValue<double>();
            var linkArray = links.Single(l => l!.AsArray()[0]!.GetValue<double>() == outputLinkId)!.AsArray();
            Assert.Equal(nodeId, (int)linkArray[1]!.GetValue<double>());
            Assert.Equal(refNodeId, (int)linkArray[3]!.GetValue<double>());
        }

        // Prompt node carries the actual composed prompt, not the template's demo text.
        var promptNode = nodes.Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "PrimitiveStringMultiline");
        var promptText = promptNode["widgets_values"]!.AsArray()[0]!.GetValue<string>();
        Assert.Equal(PromptComposer.Compose(project), promptText);
        Assert.DoesNotContain("GET READY TO", promptText);

        // No duplicate node ids, and last_node_id/last_link_id keep pace with what was added.
        var allIds = nodes.Select(n => (int)n!["id"]!.GetValue<double>()).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
        Assert.True((int)result["last_node_id"]!.GetValue<double>() >= allIds.Max());
    }

    [Fact]
    public void Export_WiresSourceVideos_UsingVhsLoadVideoPathWithObjectWidgets()
    {
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }],
            SourceVideos =
            [
                new VideoRef { Description = "opening rooftop pan" },
                new VideoRef { Description = "chase sequence" }
            ]
        };

        var resultJson = ComfyWorkflowExporter.Export(template, project);
        var result = JsonNode.Parse(resultJson)!.AsObject();
        var nodes = result["nodes"]!.AsArray();
        var links = result["links"]!.AsArray();

        var refNode = nodes.Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "MiniMaxH3ReferenceToVideo");
        var refNodeId = (int)refNode["id"]!.GetValue<double>();
        var inputs = refNode["inputs"]!.AsArray();

        // The GitHub template only ships ref_video_0 -- ref_video_1 must be freshly created.
        var slot0 = inputs.Single(i => i!["name"]!.GetValue<string>() == "ref_videos.ref_video_0");
        var slot1 = inputs.Single(i => i!["name"]!.GetValue<string>() == "ref_videos.ref_video_1");
        Assert.NotNull(slot0!["link"]);
        Assert.NotNull(slot1!["link"]);

        var videoNodes = nodes.Select(n => n!.AsObject())
            .Where(n => n["type"]!.GetValue<string>() == "VHS_LoadVideoPath")
            .ToList();
        Assert.Equal(2, videoNodes.Count);

        var titles = videoNodes.Select(n => n["title"]!.GetValue<string>()).ToList();
        Assert.Contains(titles, t => t.Contains("<Video 1>") && t.Contains("opening rooftop pan"));
        Assert.Contains(titles, t => t.Contains("<Video 2>") && t.Contains("chase sequence"));

        foreach (var node in videoNodes)
        {
            // widgets_values must serialize as a JSON object (VHS_LoadVideoPath's real shape),
            // not the positional array other loader nodes use.
            var widgets = node["widgets_values"]!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(widgets["video"]!.GetValue<string>()));

            var nodeId = (int)node["id"]!.GetValue<double>();
            var outputLinkId = node["outputs"]!.AsArray()[0]!["links"]!.AsArray()[0]!.GetValue<double>();
            var linkArray = links.Single(l => l!.AsArray()[0]!.GetValue<double>() == outputLinkId)!.AsArray();
            Assert.Equal(nodeId, (int)linkArray[1]!.GetValue<double>());
            Assert.Equal(refNodeId, (int)linkArray[3]!.GetValue<double>());
            Assert.Equal("IMAGE", linkArray[5]!.GetValue<string>());
        }
    }

    [Fact]
    public void DiscoverModelSlots_FindsAllFourLoaders_WithCorrectVaeDisambiguation()
    {
        var template = LoadTemplate();

        var slots = ComfyWorkflowExporter.DiscoverModelSlots(template);

        Assert.Equal(4, slots.Count);

        var diffusion = slots.Single(s => s.Key == "DiffusionModel");
        Assert.Equal("minimax_h3_ref2va_pruned_int8_convrot.safetensors", diffusion.CurrentFilename);
        Assert.Equal("diffusion_models", diffusion.ModelsFolder);

        var textEncoder = slots.Single(s => s.Key == "TextEncoder");
        Assert.Equal("qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors", textEncoder.CurrentFilename);
        Assert.Equal("text_encoders", textEncoder.ModelsFolder);

        // The two VAELoader nodes must be disambiguated by which named input they actually feed,
        // not by filename -- confirming that against the real template's video/audio VAE files.
        var videoVae = slots.Single(s => s.Key == "VideoVae");
        Assert.Equal("minimax_h3_video_vae_fp16.safetensors", videoVae.CurrentFilename);

        var audioVae = slots.Single(s => s.Key == "AudioVae");
        Assert.Equal("minimax_h3_audio_vae_fp32.safetensors", audioVae.CurrentFilename);

        Assert.True(slots.All(s => s.ModelsFolder == "vae" || s.Key is "DiffusionModel" or "TextEncoder"));
    }

    [Fact]
    public void Export_AppliesModelOverrides_OnlyForProvidedKeys_LeavingOthersAtTemplateDefault()
    {
        var template = LoadTemplate();
        var project = new SceneProject { Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }] };
        var overrides = new Dictionary<string, string>
        {
            ["DiffusionModel"] = "my_custom_unet.safetensors",
            ["AudioVae"] = "my_custom_audio_vae.safetensors",
            ["SomeUnknownKey"] = "should_be_ignored.safetensors"
        };

        var resultJson = ComfyWorkflowExporter.Export(template, project, modelOverrides: overrides);
        var slots = ComfyWorkflowExporter.DiscoverModelSlots(resultJson);

        Assert.Equal("my_custom_unet.safetensors", slots.Single(s => s.Key == "DiffusionModel").CurrentFilename);
        Assert.Equal("my_custom_audio_vae.safetensors", slots.Single(s => s.Key == "AudioVae").CurrentFilename);

        // Not overridden -- stayed at whatever the template shipped with.
        Assert.Equal("qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors", slots.Single(s => s.Key == "TextEncoder").CurrentFilename);
        Assert.Equal("minimax_h3_video_vae_fp16.safetensors", slots.Single(s => s.Key == "VideoVae").CurrentFilename);
    }

    [Fact]
    public void Export_SetsDurationAspectRatioAndMegapixels_FromProject()
    {
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }],
            DurationSeconds = 12.5,
            AspectRatio = WorkflowAspectRatio.PortraitWidescreen9x16,
            Megapixels = 0.7
        };

        var resultJson = ComfyWorkflowExporter.Export(template, project);
        var result = JsonNode.Parse(resultJson)!.AsObject();
        var nodes = result["nodes"]!.AsArray();

        var resolutionNode = nodes.Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "ResolutionSelector");
        var resolutionWidgets = resolutionNode["widgets_values"]!.AsArray();
        Assert.Equal("9:16 (Portrait Widescreen)", resolutionWidgets[0]!.GetValue<string>());
        Assert.Equal(0.7, resolutionWidgets[1]!.GetValue<double>());

        var durationNode = nodes.Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "PrimitiveFloat" && n["title"]!.GetValue<string>() == "Float (Duration)");
        Assert.Equal(12.5, durationNode["widgets_values"]!.AsArray()[0]!.GetValue<double>());
    }

    [Fact]
    public void Export_SetsSaveVideoFilenamePrefix_ToDateBasedComfyUiFolder()
    {
        var template = LoadTemplate();
        var project = new SceneProject { Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }] };

        var resultJson = ComfyWorkflowExporter.Export(template, project);
        var result = JsonNode.Parse(resultJson)!.AsObject();

        var saveVideoNode = result["nodes"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "SaveVideo");

        var prefix = saveVideoNode["widgets_values"]!.AsArray()[0]!.GetValue<string>();
        Assert.Equal("%date:yyyy-MM-dd%/ComfyUI", prefix);
    }

    [Fact]
    public void Export_UsesResolverOutput_WhenProvided_AndFallsBackToPlaceholderWhenResolverReturnsNull()
    {
        var template = LoadTemplate();
        var withFile = new Subject
        {
            Name = "Has File",
            Description = "a subject with a resolvable picture",
            Pictures = [new PictureRef { Description = "front" }]
        };
        var withoutFile = new Subject
        {
            Name = "No File",
            Description = "a subject with an unresolvable picture",
            Pictures = [new PictureRef { Description = "back" }]
        };
        var project = new SceneProject { Subjects = [withFile, withoutFile] };

        var resultJson = ComfyWorkflowExporter.Export(
            template, project,
            resolvePictureFilename: (subject, _) => subject.Name == "Has File" ? "resolved_real_file.png" : null);

        var result = JsonNode.Parse(resultJson)!.AsObject();
        var loadImageNodes = result["nodes"]!.AsArray()
            .Select(n => n!.AsObject())
            .Where(n => n["type"]!.GetValue<string>() == "LoadImage")
            .ToList();

        var filenames = loadImageNodes.Select(n => n["widgets_values"]!.AsArray()[0]!.GetValue<string>()).ToList();
        Assert.Contains("resolved_real_file.png", filenames);
        Assert.Contains(filenames, f => f != "resolved_real_file.png" && f.Contains("No_File"));
    }

    [Fact]
    public void Export_WithNoPicturesOrAudio_LeavesRefSlotsUnlinked()
    {
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }]
        };

        var resultJson = ComfyWorkflowExporter.Export(template, project);
        var result = JsonNode.Parse(resultJson)!.AsObject();
        var nodes = result["nodes"]!.AsArray();

        Assert.DoesNotContain(nodes, n => n!["type"]!.GetValue<string>() == "LoadImage");
        Assert.DoesNotContain(nodes, n => n!["type"]!.GetValue<string>() == "LoadAudio");

        var refNode = nodes.Select(n => n!.AsObject())
            .Single(n => n["type"]!.GetValue<string>() == "MiniMaxH3ReferenceToVideo");
        var ref0 = refNode["inputs"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "ref_images.ref_image_0");
        Assert.Null(ref0!["link"]);
    }
}
