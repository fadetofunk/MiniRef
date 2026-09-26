namespace MiniRef.Core.Services;

/// <summary>Frame-count rules of MiniMax H3 at 24 fps, mirrored from the workflow template's own
/// "Math Expression" node and the reference node's source, so the exporter can reason about clip
/// lengths without running ComfyUI.</summary>
public static class ClipFrames
{
    public const int Fps = 24;

    /// <summary>Frames the template renders for a duration: max(5, round(seconds * 24)), padded up to
    /// the next valid length of the form 17k + 5 (Python-style modulo, like the template's expression).
    /// 12 s comes out as 294 frames (~12.25 s).</summary>
    public static int ForSeconds(double seconds)
    {
        var n = Math.Max(5, (int)Math.Round(seconds * Fps, MidpointRounding.ToEven));
        var pad = ((5 - n % 17) % 17 + 17) % 17;
        return n + pad;
    }

    /// <summary>The valid clip length (17k + 5) closest to <paramref name="frames"/> (ties go up), so a
    /// requested "3 seconds" becomes 73 frames rather than being cut back to 56.</summary>
    public static int NearestValid(int frames)
    {
        var lower = LargestValidAtMost(frames);
        if (frames < 5) return lower;
        var upper = lower + 17;
        return frames - lower < upper - frames ? lower : upper;
    }

    /// <summary>The longest clip H3 was trained on, in frames (~15.08 s); "longer is untested" per the
    /// reference node's own tooltip.</summary>
    public const int MaxTrainedFrames = 362;

    /// <summary>Frames of the previous clip pinned onto the start of a continuation: the requested
    /// seconds (3 if none given) snapped to a valid clip length, never more than the previous clip has.</summary>
    public static int PinnedTailFrames(double useLastSeconds, int previousClipFrames)
    {
        var wanted = (int)Math.Round((useLastSeconds > 0 ? useLastSeconds : 3.0) * Fps);
        return LargestValidAtMost(Math.Min(NearestValid(wanted), previousClipFrames));
    }

    /// <summary>The largest valid clip length (17k + 5, at least 5) not exceeding <paramref name="frames"/>.
    /// The reference node crops a reference video to a valid length by dropping frames off its END, so a
    /// tail must already be a valid length or its last frames -- the ones that matter -- are lost.</summary>
    public static int LargestValidAtMost(int frames)
    {
        if (frames < 5) return 5;
        var n = frames;
        while (n % 17 != 5) n--;
        return n;
    }
}
