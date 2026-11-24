---
name: cupriface-ui
description: Use whenever you write, change, review or debug UI for CupriFace — HTML/CSS markup rendered by the CupriFace engine (cupri-* elements, {{bindings}}, CupriApp, CupriDocument, Assets/*.html|css in a project that references CupriFace). CupriFace is NOT a browser — there is no JavaScript and only a subset of CSS — so never preview CupriFace markup in a browser or assume web HTML/CSS will work. Covers how to see the UI as the engine draws it (`cupri preview`), the layout rules that differ from a browser, and how to verify a change.
---

# Building CupriFace UI

CupriFace renders HTML + CSS to pixels with Skia. **There is no browser, no DOM at runtime and no
JavaScript.** It looks like web development and is not, and the differences fail **silently**:

- an unsupported CSS property is ignored — no error, the style just is not there;
- an element the engine has no primitive for (`<img>`, `<video>`, `<canvas>`, `<script>`) lays out
  and stays empty;
- a `{{Binding}}` that names nothing renders as empty text;
- a box too small for its contents does not clip — the contents paint over whatever follows.

So **reasoning about the markup will not find mistakes, and a browser preview will hide them**: a
browser shows you what Chrome does with the markup, which is not what CupriFace does. Do not open
CupriFace markup in a browser, do not mock it up as a web page, and do not run it through a web
dev server to "see it". Look at it the way the engine draws it.

## The loop: preview, read the image, fix what the doctor says

```bash
cupri preview Assets/App.html --model sample.json          # App.css beside it is picked up
```

(`dotnet tool install -g CupriFace.Tool` if `cupri` is missing; in the CupriFace repository itself,
`dotnet run --project src/CupriFace.Tool -- preview …`.)

It renders the page **through the engine** at a desktop size and a phone size, writes one PNG with
both side by side, and prints what `CupriDoctor` finds, in compiler format:

```
[cupri] image: C:\src\MyApp\Assets\App.preview.png  (1508x987) — open it and look
C:\src\MyApp\Assets\App.html(14): error CF0060: {{Enviroment}} does not name anything on the model ... -> Did you mean {{Environment}}?
C:\src\MyApp\Assets\App.css(3): warning CF0050: CSS property 'float' is not supported and was ignored. -> Use flexbox
[cupri] doctor: 1 error, 1 warning (2 checks)
```

1. **Open the PNG and look at it** (your Read tool shows images). One glance settles what a
   paragraph of reasoning about flexbox cannot.
2. **Fix every error and warning the doctor printed.** Each names the line and says what to do. The
   codes are listed in the project's CLAUDE.md / TOOLBOX.md — CF0060 binding typos and CF0070/CF0075
   overflow are the quiet ones that matter most.
3. Repeat until the image looks right **and** the doctor is clean. "Looks right at one size" is not
   done: a phone is checked in the same run.

Useful options: `--model data.json` (always pass one when the markup has `{{bindings}}` — without it
they render empty and the binding checks are skipped), `--sizes desktop,phone,small-phone` or
`--sizes 1280x800`, `--outlines` (draw every box — shows overlaps), `--tree` (laid-out boxes with
coordinates: `div.panel 28,28 312x56 << CONTENT OVERFLOWS by 76px`), `--sweep` (also check every
bool flag in the model on and off — open dialogs, expanded panels), `cupri check …` for the doctor
alone (exit code 1 on errors, for CI).

The model JSON is the shape of the app's model: `{"Name": "Ada", "Items": [{"Title": "One"}]}`.
Use realistic, long-ish values — a layout that only works with "Ada" breaks on "Bartholomew".

In C# (tests, or inside an app), the same tools are `CupriDoctor.Check(html, css, CupriDoctor.DesktopAndPhone, model: model)`,
`doc.RenderToImage(w, h)`, `DeviceSheet.Render(doc, sizes)` and `doc.DumpTree(3)`.

## Rules that differ from a browser

1. **Everything is a block that fills its parent. Rows are `display:flex`.** Two `<span>`s side by
   side stack, full width each. Put things in a line with the PARENT:
   `.row { display:flex; align-items:center; gap:8px; }` — `align-items:center` is the one that
   makes items line up. For a box that shrinks to its content (button, chip, badge):
   `display:inline-flex; align-items:center`.
2. **No floats, no tables-for-layout, no `position` tricks for normal flow.** Flexbox and grid.
3. **Never draw focus or selection with `border`** — it resizes the box and shifts the row. Use
   `outline` (painted outside the box) or `box-shadow: inset …`, or recolour the engine's own ring
   with `:root { --cupri-focus: #8b5cf6; }`. `:focus` works.
4. **Pin anything whose text changes.** A button sized by its label moves its neighbours when the
   label changes: `min-width`. Numbers that tick: `font-variant-numeric: tabular-nums`.
5. **`box-sizing` is `content-box` in your CSS, `border-box` on `cupri-*` controls.** Say
   `box-sizing: border-box` yourself when you mean the outer size.
6. **A phone is not a narrow desktop.** A row of more than two things wraps (`flex-wrap:wrap`) or
   stacks under `@media (max-width: 600px)`. Columns get `flex:1; min-width:0`, never a pixel width.
   `flex: 1 1 0` in a column collapses to 0 height — set `flex:none` where a media rule stacks a row.
   `white-space:nowrap` only together with `overflow:hidden`. `@media` understands `min-/max-width`
   and `min-/max-height` in px, and nothing else.
7. **Use the `cupri-*` controls; do not rebuild them from divs.** `cupri-button`, `cupri-textfield`,
   `cupri-textarea`, `cupri-switch`, `cupri-checkbox`, `cupri-slider`, `cupri-select`, `cupri-tabs`,
   `cupri-dialog`, `cupri-icon`, `cupri-image`, `cupri-virtual`… — the full list, their attributes
   and their two-way attribute are in TOOLBOX.md §6. Images are `<cupri-image src="…">`, not `<img>`.
8. **Behaviour is C#, not script.** No `<script>`, no `onclick=`. Bind values with `{{Path}}` (an
   attribute that is entirely `{{Path}}` is two-way), repeat with `data-repeat="Items"`, show and
   hide with a bound class or `style="display:{{Flag}}"`-style bindings, and wire clicks in C# with
   `doc.OnClick(".save", e => …)` (or `CupriApp.Configure`).
9. **Theme with variables.** `--cupri-text`, `--cupri-muted`, `--cupri-surface`, `--cupri-border`,
   `--cupri-accent`. Setting `color` alone does not recolour text inputs.

When unsure whether something is supported, do not guess and do not check a browser: write it,
run `cupri preview`, and believe the doctor and the image.

## In a running app

A Debug build of a desktop app has dev tools: saving the app's `.html`/`.css` hot-reloads it into
the running window, every reload prints the doctor's findings, and **F12** docks an inspector
(element tree, picker, box model, matched rules, bindings, and editing the markup). For a change
that depends on app state, check it there too. `CUPRIFACE_DEVTOOLS=open` and
`CUPRIFACE_FRAME_DUMP=out.png` let you see it without pressing keys.

## Before you say it is done

- `cupri preview` (or `cupri check --sweep` for UI with dialogs/expanders) shows **no errors or
  warnings**, at desktop **and** phone sizes.
- You opened the PNG and it looks right at both sizes.
- You did not open the markup in a browser to decide any of the above.
