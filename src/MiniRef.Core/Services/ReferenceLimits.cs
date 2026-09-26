using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>The documented input caps of one MiniMax H3 ref2va generation: up to 9 images, 3 videos,
/// 3 standalone audio clips, and 12 files in total. Because every segment of a continuation chain is
/// its own generation carrying the whole shared cast, a segment can exceed them even when segment 1
/// doesn't -- it also takes the previous clip (and that clip's soundtrack) on top of the cast.</summary>
public static class ReferenceLimits
{
    public const int MaxImages = 9;
    public const int MaxVideos = 3;
    public const int MaxAudios = 3;
    public const int MaxTotalFiles = 12;

    /// <summary>Problems with one segment's reference inputs, as plain sentences; empty if it fits.
    /// Pass a segment view (<see cref="SceneProject.ForSegment"/>), not the whole project. A video's
    /// own soundtrack counts toward the total but not toward the standalone-audio cap.</summary>
    public static IReadOnlyList<string> Check(SceneProject segmentView)
    {
        var images = segmentView.Subjects.Sum(s => s.Pictures.Count);
        var audios = segmentView.Subjects.Sum(s => s.Audios.Count);
        var videos = segmentView.SourceVideos.Count;
        var videoAudios = segmentView.SourceVideos.Count(v => v.AudioUse != VideoAudioUse.None);
        var total = images + audios + videos + videoAudios;

        var problems = new List<string>();
        if (images > MaxImages) problems.Add($"{images} reference pictures -- the limit is {MaxImages}; the extras are left out of the export.");
        if (audios > MaxAudios) problems.Add($"{audios} voice/audio references -- the limit is {MaxAudios}; the extras are left out of the export.");
        if (videos > MaxVideos) problems.Add($"{videos} source videos -- the limit is {MaxVideos}; the extras are left out of the export.");
        if (total > MaxTotalFiles) problems.Add($"{total} reference files in total -- the limit is {MaxTotalFiles}.");
        return problems;
    }
}
