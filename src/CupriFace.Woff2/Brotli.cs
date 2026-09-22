using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CupriFace.Woff2;

/// <summary>
/// Brotli decompression, which is the one part of WOFF 2 that is not the same code everywhere.
///
/// <para><b>Off the browser</b> — desktop and Android — <see cref="BrotliStream"/> is in the BCL and
/// that is the whole story. No package, no native asset, nothing to license.</para>
///
/// <para><b>In a browser</b> <see cref="BrotliStream"/> throws
/// <see cref="PlatformNotSupportedException"/>. Not because wasm cannot do Brotli: the brotli
/// archives ship in the browser-wasm runtime pack and are already handed to the linker. It is that
/// dotnet/runtime's <c>System.IO.Compression.Brotli</c> project has no <c>-browser</c> target
/// framework, so browser falls through to a generated assembly whose every method throws. Its
/// neighbour <c>System.IO.Compression</c> does carry a <c>-browser</c> leg, which is exactly why
/// <c>DeflateStream</c> works in a browser and why the engine reads WOFF 1 there today.</para>
///
/// <para>So on browser this calls the native decoder directly, under the same module name and entry
/// points dotnet/runtime's own <c>Interop.Brotli.cs</c> declares. That module is already registered
/// for wasm because <c>DeflateStream</c> reaches zlib through it — which is what makes this need no
/// build-file change, no <c>NativeFileReference</c>, and no archive of our own. Measured cost when
/// the reference pulls the decoder into the module: about a kilobyte, and nothing once gzipped.</para>
///
/// <para><b>Mono only, and deliberately.</b> The NativeAOT-LLVM host's compiler pack ships no brotli
/// archive at all — only zlib — so there is nothing for this to reach there. That host fails with the
/// message below rather than silently rendering a substitute face. It is also why this lives in an
/// optional package: an app that never registers a WOFF 2 font references none of this, and pays for
/// none of it, on any host.</para>
/// </summary>
internal static class Brotli
{
    private const string CompressionNative = "libSystem.IO.Compression.Native";

    private const int BrotliDecoderResultSuccess = 1;

    /// <summary>
    /// One-shot decode into a buffer of exactly <paramref name="expectedSize"/> bytes — which WOFF 2
    /// always knows, because the table directory declares every table's length before the stream
    /// arrives. That removes the grow-and-copy a streaming decode would need.
    /// </summary>
    internal static byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        var output = new byte[expectedSize];
        if (expectedSize == 0) return output;

        if (OperatingSystem.IsBrowser())
            DecompressNative(compressed, output);
        else
            DecompressManaged(compressed, output);

        return output;
    }

    private static void DecompressManaged(ReadOnlySpan<byte> compressed, byte[] output)
    {
        using var src = new MemoryStream(compressed.ToArray(), writable: false);
        using var brotli = new BrotliStream(src, CompressionMode.Decompress);

        var read = 0;
        while (read < output.Length)
        {
            var n = brotli.Read(output, read, output.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read != output.Length)
            throw new ArgumentException(
                $"WOFF 2 Brotli stream expanded to {read} bytes, but its table directory declares " +
                $"{output.Length}. The file is truncated or its lengths disagree with its data.",
                nameof(compressed));
    }

    private static unsafe void DecompressNative(ReadOnlySpan<byte> compressed, byte[] output)
    {
        nuint decodedSize = (nuint)output.Length;
        int result;
        try
        {
            fixed (byte* inPtr = compressed)
            fixed (byte* outPtr = output)
            {
                result = BrotliDecoderDecompress((nuint)compressed.Length, inPtr, &decodedSize, outPtr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw new PlatformNotSupportedException(
                "WOFF 2 needs a Brotli decoder and this browser host has none reachable. The Mono " +
                "wasm host (CupriFace.Web.Mono) reaches the runtime pack's brotli archives; the " +
                "NativeAOT-LLVM host (CupriFace.Web.NativeAot) ships no brotli archive in its " +
                "compiler pack, so WOFF 2 cannot be decoded there. Convert the font to TTF/OTF or " +
                "WOFF 1, or serve it to the Mono host.", ex);
        }

        if (result != BrotliDecoderResultSuccess)
            throw new ArgumentException(
                $"WOFF 2 Brotli stream did not decode (BrotliDecoderResult {result}). The file is " +
                "truncated, corrupt, or declares the wrong decompressed size.", nameof(compressed));

        if (decodedSize != (nuint)output.Length)
            throw new ArgumentException(
                $"WOFF 2 Brotli stream expanded to {decodedSize} bytes, but its table directory " +
                $"declares {output.Length}.", nameof(compressed));
    }

    // BrotliDecoderResult BrotliDecoderDecompress(size_t encoded_size, const uint8_t encoded[],
    //                                             size_t* decoded_size, uint8_t decoded[]);
    // size_t is 32-bit on wasm32, which nuint matches. The module name and entry point are
    // dotnet/runtime's own (Libraries.CompressionNative), so this resolves against the archives the
    // runtime pack already links — verified in Chromium before this package existed.
    [DllImport(CompressionNative, EntryPoint = "BrotliDecoderDecompress")]
    private static extern unsafe int BrotliDecoderDecompress(
        nuint encodedSize, byte* encoded, nuint* decodedSize, byte* decoded);
}
