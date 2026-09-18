namespace CupriFace.Interaction;

/// <summary>
/// A file drop, scripted. One call per gesture, no window, no OS.
///
/// <para>Here for the same reason <see cref="TouchDriver"/> is: a gesture that can be received but not
/// scripted cannot be regression-tested, and a drop handler is exactly the kind of code that rots
/// quietly — it runs when someone drags something in, which is never during a test run and rarely
/// during development.</para>
///
/// <para>What makes it honest rather than a stub is that the real browser host takes the same path.
/// A dropped file in a browser <i>is</i> bytes in memory with a name, because that is all a blob
/// offers; <see cref="DroppedFile.FromBytes"/> is not a test double for it, it is the same thing. Only
/// the desktop hosts differ, and they differ by being lazier.</para>
/// </summary>
public sealed class DropDriver(CupriDocument doc)
{
    private readonly CupriDocument _doc = doc ?? throw new ArgumentNullException(nameof(doc));

    /// <summary>Drag over a point without letting go — the highlight, not the drop. Call it more than
    /// once to move across targets.</summary>
    public bool Over(float x, float y) => _doc.DispatchDropOver(x, y);

    /// <summary>Drag back out of the window, dropping nothing.</summary>
    public bool Leave() => _doc.DispatchDropLeave();

    /// <summary>
    /// The whole gesture: drag in, then release. Goes through <see cref="Over"/> first because a real
    /// drop always does, so a handler that depends on the highlight having been set sees what it would
    /// really see — and a test that skipped it would pass while the app flickered.
    /// </summary>
    public bool Drop(float x, float y, params DroppedFile[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Over(x, y);
        return _doc.DispatchFileDrop(x, y, files);
    }

    /// <summary>Drop a text file with the given contents — the shorthand that covers most tests.</summary>
    public bool DropText(float x, float y, string name, string contents) =>
        Drop(x, y, DroppedFile.FromBytes(name, System.Text.Encoding.UTF8.GetBytes(contents)));

    /// <summary>Drop files named only — zero-length content. For asserting on routing and targeting,
    /// where the bytes are beside the point.</summary>
    public bool DropNamed(float x, float y, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return Drop(x, y, Array.ConvertAll(names, n => DroppedFile.FromBytes(n, [])));
    }
}
