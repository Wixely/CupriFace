using CupriFace.Media;
using CupriFace.Media.Decoding;

// Decode a WebM headlessly and report — the portability gate's payload (see the csproj) and a
// minimal example of using CupriFace.Media without any UI at all.
//   dotnet MediaProbe.dll [clip.webm]            frame-decode check (the portability gate)
//   dotnet MediaProbe.dll --soak clip.webm [s]   REAL-TIME playback soak: play for [s] seconds
//                                                (default: the whole clip) with the real audio
//                                                device, sampling A/V lag; fails on drift.
// Exit 0 = pass; 1 = anything else (with the reason on stderr).

if (args.Length > 0 && args[0] == "--soak") return Soak(args.Skip(1).ToArray());

var clip = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "demo.webm");
Console.WriteLine($"os      : {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}");
Console.WriteLine($"rid     : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
Console.WriteLine($"clip    : {clip}");

if (!File.Exists(clip)) { Console.Error.WriteLine("clip not found"); return 1; }
if (!NativeDecoders.Available) { Console.Error.WriteLine("cupricodecs native library not loadable here"); return 1; }

var backend = new WebmVideoBackend(new NativeDecoders());
using var player = (WebmPlayer)backend.Open(new VideoSource(clip));
Console.WriteLine($"size    : {player.Surface.NaturalSize}");
Console.WriteLine($"duration: {player.Duration:F2}s");

// Step through the clip, decoding at each point, and prove the picture actually changes.
var seen = new List<string>();
for (var t = 0.0; t < Math.Max(player.Duration, 0.1); t += 0.25)
{
    player.Position = t;
    if (player.Surface.CurrentFrame is not { } frame) continue;
    using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(frame.Width, frame.Height, SkiaSharp.SKColorType.Rgba8888));
    if (!frame.ReadPixels(bmp.PeekPixels())) continue;
    var px = bmp.Bytes;
    long sum = 0;
    var distinct = new HashSet<uint>();
    for (var i = 0; i + 3 < px.Length; i += 4) { sum += px[i] + px[i + 1] + px[i + 2]; distinct.Add(BitConverter.ToUInt32(px, i)); }
    var signature = $"{t:F2}s mean={sum / (px.Length / 4 * 3.0):F1} colours={distinct.Count}";
    Console.WriteLine($"frame   : {signature}");
    seen.Add(signature);
}

if (seen.Count < 2) { Console.Error.WriteLine($"expected several decoded frames, got {seen.Count}"); return 1; }
if (seen.Distinct().Count() < 2) { Console.Error.WriteLine("every frame was identical — decode is not advancing"); return 1; }
Console.WriteLine($"OK      : {seen.Count} frames decoded, {seen.Distinct().Count()} distinct");
return 0;

// The A/V drift soak: play a (long) clip in REAL time through the real decoders and the real
// audio device, sampling the player's own lag metric — media time minus the audio actually
// played (submitted − still-queued). Steady lag = device latency, fine; GROWING lag = drift
// (every underrun shifts audio permanently later without clock feedback). Fails on growth.
static int Soak(string[] args)
{
    var clip = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "demo-av.webm");
    if (!File.Exists(clip)) { Console.Error.WriteLine($"clip not found: {clip}"); return 1; }
    if (!NativeDecoders.Available) { Console.Error.WriteLine("cupricodecs native library not loadable here"); return 1; }

    var sink = SdlAudioSink.TryCreate();
    Console.WriteLine($"clip    : {clip} ({new FileInfo(clip).Length / 1024} KiB)");

    var backend = new WebmVideoBackend(new NativeDecoders(), sink);
    using var player = (WebmPlayer)backend.Open(new VideoSource(clip));
    // Device truth is only known after the sink STARTED (the player opens it with the clip's
    // format). Headless/RDP boxes have no endpoint — SDL_AUDIODRIVER=dummy gives a device that
    // drains the queue at the real wall rate, which is exactly what the timing soak needs.
    Console.WriteLine($"audio   : {(sink is { DeviceOpen: true } ? "device open — lag measurable"
        : "NO DEVICE — set SDL_AUDIODRIVER=dummy for a realtime-draining stand-in")}");
    var limit = args.Length > 1 && double.TryParse(args[1], out var s) ? s : player.Duration;
    limit = Math.Min(limit, player.Duration);
    Console.WriteLine($"duration: {player.Duration:F1}s · soaking {limit:F1}s");

    player.Play();
    var lag0 = double.NaN;
    var worstAbsGrowth = 0.0;
    while (player.Position < limit && player.Playing)
    {
        Thread.Sleep(5000);
        var lag = player.AudioLagSeconds;
        if (double.IsNaN(lag0) && !double.IsNaN(lag)) lag0 = lag;
        var growth = double.IsNaN(lag) ? 0 : lag - lag0;
        worstAbsGrowth = Math.Max(worstAbsGrowth, Math.Abs(growth));
        Console.WriteLine($"t={player.Position,6:F1}s  lag={lag * 1000,6:F1} ms  drift={growth * 1000,6:+0.0;-0.0} ms  " +
                          $"underruns={player.AudioUnderruns}  frames={player.FramesDecoded} ({player.FramesLate} late)  " +
                          $"decode={player.DecodeMsAverage:F2} ms");
    }
    player.Pause();

    if (sink is not { DeviceOpen: true })
    {
        Console.WriteLine("SOAK    : completed (no audio device — lag not measurable here)");
        return 0;
    }
    // 40 ms of accumulated drift is where lip-sync research puts the detectability threshold.
    if (worstAbsGrowth > 0.040)
    {
        Console.Error.WriteLine($"DRIFT   : audio lag grew {worstAbsGrowth * 1000:F1} ms over the soak (limit 40 ms)");
        return 1;
    }
    if (player.AudioUnderruns > 2)
    {
        Console.Error.WriteLine($"UNDERRUNS: {player.AudioUnderruns} queue-dry refills — audible gaps");
        return 1;
    }
    Console.WriteLine($"SOAK OK : drift stayed within {worstAbsGrowth * 1000:F1} ms · {player.AudioUnderruns} underruns");
    return 0;
}
