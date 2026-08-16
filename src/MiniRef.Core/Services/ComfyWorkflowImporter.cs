using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Reconstructs a SceneProject from a ComfyUI workflow that <see cref="ComfyWorkflowExporter"/>
/// produced. This only works on workflows this tool itself exported -- it reads back the exact literal
/// phrasing PromptComposer writes into the "Input Text (Prompt)" node, plus the &lt;Picture N&gt;/
/// &lt;Audio N&gt;/&lt;Video N&gt; tagged LoadImage/LoadAudio/VHS_LoadVideoPath node titles, both of
/// which are deterministic byproducts of exporting a project, not something a hand-authored or
/// third-party workflow would happen to match. A handful of fields simply never make it into the
/// exported workflow at all (Subject.Name for a subject with no picture/audio, Classification, and
/// TaskTypes when Summary was left blank, since PromptComposer omits the whole summary section then)
/// -- those come back at their defaults rather than erroring, since round-tripping everything else is
/// far more useful than refusing the whole import over a few UI-only fields.</summary>
public static partial class ComfyWorkflowImporter
{
    private static readonly string[] SectionNames =
    [
        "subject_definitions", "summary", "retention_analysis",
        "detailed_description", "overall_soundscape", "non_diegetic_music"
    ];

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

        var project = new SceneProject();
        project.Subjects.Clear();

        var promptNode = FindNodeByType(nodes, "PrimitiveStringMultiline");
        var promptText = promptNode is not null ? GetArrayWidget(promptNode, 0) ?? "" : "";
        var sections = ParseSections(promptText);

        var defs = ParseSubjectDefinitions(sections.GetValueOrDefault("subject_definitions", ""));
        foreach (var def in defs.Subjects.OrderBy(s => s.Number))
            project.Subjects.Add(new Subject { Description = def.Description });

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
            FillNameIfBlank(subject, tagged.Name);
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

        foreach (var tagged in FindTaggedNodes(nodes, "VHS_LoadVideoPath", "Video").OrderBy(t => t.Number))
        {
            var path = tagged.Node["widgets_values"]?["video"]?.GetValue<string>();
            project.SourceVideos.Add(new VideoRef
            {
                Description = defs.VideoDescriptions.GetValueOrDefault(tagged.Number, tagged.Detail ?? ""),
                FilePath = ResolveVideoFile(path, comfyInputFolder)
            });
        }

        ApplyRetention(project, sections.GetValueOrDefault("retention_analysis", ""), audiosByNumber);

        var (taskTypes, summary) = ParseSummary(sections.GetValueOrDefault("summary", ""));
        project.TaskTypes = taskTypes;
        project.Summary = summary;

        project.OverallSoundscape = sections.GetValueOrDefault("overall_soundscape", "");
        project.NonDiegeticMusic = sections.GetValueOrDefault("non_diegetic_music", "");

        var (visualStyle, shots) = ParseDetailedDescription(sections.GetValueOrDefault("detailed_description", ""));
        project.VisualStyle = visualStyle;
        foreach (var shot in shots)
            project.Shots.Add(shot);

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

        return project;
    }

    private static void FillNameIfBlank(Subject subject, string? name)
    {
        if (string.IsNullOrWhiteSpace(subject.Name) && !string.IsNullOrWhiteSpace(name))
            subject.Name = name.Trim();
    }

    private static JsonObject? FindNodeByType(JsonArray nodes, string type) => nodes
        .Select(n => n!.AsObject())
        .FirstOrDefault(n => n["type"]?.GetValue<string>() == type);

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

    private static IEnumerable<TaggedNode> FindTaggedNodes(JsonArray nodes, string nodeType, string tagKind)
    {
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

        var headers = new List<(string Name, int Start, int ContentStart)>();
        foreach (var name in SectionNames)
        {
            var m = Regex.Match(promptText, $@"(?:^|\n\n){Regex.Escape(name)}\n");
            if (m.Success)
                headers.Add((name, m.Index, m.Index + m.Length));
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
                    var isMatch = SubjectIsRegex().Match(rest);
                    if (!isMatch.Success) break;
                    var body = rest[isMatch.Length..];

                    var appearance = AppearanceClauseRegex().Match(body);
                    string description;
                    if (appearance.Success)
                    {
                        description = body[..appearance.Index].TrimEnd();
                        foreach (Match pm in PictureTagRegex().Matches(appearance.Groups["pics"].Value))
                            result.PictureOwners[int.Parse(pm.Groups["n"].Value)] = n;
                    }
                    else
                    {
                        description = StripTrailingPeriod(body.TrimEnd());
                    }

                    result.Subjects.Add(new SubjectDef(n, description));
                    break;
                }
                case "Audio":
                {
                    var m = AudioVoiceForRegex().Match(rest);
                    if (m.Success)
                        result.AudioOwners[n] = int.Parse(m.Groups["subj"].Value);
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
    /// (S#)...") embeds a second tag mid-sentence that must stay part of the same chunk.</summary>
    private static IEnumerable<string> SplitIntoSentences(string content) =>
        SentenceBoundaryRegex().Split(content).Where(s => s.Length > 0);

    [GeneratedRegex(@"(?<=\.)\s+(?=<(?:Subject|Audio|Video) \d+>)")]
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

    private static (VisualStyle? Style, List<Shot> Shots) ParseDetailedDescription(string content)
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

        foreach (Match shotMatch in ShotChunkRegex().Matches(body))
        {
            var shotText = shotMatch.Groups["body"].Value.Trim();
            var timestamp = "";

            var tsMatch = TimestampPrefixRegex().Match(shotText);
            if (tsMatch.Success)
            {
                timestamp = tsMatch.Groups["ts"].Value;
                shotText = shotText[tsMatch.Length..];
            }

            shots.Add(new Shot { Timestamp = timestamp, Text = shotText });
        }

        return (style, shots);
    }

    [GeneratedRegex(@"^The target video is a (?<style>.+?) scene\.\s*")]
    private static partial Regex VisualStylePrefixRegex();

    [GeneratedRegex(@"\[Shot \d+\]\s*(?<body>.*?)(?=\[Shot \d+\]|\z)", RegexOptions.Singleline)]
    private static partial Regex ShotChunkRegex();

    // Only strips "At TIMESTAMP, " when it looks like an actual timestamp (digits/colons/dots) --
    // narrative text that happens to start with "At ..." (e.g. "At night, ...") isn't touched.
    [GeneratedRegex(@"^At (?<ts>[\d:.]+),\s*")]
    private static partial Regex TimestampPrefixRegex();
}
