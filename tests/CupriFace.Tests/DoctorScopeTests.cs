using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Two ways the doctor could answer about a document it was not asked about, or stay silent about
/// one it was (#183, #185). Both were invisible, and both made the report say the one thing a
/// checker must never say wrongly: nothing is wrong.
/// </summary>
public class DoctorScopeTests(ITestOutputHelper output)
{
    // Two unsupported properties, in an inline <style> — which is where nearly every document keeps
    // its CSS, and the case that used to be skipped entirely.
    private const string Noisy = """
        <div class="a">x</div>
        <style>.a { float: left; mix-blend-mode: multiply; width:10px; height:10px; }</style>
        """;
    private const string Clean = """
        <div class="b">x</div>
        <style>.b { width:10px; height:10px; }</style>
        """;

    private static int Cf0050(DoctorReport r) => r.Findings.Count(f => f.Code == "CF0050");

    // ---- #183: a null stylesheet is not an instruction to stop looking ---------------------------

    /// <summary>
    /// `null` and `""` must mean the same thing. `null` is the obvious argument when there is no
    /// external stylesheet, and the signature (`string? css`) invites it — but it used to switch the
    /// whole CSS pass off, taking the document's own &lt;style&gt; with it, and the report then said
    /// no problems found.
    /// </summary>
    [Fact]
    public void A_null_stylesheet_still_checks_the_documents_own_style_block()
    {
        var withNull = CupriDoctor.Check(Noisy, null);
        var withEmpty = CupriDoctor.Check(Noisy, "");

        output.WriteLine($"null: {Cf0050(withNull)}   empty: {Cf0050(withEmpty)}");
        Assert.Equal(2, Cf0050(withNull));
        Assert.Equal(Cf0050(withEmpty), Cf0050(withNull));
    }

    /// <summary>The same document, the same answer, whichever way the caller spells "no stylesheet".
    /// Asserted on the whole report rather than a count: a difference anywhere is the bug.</summary>
    [Fact]
    public void The_two_spellings_produce_the_same_report()
    {
        var a = CupriDoctor.Check(Noisy, null).Findings.Select(f => f.Code + "|" + f.Message).Order();
        var b = CupriDoctor.Check(Noisy, "").Findings.Select(f => f.Code + "|" + f.Message).Order();
        Assert.Equal(b, a);
    }

    /// <summary>A clean document is still clean — the fix must not have made the checker noisy.</summary>
    [Fact]
    public void A_document_with_nothing_wrong_still_reports_nothing()
    {
        Assert.Equal(0, Cf0050(CupriDoctor.Check(Clean, null)));
    }

    /// <summary>An external stylesheet is still read when there is one, and its findings still carry
    /// a line number from it.</summary>
    [Fact]
    public void An_external_stylesheet_is_still_checked_and_still_located()
    {
        var r = CupriDoctor.Check("<div class='a'>x</div>", "\n\n.a { float: left; }");
        var f = Assert.Single(r.Findings, x => x.Code == "CF0050");
        output.WriteLine($"{f.Code} line {f.Line}: {f.Message}");
        Assert.Contains("float", f.Message);
        Assert.Equal(3, f.Line);
    }

    // ---- #185: a finding belongs to the document it came from --------------------------------------

    /// <summary>
    /// Checks running at the same time must not trade findings. The sink used to be one field for
    /// the whole process, so a finding was as likely to be LOST by the document that produced it as
    /// gained by one that did not — which is what proves it moved rather than being copied.
    /// </summary>
    [Fact]
    public void Concurrent_checks_do_not_trade_findings()
    {
        int cleanGained = 0, noisyLost = 0;
        Parallel.For(0, 160, i =>
        {
            var noisy = i % 2 == 0;
            var n = Cf0050(CupriDoctor.Check(noisy ? Noisy : Clean, "", width: 400, height: 300));
            if (!noisy && n > 0) Interlocked.Increment(ref cleanGained);
            if (noisy && n != 2) Interlocked.Increment(ref noisyLost);
        });

        output.WriteLine($"clean gained {cleanGained}/80, noisy lost {noisyLost}/80");
        Assert.Equal(0, cleanGained);
        Assert.Equal(0, noisyLost);
    }

    /// <summary>
    /// The worse half: a document being RENDERED on another thread, with no doctor involved at all,
    /// used to plant its warnings in a check of a different document. There was no way to work
    /// around it either — a lock over every Check does not help, because the other thread is not
    /// calling Check.
    /// </summary>
    [Fact]
    public void A_render_on_another_thread_does_not_leak_into_a_check()
    {
        using var stop = new CancellationTokenSource();
        var renderer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var doc = CupriDocument.Load(Noisy, null);
                doc.Refresh();
                using (doc.RenderToImage(400, 300)) { }
            }
        });

        var strays = 0;
        for (var i = 0; i < 120; i++)
            if (Cf0050(CupriDoctor.Check(Clean, "", width: 400, height: 300)) > 0) strays++;

        stop.Cancel();
        renderer.Wait(TimeSpan.FromSeconds(10));

        output.WriteLine($"strays: {strays}/120");
        Assert.Equal(0, strays);
    }

    /// <summary>…and the check still finds its OWN findings while that is going on. A sink scoped so
    /// tightly that it caught nothing would pass the test above and be useless.</summary>
    [Fact]
    public void A_check_still_finds_its_own_while_another_thread_renders()
    {
        using var stop = new CancellationTokenSource();
        var renderer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var doc = CupriDocument.Load(Clean, null);
                doc.Refresh();
                using (doc.RenderToImage(400, 300)) { }
            }
        });

        var found = 0;
        for (var i = 0; i < 60; i++)
            if (Cf0050(CupriDoctor.Check(Noisy, "", width: 400, height: 300)) == 2) found++;

        stop.Cancel();
        renderer.Wait(TimeSpan.FromSeconds(10));

        output.WriteLine($"complete reports: {found}/60");
        Assert.Equal(60, found);
    }
}
