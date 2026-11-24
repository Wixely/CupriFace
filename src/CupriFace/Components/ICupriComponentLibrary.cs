namespace CupriFace.Components;

/// <summary>
/// A packaged set of components — what a NuGet class library of <c>cupri</c>-style controls exposes
/// (COMPONENT-PACKAGING.md). Added to an app with
/// <see cref="ComponentRegistry.Add(ICupriComponentLibrary)"/>, usually through an extension method
/// the library ships:
///
/// <code>
/// public static ComponentRegistry AddAcmeControls(this ComponentRegistry r) => r.Add(new AcmeControls());
///
/// // in the app
/// public override ComponentRegistry Components => base.Components.AddAcmeControls();
/// </code>
///
/// <para>It is a plain object returning plain instances, not something found by scanning an
/// assembly, so a library is trim- and NativeAOT-safe by construction: everything it registers is
/// reachable from the call that adds it.</para>
/// </summary>
public interface ICupriComponentLibrary
{
    /// <summary>A human-readable name, used in conflict messages and doctor findings —
    /// usually the package id, e.g. <c>Acme.Controls</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The tag prefix every one of this library's components carries, ending in <c>-</c>
    /// (<c>"acme-"</c>). Enforced when the library is added: two libraries can never collide on a tag,
    /// and the doctor can tell an unregistered <c>&lt;acme-*&gt;</c> from a typo of something else.
    ///
    /// <para>Use it for the library's CSS class names too (<c>.acme-card</c>). The registry cannot
    /// scope CSS, so a shared prefix is what stops two libraries fighting over <c>.card</c>.
    /// <c>cupri-</c> is reserved for CupriFace's own packages.</para>
    /// </summary>
    string Prefix { get; }

    /// <summary>The components to register — all are added, or none.</summary>
    IEnumerable<ICupriComponent> Components { get; }

    /// <summary>CSS shared by the whole library — design tokens, classes several components use —
    /// emitted just before its components' own <see cref="ICupriComponent.DefaultCss"/>, after the
    /// built-ins and before the app's stylesheet.</summary>
    string Css => "";
}
