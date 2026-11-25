#!/usr/bin/env python3
"""Compare the minimal NativeAOT-LLVM web build against the full one (#325).

    minimal-web-build.py --full <publish dir> <ilc map.xml> <exports> \
                         --minimal <publish dir> <ilc map.xml> <exports> \
                         --fonts src/WebFonts

The full build is samples/WebLlvm (the Showcase, every control); the minimal one is
samples/WebLlvmShaders, which registers two controls and publishes with CupriWebFonts=regular,
CupriWebVideo=false and CupriWebFileDrop=false. Reports raw and compressed sizes of both, then
asserts on the PUBLISHED minimal output rather than on anything managed:

  * controls it never registered, the host's video backend and its drop reader are absent from
    ILC's map (and the two controls it does use are present, so an empty map cannot pass);
  * the bold face's bytes are absent from the wasm and the regular face's are present;
  * the page can call every export the full build has: the host's export list is the same;
  * both builds were linked for size (-Oz), which the host's targets default a Release publish to.

Sizes go to $GITHUB_STEP_SUMMARY when it is set. Exit 1 on any failed assertion.
"""
import gzip, os, re, sys, zlib

def arg_triple(flag):
    i = sys.argv.index(flag)
    return sys.argv[i + 1], sys.argv[i + 2], sys.argv[i + 3]

def wasm_in(publish_dir):
    for root, _, files in os.walk(publish_dir):
        if "dotnet.native.wasm" in files:
            return os.path.join(root, "dotnet.native.wasm")
    sys.exit(f"::error::no dotnet.native.wasm under {publish_dir}")

def brotli_size(data):
    try:
        import brotli  # not in the standard library; reported when the runner has it
        return len(brotli.compress(data, quality=11))
    except ImportError:
        return None

def method_tables(map_xml):
    text = open(map_xml, encoding="utf-8").read()
    return set(re.findall(r'<ConstructedEEType Name="__MethodTable_([^"]+)"', text)), text

def host_exports(path):
    # The host's own exports; the app's generated initializer and the runtime's entry point differ
    # by app and are not the host's to keep.
    names = {l.strip() for l in open(path, encoding="utf-8") if l.strip()}
    return {n for n in names if "GeneratedInitializer" not in n and "JavaScriptExports" not in n}

def chunk(font):
    data = open(font, "rb").read()
    mid = len(data) // 2
    return data[mid:mid + 96]

full_dir, full_map, full_exports = arg_triple("--full")
min_dir, min_map, min_exports = arg_triple("--minimal")
fonts = sys.argv[sys.argv.index("--fonts") + 1]

failures = []
rows = []
for label, d in (("full (samples/WebLlvm)", full_dir), ("minimal (samples/WebLlvmShaders)", min_dir)):
    data = open(wasm_in(d), "rb").read()
    gz = len(gzip.compress(data, compresslevel=9, mtime=0))
    br = brotli_size(data)
    rows.append((label, len(data), gz, br))

constructed, min_text = method_tables(min_map)
def kept(type_suffix):
    # The type itself, not a closure inside it: a control's static helpers can be reached from
    # the engine (VideoComponent___c is), which keeps that closure and not the control.
    return any(t.endswith("_" + type_suffix) for t in constructed)

for name in ("Components_Controls_DatePickerComponent", "Components_Controls_TimePickerComponent",
             "Components_Controls_BarChartComponent", "Components_Controls_LineChartComponent",
             "Components_Controls_MarkdownComponent", "Components_Controls_CarouselComponent",
             "Components_Controls_VideoComponent", "Web_WebVideoBackend"):
    if kept(name):
        failures.append(f"{name} is still constructed in the minimal build")
if "ReadDropRangeAsync" in min_text:
    failures.append("the host's drop reader (ReadDropRangeAsync) is still compiled in the minimal build")
for name in ("Components_Controls_SliderComponent", "Components_Controls_SwitchComponent"):
    if not kept(name):
        failures.append(f"{name} is missing from the minimal build, which uses it: the map is not the one expected")

min_wasm = open(wasm_in(min_dir), "rb").read()
full_wasm = open(wasm_in(full_dir), "rb").read()
bold, regular = chunk(os.path.join(fonts, "NotoSans-Bold.ttf")), chunk(os.path.join(fonts, "NotoSans-Regular.ttf"))
if bold in min_wasm:
    failures.append("NotoSans-Bold.ttf is still embedded in the minimal build (CupriWebFonts=regular)")
if regular not in min_wasm:
    failures.append("NotoSans-Regular.ttf is missing from the minimal build, which keeps it")
if bold not in full_wasm or regular not in full_wasm:
    failures.append("the full build does not embed both faces, so the font check above proves nothing")

# A Release publish links for size (the host's CupriWebOptimizeSize default): emcc -Oz, which also
# minifies the JS glue. A default that stopped applying still links, so look for its mark: ~16
# lines of glue, against ~10,000 with comments from the compiler's own -O1.
for label, d in (("full", full_dir), ("minimal", min_dir)):
    glue = os.path.join(os.path.dirname(wasm_in(d)), "dotnet.native.js")
    lines = sum(1 for _ in open(glue, encoding="utf-8", errors="replace"))
    if lines > 500:
        failures.append(f"the {label} build's JS glue is not minified ({lines:,} lines): the size-optimised "
                        "link (emcc -Oz, CupriWebOptimizeSize) did not apply")

missing = host_exports(full_exports) - host_exports(min_exports)
if missing:
    failures.append("exports the page calls are missing from the minimal build: " + ", ".join(sorted(missing)))

def fmt(n):
    return "n/a" if n is None else f"{n:,}"

table = ["| build | wasm | gzip -9 | brotli 11 |", "|---|---:|---:|---:|"]
table += [f"| {l} | {fmt(r)} | {fmt(g)} | {fmt(b)} |" for l, r, g, b in rows]
(fr, fg, fb), (mr, mg, mb) = [(r, g, b) for _, r, g, b in rows]
table.append(f"| difference | {fr - mr:,} | {fg - mg:,} | {'n/a' if fb is None or mb is None else f'{fb - mb:,}'} |")
report = "\n".join(["### Minimal web build (#325)", "", *table, "",
                    "Different apps, so the difference is an upper bound on what the switches remove; "
                    "TOOLBOX.md \"Minimal web builds\" has the per-switch figures for one app.", ""])
print(report)
if os.environ.get("GITHUB_STEP_SUMMARY"):
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
        f.write(report + "\n")

for f in failures:
    print(f"::error::{f}")
if failures:
    sys.exit(1)
print("minimal build: every excluded part is absent, every kept part present, every export callable")
