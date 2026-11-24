using System.Globalization;
using System.Text.Json.Nodes;
using CupriFace.Binding;

namespace CupriFace.Tool;

/// <summary>
/// A binding model made of JSON, so a document can be previewed with data before the C# model it
/// will really bind to exists — or without building the app at all.
///
/// <para>It implements the engine's own <see cref="IBindableAccessor"/>, so it binds exactly as a
/// <c>[CupriBindable]</c> model does: <c>{{Name}}</c> and <c>{{Address.City}}</c> read properties,
/// <c>data-repeat="Items"</c> iterates an array of objects, a two-way control writes back, and the
/// doctor's flag sweep finds the booleans and flips them. Numbers that are whole come back as
/// <c>long</c>, so <c>{{Count}}</c> reads <c>3</c>, not <c>3.0</c>.</para>
/// </summary>
public sealed class JsonModel : IBindableAccessor, IBindableShape
{
    private readonly JsonObject _obj;

    public JsonModel(JsonObject obj) => _obj = obj;

    /// <summary>Parse a JSON object. Anything else — an array, a number — is a usage error.</summary>
    public static JsonModel Parse(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        });
        return node is JsonObject o ? new JsonModel(o) : throw new FormatException("The model must be a JSON object.");
    }

    /// <summary>The object's keys — what the doctor's binding check (CF0060) walks, since there is
    /// no .NET type here to reflect on.</summary>
    public IEnumerable<string> BindableNames => _obj.Select(kv => kv.Key);

    public object? GetBindable(string name) =>
        _obj.TryGetPropertyValue(name, out var v) ? Wrap(v) : null;

    public bool SetBindable(string name, object? value)
    {
        if (!_obj.ContainsKey(name)) return false;
        _obj[name] = value switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            long l => JsonValue.Create(l),
            int i => JsonValue.Create(i),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            decimal m => JsonValue.Create(m),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
        return true;
    }

    private static object? Wrap(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonModel(o),
        JsonArray a => a.Select(Wrap).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };
}
