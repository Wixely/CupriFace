using CupriFace;
using CupriFace.Binding;
using CupriFace.Components;
using CupriFace.Diagnostics;
using CupriFace.Dom;
using CupriFace.Interaction;
using Leaf.Controls;
using SkiaSharp;

// An app built on a component library it did not write. Headless: it renders the page at a desktop
// and a phone size into one PNG, clicks the library's own "+" button (behaviour that came WITH the
// package), rebuilds and clicks again, and runs the doctor — then exits non-zero if anything that
// the packaging contract promises did not hold.
//
//   dotnet run -- [output-folder]

var outDir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
Directory.CreateDirectory(outDir);

var app = new BookingApp();
using var doc = app.CreateDocument();
var model = (Booking)app.Model!;
var failures = new List<string>();

using (doc.RenderToImage(1024, 768)) { }                     // warm layout + images
using (var sheet = DeviceSheet.Render(doc, CupriDoctor.DesktopAndPhone))
using (var png = sheet.Encode(SKEncodedImageFormat.Png, 95))
using (var f = File.Create(Path.Combine(outDir, "component-library.png"))) png.SaveTo(f);

// The library's behaviour, untouched by the app: click "+" on the guests counter.
ClickIncrease("Guests");
if (model.Guests != 3) failures.Add($"after one click Guests should be 3, was {model.Guests}");

// A rebuild from scratch (a re-bind, as a model swap does) and the handler must still find the button.
doc.Bind(model);
ClickIncrease("Guests");
if (model.Guests != 4) failures.Add($"after a rebind and a click Guests should be 4, was {model.Guests}");

using (doc.RenderToImage(1024, 768)) { }
Console.WriteLine(doc.DumpTree(maxDepth: 4));

// The app's stylesheet beat the library's at equal specificity: the card border is the app's copper.
var card = Find(doc.Root, n => n.Element?.ClassList.Contains("leaf-card") == true)!;
Console.WriteLine($"[app] .leaf-card border colour: {card.Style.BorderColor}");

// The app's subclass replaced the library's status pill.
var mark = Find(doc.Root, n => n.Element?.ClassList.Contains("incident-mark") == true);
if (mark is null) failures.Add("the IncidentStatus subclass did not replace leaf-status");

var report = CupriDoctor.Check(app.Html, app.Css, CupriDoctor.DesktopAndPhone, app.Components, new Booking());
Console.WriteLine(report.IsClean ? "[doctor] clean" : report.ToString());
if (report.HasErrors) failures.Add("the doctor found errors");

Console.WriteLine($"[app] Guests = {model.Guests}; image: {Path.Combine(outDir, "component-library.png")}");
foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
return failures.Count == 0 ? 0 : 1;

void ClickIncrease(string path)
{
    using (doc.RenderToImage(1024, 768)) { }                 // a host lays out before it dispatches
    var counter = Find(doc.Root, n => n.Element?.GetAttribute("data-leaf-path") == path)!;
    var plus = Find(counter, n => n.Element?.GetAttribute("data-leaf-step") is { } s && !s.StartsWith('-'))!;
    var (x, y, w, h) = HitTesting.AbsoluteBox(plus);
    doc.DispatchClick(x + w / 2, y + h / 2);
}

static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
{
    if (match(n)) return n;
    foreach (var c in n.Children) if (Find(c, match) is { } hit) return hit;
    return null;
}

[CupriBindable]
public sealed partial class Booking
{
    public int Guests { get; set; } = 2;
    public int Nights { get; set; } = 1;
}

/// <summary>The app's own take on the library's status pill: the same expansion, plus a mark for
/// the state that needs attention. Subclass and call base — no fork, no copy of the library's code.</summary>
public sealed class IncidentStatus : StatusComponent
{
    public override string DefaultCss => base.DefaultCss + """
        .incident-mark { font-weight:bold; }
        """;

    public override void Expand(AngleSharp.Dom.IElement el)
    {
        base.Expand(el);
        if (el.ClassList.Contains("down"))
            el.InnerHtml += "<span class='incident-mark' aria-hidden='true'>!</span>";
    }
}

public sealed class BookingApp : CupriApp
{
    private readonly Booking _model = new();

    public override object? Model => _model;

    // The whole integration: the built-ins, the library, and one deliberate replacement. Replace
    // (not Register) says the override is meant; a typo in the tag would throw instead of quietly
    // adding a second control nobody uses.
    public override ComponentRegistry Components => base.Components
        .AddLeafControls()
        .Replace(new IncidentStatus());

    public override string Html => """
        <body>
          <div class="page">
            <leaf-card heading="Booking" subtitle="Table for tonight">
              <leaf-counter label="Guests" value="{{Guests}}" min="1" max="8"></leaf-counter>
              <leaf-counter label="Nights" value="{{Nights}}" min="1" max="14"></leaf-counter>
              <div class="statuses">
                <leaf-status state="ok">Kitchen open</leaf-status>
                <leaf-status state="warn">Busy</leaf-status>
                <leaf-status state="down">Terrace closed</leaf-status>
              </div>
              <div class="actions"><cupri-button class="reserve">Reserve</cupri-button></div>
            </leaf-card>
          </div>
        </body>
        """;

    // Plain CSS over the library: the token retunes every leaf control at once, and the class rule
    // restyles one. Both win over the library's own rules at equal specificity — no !important —
    // because the app's stylesheet is parsed after every component layer.
    public override string Css => """
        :root { --leaf-accent: #B87333; }
        body { margin:0; background:#f4f1ec; font-family:sans-serif; }
        .page { padding:24px; display:flex; flex-direction:column; }
        .leaf-card { border:2px #B87333; max-width:420px; }
        .statuses { display:flex; flex-wrap:wrap; align-items:center; gap:8px; }
        .actions { display:flex; justify-content:flex-end; }
        .reserve { min-width:120px; }
        """;
}
