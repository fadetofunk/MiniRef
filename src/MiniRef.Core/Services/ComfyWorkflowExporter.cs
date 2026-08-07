using System.Text.Json;
using System.Text.Json.Nodes;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Rewires the bundled MiniMax H3 reference-to-video ComfyUI template: strips its demo
/// reference images/prompt and replaces them with LoadImage/LoadAudio/VHS_LoadVideoPath nodes for
/// the project's actual cast, each titled with its &lt;Picture N&gt;/&lt;Audio N&gt;/&lt;Video N&gt;
/// tag and the subject it belongs to, so they're easy to point at real files by hand once the
/// workflow is open in ComfyUI -- plus the composed prompt text, wired into the same
/// PrimitiveStringMultiline node the template already feeds into the reference node's "prompt" input.
///
/// Reference-video nodes use VHS_LoadVideoPath from the ComfyUI-VideoHelperSuite custom node pack
/// (confirmed against a real user workflow) -- that pack needs to be installed for those nodes to
/// resolve when the exported workflow is opened.</summary>
public static class ComfyWorkflowExporter
{
    private const string ReferenceNodeType = "MiniMaxH3ReferenceToVideo";
    private const string PromptNodeType = "PrimitiveStringMultiline";
    private const string SaveVideoNodeType = "SaveVideo";
    private const string UnetLoaderNodeType = "UNETLoader";
    private const string ClipLoaderNodeType = "CLIPLoader";
    private const string VaeLoaderNodeType = "VAELoader";
    private const string ResolutionSelectorNodeType = "ResolutionSelector";
    private const string DurationNodeType = "PrimitiveFloat";
    private const string DurationNodeTitle = "Float (Duration)";
    private const string OutputFilenamePrefix = "%date:yyyy-MM-dd%/ComfyUI";

    /// <summary>One overridable model-loader slot found in the template. <see cref="Key"/> is
    /// stable across template re-exports and is what AppSettings.ModelOverrides is keyed by.
    /// <see cref="ModelsFolder"/> is the ComfyUI models subfolder this file type lives in
    /// (e.g. "diffusion_models", "text_encoders", "vae"), for scoping a file-browse dialog.</summary>
    public record ComfyModelSlot(string Key, string Label, string CurrentFilename, string ModelsFolder);

    /// <summary>Lists the model-loader nodes in the template so a settings UI can show what's
    /// currently configured and let the user override any of them. VAE loaders are disambiguated
    /// by tracing which named input on the reference node they actually feed ("vae" vs
    /// "audio_vae") rather than by filename, so it stays correct even if the shipped default
    /// model files get renamed upstream.</summary>
    public static IReadOnlyList<ComfyModelSlot> DiscoverModelSlots(string templateJson)
    {
        var root = JsonNode.Parse(templateJson)?.AsObject()
            ?? throw new InvalidDataException("Template is not valid JSON.");
        var nodes = root["nodes"]?.AsArray() ?? throw new InvalidDataException("Template has no 'nodes' array.");
        var links = root["links"]?.AsArray() ?? throw new InvalidDataException("Template has no 'links' array.");
        var refNode = FindNodeByType(nodes, ReferenceNodeType);

        var slots = new List<ComfyModelSlot>();

        var unet = FindNodeByType(nodes, UnetLoaderNodeType);
        if (unet is not null)
            slots.Add(new ComfyModelSlot("DiffusionModel", "Diffusion Model (UNET)", CurrentFilename(unet), ModelsFolder(unet)));

        var clip = FindNodeByType(nodes, ClipLoaderNodeType);
        if (clip is not null)
            slots.Add(new ComfyModelSlot("TextEncoder", "Text Encoder (CLIP)", CurrentFilename(clip), ModelsFolder(clip)));

        if (refNode is not null)
        {
            foreach (var vae in nodes.Select(n => n!.AsObject()).Where(n => n["type"]?.GetValue<string>() == VaeLoaderNodeType))
            {
                var vaeId = (int)vae["id"]!.GetValue<double>();
                var fedInputName = FindRefNodeInputNameFedBy(refNode, links, vaeId);
                var (key, label) = fedInputName switch
                {
                    "vae" => ("VideoVae", "Video VAE"),
                    "audio_vae" => ("AudioVae", "Audio VAE"),
                    _ => ($"Vae_{vaeId}", "VAE")
                };
                slots.Add(new ComfyModelSlot(key, label, CurrentFilename(vae), ModelsFolder(vae)));
            }
        }

        return slots;

        static string CurrentFilename(JsonObject node) => node["widgets_values"]!.AsArray()[0]!.GetValue<string>();
        static string ModelsFolder(JsonObject node) =>
            node["properties"]?["models"]?.AsArray().FirstOrDefault()?["directory"]?.GetValue<string>() ?? "";
    }

    // Documented caps on the MiniMaxH3ReferenceToVideo node itself.
    private const int MaxImageSlots = 9;
    private const int MaxAudioSlots = 3;
    private const int MaxVideoSlots = 3;

    /// <summary>
    /// <paramref name="resolvePictureFilename"/>/<paramref name="resolveAudioFilename"/> return the
    /// string to put in the LoadImage/LoadAudio widget (typically a bare filename already sitting in
    /// ComfyUI's input folder); <paramref name="resolveVideoPath"/> returns an absolute path for
    /// VHS_LoadVideoPath. Returning null (or omitting a resolver) falls back to a descriptive
    /// placeholder the user fills in by hand -- this keeps Export a pure string transform so callers
    /// that need real file I/O (copying into ComfyUI's input folder, say) can do it in the resolver
    /// without this method needing to know anything about the filesystem itself.
    /// </summary>
    public static string Export(
        string templateJson,
        SceneProject project,
        Func<Subject, PictureRef, string?>? resolvePictureFilename = null,
        Func<Subject, AudioRef, string?>? resolveAudioFilename = null,
        Func<VideoRef, string?>? resolveVideoPath = null,
        IReadOnlyDictionary<string, string>? modelOverrides = null)
    {
        var root = JsonNode.Parse(templateJson)?.AsObject()
            ?? throw new InvalidDataException("Template is not valid JSON.");
        var nodes = root["nodes"]?.AsArray() ?? throw new InvalidDataException("Template has no 'nodes' array.");
        var links = root["links"]?.AsArray() ?? throw new InvalidDataException("Template has no 'links' array.");

        var refNode = FindNodeByType(nodes, ReferenceNodeType)
            ?? throw new InvalidDataException($"Template is missing a '{ReferenceNodeType}' node.");
        var promptNode = FindNodeByType(nodes, PromptNodeType);
        var refNodeId = (int)refNode["id"]!.GetValue<double>();

        var nextNodeId = (int)root["last_node_id"]!.GetValue<double>() + 1;
        var nextLinkId = (int)root["last_link_id"]!.GetValue<double>() + 1;

        ClearExistingRefSlots(nodes, links, refNode, "ref_images.ref_image_");
        ClearExistingRefSlots(nodes, links, refNode, "ref_audios.ref_audio_");
        ClearExistingRefSlots(nodes, links, refNode, "ref_videos.ref_video_");

        var (_, pictureNumbers, audioNumbers) = ReferenceNumberer.NumberSubjects(project.Subjects);

        var pictures = project.Subjects
            .SelectMany(s => s.Pictures.Select(p => (Subject: s, Picture: p)))
            .Take(MaxImageSlots)
            .ToList();

        var audioRefs = project.Subjects
            .Where(s => s.Audio is not null)
            .Select(s => (Subject: s, Audio: s.Audio!))
            .Take(MaxAudioSlots)
            .ToList();

        var sourceVideos = project.SourceVideos.Take(MaxVideoSlots).ToList();

        const int baseX = -930;
        const int imageBaseY = 5630;
        const int audioBaseY = imageBaseY + 420;
        const int videoBaseY = audioBaseY + 320;

        for (var i = 0; i < pictures.Count; i++)
        {
            var (subject, picture) = pictures[i];
            var n = pictureNumbers[picture.Id];
            var title = BuildTitle($"Picture {n}", subject.Name, picture.Description);
            var filename = resolvePictureFilename?.Invoke(subject, picture)
                ?? BuildPlaceholderFilename($"Picture {n}", subject.Name, picture.Description, ".png");

            var nodeId = nextNodeId++;
            var linkId = nextLinkId++;
            nodes.Add(BuildLoadImageNode(nodeId, title, filename, baseX + i * 320, imageBaseY, linkId));

            var slotIndex = GetOrCreateSlotIndex(refNode, "ref_images", "ref_image", i, "IMAGE");
            links.Add(BuildLink(linkId, nodeId, 0, refNodeId, slotIndex, "IMAGE"));
            SetSlotLink(refNode, $"ref_images.ref_image_{i}", linkId);
        }

        for (var i = 0; i < audioRefs.Count; i++)
        {
            var (subject, audio) = audioRefs[i];
            var n = audioNumbers[audio.Id];
            var title = BuildTitle($"Audio {n}", subject.Name, audio.Description);
            var filename = resolveAudioFilename?.Invoke(subject, audio)
                ?? BuildPlaceholderFilename($"Audio {n}", subject.Name, audio.Description, ".mp3");

            var nodeId = nextNodeId++;
            var linkId = nextLinkId++;
            nodes.Add(BuildLoadAudioNode(nodeId, title, filename, baseX + i * 320, audioBaseY, linkId));

            var slotIndex = GetOrCreateSlotIndex(refNode, "ref_audios", "ref_audio", i, "AUDIO");
            links.Add(BuildLink(linkId, nodeId, 0, refNodeId, slotIndex, "AUDIO"));
            SetSlotLink(refNode, $"ref_audios.ref_audio_{i}", linkId);
        }

        for (var i = 0; i < sourceVideos.Count; i++)
        {
            var video = sourceVideos[i];
            var n = i + 1;
            var title = BuildTitle($"Video {n}", "", video.Description);
            var placeholderPath = resolveVideoPath?.Invoke(video)
                ?? BuildPlaceholderFilename($"Video {n}", "", video.Description, ".mp4");

            var nodeId = nextNodeId++;
            var linkId = nextLinkId++;
            nodes.Add(BuildLoadVideoNode(nodeId, title, placeholderPath, baseX + i * 320, videoBaseY, linkId));

            var slotIndex = GetOrCreateSlotIndex(refNode, "ref_videos", "ref_video", i, "IMAGE");
            links.Add(BuildLink(linkId, nodeId, 0, refNodeId, slotIndex, "IMAGE"));
            SetSlotLink(refNode, $"ref_videos.ref_video_{i}", linkId);
        }

        if (promptNode is not null)
        {
            var widgets = promptNode["widgets_values"]!.AsArray();
            widgets[0] = PromptComposer.Compose(project);
        }

        var saveVideoNode = FindNodeByType(nodes, SaveVideoNodeType);
        if (saveVideoNode is not null)
        {
            var widgets = saveVideoNode["widgets_values"]!.AsArray();
            widgets[0] = OutputFilenamePrefix;
        }

        var resolutionNode = FindNodeByType(nodes, ResolutionSelectorNodeType);
        if (resolutionNode is not null)
        {
            var widgets = resolutionNode["widgets_values"]!.AsArray();
            widgets[0] = project.AspectRatio.ToPromptToken();
            widgets[1] = project.Megapixels;
        }

        var durationNode = nodes.Select(n => n!.AsObject())
            .FirstOrDefault(n => n["type"]?.GetValue<string>() == DurationNodeType
                && n["title"]?.GetValue<string>() == DurationNodeTitle);
        if (durationNode is not null)
        {
            var widgets = durationNode["widgets_values"]!.AsArray();
            widgets[0] = project.DurationSeconds;
        }

        if (modelOverrides is { Count: > 0 })
            ApplyModelOverrides(nodes, links, refNode, modelOverrides);

        root["last_node_id"] = nextNodeId - 1;
        root["last_link_id"] = nextLinkId - 1;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? FindNodeByType(JsonArray nodes, string type) => nodes
        .Select(n => n!.AsObject())
        .FirstOrDefault(n => n["type"]?.GetValue<string>() == type);

    /// <summary>Overwrites the diffusion model / text encoder / VAE loader filenames per
    /// <see cref="DiscoverModelSlots"/>'s Key scheme. Unknown keys and blank values are ignored,
    /// so a caller can pass through a whole settings dictionary without filtering it first.</summary>
    private static void ApplyModelOverrides(JsonArray nodes, JsonArray links, JsonObject refNode, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue("DiffusionModel", out var diffusion) && !string.IsNullOrWhiteSpace(diffusion))
        {
            var node = FindNodeByType(nodes, UnetLoaderNodeType);
            if (node is not null) node["widgets_values"]!.AsArray()[0] = diffusion;
        }

        if (overrides.TryGetValue("TextEncoder", out var textEncoder) && !string.IsNullOrWhiteSpace(textEncoder))
        {
            var node = FindNodeByType(nodes, ClipLoaderNodeType);
            if (node is not null) node["widgets_values"]!.AsArray()[0] = textEncoder;
        }

        foreach (var vae in nodes.Select(n => n!.AsObject()).Where(n => n["type"]?.GetValue<string>() == VaeLoaderNodeType).ToList())
        {
            var vaeId = (int)vae["id"]!.GetValue<double>();
            var key = FindRefNodeInputNameFedBy(refNode, links, vaeId) switch
            {
                "vae" => "VideoVae",
                "audio_vae" => "AudioVae",
                _ => null
            };
            if (key is not null && overrides.TryGetValue(key, out var filename) && !string.IsNullOrWhiteSpace(filename))
                vae["widgets_values"]!.AsArray()[0] = filename;
        }
    }

    /// <summary>Traces a source node's output link(s) to find which named input it feeds on the
    /// reference node, e.g. a VAELoader feeding refNode's "audio_vae" slot. Returns null if the
    /// source node doesn't feed the reference node directly.</summary>
    private static string? FindRefNodeInputNameFedBy(JsonObject refNode, JsonArray links, int sourceNodeId)
    {
        var refNodeId = (int)refNode["id"]!.GetValue<double>();
        var link = links.Select(l => l!.AsArray())
            .FirstOrDefault(l => (int)l[1]!.GetValue<double>() == sourceNodeId && (int)l[3]!.GetValue<double>() == refNodeId);
        if (link is null) return null;

        var slot = (int)link[4]!.GetValue<double>();
        var inputs = refNode["inputs"]!.AsArray();
        return slot < inputs.Count ? inputs[slot]!["name"]?.GetValue<string>() : null;
    }

    /// <summary>Removes whatever nodes/links currently feed the template's demo ref_image_N /
    /// ref_audio_N slots, so re-running export on the same template (or exporting for a different
    /// project) always starts from a clean slate instead of stacking leftovers.</summary>
    private static void ClearExistingRefSlots(JsonArray nodes, JsonArray links, JsonObject refNode, string namePrefix)
    {
        foreach (var input in refNode["inputs"]!.AsArray())
        {
            var name = input!["name"]?.GetValue<string>() ?? "";
            if (!name.StartsWith(namePrefix, StringComparison.Ordinal)) continue;

            var linkNode = input["link"];
            if (linkNode is null) continue;

            var linkId = linkNode.GetValue<double>();
            var linkArray = links.FirstOrDefault(l => l!.AsArray()[0]!.GetValue<double>() == linkId)?.AsArray();
            if (linkArray is null) continue;

            var originId = linkArray[1]!.GetValue<double>();
            var originNode = nodes.FirstOrDefault(n => n!.AsObject()["id"]!.GetValue<double>() == originId);
            if (originNode is not null) nodes.Remove(originNode);

            links.Remove(linkArray);
            input["link"] = null;
        }
    }

    /// <summary>Finds the input descriptor named "{group}.{item}_{index}" and returns its position
    /// in the node's inputs array (which is what links reference as a target slot), creating a new
    /// descriptor past the template's built-in slots if the project needs more than it ships with.</summary>
    private static int GetOrCreateSlotIndex(JsonObject refNode, string group, string item, int index, string type)
    {
        var inputs = refNode["inputs"]!.AsArray();
        var name = $"{group}.{item}_{index}";

        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i]!["name"]?.GetValue<string>() == name)
                return i;
        }

        inputs.Add(new JsonObject
        {
            ["label"] = $"{item}_{index}",
            ["name"] = name,
            ["shape"] = 7,
            ["type"] = type,
            ["link"] = null
        });
        return inputs.Count - 1;
    }

    private static void SetSlotLink(JsonObject refNode, string name, int linkId)
    {
        foreach (var input in refNode["inputs"]!.AsArray())
        {
            if (input!["name"]?.GetValue<string>() != name) continue;
            input["link"] = linkId;
            return;
        }
    }

    private static JsonObject BuildLoadImageNode(int id, string title, string filename, int x, int y, int outputLinkId) => new()
    {
        ["id"] = id,
        ["type"] = "LoadImage",
        ["pos"] = new JsonArray(x, y),
        ["size"] = new JsonArray(290, 330),
        ["flags"] = new JsonObject(),
        ["order"] = 0,
        ["mode"] = 0,
        ["inputs"] = new JsonArray(),
        ["outputs"] = new JsonArray(
            new JsonObject { ["name"] = "IMAGE", ["type"] = "IMAGE", ["links"] = new JsonArray(outputLinkId) },
            new JsonObject { ["name"] = "MASK", ["type"] = "MASK", ["links"] = null }
        ),
        ["title"] = title,
        ["properties"] = new JsonObject { ["Node name for S&R"] = "LoadImage" },
        ["widgets_values"] = new JsonArray(filename, "image")
    };

    private static JsonObject BuildLoadAudioNode(int id, string title, string filename, int x, int y, int outputLinkId) => new()
    {
        ["id"] = id,
        ["type"] = "LoadAudio",
        ["pos"] = new JsonArray(x, y),
        ["size"] = new JsonArray(290, 120),
        ["flags"] = new JsonObject(),
        ["order"] = 0,
        ["mode"] = 0,
        ["inputs"] = new JsonArray(),
        ["outputs"] = new JsonArray(
            new JsonObject { ["name"] = "AUDIO", ["type"] = "AUDIO", ["links"] = new JsonArray(outputLinkId) }
        ),
        ["title"] = title,
        ["properties"] = new JsonObject { ["Node name for S&R"] = "LoadAudio" },
        ["widgets_values"] = new JsonArray(filename)
    };

    /// <summary>VHS_LoadVideoPath (ComfyUI-VideoHelperSuite) is unlike the core loader nodes --
    /// its widgets_values serializes as a keyed object rather than a positional array, confirmed
    /// against a real exported workflow. Only the "video" path is meaningful here; the rest are
    /// the node's own defaults.</summary>
    private static JsonObject BuildLoadVideoNode(int id, string title, string placeholderPath, int x, int y, int outputLinkId) => new()
    {
        ["id"] = id,
        ["type"] = "VHS_LoadVideoPath",
        ["pos"] = new JsonArray(x, y),
        ["size"] = new JsonArray(240, 440),
        ["flags"] = new JsonObject(),
        ["order"] = 0,
        ["mode"] = 0,
        ["inputs"] = new JsonArray(),
        ["outputs"] = new JsonArray(
            new JsonObject { ["name"] = "IMAGE", ["type"] = "IMAGE", ["links"] = new JsonArray(outputLinkId) },
            new JsonObject { ["name"] = "frame_count", ["type"] = "INT", ["links"] = null },
            new JsonObject { ["name"] = "audio", ["type"] = "AUDIO", ["links"] = null },
            new JsonObject { ["name"] = "video_info", ["type"] = "VHS_VIDEOINFO", ["links"] = null }
        ),
        ["title"] = title,
        ["properties"] = new JsonObject { ["Node name for S&R"] = "VHS_LoadVideoPath" },
        ["widgets_values"] = new JsonObject
        {
            ["video"] = placeholderPath,
            ["force_rate"] = 0,
            ["custom_width"] = 0,
            ["custom_height"] = 0,
            ["frame_load_cap"] = 0,
            ["skip_first_frames"] = 0,
            ["select_every_nth"] = 1,
            ["format"] = "AnimateDiff"
        }
    };

    private static JsonArray BuildLink(int linkId, int originId, int originSlot, int targetId, int targetSlot, string type) =>
        new(linkId, originId, originSlot, targetId, targetSlot, type);

    private static string BuildTitle(string tagLabel, string subjectName, string detail)
    {
        var name = string.IsNullOrWhiteSpace(subjectName) ? "" : $" — {subjectName.Trim()}";
        var note = string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail.Trim()})";
        return $"<{tagLabel}>{name}{note}";
    }

    private static string BuildPlaceholderFilename(string tagLabel, string subjectName, string detail, string extension)
    {
        var parts = new[] { tagLabel, subjectName, detail }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Slugify)
            .Where(p => p.Length > 0);
        var name = string.Join("_", parts);
        return (name.Length == 0 ? "reference" : name) + extension;
    }

    private static string Slugify(string s)
    {
        var chars = s.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var cleaned = new string(chars);
        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_");
        return cleaned.Trim('_');
    }
}
