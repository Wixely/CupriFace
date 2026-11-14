using CupriFace;
using CupriFace.Binding;
using SkiaSharp;

namespace CupriFace.Samples.Shaders;

/// <summary>
/// Four shaders, one for each way a shader can be used — and all four declared in the stylesheet,
/// so there is not a line of C# between the SkSL and the pixels.
///
/// <list type="bullet">
/// <item><b>The banner</b> is a <c>background-image</c>, written the way Shadertoy writes one
/// (<c>mainImage</c>, y up, <c>iTime</c>): the shape a shader pasted from there arrives in.</item>
/// <item><b>The wobbling card</b> is a <c>filter</c>. The shader is handed the card — text, border,
/// button and all — as <c>iChannel0</c>, and decides where each pixel comes from. The slider under
/// it is bound straight into the declaration: <c>shader(wave, amount {{Amount}})</c>.</item>
/// <item><b>The spotlight</b> reads <c>iMouse</c>. It asks for no frames while the pointer is
/// still, and repaints only its own box when it moves.</item>
/// <item><b>The switch</b> puts a class on <c>body</c>, whose <c>filter</c> covers the whole page:
/// scanlines, a colour split and a vignette over everything, the focus ring included.</item>
/// </list>
/// </summary>
public sealed class ShadersApp : CupriApp
{
    public override string Title => "CupriFace — shaders";
    public override int Width => 960;
    public override int Height => 640;
    public override SKColor Background => new(0x0E, 0x10, 0x16);
    public override object? Model => _model;

    private readonly ShaderModel _model;
    public ShadersApp(bool crt = false) => _model = new ShaderModel { Crt = crt };

    // Every host reports what it draws with; this page shows it, because whether a shader is cheap
    // depends almost entirely on the answer (on the CPU, an animated one is a full software
    // repaint of its box every frame).
    public override void Configure(CupriDocument doc)
    {
        _model.Renderer = Describe(doc.Renderer);
        doc.RendererChanged += r => { _model.Renderer = Describe(r); doc.Refresh(); };
    }

    private static string Describe(RendererInfo r) =>
        r.Kind == RendererKind.Unknown ? "a renderer this host does not report" : r.ToString();

    public override string Html => """
        <body class="{{PageClass}}">
          <div class="banner">
            <div class="title">Shaders</div>
            <div class="sub">SkSL on any element, or on the whole page</div>
          </div>

          <div class="row">
            <div class="col">
              <div class="card wave" style="filter: shader(wave, amount {{Amount}})">
                <div class="h">filter: shader(wave)</div>
                <div class="p">The shader is handed this card as iChannel0 and chooses where every pixel comes from.</div>
                <div class="chip">still a real button</div>
              </div>
              <div class="ctl">
                <span class="lbl">amount</span>
                <cupri-slider min="0" max="12" value="{{Amount}}" style="flex:1; min-width:0"></cupri-slider>
              </div>
            </div>

            <div class="col">
              <div class="card spot">
                <div class="h">background: shader(spot)</div>
                <div class="p">Reads iMouse. Move the pointer over this card.</div>
              </div>
              <div class="ctl">
                <span class="lbl">CRT over the whole page</span>
                <cupri-switch checked="{{Crt}}"></cupri-switch>
              </div>
            </div>
          </div>
          <div class="foot">Drawing with {{Renderer}}</div>
        </body>
        """;

    public override string Css => """
        /* Shadertoy's shape: a mainImage, y running UP, iTime and iResolution supplied. */
        @shader plasma {
            void mainImage(out vec4 fragColor, in vec2 fragCoord) {
                vec2 p = (fragCoord * 2.0 - iResolution.xy) / iResolution.y;
                float t = iTime * 0.6;
                float v = sin(p.x * 3.0 + t) + sin(p.y * 4.0 - t * 1.3)
                        + sin((p.x + p.y) * 2.5 + t * 0.7) + sin(length(p) * 5.0 - t * 2.0);
                vec3 col = 0.5 + 0.5 * cos(v + vec3(0.0, 2.1, 4.2));
                fragColor = vec4(col * vec3(0.55, 0.45, 0.85), 1.0);
            }
        }

        /* A filter: iChannel0 is what the element drew. Coordinates are px from its top-left. */
        @shader wave {
            uniform shader iChannel0;
            uniform float iTime;
            uniform float amount;
            half4 main(float2 p) {
                float2 q = p + float2(sin(p.y * 0.07 + iTime * 3.0), cos(p.x * 0.05 + iTime * 2.0)) * amount;
                return iChannel0.eval(q);
            }
        }

        /* A background that follows the pointer. No iTime, so it asks for no frames of its own. */
        @shader spot {
            uniform float4 iMouse;
            uniform half4 tint;
            half4 main(float2 p) {
                float d = distance(p, iMouse.xy);
                half glow = half(exp(-d * d / 5000.0));
                return half4(half3(0.09, 0.10, 0.14) + tint.rgb * glow * 0.75, 1.0);
            }
        }

        /* The whole page: a colour split that grows toward the edges, scanlines, a vignette. */
        @shader crt {
            uniform shader iChannel0;
            uniform float3 iResolution;
            uniform float strength;
            half4 main(float2 p) {
                float2 c = p / iResolution.xy - 0.5;
                float2 split = c * strength * 10.0;
                half3 col = half3(iChannel0.eval(p + split).r, iChannel0.eval(p).g, iChannel0.eval(p - split).b);
                half scan = half(0.88 + 0.12 * sin(p.y * 3.14159));
                half vignette = half(1.0 - dot(c, c) * 1.1);
                return half4(col * scan * vignette, 1.0);
            }
        }

        body { margin:0; padding:24px; box-sizing:border-box; background:#0E1016; color:#E8EBF2;
               font-family:sans-serif; }
        body.crt { filter: shader(crt, strength 0.6); }

        .banner { height:170px; border-radius:16px; padding:28px 32px; box-sizing:border-box;
                  background-image: shader(plasma); display:flex; flex-direction:column;
                  justify-content:flex-end; }
        .title { font-size:40px; font-weight:bold; color:#FFFFFF; }
        .sub { font-size:15px; color:#F2F0FF; margin-top:4px; }

        .row { display:flex; gap:24px; margin-top:24px; }
        .col { flex:1; min-width:0; display:flex; flex-direction:column; gap:16px; }
        @media (max-width: 640px) { .row { flex-direction:column; } .col { flex:none; } }

        .card { height:210px; border-radius:14px; padding:22px; box-sizing:border-box;
                border:1px solid #2A3040; }
        .card.wave { background:#171B26; }
        .card.spot { background-image: shader(spot, tint #5B8CFF); }
        .h { font-family:monospace; font-size:14px; color:#9DB7FF; }
        .p { font-size:15px; line-height:1.45; color:#C9CFDD; margin-top:10px; }
        .chip { display:inline-flex; align-items:center; margin-top:16px; padding:8px 14px;
                border-radius:999px; background:#2B3550; color:#E8EBF2; font-size:13px; }

        .ctl { display:flex; align-items:center; gap:14px; min-height:32px; }
        .foot { margin-top:20px; color:#7F889B; font-size:12px; overflow-wrap:anywhere; }
        .lbl { color:#9AA3B5; font-size:13px; }
        """;
}

/// <summary>What the markup binds to: one number that goes straight into a shader's uniform, and
/// one flag that decides whether the page as a whole is shaded. <c>[CupriBindable]</c> because this
/// app also runs in the browser, where a trimmed build has no reflection to bind with.</summary>
[CupriBindable]
public sealed partial class ShaderModel
{
    public double Amount { get; set; } = 4;
    public bool Crt { get; set; }
    public string PageClass => Crt ? "crt" : "";
    /// <summary>What the host draws with: <c>doc.Renderer</c>, as a line.</summary>
    public string Renderer { get; set; } = "";
}
