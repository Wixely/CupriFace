using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Inline form validation: rule attributes (required / pattern / minlength) drive a red border
/// mid-edit, and an injected error message once the field has been visited (blurred) or the form
/// validated. A field's own error="…" overrides the default message.</summary>
public class ValidationTests
{
    private sealed class M { public string Name { get; set; } = ""; public string Email { get; set; } = ""; }

    private const string Html =
        "<body>" +
          "<cupri-textfield value=\"{{Name}}\" required error=\"Tell us your name\"></cupri-textfield>" +
          "<cupri-textfield value=\"{{Email}}\" pattern=\"[^@ ]+@[^@ ]+\"></cupri-textfield>" +
        "</body>";

    private static RenderNode Field(TestDoc t, string key) => t.Find(n => n.Element?.GetAttribute("data-bind-value") == key)!;
    private static bool HasError(TestDoc t) => t.Find(n => n.Element?.ClassList.Contains("cupri-field-error") == true) is not null;
    private static bool ErrorSays(TestDoc t, string text) => t.Find(n => n.IsText && n.Text is { } s && s.Contains(text)) is not null;

    [Fact]
    public void An_untouched_invalid_field_shows_no_error()
    {
        using var t = new TestDoc(Html, "", new M(), width: 400, height: 300, components: true);
        Assert.False(HasError(t)); // Name is empty+required but hasn't been visited → no nagging
        Assert.False(Field(t, "Name").Element!.HasAttribute("data-invalid"));
    }

    [Fact]
    public void Visiting_a_required_field_then_leaving_shows_its_custom_error()
    {
        using var t = new TestDoc(Html, "", new M(), width: 400, height: 300, components: true);
        t.ClickNode(Field(t, "Name")); // focus the required field
        t.Click(5, 260);               // click empty space → blur it (now touched, still empty)

        Assert.True(Field(t, "Name").Element!.HasAttribute("data-invalid"));
        Assert.True(ErrorSays(t, "Tell us your name")); // the field's custom message, not the default
    }

    [Fact]
    public void ValidateAll_reveals_every_error_and_reports_validity()
    {
        var m = new M { Email = "not-an-email" };
        using var t = new TestDoc(Html, "", m, width: 400, height: 300, components: true);

        Assert.False(HasError(t));         // nothing visited yet
        var ok = t.Doc.ValidateAll();      // form submit
        t.Layout();
        Assert.False(ok);                  // Name required + Email pattern both fail
        Assert.True(ErrorSays(t, "name")); // required error
        Assert.True(ErrorSays(t, "format"));// pattern error

        m.Name = "Ada"; m.Email = "ada@example.com";
        Assert.True(t.Doc.ValidateAll());  // now valid
    }

    [Fact]
    public void A_valid_optional_pattern_field_shows_no_error()
    {
        var m = new M { Name = "Ada", Email = "ada@example.com" };
        using var t = new TestDoc(Html, "", m, width: 400, height: 300, components: true);
        t.Doc.ValidateAll();
        t.Layout();
        Assert.False(HasError(t));
    }
}
