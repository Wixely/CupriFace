using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>The engine→host window-command seam (fullscreen requests, e.g. a video's ⛶ button).</summary>
public class WindowCommandTests
{
    private const string Html = """
        <body>
          <div role="button" data-window-command="toggle-fullscreen">Full</div>
          <div role="button" data-window-command="exit-fullscreen">Exit</div>
        </body>
        """;

    [Fact]
    public void Activating_the_element_raises_the_command_for_the_host()
    {
        using var t = new TestDoc(Html);
        var received = new List<WindowCommand>();
        t.Doc.WindowCommandRequested += received.Add;

        t.ClickMatch(n => n.Element?.GetAttribute("data-window-command") == "toggle-fullscreen");
        t.ClickMatch(n => n.Element?.GetAttribute("data-window-command") == "exit-fullscreen");

        Assert.Equal(new[] { WindowCommand.ToggleFullscreen, WindowCommand.ExitFullscreen }, received);
    }

    [Fact]
    public void Without_a_host_subscriber_the_command_is_dropped_harmlessly()
    {
        // Headless/embedded consumers have no window. The click itself may still be "handled"
        // (focus moves to the button, like any control) — but the command goes nowhere, nothing
        // throws, and a host subscribing later gets commands as normal.
        using var t = new TestDoc(Html);
        var node = t.Find(n => n.Element?.GetAttribute("data-window-command") == "toggle-fullscreen")!;
        t.ClickNode(node);                            // no subscriber: must not throw

        WindowCommand? got = null;
        t.Doc.WindowCommandRequested += c => got = c;
        t.ClickNode(node);
        Assert.Equal(WindowCommand.ToggleFullscreen, got);
    }
}
