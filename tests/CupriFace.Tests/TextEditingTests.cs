using CupriFace.Hosting;
using CupriFace.Interaction;
using CupriFace.Text;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// What a text field does with the keys and the text that reach it, once you stop assuming and
/// start pressing them (#174).
///
/// <para>Four separate faults, found by driving a real textarea rather than by reading it:</para>
/// <list type="bullet">
/// <item>Home and End jumped to the start and end of the WHOLE buffer in a multi-line field, so Home
/// in a long note went to the top of it and Shift+Home selected everything above the caret.</item>
/// <item>Up and Down did nothing at all — the focus-movement branch runs only with no field focused,
/// so the arrows reached the insert case, found no text, and stopped. A textarea could not be walked
/// vertically by keyboard.</item>
/// <item>Pasted text kept whatever control characters the source had. A NUL from a PDF or a
/// vertical tab from a spreadsheet went into the model and back to the app as data.</item>
/// <item>Backspace deleted a code point, not a character. "é" written as e + a combining acute took
/// two presses and left a bare "e" after the first — which reads as a backspace that did not work.</item>
/// </list>
/// </summary>
public class TextEditingTests(ITestOutputHelper output)
{
    private sealed class M { public string Note { get; set; } = ""; public string One { get; set; } = ""; }

    private const string Css = "body{margin:0;font-family:sans-serif}"
        + ".f{display:block;width:360px;height:140px;border:1px solid #ccc;padding:4px}";

    private static (TestDoc T, M Model) Fields(string note = "", string one = "")
    {
        var m = new M { Note = note, One = one };
        var t = new TestDoc(
            "<body><cupri-textarea class='f' value='{{Note}}'></cupri-textarea>"
            + "<cupri-textfield class='f' value='{{One}}'></cupri-textfield></body>",
            Css, m, width: 420, height: 340, components: true);
        return (t, m);
    }

    /// <summary>Click into the textarea ON A GIVEN LINE, so Home/End have a line to be scoped to
    /// rather than whichever one the element's centre happens to land on.</summary>
    private static void ClickLine(TestDoc t, int line)
    {
        var area = t.Find(x => x.Element?.ClassList.Contains("cupri-textarea") == true)!;
        var box = HitTesting.AbsoluteBox(area);
        var lh = FontService.LineHeightPx(area.Style);
        t.Click(box.X + box.W - 6, box.Y + 6 + lh * line + lh / 2f);   // far right of that line
    }

    private static void FocusField(TestDoc t)
        => t.ClickNode(t.Find(x => x.Element?.ClassList.Contains("cupri-textfield") == true)!);

    /// <summary>Blur so the permissive buffer commits, then read what the model actually holds.</summary>
    private static string Committed(TestDoc t, M m, bool single = false)
    {
        t.Doc.DispatchKey(null, EditKey.Tab);
        return single ? m.One : m.Note;
    }

    // ---- Home and End are line-scoped in a multi-line field -------------------------------------

    [Fact]
    public void Home_goes_to_the_start_of_the_line_not_the_buffer()
    {
        var (t, m) = Fields("first line\nsecond line\nthird line");
        ClickLine(t, 1);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("first line\nXsecond line\nthird line", Committed(t, m));
        t.Dispose();
    }

    [Fact]
    public void End_goes_to_the_end_of_the_line_not_the_buffer()
    {
        var (t, m) = Fields("first line\nsecond line\nthird line");
        ClickLine(t, 1);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey(null, EditKey.End);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("first line\nsecond lineX\nthird line", Committed(t, m));
        t.Dispose();
    }

    /// <summary>Shift+Home selects to the line's start. It used to select every line above the caret
    /// as well, so one keystroke put a whole note at risk of being replaced by the next one.</summary>
    [Fact]
    public void Shift_home_selects_only_to_the_start_of_the_line()
    {
        var (t, m) = Fields("keep this\nreplace this");
        ClickLine(t, 1);
        t.Doc.DispatchKey(null, EditKey.End);
        t.Doc.DispatchKey(null, EditKey.Home, KeyMods.Shift);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("keep this\nX", Committed(t, m));
        t.Dispose();
    }

    /// <summary>A SINGLE-LINE field keeps whole-buffer Home/End, which is what an &lt;input&gt; does —
    /// its rows are soft wraps of one logical line, not lines someone typed.</summary>
    [Fact]
    public void A_single_line_field_still_takes_home_to_the_very_start()
    {
        var (t, m) = Fields(one: "abcdef");
        FocusField(t);
        t.Doc.DispatchKey(null, EditKey.End);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("Xabcdef", Committed(t, m, single: true));
        t.Dispose();
    }

    // ---- Up and Down move the caret -------------------------------------------------------------

    /// <summary>Identical lines on purpose. A vertical move preserves the caret's X, as every editor
    /// does, so in a proportional font the column it lands on depends on glyph widths — and an
    /// assertion about a specific column is only meaningful when the two rows measure the same.</summary>
    [Fact]
    public void Up_moves_the_caret_to_the_line_above()
    {
        var (t, m) = Fields("abc\nabc\nabc");
        ClickLine(t, 2);
        t.Doc.DispatchKey(null, EditKey.End);
        t.Doc.DispatchKey(null, EditKey.Up);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("abc\nabcX\nabc", Committed(t, m));
        t.Dispose();
    }

    [Fact]
    public void Down_moves_the_caret_to_the_line_below()
    {
        var (t, m) = Fields("aaa\nbbb\nccc");
        ClickLine(t, 0);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey(null, EditKey.Down);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("aaa\nXbbb\nccc", Committed(t, m));
        t.Dispose();
    }

    /// <summary>Up on the first line goes to the very start, and Down on the last to the very end —
    /// what every editor does, rather than refusing to move.</summary>
    [Fact]
    public void Up_on_the_first_line_goes_to_the_start()
    {
        var (t, m) = Fields("aaa\nbbb");
        ClickLine(t, 0);
        t.Doc.DispatchKey(null, EditKey.End);
        t.Doc.DispatchKey(null, EditKey.Up);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("Xaaa\nbbb", Committed(t, m));
        t.Dispose();
    }

    [Fact]
    public void Down_on_the_last_line_goes_to_the_end()
    {
        var (t, m) = Fields("aaa\nbbb");
        ClickLine(t, 1);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey(null, EditKey.Down);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("aaa\nbbbX", Committed(t, m));
        t.Dispose();
    }

    /// <summary>Shift+Down extends the selection instead of moving the caret.</summary>
    [Fact]
    public void Shift_down_extends_the_selection_a_line()
    {
        var (t, m) = Fields("aaa\nbbb\nccc");
        ClickLine(t, 0);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey(null, EditKey.Down, KeyMods.Shift);
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.Equal("Xbbb\nccc", Committed(t, m));
        t.Dispose();
    }

    /// <summary>
    /// The goal column survives a short line. Going down from column 5 through a two-character line
    /// and on to a long one must land back at column 5 — otherwise the caret walks left every time
    /// it passes something short, which is the difference between arrow keys that feel like a text
    /// editor and ones that do not.
    /// </summary>
    [Fact]
    public void A_run_of_vertical_moves_keeps_its_column_through_a_short_line()
    {
        var (t, m) = Fields("abcdefgh\nxy\nabcdefgh");
        ClickLine(t, 0);
        t.Doc.DispatchKey(null, EditKey.Home);
        for (var i = 0; i < 5; i++) t.Doc.DispatchKey(null, EditKey.Right);   // column 5
        t.Doc.DispatchKey(null, EditKey.Down);                                // the short line
        t.Doc.DispatchKey(null, EditKey.Down);                                // back to a long one
        t.Doc.DispatchKey("X", EditKey.None);

        var got = Committed(t, m);
        output.WriteLine(got.Replace("\n", "\\n"));
        Assert.Equal("abcdefgh\nxy\nabcdeXfgh", got);
        t.Dispose();
    }

    // ---- what a paste is allowed to carry --------------------------------------------------------

    /// <summary>A NUL and a vertical tab have no glyph and no meaning in a field, and used to go
    /// straight into the model. A tab and a newline are text and stay.</summary>
    [Fact]
    public void Pasted_control_characters_are_dropped_but_tabs_and_newlines_survive()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey("a\tb\u0000c\u000Bd\ne", EditKey.None);

        Assert.Equal("a\tbcd\ne", Committed(t, m));
        t.Dispose();
    }

    /// <summary>Windows text is CRLF. One line break, not a break plus an invisible character.</summary>
    [Theory]
    [InlineData("one\r\ntwo", "one\ntwo")]       // CRLF
    [InlineData("one\rtwo", "one\ntwo")]         // a lone CR, as old Mac text and some terminals emit
    [InlineData("one\u2028two", "one\ntwo")]     // Unicode LINE SEPARATOR, which Word produces
    public void Line_breaks_from_anywhere_normalise_to_one_newline(string pasted, string expected)
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey(pasted, EditKey.None);

        Assert.Equal(expected, Committed(t, m));
        t.Dispose();
    }

    /// <summary>…and in a single-line field those breaks still collapse to spaces, as they did.</summary>
    [Fact]
    public void A_single_line_field_still_flattens_a_pasted_break_to_a_space()
    {
        var (t, m) = Fields();
        FocusField(t);
        t.Doc.DispatchKey("one\r\ntwo", EditKey.None);

        Assert.Equal("one two", Committed(t, m, single: true));
        t.Dispose();
    }

    /// <summary>Ordinary text arrives untouched. Stripping control characters must not become a tax
    /// on the content of every other paste — tabs, newlines and astral characters are the three
    /// things a careless filter takes with it.</summary>
    [Theory]
    [InlineData("plain text")]
    [InlineData("a\tb\nc")]
    [InlineData("café \U0001F600 ok")]
    [InlineData("naïve — résumé")]
    public void Text_with_nothing_to_strip_arrives_unchanged(string text)
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey(text, EditKey.None);

        Assert.Equal(text, Committed(t, m));
        t.Dispose();
    }

    // ---- deleting takes a whole character --------------------------------------------------------

    /// <summary>"é" as e + a combining acute is two code points and ONE character. Backspace used to
    /// take the accent and leave the letter, which looks like a keypress that did nothing.</summary>
    [Fact]
    public void Backspace_deletes_a_combining_accent_and_its_letter_together()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey("café", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Backspace);

        Assert.Equal("caf", Committed(t, m));
        t.Dispose();
    }

    /// <summary>An emoji is one character made of two UTF-16 units, and always was handled — asserted
    /// so the grapheme rewrite cannot lose it.</summary>
    [Fact]
    public void Backspace_deletes_an_emoji_whole()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey("a\U0001F600", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Backspace);

        Assert.Equal("a", Committed(t, m));
        t.Dispose();
    }

    /// <summary>A family emoji is several code points joined by zero-width joiners — one character to
    /// the reader, and one backspace.</summary>
    [Fact]
    public void Backspace_deletes_a_joined_emoji_whole()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey("a\U0001F468\u200D\U0001F469\u200D\U0001F466", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Backspace);

        Assert.Equal("a", Committed(t, m));
        t.Dispose();
    }

    /// <summary>Delete does the same thing forwards.</summary>
    [Fact]
    public void Delete_takes_a_whole_character_forwards()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey("éx", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Home);
        t.Doc.DispatchKey(null, EditKey.Delete);

        Assert.Equal("x", Committed(t, m));
        t.Dispose();
    }

    // ---- and the same guarantees through a platform editor --------------------------------------

    /// <summary>A paste in the BROWSER never reaches the keystroke path: the page's real textarea
    /// takes it and pushes the whole new value through SetEditText. The stripping has to happen in
    /// both places or the web host quietly keeps the characters the desktop one drops.</summary>
    [Fact]
    public void Text_pushed_in_by_a_platform_editor_is_sanitised_too()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.SetEditText("a\u0000b\r\nc", 5, 5);

        Assert.Equal("ab\nc", Committed(t, m));
        t.Dispose();
    }

    /// <summary>Plain ASCII still moves and deletes one unit at a time — the ordinary case, which a
    /// grapheme walk is the most likely thing to get subtly wrong.</summary>
    [Fact]
    public void Plain_text_still_deletes_one_character_at_a_time()
    {
        var (t, m) = Fields();
        ClickLine(t, 0);
        t.Doc.DispatchKey("abcd", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Backspace);
        t.Doc.DispatchKey(null, EditKey.Backspace);

        Assert.Equal("ab", Committed(t, m));
        t.Dispose();
    }
}

/// <summary>
/// The schedule a held key repeats on. Holding Backspace on the GLFW desktop window deleted exactly
/// one character and then stopped: Silk's GLFW input backend raises KeyDown for a press and has no
/// case for a repeat, so GLFW's own repeats were dropped. The SDL software window never had the
/// problem, because it reads SDL's events directly and SDL delivers them — so the two desktop
/// windows disagreed about the same keystroke.
///
/// <para>The window owns which key is held; this owns when the next repeat is due, which is the part
/// worth testing and the part that needs no window to test.</para>
/// </summary>
public class RepeatClockTests
{
    [Fact]
    public void Nothing_repeats_before_the_first_press()
    {
        var c = new RepeatClock();
        Assert.False(c.Armed);
        Assert.False(c.ShouldFire(10_000));
    }

    /// <summary>The delay is the pause that stops a deliberate single press turning into two.</summary>
    [Fact]
    public void The_first_repeat_waits_for_the_delay()
    {
        var c = new RepeatClock(delayMs: 450, rateMs: 33);
        c.Press(1000);

        Assert.False(c.ShouldFire(1100));
        Assert.False(c.ShouldFire(1449));
        Assert.True(c.ShouldFire(1450));
    }

    [Fact]
    public void After_the_delay_it_repeats_at_the_rate()
    {
        var c = new RepeatClock(delayMs: 450, rateMs: 33);
        c.Press(0);
        Assert.True(c.ShouldFire(450));

        Assert.False(c.ShouldFire(470));
        Assert.True(c.ShouldFire(483));
        Assert.True(c.ShouldFire(516));
    }

    /// <summary>A key held for a second deletes about the number of characters a person expects, not
    /// one and not a hundred.</summary>
    [Fact]
    public void A_second_of_holding_is_about_seventeen_repeats()
    {
        var c = new RepeatClock(delayMs: 450, rateMs: 33);
        c.Press(0);

        var fired = 0;
        for (var ms = 0; ms <= 1000; ms++) if (c.ShouldFire(ms)) fired++;

        Assert.InRange(fired, 14, 20);
    }

    [Fact]
    public void Releasing_stops_it()
    {
        var c = new RepeatClock(delayMs: 450, rateMs: 33);
        c.Press(0);
        c.Release();

        Assert.False(c.Armed);
        Assert.False(c.ShouldFire(10_000));
    }

    /// <summary>A stall must not come back as a burst. A key held through a two-second hitch deletes
    /// ONE character on the far side of it, not sixty — the next repeat is scheduled from now, not
    /// from when it was due.</summary>
    [Fact]
    public void A_stall_does_not_produce_a_backlog()
    {
        var c = new RepeatClock(delayMs: 450, rateMs: 33);
        c.Press(0);

        Assert.True(c.ShouldFire(2450));          // one frame, two seconds late
        Assert.False(c.ShouldFire(2460));         // and the next is a rate away from NOW
        Assert.True(c.ShouldFire(2483));
    }

    /// <summary>A second press re-arms from scratch, so tapping a key never repeats.</summary>
    [Fact]
    public void A_new_press_restarts_the_delay()
    {
        var c = new RepeatClock(delayMs: 450, rateMs: 33);
        c.Press(0);
        Assert.True(c.ShouldFire(450));

        c.Press(500);
        Assert.False(c.ShouldFire(600));
        Assert.True(c.ShouldFire(950));
    }
}
