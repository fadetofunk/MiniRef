using System.Text.Json;
using System.Text.Json.Serialization;
using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

public static class ProjectStore
{
    public const string FileExtension = ".mmref.json";

    private static readonly JsonSerializerOptions Options = new()
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
