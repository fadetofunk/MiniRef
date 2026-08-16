using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Saves/loads a Character Pod -- a single Subject (with its pictures/audio) packaged as
/// a standalone, portable ".mrpod" file so it can be reused across projects instead of being
/// redefined from scratch every time. The archive is a plain zip (no external 7-Zip dependency);
/// only the extension is custom. Layout: "manifest.json" at the root, referenced files under
/// "files/".</summary>
public static class CharacterPodStore
{
    public const string FileExtension = ".mrpod";
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Writes (or overwrites) <paramref name="subject"/> to <paramref name="podFilePath"/>.
    /// A picture/audio reference with no FilePath, or one pointing at a file that no longer exists,
    /// is recorded with a null ArchiveFileName and its description preserved -- the same placeholder
    /// concept ComfyWorkflowExporter already uses for a reference with nothing picked yet -- rather
    /// than failing the whole save.</summary>
    public static void Save(Subject subject, string podFilePath)
    {
        var directory = Path.GetDirectoryName(podFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(podFilePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new CharacterPodManifest
        {
            SchemaVersion = CurrentSchemaVersion,
            Classification = subject.Classification,
            Name = subject.Name,
            Description = subject.Description,
            Retention = subject.Retention,
            RetentionNote = subject.RetentionNote
        };

        foreach (var picture in subject.Pictures)
        {
            manifest.Pictures.Add(new CharacterPodManifest.PictureEntry
            {
                Description = picture.Description,
                ArchiveFileName = AddFileEntry(zip, picture.FilePath, picture.Id)
            });
        }

        foreach (var audio in subject.Audios)
        {
            manifest.Audios.Add(new CharacterPodManifest.AudioEntry
            {
                Description = audio.Description,
                Retention = audio.Retention,
                RetentionNote = audio.RetentionNote,
                ArchiveFileName = AddFileEntry(zip, audio.FilePath, audio.Id)
            });
        }

        var manifestEntry = zip.CreateEntry("manifest.json");
        using var manifestStream = manifestEntry.Open();
        using var writer = new StreamWriter(manifestStream, Encoding.UTF8);
        writer.Write(JsonSerializer.Serialize(manifest, Options));
    }

    /// <summary>Archive/cache filenames are the reference's own full Guid ("N" format) plus its
    /// original extension -- not derived from the original filename at all -- so two pictures that
    /// happen to share a source filename from different folders can never collide. (This is the
    /// same class of bug recently fixed in MainViewModel.BuildComfyInputFileName for ComfyUI export;
    /// using the reference's own unique Id sidesteps it entirely instead of partially mitigating it.)</summary>
    private static string? AddFileEntry(ZipArchive zip, string? filePath, Guid referenceId)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        var archiveFileName = referenceId.ToString("N") + Path.GetExtension(filePath);
        zip.CreateEntryFromFile(filePath, "files/" + archiveFileName);
        return archiveFileName;
    }

    /// <summary>Reads <paramref name="podFilePath"/> and builds a fresh Subject from it -- a new
    /// Guid for the Subject and every PictureRef/AudioRef (via their own field initializers), with
    /// FilePath pointing at copies extracted into <paramref name="cacheFolder"/> (default:
    /// <see cref="GetCacheFolderFor"/>). The cache folder is fully overwritten by every Load rather
    /// than reused/diffed -- simple and correct, and cheap enough given pods hold a handful of small
    /// reference files.</summary>
    public static Subject Load(string podFilePath, string? cacheFolder = null)
    {
        cacheFolder ??= GetCacheFolderFor(podFilePath);

        using var stream = new FileStream(podFilePath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException($"'{Path.GetFileName(podFilePath)}' isn't a Character Pod -- no manifest.json found inside it.");

        CharacterPodManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<CharacterPodManifest>(manifestStream, Options)
                ?? throw new InvalidDataException($"'{Path.GetFileName(podFilePath)}' has an unreadable manifest.json.");
        }

        if (manifest.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"'{Path.GetFileName(podFilePath)}' was saved by a newer version of MiniRef (schema {manifest.SchemaVersion}) " +
                $"and can't be opened by this version (schema {CurrentSchemaVersion}).");

        Directory.CreateDirectory(cacheFolder);
        foreach (var existing in Directory.EnumerateFiles(cacheFolder))
            File.Delete(existing);

        var subject = new Subject
        {
            Classification = manifest.Classification,
            Name = manifest.Name,
            Description = manifest.Description,
            Retention = manifest.Retention,
            RetentionNote = manifest.RetentionNote
        };

        foreach (var pictureEntry in manifest.Pictures)
        {
            subject.Pictures.Add(new PictureRef
            {
                Description = pictureEntry.Description,
                FilePath = ExtractFileEntry(zip, pictureEntry.ArchiveFileName, cacheFolder, podFilePath)
            });
        }

        foreach (var audioEntry in manifest.Audios)
        {
            subject.Audios.Add(new AudioRef
            {
                Description = audioEntry.Description,
                Retention = audioEntry.Retention,
                RetentionNote = audioEntry.RetentionNote,
                FilePath = ExtractFileEntry(zip, audioEntry.ArchiveFileName, cacheFolder, podFilePath)
            });
        }

        return subject;
    }

    private static string? ExtractFileEntry(ZipArchive zip, string? archiveFileName, string cacheFolder, string podFilePath)
    {
        if (archiveFileName is null) return null;

        var entry = zip.GetEntry("files/" + archiveFileName)
            ?? throw new InvalidDataException(
                $"'{Path.GetFileName(podFilePath)}' is missing a file its manifest references ('{archiveFileName}') -- the pod may be corrupt.");

        var targetPath = Path.Combine(cacheFolder, archiveFileName);
        entry.ExtractToFile(targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>Where a pod's picture/audio files get extracted to on Load -- a shared, app-local
    /// cache (parallel to SessionStore's "%AppData%\MiniRef\session\") rather than per-project, so
    /// loading the same pod into multiple projects doesn't duplicate files. Deterministic from the
    /// pod file's own full path alone: the same pod file always maps to the same folder (stable
    /// reuse across repeated loads), while two different pod files that happen to share a display
    /// name still land in distinct folders via the hash suffix.</summary>
    public static string GetCacheFolderFor(string podFilePath)
    {
        var slug = SanitizeFileName(Path.GetFileNameWithoutExtension(podFilePath));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(podFilePath).ToUpperInvariant())))[..8];

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniRef", "pods-cache", $"{slug}-{hash}");
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Character Pod" : cleaned;
    }
}
