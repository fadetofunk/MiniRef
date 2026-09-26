using MiniRef.Core.Models;

namespace MiniRef.Core.Services;

/// <summary>What one segment of a chain actually generates. <see cref="SavedSeconds"/> is the clip the
/// author asked for (their Duration setting) -- for a pinned-ending continuation the model also renders
/// <see cref="GuideFrames"/> of the previous clip's ending at the front, so <see cref="GeneratedFrames"/>
/// is longer, and the exporter drops those guide frames from what gets saved.</summary>
public sealed record SegmentPlan(int Index, double SavedSeconds, int GuideFrames, int GeneratedFrames)
{
    public double GuideSeconds => GuideFrames / (double)ClipFrames.Fps;
    public double GeneratedSeconds => GeneratedFrames / (double)ClipFrames.Fps;
}

public static class SegmentPlanner
{
    public static IReadOnlyList<SegmentPlan> Plan(SceneProject project)
    {
        var plans = new List<SegmentPlan>();
        var first = ClipFrames.ForSeconds(project.DurationSeconds);
        plans.Add(new SegmentPlan(0, project.DurationSeconds, 0, first));

        for (var k = 1; k < project.SegmentCount; k++)
        {
            var segment = project.Continuations[k - 1];
            var guide = segment.PreviousVideo.Handoff == PreviousClipHandoff.PinEnding
                ? ClipFrames.PinnedTailFrames(segment.PreviousVideo.UseLastSeconds, plans[k - 1].GeneratedFrames)
                : 0;
            var generated = ClipFrames.ForSeconds(segment.DurationSeconds + guide / (double)ClipFrames.Fps);
            plans.Add(new SegmentPlan(k, segment.DurationSeconds, guide, generated));
        }

        return plans;
    }
}
