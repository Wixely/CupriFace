using System.Text;
using CupriFace.Interaction;

namespace CupriFace.DevTools;

/// <summary>
/// Editing the app's markup from the panel: delete an element, edit it as HTML, insert HTML after it
/// or inside it, cut / copy / paste elements, undo, and save the result back to the markup file.
///
/// <para><b>Edits are to the TEMPLATE TEXT.</b> A document is rebuilt from its template on every
/// interaction, so changing the live elements would last until the next click. Instead every
/// template element is tagged with where it was written (<see cref="CupriDocument.TrackSourceElements"/>),
/// the selected element is traced back to that span of text, the text is spliced
/// (<see cref="MarkupEdit"/>), and <see cref="CupriDocument.ReplaceSource"/> applies it — the same
/// swap a hot reload makes, so the model, focus and scroll survive an edit too. A <c>data-repeat</c>
/// row is one element in the template, so editing any copy edits them all; the inside of a component
/// is generated, so selecting it edits the component's own tag.</para>
///
/// <para><b>Edits are live until saved.</b> They change the running app only. <b>Save to file</b>
/// writes the template back over the markup file — refused unless that file still holds exactly the
/// text the edits started from, which also covers an app whose markup is built in code rather than
/// read as written. A save is then just a file change, and hot reload picks it up like any other.</para>
/// </summary>
public sealed partial class DevToolsPanel
{
    private enum EditorMode { None, Edit, InsertAfter, InsertInside }

    private EditorMode _mode;
    private int _editSource = -1;             // which template element the editor is about
    private readonly List<string> _undoEdits = [];
    private string? _baseHtml;                 // the template before the first unsaved edit
    private string? _lastApplied;              // the template as this panel last left it
    private string _editError = "";

    /// <summary>Raised when the panel puts text on the clipboard (Copy, Cut, Ctrl+C on an element).
    /// The host owns the clipboard and writes it.</summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>How many edits are live and not yet saved to the markup file.</summary>
    public int UnsavedEdits => _undoEdits.Count;

    private void WireEditing()
    {
        _target.TrackSourceElements = true;
        _ui.OnClick(".dt-edit", _ => OpenEditor(EditorMode.Edit));
        _ui.OnClick(".dt-ins-after", _ => OpenEditor(EditorMode.InsertAfter));
        _ui.OnClick(".dt-ins-inside", _ => OpenEditor(EditorMode.InsertInside));
        _ui.OnClick(".dt-copy", _ => CopySelected());
        _ui.OnClick(".dt-delete", _ => DeleteSelected());
        _ui.OnClick(".dt-apply", _ => ApplyEditor());
        _ui.OnClick(".dt-cancel", _ => CloseEditor());
        _ui.OnClick(".dt-undo", _ => UndoEdit());
        _ui.OnClick(".dt-save", _ => SaveToFile());
    }

    // ---- text and clipboard while the panel has the keyboard -----------------------------------

    /// <summary>Typed text. Goes to the HTML editor when it is open; swallowed otherwise while the
    /// panel has the keyboard, so typing in the panel never reaches the app.</summary>
    public bool PanelText(string text)
    {
        if (!_open || !PanelHasFocus) return false;
        if (_mode != EditorMode.None) _dirty |= _ui.DispatchKey(text, EditKey.None);
        return true;
    }

    /// <summary>
    /// A Ctrl/Cmd chord while the panel has the keyboard. In the HTML editor they are a text field's
    /// (select all, copy, cut, paste, undo, redo). On the tree they are the element's: Ctrl+C copies
    /// its HTML, Ctrl+X cuts it, Ctrl+V pastes HTML after it, Ctrl+Z undoes the last edit.
    /// </summary>
    public bool PanelShortcut(char ch, KeyMods mods, Func<string?> readClipboard, Action<string> writeClipboard)
    {
        if (!_open || !PanelHasFocus) return false;
        if (_mode != EditorMode.None)
        {
            switch (ch)
            {
                case 'a': _ui.DispatchKey(null, EditKey.SelectAll); break;
                case 'c': if (_ui.CopySelection() is { } cp) writeClipboard(cp); break;
                case 'x': if (_ui.CutSelection() is { } ct) writeClipboard(ct); break;
                case 'v': if (readClipboard() is { Length: > 0 } pv) _ui.DispatchKey(pv, EditKey.None); break;
                case 'z': if (mods.HasFlag(KeyMods.Shift)) _ui.Redo(); else _ui.Undo(); break;
                case 'y': _ui.Redo(); break;
                case 's': ApplyEditor(); break;
            }
            _dirty = true;
            return true;
        }
        switch (ch)
        {
            case 'c': if (SelectedOuter() is { } html) writeClipboard(html); break;
            case 'x': if (SelectedOuter() is { } cut) { writeClipboard(cut); DeleteSelected(); } break;
            case 'v': if (readClipboard() is { Length: > 0 } paste) PasteAfter(paste); break;
            case 'z': UndoEdit(); break;
            case 's': SaveToFile(); break;
        }
        _dirty = true;
        return true;
    }

    // ---- the edits -------------------------------------------------------------------------

    /// <summary>The template element behind the selection — the selected element itself, or the
    /// nearest ancestor that was written in the markup — and where it sits in the template text.</summary>
    private (int Source, MarkupSpan Span)? Editable()
    {
        var html = _target.TemplateHtml;
        var starts = _target.SourceStarts;
        for (var n = SelectedNode; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute(CupriDocument.SourceAttribute) is { } a
                && int.TryParse(a, out var i) && i < starts.Count
                && MarkupEdit.Find(html, starts[i]) is { } span)
                return (i, span);
        return null;
    }

    private string? SelectedOuter() =>
        Editable() is { } e ? MarkupEdit.Outer(_target.TemplateHtml, e.Span) : null;

    private void OpenEditor(EditorMode mode)
    {
        if (Editable() is not { } e) return;
        _mode = mode;
        _editSource = e.Source;
        _editError = "";
        _m.EditorText = mode == EditorMode.Edit ? MarkupEdit.Outer(_target.TemplateHtml, e.Span) : "";
        PanelHasFocus = true;
        MarkStale();
        _focusEditor = true;
    }
    private bool _focusEditor;

    private void CloseEditor()
    {
        _mode = EditorMode.None;
        _editSource = -1;
        _editError = "";
        MarkStale();
    }

    private void ApplyEditor()
    {
        if (_mode == EditorMode.None) return;
        // A text field hands its text to the model when it loses focus. Ctrl+S never leaves it, so
        // let go of it here, or the edit would apply whatever the box held before the typing.
        _ui.Blur();
        var html = _target.TemplateHtml;
        var starts = _target.SourceStarts;
        if (_editSource < 0 || _editSource >= starts.Count || MarkupEdit.Find(html, starts[_editSource]) is not { } span)
        {
            _editError = "The element is not in the markup any more (it was reloaded). Pick it again.";
            MarkStale();
            return;
        }
        var text = _m.EditorText ?? "";
        if (_mode != EditorMode.Edit && string.IsNullOrWhiteSpace(text)) { CloseEditor(); return; }
        var next = _mode switch
        {
            EditorMode.Edit => string.IsNullOrWhiteSpace(text) ? MarkupEdit.Delete(html, span) : MarkupEdit.Replace(html, span, text),
            EditorMode.InsertAfter => MarkupEdit.InsertAfter(html, span, text),
            _ => MarkupEdit.InsertInside(html, span, text),
        };
        if (next is null)
        {
            _editError = $"<{span.Tag}> has no end tag to insert before.";
            MarkStale();
            return;
        }
        if (Apply(next)) CloseEditor();
    }

    private void DeleteSelected()
    {
        if (Editable() is not { } e) return;
        if (Apply(MarkupEdit.Delete(_target.TemplateHtml, e.Span)))
            _selected = _selected is { Length: > 0 } p ? p[..^1] : null;   // the parent is what is left
    }

    private void CopySelected()
    {
        if (SelectedOuter() is { } html) ClipboardWriteRequested?.Invoke(html);
    }

    private void PasteAfter(string html)
    {
        if (Editable() is not { } e) return;
        Apply(MarkupEdit.InsertAfter(_target.TemplateHtml, e.Span, html));
    }

    /// <summary>Swap the edited template in, keeping the previous one for undo. False — with the
    /// document back as it was — when the new markup would not build.</summary>
    private bool Apply(string next)
    {
        var before = _target.TemplateHtml;
        if (string.Equals(next, before, StringComparison.Ordinal)) return false;
        try { _target.ReplaceSource(next); }
        catch (Exception ex)
        {
            try { _target.ReplaceSource(before); } catch { /* it built a moment ago */ }
            _editError = $"That markup did not build: {ex.Message}";
            MarkStale();
            return false;
        }
        _baseHtml ??= before;
        _undoEdits.Add(before);
        _lastApplied = next;
        MarkStale();
        return true;
    }

    private void UndoEdit()
    {
        if (_undoEdits.Count == 0) return;
        var previous = _undoEdits[^1];
        _undoEdits.RemoveAt(_undoEdits.Count - 1);
        try { _target.ReplaceSource(previous); } catch { /* it built before */ }
        _lastApplied = previous;
        if (_undoEdits.Count == 0) _baseHtml = null;
        MarkStale();
    }

    /// <summary>
    /// Write the edited template over the markup file. Only when the file still holds exactly the
    /// text the edits began from: anything else means it was changed on disk since, or the app
    /// builds its markup in code from it — and writing the template over either would lose work.
    /// </summary>
    private void SaveToFile()
    {
        if (_undoEdits.Count == 0 || _baseHtml is null) return;
        if (_reload?.MarkupFile is not { } file)
        {
            _editError = "The markup is not from a file, so there is nothing to save to. Copy the HTML instead.";
            MarkStale();
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(file);
            var onDisk = Resources.CupriSource.File(file).ReadText();
            if (!string.Equals(onDisk, _baseHtml, StringComparison.Ordinal))
            {
                _editError = $"{Path.GetFileName(file)} is not the text these edits started from (changed on disk, " +
                             "or the app builds its markup in code). Not saved — copy the HTML instead.";
                MarkStale();
                return;
            }
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            File.WriteAllText(file, _target.TemplateHtml, new UTF8Encoding(bom));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Resources.CupriResourceException)
        {
            _editError = $"Could not save {Path.GetFileName(file)}: {ex.Message}";
            MarkStale();
            return;
        }
        // Saved: the file IS the live template now, so there is nothing unsaved and nothing to undo
        // back to that the file would agree with. Hot reload sees the write and finds no change.
        _undoEdits.Clear();
        _baseHtml = null;
        _editError = "";
        MarkStale();
    }

    /// <summary>A hot reload replaced the template under the edits (the file changed on disk): they
    /// are gone, and so is anything to undo.</summary>
    private void OnReloaded()
    {
        if (_lastApplied is not null && !string.Equals(_target.TemplateHtml, _lastApplied, StringComparison.Ordinal))
        {
            _undoEdits.Clear();
            _baseHtml = null;
            _lastApplied = null;
            if (_mode != EditorMode.None) CloseEditor();
        }
        MarkStale();
    }

    // ---- the model -------------------------------------------------------------------------

    private void BuildEditing()
    {
        var e = Editable();
        _m.EditDisplay = e is null ? "none" : "flex";
        if (e is { } ed)
        {
            var line = 1 + _target.TemplateHtml.AsSpan(0, ed.Span.Start).Count('\n');
            var owner = SelectedNode?.Element?.GetAttribute(CupriDocument.SourceAttribute) is null
                ? $"Edits apply to the <{ed.Span.Tag}> it is part of, line {line}."
                : $"<{ed.Span.Tag}> on line {line} of the markup.";
            _m.EditTarget = owner;
        }
        _m.EditorDisplay = _mode == EditorMode.None ? "none" : "flex";
        _m.EditorTitle = _mode switch
        {
            EditorMode.Edit => "Edit as HTML — Apply replaces the element (empty deletes it)",
            EditorMode.InsertAfter => "Insert HTML after the element",
            EditorMode.InsertInside => "Insert HTML as the element's last child",
            _ => "",
        };
        _m.EditError = _editError;
        _m.EditErrorDisplay = _editError.Length > 0 ? "flex" : "none";

        var n = _undoEdits.Count;
        _m.EditsDisplay = n > 0 || _editError.Length > 0 && _mode == EditorMode.None ? "flex" : "none";
        var file = _reload?.MarkupFile is { } f ? Path.GetFileName(f) : null;
        var count = n == 0 ? ""
            : $"{n} live edit{(n == 1 ? "" : "s")}" + (file is null ? " — the markup has no file to save to" : $", not saved to {file}");
        // A refused save is said beside the count it failed to save, not instead of it.
        _m.EditsNote = _mode == EditorMode.None && _editError.Length > 0
            ? (count.Length > 0 ? count + ". " : "") + _editError
            : count;
        _m.UndoDisplay = n > 0 ? "flex" : "none";
        _m.SaveDisplay = n > 0 && file is not null ? "flex" : "none";
    }

    /// <summary>Put the caret in the HTML editor the frame after it opens (it has to exist first).
    /// True when it did, and the panel needs drawing again.</summary>
    private bool FocusEditorIfAsked()
    {
        if (!_focusEditor) return false;
        _focusEditor = false;
        // Focus by selector, not by clicking where the box is: the details pane scrolls, and the
        // editor may be below what it shows — a click there lands on whatever is drawn instead.
        if (!_ui.Focus("[data-bind-value='EditorText']")) return false;   // the field the component binds
        _dirty = true;
        return true;
    }
}
