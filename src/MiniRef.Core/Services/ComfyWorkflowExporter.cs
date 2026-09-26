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

    /// <summary>Appended to the title of every node cloned for a continuation segment
    /// ("Float (Duration) — Segment 2"), which is how <see cref="ComfyWorkflowImporter"/> finds a
    /// chain's later segments again. Segment 1's own nodes keep their original titles.</summary>
    public const string SegmentTitleMarker = " — Segment ";

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
        ClearExistingRefSlots(nodes, links, refNode, "ref_video_audios.ref_video_audio_");

        var (_, pictureNumbers, audioNumbers) = ReferenceNumberer.NumberSubjects(
            project.Subjects, ReferenceNumberer.CountVideoAudios(project.SourceVideos) + 1);

        var pictures = project.Subjects
            .SelectMany(s => s.Pictures.Select(p => (Subject: s, Picture: p)))
            .Take(MaxImageSlots)
            .ToList();

        var audioRefs = project.Subjects
            .SelectMany(s => s.Audios.Select(a => (Subject: s, Audio: a)))
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
            int? audioLinkId = video.AudioUse == VideoAudioUse.None ? null : nextLinkId++;
            nodes.Add(BuildLoadVideoNode(nodeId, title, placeholderPath, baseX + i * 320, videoBaseY, linkId, audioLinkId));

            var slotIndex = GetOrCreateSlotIndex(refNode, "ref_videos", "ref_video", i, "IMAGE");
            links.Add(BuildLink(linkId, nodeId, 0, refNodeId, slotIndex, "IMAGE"));
            SetSlotLink(refNode, $"ref_videos.ref_video_{i}", linkId);

            // The video's own soundtrack rides on the loader's "audio" output into the reference
            // node's paired ref_video_audios slot, at the same index as its ref_videos slot.
            if (audioLinkId is { } audioLink)
            {
                var audioSlotIndex = GetOrCreateSlotIndex(refNode, "ref_video_audios", "ref_video_audio", i, "AUDIO");
                links.Add(BuildLink(audioLink, nodeId, 2, refNodeId, audioSlotIndex, "AUDIO"));
                SetSlotLink(refNode, $"ref_video_audios.ref_video_audio_{i}", audioLink);
            }
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

        var ids = new IdAllocator(nextNodeId, nextLinkId);
        if (project.SegmentCount > 1)
            AppendContinuationSegments(nodes, links, project, ids);

        if (modelOverrides is { Count: > 0 })
            ApplyModelOverrides(nodes, links, refNode, modelOverrides);

        root["last_node_id"] = ids.NextNodeId - 1;
        root["last_link_id"] = ids.NextLinkId - 1;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? FindNodeByType(JsonArray nodes, string type) => nodes
        .Select(n => n!.AsObject())
        .FirstOrDefault(n => n["type"]?.GetValue<string>() == type);

    private sealed class IdAllocator(int nextNodeId, int nextLinkId)
    {
        public int NextNodeId { get; private set; } = nextNodeId;
        public int NextLinkId { get; private set; } = nextLinkId;
        public int TakeNodeId() => NextNodeId++;
        public int TakeLinkId() => NextLinkId++;
    }

    /// <summary>Node types every segment shares instead of getting its own copy: the model/encoder/VAE
    /// loaders, the sampler and scheduler settings, the resolution selector, and all the cast
    /// loaders -- which is how a reference picture is set once and reused by every segment.</summary>
    private static readonly HashSet<string> SharedNodeTypes =
    [
        UnetLoaderNodeType, ClipLoaderNodeType, VaeLoaderNodeType, ResolutionSelectorNodeType,
        "KSamplerSelect", "BasicScheduler", "LoadImage", "LoadAudio", "VHS_LoadVideoPath", "MarkdownNote"
    ];

    /// <summary>Numbers read back out of the workflow JSON can be either parsed values (whole numbers
    /// that may carry a double) or ones this exporter just built as ints -- read both.</summary>
    private static int ToInt(JsonNode? node)
    {
        var value = node!.AsValue();
        return value.TryGetValue<int>(out var i) ? i : (int)value.GetValue<double>();
    }

    private static double ToDouble(JsonNode? node)
    {
        var value = node!.AsValue();
        return value.TryGetValue<double>(out var d) ? d : value.GetValue<int>();
    }

    /// <summary>Turns the single-clip graph into a chain: for every continuation segment, duplicates
    /// the per-clip sampling stack (reference node, prompt, duration, noise, guider, sampler, decode,
    /// create/save video) and feeds it the previous stack's decoded frames and audio as &lt;Video 1&gt;
    /// and its soundtrack -- entirely in memory, no file round trip -- so loading the one workflow and
    /// queueing it renders every clip back to back. The loaders, sampler/scheduler settings,
    /// resolution, and cast (pictures, voices) are shared, not copied.
    ///
    /// The stack is found by walking back from SaveVideo until a shared node type is reached, rather
    /// than by a hard-coded node list, so it follows the template if it gains or loses a node. Run
    /// after segment 1's own cast has been wired, so every clone links to those same loaders.</summary>
    private static void AppendContinuationSegments(JsonArray nodes, JsonArray links, SceneProject project, IdAllocator ids)
    {
        var saveNode = FindNodeByType(nodes, SaveVideoNodeType)
            ?? throw new InvalidDataException($"Template is missing a '{SaveVideoNodeType}' node, so there's no clip to chain from.");
        var stackIds = CollectSegmentStack(nodes, links, ToInt(saveNode["id"]));
        var stackSet = stackIds.ToHashSet();
        var byId = nodes.Select(n => n!.AsObject()).ToDictionary(n => ToInt(n["id"]));

        int RoleId(string type) => stackIds.FirstOrDefault(id => byId[id]["type"]!.GetValue<string>() == type);
        var refOriginal = RoleId(ReferenceNodeType);
        var decodeOriginal = RoleId("VAEDecode");
        var audioDecodeOriginal = RoleId("VAEDecodeAudio");
        var promptOriginal = RoleId(PromptNodeType);
        var saveOriginal = RoleId(SaveVideoNodeType);
        var durationOriginal = stackIds.FirstOrDefault(id => byId[id]["type"]!.GetValue<string>() == DurationNodeType
            && byId[id]["title"]?.GetValue<string>() == DurationNodeTitle);
        if (refOriginal == 0 || decodeOriginal == 0)
            throw new InvalidDataException("Template's clip stack has no reference node or video decode node to chain through.");

        // Segment 1 keeps its nodes as they are; just label its output so the chain reads clearly.
        RetitleAndPrefix(byId[saveOriginal], 1, "Save Video");

        // Where the clones sit: to the right of the original stack, one stack-width per segment.
        var xs = stackIds.Select(id => ToDouble(byId[id]["pos"]!.AsArray()[0])).ToList();
        var spanX = xs.Max() - xs.Min() + 700;

        var previousDecode = decodeOriginal;
        var previousAudioDecode = audioDecodeOriginal;
        var previousSeconds = project.DurationSeconds;

        // Snapshot the original stack's links once; every clone is stamped from these.
        var stackLinks = links.Select(l => l!.AsArray())
            .Where(l => stackSet.Contains(ToInt(l[3])))
            .Select(l => (Id: ToInt(l[0]), Origin: ToInt(l[1]), OriginSlot: ToInt(l[2]), Target: ToInt(l[3]), TargetSlot: ToInt(l[4]), Type: l[5]!.GetValue<string>()))
            .ToList();

        for (var k = 1; k < project.SegmentCount; k++)
        {
            var segment = project.Continuations[k - 1];
            var view = project.ForSegment(k);

            // -- clone the stack's nodes with fresh ids and blank link bookkeeping
            var idMap = new Dictionary<int, int>();
            var clones = new Dictionary<int, JsonObject>();
            foreach (var originalId in stackIds)
            {
                var clone = (JsonObject)byId[originalId].DeepClone();
                var newId = ids.TakeNodeId();
                clone["id"] = newId;
                idMap[originalId] = newId;
                clones[newId] = clone;

                foreach (var input in clone["inputs"]?.AsArray() ?? [])
                    input!["link"] = null;
                foreach (var output in clone["outputs"]?.AsArray() ?? [])
                    output!["links"] = null;

                var pos = clone["pos"]!.AsArray();
                clone["pos"] = new JsonArray(ToDouble(pos[0]) + spanX * k, ToDouble(pos[1]));

                var baseTitle = clone["title"]?.GetValue<string>() is { Length: > 0 } t ? t : clone["type"]!.GetValue<string>();
                clone["title"] = $"{baseTitle}{SegmentTitleMarker}{k + 1}";

                nodes.Add(clone);
                byId[newId] = clone;
            }

            // -- re-create every link into the stack: between clones, or from the same shared node
            foreach (var link in stackLinks)
            {
                var newLinkId = ids.TakeLinkId();
                var originIsClone = stackSet.Contains(link.Origin);
                var newOrigin = originIsClone ? idMap[link.Origin] : link.Origin;
                var newTarget = idMap[link.Target];

                links.Add(BuildLink(newLinkId, newOrigin, link.OriginSlot, newTarget, link.TargetSlot, link.Type));
                clones[newTarget]["inputs"]!.AsArray()[link.TargetSlot]!["link"] = newLinkId;
                AddOutputLink(byId[newOrigin], link.OriginSlot, newLinkId);
            }

            var cloneRef = clones[idMap[refOriginal]];
            var cloneRefId = idMap[refOriginal];

            // -- segment 1's own source videos don't belong to a continuation: its <Video 1> is the
            //    previous clip, wired below
            foreach (var input in cloneRef["inputs"]!.AsArray())
            {
                var name = input!["name"]?.GetValue<string>() ?? "";
                if (input["link"] is { } linkId
                    && (name.StartsWith("ref_videos.ref_video_", StringComparison.Ordinal)
                        || name.StartsWith("ref_video_audios.ref_video_audio_", StringComparison.Ordinal)))
                    RemoveLink(byId, links, ToInt(linkId));
            }

            // -- how much of the previous clip to hand over. The reference node keeps a reference video's
            //    FIRST frames when it's longer than the new clip and crops it to a valid length (17k + 5)
            //    by dropping frames off the END -- so anything short of "exactly a valid length no longer
            //    than the new clip" would throw away the ending, which is what a continuation needs. Take
            //    the tail ourselves instead (also far cheaper: every reference frame is extra tokens on
            //    every sampling step).
            var previousFrames = ClipFrames.ForSeconds(previousSeconds);
            var wantFrames = segment.PreviousVideo.UseLastSeconds > 0
                ? (int)Math.Round(segment.PreviousVideo.UseLastSeconds * ClipFrames.Fps)
                : previousFrames;
            var tailFrames = ClipFrames.LargestValidAtMost(
                Math.Min(ClipFrames.NearestValid(wantFrames), Math.Min(previousFrames, ClipFrames.ForSeconds(segment.DurationSeconds))));
            var tailSeconds = tailFrames / (double)ClipFrames.Fps;

            var videoSource = previousDecode;
            var audioSource = previousAudioDecode;
            var refPos = cloneRef["pos"]!.AsArray();
            var trimX = ToDouble(refPos[0]) - 380;
            var trimY = ToDouble(refPos[1]) + 900;

            if (tailFrames < previousFrames)
            {
                var frameTrim = BuildImageFromBatchNode(ids.TakeNodeId(), $"Last {tailSeconds:0.##}s of previous clip (frames){SegmentTitleMarker}{k + 1}",
                    trimX, trimY, -tailFrames, tailFrames);
                nodes.Add(frameTrim);
                var frameTrimId = ToInt(frameTrim["id"]);
                byId[frameTrimId] = frameTrim;

                var toTrimLink = ids.TakeLinkId();
                links.Add(BuildLink(toTrimLink, previousDecode, 0, frameTrimId, 0, "IMAGE"));
                frameTrim["inputs"]!.AsArray()[0]!["link"] = toTrimLink;
                AddOutputLink(byId[previousDecode], 0, toTrimLink);
                videoSource = frameTrimId;

                if (segment.PreviousVideo.AudioUse != VideoAudioUse.None && previousAudioDecode != 0)
                {
                    var audioTrim = BuildTrimAudioNode(ids.TakeNodeId(), $"Last {tailSeconds:0.##}s of previous clip (audio){SegmentTitleMarker}{k + 1}",
                        trimX, trimY + 260, -tailSeconds, tailSeconds);
                    nodes.Add(audioTrim);
                    var audioTrimId = ToInt(audioTrim["id"]);
                    byId[audioTrimId] = audioTrim;

                    var toAudioTrimLink = ids.TakeLinkId();
                    links.Add(BuildLink(toAudioTrimLink, previousAudioDecode, 0, audioTrimId, 0, "AUDIO"));
                    audioTrim["inputs"]!.AsArray()[0]!["link"] = toAudioTrimLink;
                    AddOutputLink(byId[previousAudioDecode], 0, toAudioTrimLink);
                    audioSource = audioTrimId;
                }
            }

            var videoSlot = GetOrCreateSlotIndex(cloneRef, "ref_videos", "ref_video", 0, "IMAGE");
            var videoLinkId = ids.TakeLinkId();
            links.Add(BuildLink(videoLinkId, videoSource, 0, cloneRefId, videoSlot, "IMAGE"));
            SetSlotLink(cloneRef, "ref_videos.ref_video_0", videoLinkId);
            AddOutputLink(byId[videoSource], 0, videoLinkId);

            if (segment.PreviousVideo.AudioUse != VideoAudioUse.None && audioSource != 0)
            {
                var audioSlot = GetOrCreateSlotIndex(cloneRef, "ref_video_audios", "ref_video_audio", 0, "AUDIO");
                var audioLinkId = ids.TakeLinkId();
                links.Add(BuildLink(audioLinkId, audioSource, 0, cloneRefId, audioSlot, "AUDIO"));
                SetSlotLink(cloneRef, "ref_video_audios.ref_video_audio_0", audioLinkId);
                AddOutputLink(byId[audioSource], 0, audioLinkId);
            }

            // -- this segment's own prompt, length, and output name
            if (promptOriginal != 0)
                clones[idMap[promptOriginal]]["widgets_values"]!.AsArray()[0] = PromptComposer.Compose(view);
            if (durationOriginal != 0)
                clones[idMap[durationOriginal]]["widgets_values"]!.AsArray()[0] = segment.DurationSeconds;
            if (saveOriginal != 0)
                RetitleAndPrefix(clones[idMap[saveOriginal]], k + 1, "Save Video");

            previousDecode = idMap[decodeOriginal];
            previousAudioDecode = audioDecodeOriginal == 0 ? 0 : idMap[audioDecodeOriginal];
            previousSeconds = segment.DurationSeconds;
        }
    }

    /// <summary>Core ImageFromBatch: takes <paramref name="length"/> frames starting at
    /// <paramref name="batchIndex"/>; a negative index counts from the end, so (-N, N) is the last N.</summary>
    private static JsonObject BuildImageFromBatchNode(int id, string title, double x, double y, int batchIndex, int length) => new()
    {
        ["id"] = id,
        ["type"] = "ImageFromBatch",
        ["pos"] = new JsonArray(x, y),
        ["size"] = new JsonArray(290, 80),
        ["flags"] = new JsonObject(),
        ["order"] = 0,
        ["mode"] = 0,
        ["inputs"] = new JsonArray(new JsonObject { ["name"] = "image", ["type"] = "IMAGE", ["link"] = null }),
        ["outputs"] = new JsonArray(new JsonObject { ["name"] = "IMAGE", ["type"] = "IMAGE", ["links"] = null }),
        ["title"] = title,
        ["properties"] = new JsonObject { ["Node name for S&R"] = "ImageFromBatch" },
        ["widgets_values"] = new JsonArray(batchIndex, length)
    };

    /// <summary>Core TrimAudioDuration: a negative start counts from the end, so (-S, S) is the last S seconds.</summary>
    private static JsonObject BuildTrimAudioNode(int id, string title, double x, double y, double startSeconds, double durationSeconds) => new()
    {
        ["id"] = id,
        ["type"] = "TrimAudioDuration",
        ["pos"] = new JsonArray(x, y),
        ["size"] = new JsonArray(290, 80),
        ["flags"] = new JsonObject(),
        ["order"] = 0,
        ["mode"] = 0,
        ["inputs"] = new JsonArray(new JsonObject { ["name"] = "audio", ["type"] = "AUDIO", ["link"] = null }),
        ["outputs"] = new JsonArray(new JsonObject { ["name"] = "AUDIO", ["type"] = "AUDIO", ["links"] = null }),
        ["title"] = title,
        ["properties"] = new JsonObject { ["Node name for S&R"] = "TrimAudioDuration" },
        ["widgets_values"] = new JsonArray(startSeconds, durationSeconds)
    };

    /// <summary>Names a SaveVideo after its segment, e.g. ".../ComfyUI_part2", so each clip of the
    /// chain lands in its own numbered file.</summary>
    private static void RetitleAndPrefix(JsonObject saveNode, int segmentNumber, string title)
    {
        saveNode["title"] = $"{title}{SegmentTitleMarker}{segmentNumber}";
        saveNode["widgets_values"]!.AsArray()[0] = $"{OutputFilenamePrefix}_part{segmentNumber}";
    }

    /// <summary>Every node feeding SaveVideo, found by walking links backwards and stopping at any
    /// <see cref="SharedNodeTypes"/> node -- i.e. the per-clip stack -- in the template's own order.</summary>
    private static List<int> CollectSegmentStack(JsonArray nodes, JsonArray links, int saveNodeId)
    {
        var typeById = nodes.ToDictionary(n => ToInt(n!["id"]), n => n!["type"]!.GetValue<string>());
        var linkList = links.Select(l => l!.AsArray()).ToList();

        var stack = new HashSet<int> { saveNodeId };
        var queue = new Queue<int>([saveNodeId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var link in linkList)
            {
                if (ToInt(link[3]) != id) continue;
                var origin = ToInt(link[1]);
                if (!typeById.TryGetValue(origin, out var type) || SharedNodeTypes.Contains(type)) continue;
                if (stack.Add(origin)) queue.Enqueue(origin);
            }
        }

        return nodes.Select(n => ToInt(n!["id"])).Where(stack.Contains).ToList();
    }

    private static void AddOutputLink(JsonObject node, int outputSlot, int linkId)
    {
        var output = node["outputs"]!.AsArray()[outputSlot]!.AsObject();
        if (output["links"] is not JsonArray list)
            output["links"] = list = new JsonArray();
        list.Add(linkId);
    }

    /// <summary>Deletes one link and its bookkeeping on both ends (the origin's output list and the
    /// target's input), leaving the nodes themselves in place.</summary>
    private static void RemoveLink(Dictionary<int, JsonObject> byId, JsonArray links, int linkId)
    {
        var link = links.Select(l => l!.AsArray()).FirstOrDefault(l => ToInt(l[0]) == linkId);
        if (link is null) return;

        var origin = ToInt(link[1]);
        var slot = ToInt(link[2]);
        if (byId.TryGetValue(origin, out var originNode)
            && originNode["outputs"]!.AsArray()[slot]!["links"] is JsonArray originLinks)
        {
            var entry = originLinks.FirstOrDefault(l => ToInt(l) == linkId);
            if (entry is not null) originLinks.Remove(entry);
        }

        if (byId.TryGetValue(ToInt(link[3]), out var targetNode))
            targetNode["inputs"]!.AsArray()[ToInt(link[4])]!["link"] = null;

        links.Remove(link);
    }

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
    private static JsonObject BuildLoadVideoNode(int id, string title, string placeholderPath, int x, int y, int outputLinkId, int? audioLinkId = null) => new()
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
            new JsonObject { ["name"] = "audio", ["type"] = "AUDIO", ["links"] = audioLinkId is { } id2 ? new JsonArray(id2) : null },
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
