namespace CupriFace.Interaction;

/// <summary>
/// A controller a host has opened, reported through <see cref="CupriDocument.GamepadConnected"/>.
///
/// <para>This exists so an application can ANSWER a question in code that it previously had to answer
/// by reading a diagnostic log: is there a pad, and did the platform understand it? An app with a
/// fallback reader of its own needs to know whether to start it, and
/// <c>CUPRIFACE_KEY_DEBUG</c> — a file, written for a human — was the only thing that knew.</para>
/// </summary>
/// <param name="Name">What the platform calls it. Free text from the driver; useful in a log or a
/// settings screen, never worth branching on.</param>
/// <param name="Backend">Which path opened it: <c>glfw</c>, <c>sdl</c>, <c>android</c> or
/// <c>web</c>. Tells an app which half of the host it is actually running against, which on Linux is
/// the difference between a GL window and the software fallback.</param>
/// <param name="Recognised">
/// Whether the platform had a real gamepad mapping for the device.
///
/// <para>False means it came in through the joystick fallback: the platform did not recognise it, so
/// its axes and buttons are numbered rather than named, and CupriFace is guessing that axes 0 and 1
/// are the left stick. That guess is right often enough to be worth making and wrong often enough to
/// be worth telling you about — an app with its own device knowledge should prefer its own reader
/// when this is false.</para>
/// </param>
public readonly record struct GamepadInfo(string Name, string Backend, bool Recognised)
{
    public override string ToString() =>
        $"{Name} [{Backend}{(Recognised ? "" : ", unrecognised — joystick fallback")}]";
}
