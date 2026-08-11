namespace MiniRef.Core.Models;

/// <summary>The manifest.json shape inside a Character Pod (.mrpod) archive -- a portable,
/// reusable Subject (see Services.CharacterPodStore). Plain DTO, not ObservableObject: this is
/// a wire format, not a UI-bound model. No Id fields -- Load always mints fresh Guids for the
/// Subject/PictureRef/AudioRef it builds, so array order alone is enough to preserve identity.</summary>
public class CharacterPodManifest
{
    public int SchemaVersion { get; set; } = 1;
    public SubjectClassification Classification { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public VisualRetentionType? Retention { get; set; }
    public string RetentionNote { get; set; } = "";
    public List<PictureEntry> Pictures { get; set; } = [];
    public AudioEntry? Audio { get; set; }

    public class PictureEntry
    {
        public string Description { get; set; } = "";

        /// <summary>Filename under "files/" in the archive, or null if this picture had no file
        /// (or a file that no longer existed) at save time -- same placeholder concept
        /// ComfyWorkflowExporter uses for a picture with nothing picked yet.</summary>
        public string? ArchiveFileName { get; set; }
    }

    public class AudioEntry
    {
        public string Description { get; set; } = "";
        public AudioRetentionType? Retention { get; set; }
        public string RetentionNote { get; set; } = "";
        public string? ArchiveFileName { get; set; }
    }
}
