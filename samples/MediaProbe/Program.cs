using CupriFace.Media;
using CupriFace.Media.Decoding;

// Decode a WebM headlessly and report — the portability gate's payload (see the csproj) and a
// minimal example of using CupriFace.Media without any UI at all.
//   dotnet MediaProbe.dll [clip.webm]
// Exit 0 = frames decoded to real pictures; 1 = anything else (with the reason on stderr).

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
