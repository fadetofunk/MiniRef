using MiniRef.Core.Models;
using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

public class ReferenceNumbererTests
{
    [Fact]
    public void RewriteTagsAfterRenumbering_DeletingAPicture_ShiftsLaterPictureTagsDown()
    {
        var subject = new Subject
        {
            Pictures =
            [
                new PictureRef(), // <Picture 1>, survives
                new PictureRef(), // <Picture 2>, about to be deleted
                new PictureRef()  // <Picture 3> -> becomes <Picture 2>
            ]
        };
        var project = new SceneProject
        {
            Subjects = [subject],
            Shots = [new Shot { Text = "Close on <Picture 1>, then <Picture 3> comes into frame." }],
            Summary = "References <Picture 3> for the reveal."
        };

        var before = ReferenceNumberer.Compute(project);
        subject.Pictures.RemoveAt(1);
        var after = ReferenceNumberer.Compute(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        Assert.Equal("Close on <Picture 1>, then <Picture 2> comes into frame.", project.Shots[0].Text);
        Assert.Equal("References <Picture 2> for the reveal.", project.Summary);
    }

    [Fact]
    public void RewriteTagsAfterRenumbering_DeletingAnAudio_ShiftsLaterAudioTagsDown_IndependentlyOfPictures()
    {
        var subject = new Subject
        {
            Audios =
            [
                new AudioRef(), // <Audio 1>, about to be deleted
                new AudioRef()  // <Audio 2> -> becomes <Audio 1>
            ]
        };
        var project = new SceneProject
        {
            Subjects = [subject],
            Shots = [new Shot { Text = "<Subject 1> (S1) says, <d>[English] hi</d> while <Audio 2> plays underneath." }]
        };

        var before = ReferenceNumberer.Compute(project);
        subject.Audios.RemoveAt(0);
        var after = ReferenceNumberer.Compute(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        Assert.Equal("<Subject 1> (S1) says, <d>[English] hi</d> while <Audio 1> plays underneath.", project.Shots[0].Text);
    }

    [Fact]
    public void RewriteTagsAfterRenumbering_DeletingASubject_CascadesToThatSubjectsSurvivingSiblingsPicturesAndSubjectTags()
    {
        var first = new Subject { Pictures = [new PictureRef()] };        // <Subject 1>, <Picture 1>
        var second = new Subject { Pictures = [new PictureRef()] };       // <Subject 2>, <Picture 2> -- deleted
        var third = new Subject { Pictures = [new PictureRef()] };        // <Subject 3>, <Picture 3> -> <Subject 2>, <Picture 2>

        var project = new SceneProject
        {
            Subjects = [first, second, third],
            Shots = [new Shot { Text = "<Subject 1> stands beside <Subject 3>, framed by <Picture 3>." }]
        };

        var before = ReferenceNumberer.Compute(project);
        project.Subjects.Remove(second);
        var after = ReferenceNumberer.Compute(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        Assert.Equal("<Subject 1> stands beside <Subject 2>, framed by <Picture 2>.", project.Shots[0].Text);
    }

    [Fact]
    public void RewriteTagsAfterRenumbering_ReorderingSubjects_RewritesSubjectAndItsPictureTags()
    {
        var hero = new Subject { Pictures = [new PictureRef()] };    // starts <Subject 1>/<Picture 1>
        var sidekick = new Subject { Pictures = [new PictureRef()] }; // starts <Subject 2>/<Picture 2>

        var project = new SceneProject
        {
            Subjects = [hero, sidekick],
            Shots = [new Shot { Text = "<Subject 1> (from <Picture 1>) and <Subject 2> (from <Picture 2>) meet." }]
        };

        var before = ReferenceNumberer.Compute(project);
        project.Subjects.Move(1, 0); // sidekick now first -> becomes <Subject 1>/<Picture 1>; hero -> <Subject 2>/<Picture 2>
        var after = ReferenceNumberer.Compute(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        Assert.Equal("<Subject 2> (from <Picture 2>) and <Subject 1> (from <Picture 1>) meet.", project.Shots[0].Text);
    }

    [Fact]
    public void RewriteTagsAfterRenumbering_LeavesTheDeletedReferencesOwnDanglingTagUntouched()
    {
        var subject = new Subject { Pictures = [new PictureRef(), new PictureRef()] };
        var project = new SceneProject
        {
            Subjects = [subject],
            // References the picture that's about to be deleted (<Picture 1>) plus the survivor (<Picture 2>).
            Shots = [new Shot { Text = "<Picture 1> and <Picture 2>." }]
        };

        var before = ReferenceNumberer.Compute(project);
        subject.Pictures.RemoveAt(0);
        var after = ReferenceNumberer.Compute(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        // <Picture 2> survived and shifted down to 1; the deleted picture's own dangling
        // "<Picture 1>" text is left as-is (nothing sensible to rewrite it to) rather than being
        // confused for the survivor that now also happens to be numbered 1.
        Assert.Equal("<Picture 1> and <Picture 1>.", project.Shots[0].Text);
    }

    [Fact]
    public void RewriteTagsAfterRenumbering_NoActualRenumbering_LeavesTextUntouched()
    {
        var subject = new Subject { Pictures = [new PictureRef(), new PictureRef()] };
        var project = new SceneProject
        {
            Subjects = [subject],
            Shots = [new Shot { Text = "<Picture 1> then <Picture 2>." }]
        };

        // Deleting the LAST picture doesn't renumber any survivor.
        var before = ReferenceNumberer.Compute(project);
        subject.Pictures.RemoveAt(1);
        var after = ReferenceNumberer.Compute(project);
        ReferenceNumberer.RewriteTagsAfterRenumbering(project, before, after);

        Assert.Equal("<Picture 1> then <Picture 2>.", project.Shots[0].Text);
    }
}
