using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

public class ComfyWorkflowImporterTests
{
    private static string LoadTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video_minimax_h3_r2v.template.json"));

    [Fact]
    public void Import_RoundTripsSubjectsPicturesAudioAndShots_ThroughAFreshExport()
    {
        var template = LoadTemplate();
        var sarah = new Subject
        {
            Name = "Sarah Connor",
            Description = "a weary survivor with a buzzcut",
            Pictures = [new PictureRef { Description = "front view" }, new PictureRef { Description = "profile" }],
            Audios = [new AudioRef { Description = "a hoarse, exhausted voice" }]
        };
        var terminator = new Subject
        {
            Name = "The Terminator",
            Description = "a relentless cyborg",
            Pictures = [new PictureRef { Description = "front view" }]
        };
        var project = new SceneProject
        {
            Subjects = [sarah, terminator],
            Shots =
            [
                new Shot { Text = "<Subject 1> and <Subject 2> face off in a burning warehouse." },
                new Shot { Timestamp = "00:03.500", Text = "<Subject 2> (S2) says, <d>[English] Come with me if you want to live.</d>" }
            ],
            VisualStyle = VisualStyle.LiveAction,
            DurationSeconds = 8.5,
            AspectRatio = WorkflowAspectRatio.PortraitWidescreen9x16,
            Megapixels = 0.7
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        Assert.Equal(2, imported.Subjects.Count);

        var importedSarah = imported.Subjects[0];
        Assert.Equal("Sarah Connor", importedSarah.Name);
        Assert.Equal("a weary survivor with a buzzcut", importedSarah.Description);
        Assert.Equal(2, importedSarah.Pictures.Count);
        Assert.Contains(importedSarah.Pictures, p => p.Description == "front view");
        Assert.Contains(importedSarah.Pictures, p => p.Description == "profile");
        Assert.Single(importedSarah.Audios);
        Assert.Equal("a hoarse, exhausted voice", importedSarah.Audios[0].Description);

        var importedTerminator = imported.Subjects[1];
        Assert.Equal("The Terminator", importedTerminator.Name);
        Assert.Equal("a relentless cyborg", importedTerminator.Description);
        Assert.Single(importedTerminator.Pictures);

        Assert.Equal(2, imported.Shots.Count);
        Assert.Equal("<Subject 1> and <Subject 2> face off in a burning warehouse.", imported.Shots[0].Text);
        Assert.Equal("", imported.Shots[0].Timestamp);
        Assert.Equal("00:03.500", imported.Shots[1].Timestamp);
        Assert.Equal("<Subject 2> (S2) says, <d>[English] Come with me if you want to live.</d>", imported.Shots[1].Text);

        Assert.Equal(VisualStyle.LiveAction, imported.VisualStyle);
        Assert.Equal(8.5, imported.DurationSeconds);
        Assert.Equal(WorkflowAspectRatio.PortraitWidescreen9x16, imported.AspectRatio);
        Assert.Equal(0.7, imported.Megapixels);
    }

    [Fact]
    public void Import_RoundTripsSourceVideosAndRetention()
    {
        var template = LoadTemplate();
        var narrator = new Subject
        {
            Name = "Narrator",
            Description = "an unseen voice",
            Retention = VisualRetentionType.PartiallyPreserved,
            RetentionNote = "keeps the voice, not the face"
        };
        var project = new SceneProject
        {
            Subjects = [narrator],
            SourceVideos =
            [
                new VideoRef
                {
                    Description = "opening rooftop pan",
                    Retention = VisualRetentionType.WeakReference,
                    RetentionNote = "cut and pacing structure only"
                }
            ],
            Shots = [new Shot { Text = "<Subject 1> watches the city below." }]
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        var subject = Assert.Single(imported.Subjects);
        Assert.Equal(VisualRetentionType.PartiallyPreserved, subject.Retention);
        Assert.Equal("keeps the voice, not the face", subject.RetentionNote);

        var video = Assert.Single(imported.SourceVideos);
        Assert.Equal("opening rooftop pan", video.Description);
        Assert.Equal(VisualRetentionType.WeakReference, video.Retention);
        Assert.Equal("cut and pacing structure only", video.RetentionNote);
    }

    [Fact]
    public void Import_RoundTripsAudioRetention()
    {
        var template = LoadTemplate();
        var subject = new Subject
        {
            Name = "Singer",
            Description = "a jazz singer",
            // PromptComposer only emits an <Audio N> retention line when the owning subject also
            // has a retention set (see BuildRetentionAnalysis's early continue) -- so this needs a
            // subject-level retention too, even though it's not itself under test here.
            Retention = VisualRetentionType.FullyPreserved,
            Audios =
            [
                new AudioRef
                {
                    Description = "smoky alto voice",
                    Retention = AudioRetentionType.PartiallyCopy,
                    RetentionNote = "same voice and pacing, new lyrics"
                }
            ]
        };
        var project = new SceneProject
        {
            Subjects = [subject],
            Shots = [new Shot { Text = "<Subject 1> steps up to the microphone." }]
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        var importedAudio = Assert.Single(imported.Subjects[0].Audios);
        Assert.Equal(AudioRetentionType.PartiallyCopy, importedAudio.Retention);
        Assert.Equal("same voice and pacing, new lyrics", importedAudio.RetentionNote);
    }

    [Fact]
    public void Import_RoundTripsMultipleAudiosOnOneSubject()
    {
        var template = LoadTemplate();
        var subject = new Subject
        {
            Name = "Singer",
            Description = "a jazz singer",
            Audios =
            [
                new AudioRef { Description = "speaking voice" },
                new AudioRef { Description = "singing voice" }
            ]
        };
        var project = new SceneProject
        {
            Subjects = [subject],
            Shots = [new Shot { Text = "<Subject 1> steps up to the microphone." }]
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        var importedSinger = Assert.Single(imported.Subjects);
        Assert.Equal(2, importedSinger.Audios.Count);
        Assert.Contains(importedSinger.Audios, a => a.Description == "speaking voice");
        Assert.Contains(importedSinger.Audios, a => a.Description == "singing voice");
    }

    [Fact]
    public void Import_RoundTripsTaskTypesAndSummary_OnlyWhenSummaryWasNonBlank()
    {
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }],
            Shots = [new Shot { Text = "<Subject 1> speaks." }],
            TaskTypes = TaskType.ReferenceGeneration | TaskType.AudioReference,
            Summary = "A confrontation unfolds."
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        Assert.Equal("A confrontation unfolds.", imported.Summary);
        Assert.True(imported.TaskTypes.HasFlag(TaskType.ReferenceGeneration));
        Assert.True(imported.TaskTypes.HasFlag(TaskType.AudioReference));
        Assert.False(imported.TaskTypes.HasFlag(TaskType.VideoEditing));
    }

    [Fact]
    public void Import_LeavesTaskTypesAtNone_WhenSummaryWasBlank()
    {
        // PromptComposer drops the whole "summary" section (and with it the "[task type]" prefix)
        // whenever Summary is blank, so there's nothing in the exported workflow to recover
        // TaskTypes from -- this documents that known, unavoidable gap rather than asserting
        // something the export format can't actually support.
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }],
            Shots = [new Shot { Text = "<Subject 1> speaks." }],
            TaskTypes = TaskType.VideoEditing
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        Assert.Equal("", imported.Summary);
        Assert.Equal(TaskType.None, imported.TaskTypes);
    }

    [Fact]
    public void Import_RoundTripsOverallSoundscapeAndNonDiegeticMusic()
    {
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Narrator", Description = "an unseen voice" }],
            Shots = [new Shot { Text = "<Subject 1> speaks." }],
            OverallSoundscape = "Steady rain taps against the windows.",
            NonDiegeticMusic = "A slow piano melody, gradually increasing in volume."
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson);

        Assert.Equal("Steady rain taps against the windows.", imported.OverallSoundscape);
        Assert.Equal("A slow piano melody, gradually increasing in volume.", imported.NonDiegeticMusic);
    }

    [Fact]
    public void Import_ResolvesPictureAndAudioFilePaths_FromTheComfyUiInputFolder()
    {
        var template = LoadTemplate();
        var subject = new Subject
        {
            Name = "Sarah",
            Description = "a survivor",
            Pictures = [new PictureRef { Description = "front view" }],
            Audios = [new AudioRef { Description = "her voice" }]
        };
        var project = new SceneProject
        {
            Subjects = [subject],
            Shots = [new Shot { Text = "<Subject 1> runs." }]
        };

        var tempInputFolder = Path.Combine(Path.GetTempPath(), "miniref-import-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempInputFolder);
        try
        {
            File.WriteAllText(Path.Combine(tempInputFolder, "sarah.png"), "fake png bytes");
            File.WriteAllText(Path.Combine(tempInputFolder, "sarah.mp3"), "fake mp3 bytes");

            var workflowJson = ComfyWorkflowExporter.Export(
                template, project,
                resolvePictureFilename: (_, _) => "sarah.png",
                resolveAudioFilename: (_, _) => "sarah.mp3");

            var imported = ComfyWorkflowImporter.Import(workflowJson, comfyInputFolder: tempInputFolder);

            Assert.Equal(Path.Combine(tempInputFolder, "sarah.png"), imported.Subjects[0].Pictures[0].FilePath);
            Assert.Equal(Path.Combine(tempInputFolder, "sarah.mp3"), imported.Subjects[0].Audios[0].FilePath);
        }
        finally
        {
            Directory.Delete(tempInputFolder, recursive: true);
        }
    }

    [Fact]
    public void Import_LeavesFilePathNull_WhenNoMatchingFileIsFound()
    {
        var template = LoadTemplate();
        var project = new SceneProject
        {
            Subjects = [new Subject { Name = "Sarah", Description = "a survivor", Pictures = [new PictureRef { Description = "front" }] }],
            Shots = [new Shot { Text = "<Subject 1> runs." }]
        };

        var workflowJson = ComfyWorkflowExporter.Export(template, project);
        var imported = ComfyWorkflowImporter.Import(workflowJson, comfyInputFolder: Path.GetTempPath());

        Assert.Null(imported.Subjects[0].Pictures[0].FilePath);
    }

    [Fact]
    public void Import_Throws_WhenGivenSomethingWithoutANodesArray()
    {
        Assert.Throws<InvalidDataException>(() => ComfyWorkflowImporter.Import("""{"foo": "bar"}"""));
    }

    [Fact]
    public void Import_ProducesAnEmptyProject_ForAWorkflowWithNoMiniRefContent()
    {
        var imported = ComfyWorkflowImporter.Import("""{"nodes": [], "links": []}""");

        Assert.Empty(imported.Subjects);
        Assert.Empty(imported.Shots);
        Assert.Empty(imported.SourceVideos);
    }
}
