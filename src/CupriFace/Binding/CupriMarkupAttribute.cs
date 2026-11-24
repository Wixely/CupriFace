namespace CupriFace.Binding;

/// <summary>
/// Names the markup a model is bound to, so the build can check every <c>{{Path}}</c> in it against
/// this type (CF0060, the build-time half of the doctor's check).
///
/// <para>Most apps never need it: a <c>CupriApp</c> whose <c>MarkupSource</c> is <c>Assets.X.Html</c>
/// and whose <c>Model</c> returns a typed field is worked out on its own, and so is
/// <c>CupriDocument.Load(html).Bind(model)</c>. This is for what inference cannot see — markup
/// loaded in one place and bound in another, a model handed in from outside, a host that is not a
/// <c>CupriApp</c>.</para>
///
/// <code>
/// [CupriMarkup("Assets/Board.html")]
/// public sealed partial class BoardModel { … }
///
/// // or, for a model you cannot annotate:
/// [assembly: CupriMarkup("Assets/Board.html", typeof(BoardModel))]
/// </code>
///
/// <para>The name is the asset's logical name, exactly as <c>EmbeddedAsset</c> takes it. The file has
/// to reach the compiler as an AdditionalFile — the CupriFace package's Assets pipeline does that for
/// everything under <c>Assets/</c>. It has no effect at runtime.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly,
                AllowMultiple = true, Inherited = false)]
public sealed class CupriMarkupAttribute : Attribute
{
    /// <summary>On a model type: the markup asset bound to it.</summary>
    public CupriMarkupAttribute(string asset) => Asset = asset;

    /// <summary>On the assembly: the markup asset, and the model type bound to it.</summary>
    public CupriMarkupAttribute(string asset, Type model)
    {
        Asset = asset;
        Model = model;
    }

    /// <summary>The asset's logical name, e.g. <c>"Assets/Board.html"</c>.</summary>
    public string Asset { get; }

    /// <summary>The model type, when the attribute is on the assembly rather than on the model.</summary>
    public Type? Model { get; }
}
