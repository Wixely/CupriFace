using System.Diagnostics.CodeAnalysis;

namespace CupriFace.Web;

/// <summary>
/// The parts of a web host an app can leave out of its binary (#325): the two bundled sans faces,
/// browser video, and files dropped on the canvas. Each is a feature switch, on unless the app's
/// build says otherwise.
///
/// <para><b>Off means gone, not skipped.</b> The app sets MSBuild properties —
/// <c>&lt;CupriWebFonts&gt;all|regular|none&lt;/CupriWebFonts&gt;</c>,
/// <c>&lt;CupriWebVideo&gt;false&lt;/CupriWebVideo&gt;</c>,
/// <c>&lt;CupriWebFileDrop&gt;false&lt;/CupriWebFileDrop&gt;</c> — and the host's build targets turn
/// them into these switches as trimmable runtime options. A trimmed or NativeAOT publish folds each
/// test to a constant and removes the code behind it, and the host's embedded
/// <c>ILLink.Substitutions.xml</c> removes a font that is off from the resources as well, since data
/// that nothing reads is otherwise still kept. Every use in the host must test the switch directly
/// for that to hold.</para>
///
/// <para>The page's exports stay either way, so nothing in the JS half can call a function that is
/// not there; with a feature off they do nothing.</para>
/// </summary>
internal static class WebFeatures
{
    public const string RegularFontSwitch = "CupriFace.Web.Fonts.Regular";
    public const string BoldFontSwitch = "CupriFace.Web.Fonts.Bold";
    public const string VideoSwitch = "CupriFace.Web.Video";
    public const string FileDropSwitch = "CupriFace.Web.FileDrop";

    /// <summary>NotoSans-Regular.ttf is embedded and loaded. Without either face the app registers
    /// its own with <see cref="CupriDocument.LoadFont(byte[])"/> in <c>Configure</c> (the first face
    /// registered becomes the generic sans-serif), or text renders in the wasm Skia build's only
    /// face, Noto Mono.</summary>
    [FeatureSwitchDefinition(RegularFontSwitch)]
    public static bool RegularFont { get; } = On(RegularFontSwitch);

    /// <summary>NotoSans-Bold.ttf is embedded and loaded. Without it, bold text is the regular face
    /// thickened (FontService's synthetic bold).</summary>
    [FeatureSwitchDefinition(BoldFontSwitch)]
    public static bool BoldFont { get; } = On(BoldFontSwitch);

    /// <summary>&lt;cupri-video&gt; plays through the browser's own &lt;video&gt;. Without it a
    /// video control has no backend and shows its poster.</summary>
    [FeatureSwitchDefinition(VideoSwitch)]
    public static bool Video { get; } = On(VideoSwitch);

    /// <summary>Files dropped on the canvas reach the document's drop targets. Without it the host
    /// tells the page it accepts no drops.</summary>
    [FeatureSwitchDefinition(FileDropSwitch)]
    public static bool FileDrop { get; } = On(FileDropSwitch);

    private static bool On(string name) => !AppContext.TryGetSwitch(name, out var on) || on;
}
