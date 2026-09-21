using CupriFace.Text;

namespace CupriFace.Woff2;

/// <summary>
/// Turning the optional package on:
///
/// <code>
/// public override void Configure(CupriDocument doc) => doc.UseWoff2();
/// </code>
///
/// <para>Or once at startup, before any font is registered:</para>
///
/// <code>
/// Woff2Extensions.UseWoff2();
/// </code>
///
/// <para><b>Process-wide, despite the document receiver.</b> Font registration is not per-document —
/// <c>CupriApp.Fonts</c>, <c>LoadFonts</c>, an <c>@font-face</c> rule and anything fetching a face at
/// runtime all funnel through one place — so installing a decoder for one document installs it for
/// all of them. The document overload exists because that is where the other optional packages are
/// switched on and it is where people look; it is not a scoping claim.</para>
/// </summary>
public static class Woff2Extensions
{
    /// <summary>Decode WOFF 2 fonts from here on. Idempotent, and safe to call from several
    /// documents.</summary>
    public static void UseWoff2() => FontService.Woff2Decoder = Woff2Reader.ToSfnt;

    /// <summary>Decode WOFF 2 fonts, switched on where the other optional packages are. Returns the
    /// document so it chains.</summary>
    public static CupriDocument UseWoff2(this CupriDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        UseWoff2();
        return doc;
    }
}
