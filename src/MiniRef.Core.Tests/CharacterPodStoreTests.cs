using System.IO.Compression;
using System.Text.Json;
using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

public class CharacterPodStoreTests
{
    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "miniref-pod-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static Subject BuildFullSubject(string picture1Path, string picture2Path, string audioPath) => new()
    {
        Classification = SubjectClassification.Person,
        Name = "Sarah Connor",
        Description = "a weary survivor",
        Retention = VisualRetentionType.AttributeTransfer,
        RetentionNote = "hairstyle and color only",
        Pictures =
        [
            new PictureRef { Description = "front view", FilePath = picture1Path },
            new PictureRef { Description = "profile", FilePath = picture2Path }
        ],
        Audio = new AudioRef
        {
            Description = "calm, breathy speaking voice",
            Retention = AudioRetentionType.PartiallyCopy,
            RetentionNote = "same voice, new words",
            FilePath = audioPath
        }
    };

    [Fact]
    public void RoundTrip_PreservesAllFieldsAndFileContent()
    {
        var work = NewTempFolder();
        try
        {
            var picture1Path = Path.Combine(work, "front.jpg");
            var picture2Path = Path.Combine(work, "profile.jpg");
            var audioPath = Path.Combine(work, "voice.mp3");
            File.WriteAllBytes(picture1Path, [1, 2, 3]);
            File.WriteAllBytes(picture2Path, [4, 5, 6, 7]);
            File.WriteAllBytes(audioPath, [8, 9]);

            var original = BuildFullSubject(picture1Path, picture2Path, audioPath);
            var podPath = Path.Combine(work, "Sarah.mrpod");
            var cacheFolder = Path.Combine(work, "cache");

            CharacterPodStore.Save(original, podPath);
            var loaded = CharacterPodStore.Load(podPath, cacheFolder);

            Assert.Equal(original.Classification, loaded.Classification);
            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.Description, loaded.Description);
            Assert.Equal(original.Retention, loaded.Retention);
            Assert.Equal(original.RetentionNote, loaded.RetentionNote);

            Assert.Equal(2, loaded.Pictures.Count);
            Assert.Equal("front view", loaded.Pictures[0].Description);
            Assert.Equal("profile", loaded.Pictures[1].Description);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(loaded.Pictures[0].FilePath!));
            Assert.Equal([4, 5, 6, 7], File.ReadAllBytes(loaded.Pictures[1].FilePath!));

            Assert.NotNull(loaded.Audio);
            Assert.Equal("calm, breathy speaking voice", loaded.Audio!.Description);
            Assert.Equal(AudioRetentionType.PartiallyCopy, loaded.Audio.Retention);
            Assert.Equal("same voice, new words", loaded.Audio.RetentionNote);
            Assert.Equal([8, 9], File.ReadAllBytes(loaded.Audio.FilePath!));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Load_AssignsFreshIds()
    {
        var work = NewTempFolder();
        try
        {
            var original = BuildFullSubject(picture1Path: "", picture2Path: "", audioPath: "");
            var podPath = Path.Combine(work, "Sarah.mrpod");

            CharacterPodStore.Save(original, podPath);
            var loaded = CharacterPodStore.Load(podPath, Path.Combine(work, "cache"));

            Assert.NotEqual(original.Id, loaded.Id);
            Assert.NotEqual(original.Pictures[0].Id, loaded.Pictures[0].Id);
            Assert.NotEqual(original.Pictures[1].Id, loaded.Pictures[1].Id);
            Assert.NotEqual(original.Audio!.Id, loaded.Audio!.Id);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Save_TwoPicturesWithSameOriginalFilename_DoNotCollide()
    {
        var work = NewTempFolder();
        try
        {
            var folderA = Path.Combine(work, "a");
            var folderB = Path.Combine(work, "b");
            Directory.CreateDirectory(folderA);
            Directory.CreateDirectory(folderB);

            var pathA = Path.Combine(folderA, "photo.jpg");
            var pathB = Path.Combine(folderB, "photo.jpg");
            File.WriteAllBytes(pathA, [1, 1, 1]);
            File.WriteAllBytes(pathB, [2, 2, 2, 2]);

            var subject = new Subject
            {
                Pictures =
                [
                    new PictureRef { Description = "from folder A", FilePath = pathA },
                    new PictureRef { Description = "from folder B", FilePath = pathB }
                ]
            };
            var podPath = Path.Combine(work, "Collision.mrpod");
            var cacheFolder = Path.Combine(work, "cache");

            CharacterPodStore.Save(subject, podPath);
            var loaded = CharacterPodStore.Load(podPath, cacheFolder);

            Assert.NotEqual(loaded.Pictures[0].FilePath, loaded.Pictures[1].FilePath);
            Assert.Equal([1, 1, 1], File.ReadAllBytes(loaded.Pictures[0].FilePath!));
            Assert.Equal([2, 2, 2, 2], File.ReadAllBytes(loaded.Pictures[1].FilePath!));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Save_MissingOrUnsetFilePath_BecomesPlaceholder()
    {
        var work = NewTempFolder();
        try
        {
            var subject = new Subject
            {
                Pictures =
                [
                    new PictureRef { Description = "never picked", FilePath = null },
                    new PictureRef { Description = "deleted since", FilePath = Path.Combine(work, "gone.jpg") }
                ]
            };
            var podPath = Path.Combine(work, "Placeholder.mrpod");

            CharacterPodStore.Save(subject, podPath);
            var loaded = CharacterPodStore.Load(podPath, Path.Combine(work, "cache"));

            Assert.Null(loaded.Pictures[0].FilePath);
            Assert.Equal("never picked", loaded.Pictures[0].Description);
            Assert.Null(loaded.Pictures[1].FilePath);
            Assert.Equal("deleted since", loaded.Pictures[1].Description);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingManifest_Throws()
    {
        var work = NewTempFolder();
        try
        {
            var podPath = Path.Combine(work, "NoManifest.mrpod");
            using (var stream = new FileStream(podPath, FileMode.Create))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                zip.CreateEntry("some_other_file.txt");

            Assert.Throws<InvalidDataException>(() => CharacterPodStore.Load(podPath, Path.Combine(work, "cache")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Load_SchemaVersionNewerThanSupported_Throws()
    {
        var work = NewTempFolder();
        try
        {
            var podPath = Path.Combine(work, "FutureVersion.mrpod");
            using (var stream = new FileStream(podPath, FileMode.Create))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("manifest.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(JsonSerializer.Serialize(new CharacterPodManifest { SchemaVersion = 99, Name = "From the future" }));
            }

            Assert.Throws<InvalidDataException>(() => CharacterPodStore.Load(podPath, Path.Combine(work, "cache")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void GetCacheFolderFor_IsStablePerPathAndDistinctAcrossPaths()
    {
        var podPathA = @"C:\pods\Sarah.mrpod";
        var podPathB = @"C:\other\Sarah.mrpod"; // same base filename, different folder

        var first = CharacterPodStore.GetCacheFolderFor(podPathA);
        var second = CharacterPodStore.GetCacheFolderFor(podPathA);
        var third = CharacterPodStore.GetCacheFolderFor(podPathB);

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
    }
}
