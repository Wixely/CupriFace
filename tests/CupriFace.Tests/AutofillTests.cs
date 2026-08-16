using CupriFace.Accessibility;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Password managers. Android sees our GL surface as one opaque view, so an autofill service has
/// nothing to fill unless it is handed a virtual structure — architecturally the same problem the
/// TalkBack bridge solves, with a different consumer. These pin the ENGINE half: which fields are
/// offered, and that filling one behaves exactly as typing into it would.
/// </summary>
public class AutofillTests
{
    private sealed class Login
    {
        public string User { get; set; } = "";
        public string Pass { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private const string Html = """
        <body>
          <cupri-textfield value="{{User}}" autocomplete="username" aria-label="Username"></cupri-textfield>
          <cupri-password value="{{Pass}}" autocomplete="current-password" aria-label="Password"></cupri-password>
          <cupri-textfield value="{{Note}}" aria-label="Note"></cupri-textfield>
        </body>
        """;

    private static List<AccessibilityNode> Fillable(AccessibilityNode root)
    {
        var found = new List<AccessibilityNode>();
        void Walk(AccessibilityNode n)
        {
            if (n.Role is "textbox" or "spinbutton" && n.AutofillHint is { Length: > 0 }) found.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return found;
    }

    [Fact]
    public void Only_fields_the_author_labelled_are_offered_to_a_password_manager()
    {
        using var t = new TestDoc(Html, "body{margin:0}", new Login(), components: true);
        var fields = Fillable(t.Doc.BuildAccessibilityTree(400, 300));

        // The unlabelled note field is deliberately absent: guessing that something is a password
        // field would be a security bug wearing a convenience costume.
        Assert.Equal(2, fields.Count);
        Assert.Equal(new[] { "username", "current-password" }, fields.Select(f => f.AutofillHint));
    }

    [Fact]
    public void A_filled_value_lands_on_the_model_exactly_as_typing_would()
    {
        var model = new Login();
        using var t = new TestDoc(Html, "body{margin:0}", model, components: true);
        var fields = Fillable(t.Doc.BuildAccessibilityTree(400, 300));

        Assert.True(t.Doc.AccessibilitySetText(fields[0].Path, "ada@example.com"));
        Assert.True(t.Doc.AccessibilitySetText(fields[1].Path, "hunter2"));

        Assert.Equal("ada@example.com", model.User);
        Assert.Equal("hunter2", model.Pass);
    }

    [Fact]
    public void A_fillable_field_carries_a_rectangle_a_service_can_draw_next_to()
    {
        using var t = new TestDoc(Html, "body{margin:0}", new Login(), components: true);
        var fields = Fillable(t.Doc.BuildAccessibilityTree(400, 300));

        foreach (var f in fields)
        {
            Assert.True(f.Bounds.W > 10, $"{f.AutofillHint} has no width to anchor a fill dialog to");
            Assert.True(f.Bounds.H > 10, $"{f.AutofillHint} has no height");
        }
        Assert.True(fields[1].Bounds.Y > fields[0].Bounds.Y, "the password field should sit below the username");
    }

    [Fact]
    public void Filling_something_that_is_not_a_bound_field_is_refused()
    {
        using var t = new TestDoc(Html, "body{margin:0}", new Login(), components: true);
        Assert.False(t.Doc.AccessibilitySetText("", "nope"));        // the root, not a field
        Assert.False(t.Doc.AccessibilitySetText("/9/9/9", "nope"));  // nothing at all
    }
}
