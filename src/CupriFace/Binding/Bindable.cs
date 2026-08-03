namespace CupriFace.Binding;

/// <summary>
/// Marks a model type as bindable. The CupriFace source generator emits an
/// <see cref="IBindableAccessor"/> implementation for the (partial) type so binding
/// resolves properties with zero reflection — AOT/trim-clean (DESIGN.md §6).
/// Types without this attribute still bind via a reflection fallback (not AOT-safe).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class CupriBindableAttribute : Attribute;

/// <summary>
/// Reflection-free property lookup by name. Implemented by the source generator for
/// <see cref="CupriBindableAttribute"/> types; consumed by the binding engine.
/// </summary>
public interface IBindableAccessor
{
    /// <summary>Return the value of the named public property, or null if unknown.</summary>
    object? GetBindable(string name);
}
