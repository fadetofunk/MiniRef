using System.Collections.ObjectModel;
using MiniRef.Core.Models;
using MiniRef.Core.Services;

namespace MiniRef.App.Views;

public record TagChip(string Label, string TagText);

/// <summary>Builds the "+ Subject / Picture / Audio / Video" insert-tag chip list, shared between
/// ShotCard and the Summary editor so both stay numbered consistently.</summary>
public static class TagChipBuilder
{
    public static List<TagChip> Build(IReadOnlyList<Subject> subjects, IReadOnlyList<VideoRef>? sourceVideos = null)
    {
        var chips = new List<TagChip>();
        var (subjectNumbers, pictureNumbers, audioNumbers) = ReferenceNumberer.NumberSubjects(
            subjects, ReferenceNumberer.CountVideoAudios(sourceVideos ?? []) + 1);

        foreach (var s in subjects)
        {
            var n = subjectNumbers[s.Id];
            var label = string.IsNullOrWhiteSpace(s.Name) ? $"Subject {n}" : s.Name;
            chips.Add(new TagChip($"+ {label}", ReferenceNumberer.SubjectTag(n)));

            foreach (var p in s.Pictures)
                chips.Add(new TagChip($"+ Picture {pictureNumbers[p.Id]}", ReferenceNumberer.PictureTag(pictureNumbers[p.Id])));

            foreach (var audio in s.Audios)
                chips.Add(new TagChip($"+ Audio {audioNumbers[audio.Id]}", ReferenceNumberer.AudioTag(audioNumbers[audio.Id])));
        }

        if (sourceVideos is not null)
        {
            var videoNumbers = ReferenceNumberer.NumberVideos(sourceVideos);
            var videoAudioNumbers = ReferenceNumberer.NumberVideoAudios(sourceVideos);
            foreach (var v in sourceVideos)
            {
                var n = videoNumbers[v.Id];
                var label = v.FromPreviousSegment ? "Previous segment"
                    : string.IsNullOrWhiteSpace(v.Description) ? $"Video {n}" : v.Description;
                chips.Add(new TagChip($"+ {label}", ReferenceNumberer.VideoTag(n)));

                if (videoAudioNumbers.TryGetValue(v.Id, out var audioNumber))
                    chips.Add(new TagChip($"+ Audio {audioNumber} (Video {n})", ReferenceNumberer.AudioTag(audioNumber)));
            }
        }

        return chips;
    }
}
