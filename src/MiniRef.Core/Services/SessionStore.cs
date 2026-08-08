using System.Text.Json;
using System.Text.Json.Serialization;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>Persists which projects were open across app restarts, and a full working copy of
/// each one's current content (not just the file it was last saved to) -- so closing and reopening
/// the app resumes exactly where the user left off, including edits never explicitly saved to a
/// project file. Lives at "%AppData%\MiniRef\session\", parallel to SettingsStore's settings.json.</summary>
public static class SessionStore
{
    public record TabEntry(string CacheFileName, string? SavedFilePath);
    public record Manifest(int ActiveIndex, List<TabEntry> Tabs);

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SessionFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniRef", "session");

    private static string ManifestPath => Path.Combine(SessionFolder, "manifest.json");

    /// <summary>Writes every currently-open tab's current content to the session cache and records
    /// which one was active, replacing whatever session was saved before. Tabs no longer open (their
    /// cache file isn't in <paramref name="tabs"/> this time) have their leftover cache file deleted.</summary>
    public static void Save(IReadOnlyList<(Guid Id, SceneProject Project, string? FilePath)> tabs, int activeIndex)
    {
        Directory.CreateDirectory(SessionFolder);

        var keepFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        var entries = new List<TabEntry>();

        foreach (var (id, project, filePath) in tabs)
        {
            var cacheFileName = $"{id}.json";
            File.WriteAllText(Path.Combine(SessionFolder, cacheFileName), JsonSerializer.Serialize(project, ProjectStore.Options));
            entries.Add(new TabEntry(cacheFileName, filePath));
            keepFiles.Add(cacheFileName);
        }

        foreach (var file in Directory.EnumerateFiles(SessionFolder, "*.json"))
        {
            if (!keepFiles.Contains(Path.GetFileName(file)))
                File.Delete(file);
        }

        var manifest = new Manifest(activeIndex, entries);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, ManifestOptions));
    }

    /// <summary>Loads back whatever <see cref="Save"/> last wrote. Returns null if there's no
    /// session to restore (first run, or the session folder was cleared) or nothing in it survived
    /// (every cache file went missing/corrupt) -- callers should fall back to a single fresh project
    /// in either case. Corrupt individual tab entries are skipped rather than failing the whole load,
    /// so one bad cache file doesn't strand every other open project.</summary>
    public static List<(SceneProject Project, string? FilePath)>? Load(out int activeIndex)
    {
        activeIndex = 0;
        if (!File.Exists(ManifestPath)) return null;

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), ManifestOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null || manifest.Tabs.Count == 0) return null;

        var result = new List<(SceneProject, string?)>();
        foreach (var entry in manifest.Tabs)
        {
            var cachePath = Path.Combine(SessionFolder, entry.CacheFileName);
            if (!File.Exists(cachePath)) continue;

            try
            {
                var project = JsonSerializer.Deserialize<SceneProject>(File.ReadAllText(cachePath), ProjectStore.Options);
                if (project is not null) result.Add((project, entry.SavedFilePath));
            }
            catch (JsonException)
            {
                // Skip this one tab; the rest of the session is still worth restoring.
            }
        }

        if (result.Count == 0) return null;

        activeIndex = Math.Clamp(manifest.ActiveIndex, 0, result.Count - 1);
        return result;
    }
}
