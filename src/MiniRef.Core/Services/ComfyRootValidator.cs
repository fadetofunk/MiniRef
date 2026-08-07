namespace MiniRef.Core.Services;

/// <summary>Checks whether a folder looks like a real ComfyUI install, so the Settings dialog
/// can warn before the user saves a path that will silently break every export.</summary>
public static class ComfyRootValidator
{
    public static bool LooksValid(string? rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            return false;

        return Directory.Exists(Path.Combine(rootFolder, "input"))
            && Directory.Exists(Path.Combine(rootFolder, "models"));
    }
}
