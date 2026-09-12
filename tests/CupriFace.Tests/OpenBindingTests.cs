using AngleSharp.Dom;
using CupriFace.Demo;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A control that opens a panel — select, popover, drawer, the pickers — keeps its open state in the
/// MODEL. So <c>&lt;cupri-select value="{{V}}"&gt;</c> written without <c>open="{{Flag}}"</c> expands,
/// lays out, draws its trigger, and is dead: <c>ToggleNearestOpen</c> walks up from the clicked node
/// looking for <c>data-bind-open</c>, finds nothing, and there is nowhere to record "I am open".
///
/// <para><b>Why this needs a test rather than care.</b> The click is reported as HANDLED either way,
/// so neither the return value nor the log nor the rendered frame says anything is wrong — the
/// dropdown simply does not appear. It reached a shipped Showcase page that way and was found by a
/// person clicking it.</para>
/// </summary>
public class OpenBindingTests
{
    /// <summary>Every element carrying the engine's toggle attribute, with whether an ancestor
    /// actually binds open state — the same walk <c>CupriDocument.ToggleNearestOpen</c> performs at
    /// runtime, so this cannot drift from what clicking really does.</summary>
    private static List<(string Tag, bool Bound)> Toggles(CupriDocument doc)
    {
        var found = new List<(string, bool)>();
        IDocument? dom = null;
        doc.OnRebuilt(d => dom = d);
        doc.Refresh();
        using (doc.RenderToImage(1200, 900)) { }
        if (dom is null) return found;

        foreach (var el in dom.All)
        {
            if (!el.HasAttribute("data-cupri-toggle")) continue;

            var bound = false;
            for (var n = el; n is not null; n = n.ParentElement)
                if (n.GetAttribute("data-bind-open") is { Length: > 0 }) { bound = true; break; }

            var owner = el;
            for (var n = el; n is not null; n = n.ParentElement)
                if (n.LocalName.StartsWith("cupri-", StringComparison.OrdinalIgnoreCase)) { owner = n; break; }

            found.Add((owner.LocalName, bound));
        }
        return found;
    }

    /// <summary>The regression: the Keyboard page's plan dropdown had no open binding and could not
    /// be opened at all. Asserted across every section, since a section only expands when it is the
    /// visible one.</summary>
    [Theory]
    [InlineData("controls")]
    [InlineData("components")]
    [InlineData("overlays")]
    [InlineData("keyboard")]
    [InlineData("settings")]
    [InlineData("styling")]
    public void NoShowcaseControlIsUnopenable(string section)
    {
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        ((ShowcaseModel)app.Model!).Section = section;

        var dead = Toggles(doc).Where(t => !t.Bound).Select(t => t.Tag).Distinct().ToArray();

        Assert.True(dead.Length == 0,
            $"On '{section}' these can never open (no open=\"{{{{Flag}}}}\" binding): {string.Join(", ", dead)}");
    }

    /// <summary>The other shipped apps, for the same reason.</summary>
    [Theory]
    [InlineData("settings")]
    [InlineData("controls")]
    [InlineData("mobile")]
    public void NoShippedAppControlIsUnopenable(string which)
    {
        CupriApp app = which switch
        {
            "settings" => new SettingsApp(),
            "controls" => new ControlsApp(),
            _ => new MobileApp(),
        };
        using var doc = app.CreateDocument();

        var dead = Toggles(doc).Where(t => !t.Bound).Select(t => t.Tag).Distinct().ToArray();

        Assert.True(dead.Length == 0,
            $"In {which} these can never open: {string.Join(", ", dead)}");
    }

    /// <summary>And the behaviour itself, so the test above is known to be testing something: a
    /// select without the binding really does stay shut when clicked, and with it really does open.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ASelectOpensOnlyWhenItsOpenStateIsBound(bool bindOpen, bool expectOpen)
    {
        var open = bindOpen ? " open=\"{{O}}\"" : "";
        using var t = new TestDoc(
            $"<body><cupri-select value=\"{{{{V}}}}\"{open}>"
            + "<cupri-option value='a'>A</cupri-option><cupri-option value='b'>B</cupri-option>"
            + "</cupri-select></body>", "", new PlanModel(), components: true);

        RenderNode? trigger = null;
        void Find(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("cupri-select-trigger") == true) trigger ??= n;
            foreach (var c in n.Children) Find(c);
        }
        Find(t.Doc.Root);
        Assert.NotNull(trigger);

        t.Doc.DispatchClick(trigger!.X + trigger.Width / 2, trigger.Y + trigger.Height / 2);
        t.Layout();

        var listed = false;
        void Look(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("cupri-select-list") == true) listed = true;
            foreach (var c in n.Children) Look(c);
        }
        Look(t.Doc.Root);

        Assert.Equal(expectOpen, listed);
    }

    [CupriFace.Binding.CupriBindable]
    public sealed partial class PlanModel
    {
        public string V { get; set; } = "a";
        public bool O { get; set; }
    }
}
