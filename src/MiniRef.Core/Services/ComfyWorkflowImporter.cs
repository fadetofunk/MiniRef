using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Reconstructs a SceneProject from a ComfyUI workflow's prompt text and node titles.
/// It's tuned for workflows <see cref="ComfyWorkflowExporter"/> produced -- it reads back the exact
/// literal phrasing PromptComposer writes into the "Input Text (Prompt)" node, plus the
/// &lt;Picture N&gt;/&lt;Audio N&gt;/&lt;Video N&gt; tagged LoadImage/LoadAudio/VHS_LoadVideoPath
/// node titles -- but it also does a best effort on a hand-written or AI-drafted prompt that only
/// follows the MiniMax H3 guide's own conventions: section headers written "name: ..." on one line
/// rather than "name\n...", and free-form "&lt;Subject N&gt; is ..." sentences that mention their
/// &lt;Picture k&gt; inline instead of via the canonical "whose appearance comes from" clause. Any
/// &lt;Subject N&gt; referenced anywhere in the prompt becomes a real Subject on import even when
/// subject_definitions was missing or unparseable, so nothing gets silently dropped. A handful of
/// fields simply never make it into the exported workflow at all (Subject.Name for a subject with
/// no picture/audio, Classification, and TaskTypes when Summary was left blank, since PromptComposer
/// omits the whole summary section then) -- those come back at their defaults rather than erroring,
/// since round-tripping everything else is far more useful than refusing the whole import over a few
/// UI-only fields.</summary>
public static partial class ComfyWorkflowImporter
{
    private static readonly string[] SectionNames =
    [
        "subject_definitions", "summary", "retention_analysis",
        "detailed_description", "overall_soundscape", "non_diegetic_music"
    ];

    /// <summary>Header names a hand-written or AI-drafted prompt uses for a section MiniRef knows
    /// under a different name. The MiniMax H3 ref2va guide calls the shot-by-shot block
    /// "integrated_multimodal_description" (audio and video woven together) where the plain video
    /// guide -- and PromptComposer -- call it "detailed_description"; both parse identically.</summary>
    private static readonly Dictionary<string, string> SectionAliases = new()
    {
        ["integrated_multimodal_description"] = "detailed_description",
        ["multimodal_description"] = "detailed_description",
    };

    /// <param name="comfyInputFolder">ComfyUI's "input" folder, if known -- used to resolve
    /// LoadImage/LoadAudio filenames and VHS_LoadVideoPath paths back to real files on disk.
    /// Pictures/audio/video whose file can't be found this way come back with a null FilePath,
    /// same as a freshly added reference with nothing picked yet.</param>
    public static SceneProject Import(string workflowJson, string? comfyInputFolder = null)
    {
        var root = JsonNode.Parse(workflowJson)?.AsObject()
            ?? throw new InvalidDataException("Workflow is not valid JSON.");
        var nodes = root["nodes"]?.AsArray()
            ?? throw new InvalidDataException("Workflow has no 'nodes' array -- this doesn't look like a ComfyUI workflow.");

        return BuildProject(ExtractPromptText(nodes), nodes, comfyInputFolder);
    }

    /// <summary>Builds a SceneProject straight from the six-section MiniMax H3 prompt text, with no
    /// ComfyUI workflow around it -- for pasting in a prompt drafted elsewhere. Sections may be
    /// written "name\n..." or the guide's "name: ..." one-liner. Every &lt;Subject N&gt;/&lt;Picture
    /// N&gt;/&lt;Audio N&gt;/&lt;Video N&gt; the text declares becomes a real reference (with no file
    /// attached -- there are no LoadImage/LoadAudio nodes to resolve one from), and spoken &lt;d&gt;
    /// lines in the shots come across as structured dialogue so speaker (Sx) IDs and, once a voice
    /// reference is added, the voice-timbre sentence all compose automatically.</summary>
    public static SceneProject ImportPromptText(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            throw new InvalidDataException("There's no prompt text to import.");

        return BuildProject(promptText, nodes: null, comfyInputFolder: null);
    }

    /// <param name="nodes">The ComfyUI workflow's node array, or null when importing bare prompt
    /// text. Picture/audio/video file paths, node-title character names, the resolution selector,
    /// and the duration node all come from nodes -- a null here leaves those at their defaults
    /// while everything the prompt text itself carries still comes through.</param>
    private static SceneProject BuildProject(string promptText, JsonArray? nodes, string? comfyInputFolder)
    {
        // Text pasted from a Windows control (the Import Prompt Text box especially -- a WPF TextBox
        // hands back "\r\n") arrives with CRLF line endings, which the section-header and
        // paragraph-break regexes below match on "\n\n" and would silently miss -- collapsing the
        // whole prompt into subject_definitions and dumping it into the subjects' appearance text.
        // Normalize to "\n" once here so every downstream parser sees the shape it expects.
        promptText = promptText.Replace("\r\n", "\n").Replace('\r', '\n');

        var project = new SceneProject();
        project.Subjects.Clear();

        var sections = ParseSections(promptText);

        var defs = ParseSubjectDefinitions(sections.GetValueOrDefault("subject_definitions", ""));
        foreach (var def in defs.Subjects.OrderBy(s => s.Number))
            project.Subjects.Add(new Subject { Description = def.Description });

        // Guarantee a real Subject for every <Subject N> the workflow mentions anywhere -- even when
        // subject_definitions was missing, truncated, or phrased in a way ParseSubjectDefinitions
        // couldn't read -- so the picture/audio/shot wiring below always has somewhere to land.
        for (var have = project.Subjects.Count; have < HighestReferencedNumber(promptText, "Subject"); have++)
            project.Subjects.Add(new Subject());

        var picturesFromNodes = new HashSet<int>();
        foreach (var tagged in FindTaggedNodes(nodes, "LoadImage", "Picture"))
        {
            if (!defs.PictureOwners.TryGetValue(tagged.Number, out var subjectNumber)) continue;
            if (subjectNumber < 1 || subjectNumber > project.Subjects.Count) continue;

            var subject = project.Subjects[subjectNumber - 1];
            subject.Pictures.Add(new PictureRef
            {
                Description = tagged.Detail ?? "",
                FilePath = ResolveInputFile(GetArrayWidget(tagged.Node, 0), comfyInputFolder)
            });
            picturesFromNodes.Add(tagged.Number);
            FillNameIfBlank(subject, tagged.Name);
        }

        // A <Picture N> that subject_definitions attributes to a subject but that has no LoadImage
        // node behind it (every owned picture, when importing bare prompt text) still becomes a
        // real, file-less PictureRef -- otherwise the reference would silently vanish on import.
        // No-op for a workflow this tool exported, where every owned picture has a matching node.
        foreach (var (pictureNumber, subjectNumber) in defs.PictureOwners.OrderBy(kv => kv.Key))
        {
            if (picturesFromNodes.Contains(pictureNumber)) continue;
            if (subjectNumber < 1 || subjectNumber > project.Subjects.Count) continue;
            project.Subjects[subjectNumber - 1].Pictures.Add(new PictureRef());
        }

        var audiosByNumber = new Dictionary<int, AudioRef>();
        foreach (var tagged in FindTaggedNodes(nodes, "LoadAudio", "Audio"))
        {
            if (!defs.AudioOwners.TryGetValue(tagged.Number, out var subjectNumber)) continue;
            if (subjectNumber < 1 || subjectNumber > project.Subjects.Count) continue;

            var subject = project.Subjects[subjectNumber - 1];
            var audio = new AudioRef
            {
                Description = tagged.Detail ?? "",
                FilePath = ResolveInputFile(GetArrayWidget(tagged.Node, 0), comfyInputFolder)
            };
            subject.Audios.Add(audio);
            audiosByNumber[tagged.Number] = audio;
            FillNameIfBlank(subject, tagged.Name);
        }

        // Same fallback as pictures: an <Audio N> the voice-timbre sentence attributes to a subject
        // but with no LoadAudio node behind it still becomes a real, file-less AudioRef.
        foreach (var (audioNumber, subjectNumber) in defs.AudioOwners.OrderBy(kv => kv.Key))
        {
            if (audiosByNumber.ContainsKey(audioNumber)) continue;
            if (subjectNumber < 1 || subjectNumber > project.Subjects.Count) continue;
            var audio = new AudioRef();
            project.Subjects[subjectNumber - 1].Audios.Add(audio);
            audiosByNumber[audioNumber] = audio;
        }

        foreach (var tagged in FindTaggedNodes(nodes, "VHS_LoadVideoPath", "Video").OrderBy(t => t.Number))
        {
            var path = tagged.Node["widgets_values"]?["video"]?.GetValue<string>();
            project.SourceVideos.Add(new VideoRef
            {
                Description = defs.VideoDescriptions.GetValueOrDefault(tagged.Number, tagged.Detail ?? ""),
                FilePath = ResolveVideoFile(path, comfyInputFolder)
            });
        }

        // No VHS_LoadVideoPath nodes to walk when importing bare prompt text -- take the <Video N>
        // list straight from subject_definitions instead.
        if (nodes is null)
        {
            foreach (var (_, description) in defs.VideoDescriptions.OrderBy(kv => kv.Key))
                project.SourceVideos.Add(new VideoRef { Description = description });
        }

        foreach (var (videoNumber, audioUse) in defs.VideoAudioUses)
        {
            if (videoNumber >= 1 && videoNumber <= project.SourceVideos.Count)
                project.SourceVideos[videoNumber - 1].AudioUse = audioUse;
        }

        ApplyRetention(project, sections.GetValueOrDefault("retention_analysis", ""), audiosByNumber);

        var (taskTypes, summary) = ParseSummary(sections.GetValueOrDefault("summary", ""));
        project.TaskTypes = taskTypes;
        project.Summary = summary;

        project.OverallSoundscape = sections.GetValueOrDefault("overall_soundscape", "");
        project.NonDiegeticMusic = sections.GetValueOrDefault("non_diegetic_music", "");

        var (visualStyle, shots) = ParseDetailedDescription(sections.GetValueOrDefault("detailed_description", ""), project.Subjects);
        project.VisualStyle = visualStyle;
        foreach (var shot in shots)
            project.Shots.Add(shot);

        // Resolution and duration live in workflow nodes only -- nothing in the prompt text
        // carries them, so a bare-text import just keeps SceneProject's defaults.
        if (nodes is null) return project;

        if (FindNodeByType(nodes, "ResolutionSelector") is { } resolutionNode)
        {
            var widgets = resolutionNode["widgets_values"]?.AsArray();
            if (widgets is { Count: > 0 } && widgets[0]?.GetValue<string>() is { } ratioToken
                && EnumFormatting.TryParseAspectRatioToken(ratioToken, out var ratio))
                project.AspectRatio = ratio;
            if (widgets is { Count: > 1 } && widgets[1] is { } mp)
                project.Megapixels = mp.GetValue<double>();
        }

        var durationNode = nodes.Select(n => n!.AsObject())
            .FirstOrDefault(n => n["type"]?.GetValue<string>() == "PrimitiveFloat"
                && n["title"]?.GetValue<string>() == "Float (Duration)");
        if (durationNode?["widgets_values"]?.AsArray() is { Count: > 0 } durationWidgets && durationWidgets[0] is { } dur)
            project.DurationSeconds = dur.GetValue<double>();

        // Segment 1's noise node is the first RandomNoise; "fixed" means the export pinned the seeds.
        if (FindNodeByType(nodes, "RandomNoise")?["widgets_values"]?.AsArray() is { Count: > 1 } noise
            && noise[1]?.GetValue<string>() == "fixed")
        {
            project.FixSeed = true;
            project.Seed = (long)noise[0]!.GetValue<double>();
        }

        ImportContinuationSegments(project, nodes);

        return project;
    }

    // ---- continuation segments: the "... — Segment N" prompt/duration nodes a chained export clones ----

    private static readonly Regex SegmentTitleRegex =
        new(Regex.Escape(ComfyWorkflowExporter.SegmentTitleMarker) + @"(?<n>\d+)$");

    /// <summary>Rebuilds segments 2..N of a chained workflow. Each one's prompt is its own PrimitiveStringMultiline
    /// node titled "... — Segment N", so it goes through the same section parsing as segment 1's --
    /// but only the parts that belong to a segment are kept: shots, summary, soundscape, music, duration,
    /// and how the previous clip (its &lt;Video 1&gt;) is described and its soundtrack used. The cast
    /// comes from segment 1 (it's shared), and the task types the continuation implies (video continuation,
    /// audio reuse/reference) are dropped since <see cref="SceneProject.ForSegment"/> re-adds them.</summary>
    private static void ImportContinuationSegments(SceneProject project, JsonArray nodes)
    {
        var promptNodes = nodes.Select(n => n!.AsObject())
            .Where(n => n["type"]?.GetValue<string>() == "PrimitiveStringMultiline")
            .Select(n => (Node: n, Match: SegmentTitleRegex.Match(n["title"]?.GetValue<string>() ?? "")))
            .Where(x => x.Match.Success && int.Parse(x.Match.Groups["n"].Value) >= 2)
            .Select(x => (Number: int.Parse(x.Match.Groups["n"].Value), x.Node))
            .OrderBy(x => x.Number);

        const TaskType implied = TaskType.VideoContinuation | TaskType.AudioReuse | TaskType.AudioReference;

        foreach (var (number, promptNode) in promptNodes)
        {
            var text = (GetArrayWidget(promptNode, 0) ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var sections = ParseSections(text);
            var defs = ParseSubjectDefinitions(sections.GetValueOrDefault("subject_definitions", ""));

            var segment = new SceneSegment();
            var (taskTypes, summary) = ParseSummary(sections.GetValueOrDefault("summary", ""));
            segment.TaskTypes = taskTypes & ~implied;
            segment.Summary = summary;
            segment.OverallSoundscape = sections.GetValueOrDefault("overall_soundscape", "");
            segment.NonDiegeticMusic = sections.GetValueOrDefault("non_diegetic_music", "");

            var (_, shots) = ParseDetailedDescription(sections.GetValueOrDefault("detailed_description", ""), project.Subjects);
            foreach (var shot in shots)
                segment.Shots.Add(shot);

            if (defs.VideoDescriptions.TryGetValue(1, out var description))
                segment.PreviousVideo.Description = description;
            segment.PreviousVideo.AudioUse = defs.VideoAudioUses.GetValueOrDefault(1, VideoAudioUse.None);

            // Reuse the retention parser on a throwaway project holding only this segment's <Video 1>,
            // so it can't touch the shared subjects' own retention.
            ApplyRetention(new SceneProject { Subjects = [], SourceVideos = segment.VideoList },
                sections.GetValueOrDefault("retention_analysis", ""), new Dictionary<int, AudioRef>());

            bool IsSegmentNode(JsonObject n, string type) =>
                n["type"]?.GetValue<string>() == type
                && SegmentTitleRegex.Match(n["title"]?.GetValue<string>() ?? "") is { Success: true } m
                && int.Parse(m.Groups["n"].Value) == number;

            // A pinned-ending continuation has a MiniMaxH3AddGuide titled "... - Segment N"; a reference-video
            // one doesn't. Either way the previous clip's tail comes from the trim node that COUNTS FROM THE
            // END (negative batch_index) -- the "drop the pinned frames" node after the decode counts from the start.
            var addGuide = nodes.Select(n => n!.AsObject()).FirstOrDefault(n => IsSegmentNode(n, "MiniMaxH3AddGuide"));
            var tailNode = nodes.Select(n => n!.AsObject()).FirstOrDefault(n =>
                IsSegmentNode(n, "ImageFromBatch")
                && n["widgets_values"]?.AsArray() is { Count: > 1 } w && w[0]!.GetValue<double>() < 0);
            var tailFrames = tailNode?["widgets_values"]?.AsArray() is { Count: > 1 } tailWidgets ? tailWidgets[1]!.GetValue<double>() : 0;

            segment.PreviousVideo.UseLastSeconds = tailFrames > 0 ? Math.Round(tailFrames / ClipFrames.Fps, 1) : 0;

            var guideFrames = 0.0;
            if (addGuide is not null)
            {
                segment.PreviousVideo.Handoff = PreviousClipHandoff.PinEnding;
                guideFrames = tailFrames;
                // The guide's soundtrack input is only wired when the previous clip's audio was in use.
                var audioInput = addGuide["inputs"]?.AsArray().FirstOrDefault(i => i!["name"]?.GetValue<string>() == "audio");
                segment.PreviousVideo.AudioUse = audioInput?["link"] is not null ? VideoAudioUse.Reference : VideoAudioUse.None;
            }
            else
            {
                segment.PreviousVideo.Handoff = PreviousClipHandoff.ReferenceVideo;
            }

            // The duration node holds the requested clip PLUS any pinned ending; subtract it back out.
            var durationTitle = ComfyWorkflowExporterDurationTitle + ComfyWorkflowExporter.SegmentTitleMarker + number;
            var durationNode = nodes.Select(n => n!.AsObject())
                .FirstOrDefault(n => n["type"]?.GetValue<string>() == "PrimitiveFloat"
                    && n["title"]?.GetValue<string>() == durationTitle);
            if (durationNode?["widgets_values"]?.AsArray() is { Count: > 0 } widgets && widgets[0] is { } seconds)
                segment.DurationSeconds = Math.Round(seconds.GetValue<double>() - guideFrames / ClipFrames.Fps, 2);

            project.Continuations.Add(segment);
        }

        // Pinned continuations normally have "Drop the pinned ..." nodes before their CreateVideo; without
        // them the diagnostic that keeps the pinned frames was on.
        project.KeepPinnedFrames = project.Continuations.Any(c => c.PreviousVideo.Handoff == PreviousClipHandoff.PinEnding)
            && !nodes.Any(n => n!["type"]?.GetValue<string>() == "ImageFromBatch"
                && n["title"]?.GetValue<string>()?.StartsWith("Drop the pinned", StringComparison.Ordinal) == true);

        // The joined-output pair is an extra SaveVideo titled "... - Joined"; without one, it was switched off.
        if (project.Continuations.Count > 0)
        {
            project.SaveJoinedVideo = nodes.Any(n =>
                n!["type"]?.GetValue<string>() == "SaveVideo"
                && n["title"]?.GetValue<string>()?.EndsWith(ComfyWorkflowExporter.JoinedTitleMarker) == true);

            // Clips saved on their own are SaveVideos titled "... - Segment N"; a joined-only export has none.
            project.SaveIndividualClips = !project.SaveJoinedVideo || nodes.Any(n =>
                n!["type"]?.GetValue<string>() == "SaveVideo"
                && SegmentTitleRegex.IsMatch(n["title"]?.GetValue<string>() ?? ""));
        }
    }

    private const string ComfyWorkflowExporterDurationTitle = "Float (Duration)";

    private static void FillNameIfBlank(Subject subject, string? name)
    {
        if (string.IsNullOrWhiteSpace(subject.Name) && !string.IsNullOrWhiteSpace(name))
            subject.Name = name.Trim();
    }

    private static JsonObject? FindNodeByType(JsonArray? nodes, string type) => nodes?
        .Select(n => n!.AsObject())
        .FirstOrDefault(n => n["type"]?.GetValue<string>() == type);

    /// <summary>The composed prompt normally lives in the "Input Text (Prompt)"
    /// PrimitiveStringMultiline node, but a hand-built workflow often skips that and types straight
    /// into the reference node's own "prompt" widget (widget 0) -- fall back to it.</summary>
    private static string ExtractPromptText(JsonArray nodes)
    {
        var primitive = FindNodeByType(nodes, "PrimitiveStringMultiline");
        if (primitive is not null && GetArrayWidget(primitive, 0) is { Length: > 0 } text)
            return text;

        var refNode = FindNodeByType(nodes, "MiniMaxH3ReferenceToVideo");
        return (refNode is not null ? GetArrayWidget(refNode, 0) : null) ?? "";
    }

    private static int HighestReferencedNumber(string text, string kind)
    {
        var highest = 0;
        foreach (Match m in Regex.Matches(text, $@"<{Regex.Escape(kind)} (\d+)>"))
            if (int.TryParse(m.Groups[1].Value, out var n) && n > highest)
                highest = n;
        return highest;
    }

    private static string? GetArrayWidget(JsonObject node, int index)
    {
        var arr = node["widgets_values"]?.AsArray();
        return arr is not null && index < arr.Count ? arr[index]?.GetValue<string>() : null;
    }

    private static string? ResolveInputFile(string? filename, string? comfyInputFolder)
    {
        if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(comfyInputFolder)) return null;
        var candidate = Path.Combine(comfyInputFolder, filename);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>VHS_LoadVideoPath's "video" widget is either the original absolute path (if it
    /// resolved to a real file at export time) or a placeholder bare filename -- unlike
    /// pictures/audio, source videos are never copied into ComfyUI's input folder.</summary>
    private static string? ResolveVideoFile(string? path, string? comfyInputFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path) && File.Exists(path)) return path;
        return ResolveInputFile(path, comfyInputFolder);
    }

    // ---- Node titles: "<Picture N> — Subject Name (detail)", any of the three trailing pieces optional ----

    private readonly record struct TaggedNode(JsonObject Node, int Number, string? Name, string? Detail);

    private static IEnumerable<TaggedNode> FindTaggedNodes(JsonArray? nodes, string nodeType, string tagKind)
    {
        if (nodes is null) yield break;

        foreach (var raw in nodes)
        {
            var node = raw!.AsObject();
            if (node["type"]?.GetValue<string>() != nodeType) continue;
            var title = node["title"]?.GetValue<string>();
            if (string.IsNullOrEmpty(title)) continue;

            var m = TitleRegex().Match(title);
            if (!m.Success || m.Groups["kind"].Value != tagKind) continue;

            yield return new TaggedNode(
                node,
                int.Parse(m.Groups["n"].Value),
                m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : null,
                m.Groups["detail"].Success ? m.Groups["detail"].Value.Trim() : null);
        }
    }

    [GeneratedRegex(@"^<(?<kind>Picture|Audio|Video) (?<n>\d+)>(?:\s—\s(?<name>.+?))?(?:\s\((?<detail>[^)]*)\))?$")]
    private static partial Regex TitleRegex();

    // ---- Composed prompt text: split into its six named sections ----

    private static Dictionary<string, string> ParseSections(string promptText)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(promptText)) return result;

        // Accept both the shape PromptComposer emits ("name\n<content>") and the MiniMax H3 guide's
        // own "name: <content>" one-liner shape that a hand-written or AI-drafted prompt tends to use,
        // plus any known alias header name (mapped back to its canonical section here).
        var headers = new List<(string Name, int Start, int ContentStart)>();
        foreach (var name in SectionNames.Concat(SectionAliases.Keys))
        {
            var m = Regex.Match(promptText, $@"(?:^|\n\n){Regex.Escape(name)}[ \t]*:?[ \t]*\n?");
            if (m.Success)
                headers.Add((SectionAliases.GetValueOrDefault(name, name), m.Index, m.Index + m.Length));
        }
        headers.Sort((a, b) => a.Start.CompareTo(b.Start));

        for (var i = 0; i < headers.Count; i++)
        {
            var contentEnd = i + 1 < headers.Count ? headers[i + 1].Start : promptText.Length;
            var start = headers[i].ContentStart;
            result[headers[i].Name] = promptText[start..Math.Max(start, contentEnd)].Trim();
        }

        return result;
    }

    // ---- subject_definitions: one sentence per <Subject N>/<Audio N>/<Video N> ----

    private readonly record struct SubjectDef(int Number, string Description);

    private sealed class SubjectDefinitions
    {
        public List<SubjectDef> Subjects { get; } = [];
        public Dictionary<int, int> PictureOwners { get; } = [];
        public Dictionary<int, int> AudioOwners { get; } = [];
        public Dictionary<int, string> VideoDescriptions { get; } = [];

        /// <summary>Video number -> how its own soundtrack is used, from the "&lt;Audio N&gt; is the
        /// synchronized audio track of &lt;Video M&gt;" sentence.</summary>
        public Dictionary<int, VideoAudioUse> VideoAudioUses { get; } = [];
    }

    private static SubjectDefinitions ParseSubjectDefinitions(string content)
    {
        var result = new SubjectDefinitions();
        if (string.IsNullOrWhiteSpace(content)) return result;

        foreach (var chunk in SplitIntoSentences(content))
        {
            var tagMatch = LeadingTagRegex().Match(chunk);
            if (!tagMatch.Success) continue;
            var n = int.Parse(tagMatch.Groups["n"].Value);
            var rest = chunk[tagMatch.Length..].Trim();

            switch (tagMatch.Groups["kind"].Value)
            {
                case "Subject":
                {
                    // Canonical: "<Subject N> is <desc>, whose appearance comes from <Picture a> and <Picture b>."
                    // Lenient:   "<Subject N> is <free text that mentions <Picture k> somewhere inline>".
                    // Drop a leading "is ", claim every <Picture k> the sentence names, and cut the
                    // canonical appearance clause (redundant once the pictures are linked) if present.
                    var body = SubjectIsRegex().Match(rest) is { Success: true } isMatch ? rest[isMatch.Length..] : rest;

                    foreach (Match pm in PictureTagRegex().Matches(body))
                        result.PictureOwners[int.Parse(pm.Groups["n"].Value)] = n;

                    var appearance = AppearanceClauseRegex().Match(body);
                    var description = appearance.Success ? body[..appearance.Index] : body;
                    description = StripTrailingPeriod(description.Trim()).TrimEnd(',', ';', ' ');

                    result.Subjects.Add(new SubjectDef(n, description));
                    break;
                }
                case "Audio":
                {
                    var m = AudioVoiceForRegex().Match(rest);
                    if (m.Success)
                    {
                        result.AudioOwners[n] = int.Parse(m.Groups["subj"].Value);
                        break;
                    }

                    var videoAudio = VideoAudioForRegex().Match(rest);
                    if (videoAudio.Success)
                    {
                        result.VideoAudioUses[int.Parse(videoAudio.Groups["vid"].Value)] =
                            videoAudio.Groups["rest"].Value.Contains("reused", StringComparison.OrdinalIgnoreCase)
                                ? VideoAudioUse.Reuse
                                : VideoAudioUse.Reference;
                    }
                    break;
                }
                case "Video":
                {
                    var m = VideoIsRegex().Match(rest);
                    if (m.Success)
                        result.VideoDescriptions[n] = StripTrailingPeriod(m.Groups["desc"].Value.Trim());
                    break;
                }
            }
        }

        return result;
    }

    private static string StripTrailingPeriod(string text) => text.EndsWith('.') ? text[..^1] : text;

    /// <summary>Splits subject_definitions/retention_analysis content into one piece per sentence.
    /// Only splits at a period immediately followed by a new tag -- not at every tag occurrence --
    /// since the audio sentence ("&lt;Audio N&gt; is the voice-timbre reference for &lt;Subject M&gt;
    /// (S#)...") embeds a second tag mid-sentence that must stay part of the same chunk. &lt;Picture N&gt;
    /// is a boundary too: a guide-style retention_analysis puts a "&lt;Picture N&gt; (...): level - note."
    /// line right after the subject's, and it must not get swallowed into the subject's note.</summary>
    private static IEnumerable<string> SplitIntoSentences(string content) =>
        SentenceBoundaryRegex().Split(content).Where(s => s.Length > 0);

    [GeneratedRegex(@"(?<=\.)\s+(?=<(?:Subject|Audio|Video|Picture) \d+>)")]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(@"^<(?<kind>Subject|Audio|Video) (?<n>\d+)>")]
    private static partial Regex LeadingTagRegex();

    [GeneratedRegex(@"^is\s+")]
    private static partial Regex SubjectIsRegex();

    [GeneratedRegex(@",\s*whose appearance comes from (?<pics>.+)\.\z")]
    private static partial Regex AppearanceClauseRegex();

    [GeneratedRegex(@"<Picture (?<n>\d+)>")]
    private static partial Regex PictureTagRegex();

    [GeneratedRegex(@"^is the voice-timbre reference for <Subject (?<subj>\d+)> \(S\d+\)(?:,\s*.+)?\.$")]
    private static partial Regex AudioVoiceForRegex();

    [GeneratedRegex(@"^is the synchronized audio track of <Video (?<vid>\d+)>(?<rest>.*)\.$")]
    private static partial Regex VideoAudioForRegex();

    [GeneratedRegex(@"^is (?<desc>.+)\.$")]
    private static partial Regex VideoIsRegex();

    // ---- retention_analysis: one line per <Subject N>/<Audio N>/<Video N> that has a retention set ----

    private static void ApplyRetention(SceneProject project, string content, IReadOnlyDictionary<int, AudioRef> audiosByNumber)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        foreach (var chunk in SplitIntoSentences(content))
        {
            var tagMatch = LeadingTagRegex().Match(chunk);
            if (!tagMatch.Success) continue;
            var n = int.Parse(tagMatch.Groups["n"].Value);
            var rest = chunk[tagMatch.Length..].Trim();

            switch (tagMatch.Groups["kind"].Value)
            {
                case "Subject":
                {
                    if (n < 1 || n > project.Subjects.Count) break;
                    var m = SubjectRetentionRegex().Match(rest);
                    if (!m.Success || !EnumFormatting.TryParseVisualRetentionToken(m.Groups["level"].Value, out var level)) break;
                    var subject = project.Subjects[n - 1];
                    subject.Retention = level;
                    subject.RetentionNote = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : "";
                    break;
                }
                case "Audio":
                {
                    if (!audiosByNumber.TryGetValue(n, out var audio)) break;
                    var m = LevelNoteRegex().Match(rest);
                    if (!m.Success || !EnumFormatting.TryParseAudioRetentionToken(m.Groups["level"].Value, out var level)) break;
                    audio.Retention = level;
                    audio.RetentionNote = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : "";
                    break;
                }
                case "Video":
                {
                    if (n < 1 || n > project.SourceVideos.Count) break;
                    var m = LevelNoteRegex().Match(rest);
                    if (!m.Success || !EnumFormatting.TryParseVisualRetentionToken(m.Groups["level"].Value, out var level)) break;
                    var video = project.SourceVideos[n - 1];
                    video.Retention = level;
                    video.RetentionNote = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : "";
                    break;
                }
            }
        }
    }

    [GeneratedRegex(@"^\(appears in [^)]*\):\s*(?<level>\S+)(?:\s-\s(?<note>.+))?\.$")]
    private static partial Regex SubjectRetentionRegex();

    [GeneratedRegex(@"^:\s*(?<level>\S+)(?:\s-\s(?<note>.+))?\.$")]
    private static partial Regex LevelNoteRegex();

    // ---- summary: "[task type + task type] free text", either half optional ----

    private static (TaskType TaskTypes, string Summary) ParseSummary(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (TaskType.None, "");

        var m = SummaryPrefixRegex().Match(content);
        if (!m.Success) return (TaskType.None, content.Trim());

        var types = TaskType.None;
        foreach (var token in m.Groups["types"].Value.Split(" + ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (EnumFormatting.TryParseTaskTypeToken(token, out var t))
                types |= t;
        }

        return (types, content[m.Length..].Trim());
    }

    [GeneratedRegex(@"^\[(?<types>.+?)\]\s*")]
    private static partial Regex SummaryPrefixRegex();

    // ---- detailed_description: optional visual-style sentence, then "[Shot N] At TS, text" per shot ----

    private static (VisualStyle? Style, List<Shot> Shots) ParseDetailedDescription(string content, IReadOnlyList<Subject> subjects)
    {
        var shots = new List<Shot>();
        if (string.IsNullOrWhiteSpace(content)) return (null, shots);

        VisualStyle? style = null;
        var body = content;
        var styleMatch = VisualStylePrefixRegex().Match(body);
        if (styleMatch.Success && EnumFormatting.TryParseVisualStyleToken(styleMatch.Groups["style"].Value, out var parsedStyle))
        {
            style = parsedStyle;
            body = body[styleMatch.Length..];
        }
        else if (TryScanVisualStyle(body, out var scannedStyle))
        {
            style = scannedStyle;
        }

        foreach (Match shotMatch in ShotChunkRegex().Matches(body))
        {
            foreach (var (timestamp, text) in SplitShotIntoBeats(shotMatch.Groups["body"].Value.Trim()))
            {
                var shot = new Shot { Timestamp = timestamp, Text = text };
                ExtractDialogue(shot, subjects);
                shots.Add(shot);
            }
        }

        return (style, shots);
    }

    /// <summary>A prompt sometimes writes a "single continuous shot" as one [Shot N] label followed
    /// by several blank-line-separated paragraphs, each its own timed beat ("At approximately
    /// 00:04.000, ...", "From 00:09.000 to 00:12.000, ..."). MiniRef models one Shot per beat, so
    /// those are split out here -- but only when a later paragraph actually opens with a timestamp
    /// clause. A [Shot N] whose paragraphs are just continuous prose with no timestamps stays a
    /// single shot, exactly as before, and so does a single-paragraph [Shot N].</summary>
    private static IEnumerable<(string Timestamp, string Text)> SplitShotIntoBeats(string shotBody)
    {
        var paragraphs = ParagraphBreakRegex().Split(shotBody)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var beatMatches = paragraphs.Select(p => BeatTimestampPrefixRegex().Match(p)).ToList();

        if (paragraphs.Count < 2 || !beatMatches.Skip(1).Any(m => m.Success))
        {
            // Pre-split behavior: one Shot for the whole [Shot N], narrow leading "At TS, " only.
            var text = shotBody.Trim();
            var m = TimestampPrefixRegex().Match(text);
            yield return m.Success ? (m.Groups["ts"].Value, text[m.Length..]) : ("", text);
            yield break;
        }

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var m = beatMatches[i];
            yield return m.Success
                ? (m.Groups["ts"].Value, paragraphs[i][m.Length..].Trim())
                : ("", paragraphs[i]);
        }
    }

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex ParagraphBreakRegex();

    /// <summary>The leading timestamp clause of a timed beat -- "At 00:04.000, ", "At approximately
    /// 00:04.000, ", "From 00:09.000 to 00:12.000, " -- only when it opens the paragraph. A range
    /// keeps its start time. Broader than <see cref="TimestampPrefixRegex"/> (which stays as the
    /// narrow whole-shot prefix, unchanged for existing single-shot imports).</summary>
    [GeneratedRegex(@"^(?:At|From)\s+(?:approximately\s+|around\s+|about\s+|roughly\s+)?(?<ts>\d{1,2}:\d{2}(?:[.:]\d{1,3})?)(?:\s+to\s+\d{1,2}:\d{2}(?:[.:]\d{1,3})?)?,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex BeatTimestampPrefixRegex();

    /// <summary>Pulls spoken "&lt;d&gt;[Language] ...&lt;/d&gt;" runs out of a shot's narrative into
    /// structured <see cref="DialogueLine"/>s, leaving the text itself untouched (the &lt;d&gt; run
    /// stays where the model needs it). The speaker is the &lt;Subject N&gt; tag nearest the line --
    /// the guide's phrasing always names the speaker right before "says, &lt;d&gt;..." -- falling
    /// back to the nearest one after it, and skipped entirely only when the shot names no subject
    /// at all. Structured dialogue is what drives speaker (Sx) numbering and, once the speaker has
    /// a voice reference, the auto-generated voice-timbre sentence -- so a pasted-in prompt gets
    /// both without the lines being re-entered by hand.</summary>
    private static void ExtractDialogue(Shot shot, IReadOnlyList<Subject> subjects)
    {
        if (subjects.Count == 0) return;

        foreach (Match block in DialogueBlockRegex().Matches(shot.Text))
        {
            var speakerNumber = NearestSubjectNumber(shot.Text, block.Index);
            if (speakerNumber < 1 || speakerNumber > subjects.Count) continue;

            var inner = DialogueInnerRegex().Match(block.Groups["inner"].Value);
            var text = inner.Groups["text"].Value.Trim();
            if (text.Length == 0) continue;

            shot.Dialogue.Add(new DialogueLine
            {
                SpeakerSubjectId = subjects[speakerNumber - 1].Id,
                Language = inner.Groups["lang"].Success ? inner.Groups["lang"].Value.Trim() : "English",
                Text = text
            });
        }
    }

    /// <summary>The &lt;Subject N&gt; nearest <paramref name="position"/> -- the last one starting
    /// at or before it, or failing that the first one after it. 0 when the text names none.</summary>
    private static int NearestSubjectNumber(string text, int position)
    {
        var before = 0;
        foreach (Match m in SubjectTagRegex().Matches(text))
        {
            var n = int.Parse(m.Groups["n"].Value);
            if (m.Index <= position) before = n;
            else return before != 0 ? before : n;
        }
        return before;
    }

    [GeneratedRegex(@"<d>(?<inner>.*?)</d>", RegexOptions.Singleline)]
    private static partial Regex DialogueBlockRegex();

    [GeneratedRegex(@"^\s*(?:\[(?<lang>[^\]]+)\]\s*)?(?<text>.*?)\s*$", RegexOptions.Singleline)]
    private static partial Regex DialogueInnerRegex();

    [GeneratedRegex(@"<Subject (?<n>\d+)>")]
    private static partial Regex SubjectTagRegex();

    [GeneratedRegex(@"^The target video is a (?<style>.+?) scene\.\s*")]
    private static partial Regex VisualStylePrefixRegex();

    /// <summary>Fallback for a detailed_description that doesn't open with the canonical
    /// "The target video is a X scene." -- e.g. "The target video is in a cinematic live-action
    /// style with a desaturated palette." Scans only the lead-in before the first [Shot N] for any
    /// known style token, taking the earliest-occurring one so "cinematic live-action" resolves to
    /// the word that leads.</summary>
    private static bool TryScanVisualStyle(string body, out VisualStyle style)
    {
        style = default;
        var firstShot = body.IndexOf("[Shot ", StringComparison.Ordinal);
        var leadIn = firstShot >= 0 ? body[..firstShot] : body;

        var earliest = int.MaxValue;
        foreach (var candidate in Enum.GetValues<VisualStyle>())
        {
            var at = leadIn.IndexOf(candidate.ToPromptToken(), StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at < earliest)
            {
                earliest = at;
                style = candidate;
            }
        }

        return earliest != int.MaxValue;
    }

    [GeneratedRegex(@"\[Shot \d+\]\s*(?<body>.*?)(?=\[Shot \d+\]|\z)", RegexOptions.Singleline)]
    private static partial Regex ShotChunkRegex();

    // Only strips "At TIMESTAMP, " when it looks like an actual timestamp (digits/colons/dots) --
    // narrative text that happens to start with "At ..." (e.g. "At night, ...") isn't touched.
    [GeneratedRegex(@"^At (?<ts>[\d:.]+),\s*")]
    private static partial Regex TimestampPrefixRegex();
}
