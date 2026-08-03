using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Number input, headless: click the up/down steppers (bound int nudged + clamped) and
// type digits (numeric-filtered) — verifying the two-way bound number end to end.
var form = new Form();

const string html = """
<body>
  <div class="page">
    <div class="lbl">Quantity</div>
    <cupri-number value="{{Count}}" min="0" max="10" step="1"></cupri-number>
    <div class="bound">Bound value: {{Count}}</div>
  </div>
</body>
""";
const string css = """
.page { padding:28px; background:#f4f5f7; font-family:sans-serif; }
.lbl { color:#48505c; font-size:14px; margin-bottom:8px; }
.bound { color:#1e2430; font-size:15px; margin-top:18px; }
""";

using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default()).Bind(form);
using (var _ = doc.RenderToImage(460, 180)) { }   // lay out
Snap("num-initial.png");                            // shows 3

// Click the "up" stepper (chevron-up, data-cupri-step=1) three times → 6.
for (var i = 0; i < 3; i++) ClickStep("1");
Snap("num-stepped-up.png");
var afterUp = form.Count;                            // expect 6

// Click "down" once → 5.
ClickStep("-1");
var afterDown = form.Count;                          // expect 5

// Clamp at max: step up past 10 stays 10.
for (var i = 0; i < 12; i++) ClickStep("1");
var clamped = form.Count;                            // expect 10

// Validation philosophy: type freely (even invalid), see a red border, validate on blur.
// From "10", type "5" → buffer "105" > max 10 → INVALID: red border, model keeps 10.
ClickField();
doc.DispatchKey(null, EditKey.End);
doc.DispatchKey("5", EditKey.None);                 // buffer "105" (over max) — allowed, but flagged
var invalidBorder = FieldHasInvalid();              // expect true (red border)
var modelWhileInvalid = form.Count;                 // expect 10 (last good value kept)
Snap("num-invalid.png");                            // shows "105" with a red border

// Blur (click outside the field) → validate + clamp to max.
Blur();
var afterBlur = form.Count;                         // expect 10 (105 clamped)
var borderCleared = !FieldHasInvalid();             // red border gone
Snap("num-after-blur.png");

// Unparseable buffer reverts on blur (keeps the last good value). Append a letter so we
// never pass through a valid intermediate that would live-commit.
ClickField();
doc.DispatchKey(null, EditKey.End);
doc.DispatchKey("x", EditKey.None);                 // "10x" — invalid, never parseable
var modelWhileGarbage = form.Count;                 // expect 10 (not committed)
Blur();
var afterRevert = form.Count;                       // expect 10 (unparseable → reverted to last good)

Console.WriteLine($"[CupriFace] up={afterUp} down={afterDown} clamped={clamped} " +
    $"invalidBorder={invalidBorder} modelWhileInvalid={modelWhileInvalid} afterBlur={afterBlur} " +
    $"borderCleared={borderCleared} modelWhileGarbage={modelWhileGarbage} afterRevert={afterRevert}");
var pass = afterUp == 6 && afterDown == 5 && clamped == 10
    && invalidBorder && modelWhileInvalid == 10 && afterBlur == 10 && borderCleared
    && modelWhileGarbage == 10 && afterRevert == 10;
Console.WriteLine(pass
    ? "[CupriFace] PASS: permissive typing → red border while invalid → clamp/revert on blur."
    : "[CupriFace] FAIL");
return pass ? 0 : 1;

bool FieldHasInvalid()
{
    using (var _ = doc.RenderToImage(460, 180)) { }
    return Find(doc.Root, n => n.Element?.GetAttribute("role") == "spinbutton")!
        .Element!.HasAttribute("data-invalid");
}

void Blur()
{
    using (var _ = doc.RenderToImage(460, 180)) { }
    doc.DispatchClick(8, 8); // page padding — no field there → blur + validate
}

// Lay out the current (possibly just-rebuilt) tree, then click the matching node's centre.
// A live host renders every frame, so layout is always current before the next click; the
// headless sample must lay out explicitly between clicks.
void ClickStep(string dir) => Click(n => n.Element?.GetAttribute("data-cupri-step") == dir);
void ClickField() => Click(n => n.Element?.GetAttribute("role") == "spinbutton");
void Click(Func<RenderNode, bool> match)
{
    using (var _ = doc.RenderToImage(460, 180)) { } // lay out current tree
    var n = Find(doc.Root, match)!;
    var b = HitTesting.AbsoluteBox(n);
    doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2);
}

void Snap(string name)
{
    using var image = doc.RenderToImage(460, 180, new SKColor(0xf4, 0xf5, 0xf7));
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}

static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
{
    if (match(n)) return n;
    foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
    return null;
}

sealed class Form
{
    public int Count { get; set; } = 3;
}
