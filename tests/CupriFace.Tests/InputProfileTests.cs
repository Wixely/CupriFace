using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The capability signal. Not a platform flag and not a media feature — the engine puts what kind
/// of input is driving the app onto the body as classes, and ordinary CSS does the rest.
/// </summary>
public class InputProfileTests
{
    private const string Html = "<body><div class='stepper'>x</div></body>";
    private const string Css = """
        body { margin:0 }
        .stepper { width:100px; height:20px; }
        .cupri-coarse .stepper { width:40px; }     /* no room for arrows a finger can't hit */
        """;

    [Fact]
    public void A_desktop_document_reads_as_fine_pointered_and_hovering()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(300, 100);

        Assert.Equal(InputProfile.Desktop, doc.InputProfile);
        Assert.Equal(100f, TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("stepper") == true)!.Width, 1);
    }

    [Fact]
    public void A_touch_profile_reaches_ordinary_css()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.InputProfile = InputProfile.Touch;
        doc.BuildFrame(300, 100);

        var stepper = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("stepper") == true)!;
        Assert.Equal(40f, stepper.Width, 1);   // the .cupri-coarse rule won, with no new CSS machinery
    }

    [Fact]
    public void Switching_profiles_restyles_without_the_app_doing_anything()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(300, 100);
        doc.InputProfile = InputProfile.Touch;
        doc.BuildFrame(300, 100);
        Assert.Equal(40f, TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("stepper") == true)!.Width, 1);

        doc.InputProfile = InputProfile.Desktop;
        doc.BuildFrame(300, 100);
        Assert.Equal(100f, TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("stepper") == true)!.Width, 1);
    }
}
