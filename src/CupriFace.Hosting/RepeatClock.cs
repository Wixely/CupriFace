namespace CupriFace.Hosting;

/// <summary>
/// The schedule a held key repeats on: nothing after the press until the delay has passed, then one
/// repeat per rate for as long as it is held.
///
/// <para>This exists because one desktop window got repeats for free and the other did not. The SDL
/// software window reads SDL's events directly and SDL delivers key repeats, so holding Backspace
/// there deleted a run of characters. The GLFW window goes through Silk's input layer, whose GLFW
/// backend raises KeyDown for <c>InputAction.Press</c> only — it has no case for
/// <c>InputAction.Repeat</c> — so GLFW's repeats were dropped and a held key did exactly one thing.
/// Typing still repeated, because the character callback fires on repeat, which made a field feel
/// broken rather than unfinished: letters flowed and deletion did not.</para>
///
/// <para>Kept here, free of any windowing type, for the reason <c>Hosting.props</c> gives: the timing
/// is the part worth testing, and it needs no window to test. The window owns which key is down and
/// what it means; this owns only when the next repeat is due.</para>
/// </summary>
public sealed class RepeatClock(double delayMs = 450, double rateMs = 33)
{
    private double _nextMs;

    /// <summary>Is a key currently held and scheduled to repeat?</summary>
    public bool Armed { get; private set; }

    /// <summary>A fresh key press. The first repeat is due one DELAY later — the pause that stops a
    /// deliberate single press from turning into two.</summary>
    public void Press(double nowMs)
    {
        Armed = true;
        _nextMs = nowMs + delayMs;
    }

    /// <summary>The key came up, or is no longer held. Nothing repeats until the next press.</summary>
    public void Release() => Armed = false;

    /// <summary>True once for each repeat that has come due, advancing the schedule by one rate.
    ///
    /// <para>The next repeat is scheduled from NOW rather than from when it was due, so a stall — a
    /// slow frame, a dragged window, a breakpoint — does not come back as a burst of repeats
    /// catching up on wall-clock time. A key held through a two-second hitch deletes one character
    /// on the far side of it, not sixty.</para></summary>
    public bool ShouldFire(double nowMs)
    {
        if (!Armed || nowMs < _nextMs) return false;
        _nextMs = nowMs + rateMs;
        return true;
    }
}
