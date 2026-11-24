namespace CupriFace.Components;

/// <summary>
/// What a component — or a whole <see cref="ICupriComponentLibrary"/> — implements to bring its own
/// event handling, so a package ships markup, CSS AND behaviour rather than markup plus a page of
/// instructions for wiring it (COMPONENT-PACKAGING.md, "Behaviour").
///
/// <para><b>How it works.</b> The document calls <see cref="Attach"/> ONCE, when it is given a
/// registry holding the component (<see cref="CupriDocument.UseComponents"/>), or at its next rebuild
/// for a component added to that registry later. The behaviour registers ordinary document handlers
/// keyed on attributes its own expansion emits:</para>
///
/// <code>
/// public void Attach(CupriDocument doc) =&gt;
///     doc.OnAction("data-acme-step", e =&gt; { /* read e.Element, write e.Model */ return true; });
/// </code>
///
/// <para><b>Why handlers are keyed on attributes, not attached to elements.</b> The DOM is rebuilt
/// from the template on every model change and every keystroke, so an element a handler was hung on
/// is gone a moment later. A handler keyed on <c>data-acme-step</c> finds the attribute again in
/// whatever DOM is current, which is how the built-in <c>data-*</c> hooks have always survived a
/// rebuild — and why nothing here needs re-attaching after one. The same rule decides what state may
/// live where: per-document state goes in a closure inside <see cref="Attach"/> (the component
/// instance is shared by every document using the registry); per-element state goes in the model or
/// in an attribute the expansion re-emits.</para>
///
/// <para>Use the document's existing seams — <see cref="CupriDocument.OnAction"/> for a click or
/// keyboard activation, <see cref="CupriDocument.OnPointer"/> for a drag,
/// <see cref="CupriDocument.OnWheel"/>, <see cref="CupriDocument.OnSubmit"/>,
/// <see cref="CupriDocument.OnContext"/>, and <see cref="CupriDocument.OnRebuilt"/> to see each
/// freshly expanded DOM — and namespace the attributes with the library's prefix so two libraries
/// cannot answer each other's clicks.</para>
///
/// <para>There is no detach. A component replaced in the registry after a document attached it keeps
/// its handlers on that document, so compose the registry first and hand it over second, which is
/// what <c>CupriApp.Components</c> does.</para>
/// </summary>
public interface ICupriBehaviour
{
    /// <summary>Register this component's handlers on <paramref name="document"/>. Called once per
    /// document.</summary>
    void Attach(CupriDocument document);
}
