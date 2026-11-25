using CupriFace.Binding;

namespace CupriFace.DevTools;

// The dev tools panel is a CupriFace document like any other, bound to these. [CupriBindable] for the
// same reason an app's models are: the generated accessors bind with no reflection.

[CupriBindable]
internal sealed partial class InspectorModel
{
    public string Status { get; set; } = "";
    public string StatusCss { get; set; } = "";
    public string PickCss { get; set; } = "";
    public string OutlinesCss { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string DockLabel { get; set; } = "Pop out";
    public string DockDisplay { get; set; } = "none";

    public List<TreeRow> Rows { get; set; } = [];
    public string TreeNote { get; set; } = "";

    public string DetailsDisplay { get; set; } = "none";
    public string EmptyDisplay { get; set; } = "flex";
    public string Selected { get; set; } = "";
    public string Classes { get; set; } = "";
    public string Box { get; set; } = "";
    public string Margin { get; set; } = "";
    public string Border { get; set; } = "";
    public string Padding { get; set; } = "";
    public string SourceText { get; set; } = "";
    public string SourceDisplay { get; set; } = "none";
    public List<NameValue> Computed { get; set; } = [];
    public List<NameValue> Bindings { get; set; } = [];
    public string BindingsDisplay { get; set; } = "none";
    public List<NameValue> Attributes { get; set; } = [];
    public string AttributesDisplay { get; set; } = "none";
    public List<RuleLine> Rules { get; set; } = [];
    public string RulesDisplay { get; set; } = "none";

    public string EditDisplay { get; set; } = "none";
    public string EditTarget { get; set; } = "";
    public string EditorDisplay { get; set; } = "none";
    public string EditorTitle { get; set; } = "";
    public string EditorText { get; set; } = "";
    public string EditError { get; set; } = "";
    public string EditErrorDisplay { get; set; } = "none";
    public string EditsDisplay { get; set; } = "none";
    public string EditsNote { get; set; } = "";
    public string UndoDisplay { get; set; } = "none";
    public string SaveDisplay { get; set; } = "none";

    public string ElementsDisplay { get; set; } = "flex";
    public string PerfDisplay { get; set; } = "none";
    public string ElementsTabCss { get; set; } = "on";
    public string PerfTabCss { get; set; } = "";
    public List<PerfBar> Bars { get; set; } = [];
    public int BudgetBottom { get; set; }
    public string ChartScale { get; set; } = "";
    public string PerfNote { get; set; } = "";
    public List<NameValue> PerfSummary { get; set; } = [];
    public string LastRebuild { get; set; } = "";
    public List<NameValue> RebuildPhases { get; set; } = [];

    public List<ProblemRow> Problems { get; set; } = [];
    public string ProblemSummary { get; set; } = "";
    public string ProblemsDisplay { get; set; } = "none";
    public string ProblemsWrapCss { get; set; } = "";
}

[CupriBindable]
internal sealed partial class TreeRow
{
    public int Index { get; set; }
    public int Indent { get; set; }
    public string Toggle { get; set; } = "";
    public string Label { get; set; } = "";
    public string Size { get; set; } = "";
    public string Flag { get; set; } = "";
    public string RowCss { get; set; } = "";
}

[CupriBindable]
internal sealed partial class PerfBar
{
    public int Height { get; set; }
    public string BarCss { get; set; } = "";
}

[CupriBindable]
internal sealed partial class NameValue
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>One line of the matched-rules list: a selector heading, or one declaration under it.</summary>
[CupriBindable]
internal sealed partial class RuleLine
{
    public string Text { get; set; } = "";
    public string Value { get; set; } = "";
    public string LineCss { get; set; } = "";
}

[CupriBindable]
internal sealed partial class ProblemRow
{
    public int Index { get; set; }
    public string Severity { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Where { get; set; } = "";
    public string ProblemCss { get; set; } = "";
}
