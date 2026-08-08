using System.Text.Json;
using System.Text.Json.Serialization;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

public static class ProjectStore
{
    public const string FileExtension = ".mmref.json";

    /// <summary>Shared with <see cref="SessionStore"/> and the App layer's ProjectTab so every
    /// place that (de)serializes a SceneProject -- to its own file, to the session cache, or just
    /// to compare against a last-saved snapshot for dirty-tracking -- agrees on the same shape.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(SceneProject project, string path)
    {
        var json = JsonSerializer.Serialize(project, Options);
        File.WriteAllText(path, json);
    }

    public static SceneProject Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SceneProject>(json, Options)
               ?? throw new InvalidDataException($"Could not read project file: {path}");
    }
}
