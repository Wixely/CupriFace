using Microsoft.Playwright;
using Xunit;

namespace CupriFace.WebTouchGate;

/// <summary>
/// The web host's touch contract, driven by a real browser.
///
/// The bug that made this gate necessary: fingers went down the MOUSE path, so a phone in a
/// browser activated buttons on touch-DOWN and lists stopped dead where a thumb left them. Nothing
/// caught it because nothing drove a browser with a finger.
///
/// Each test measures the CANVAS, because that is all the engine produces — there is no DOM to
/// query. A frame hash is the honest observable: "did what is on screen change", nothing more.
/// </summary>
[Collection("web")]
public class TouchTests(WebHostFixture host)
{
    // A cheap, stable digest of the painted canvas.
    private const string HashFn = """
        () => { const c = document.getElementById('cupri'); const d = c.toDataURL('image/png');
                let h = 0; for (let i = 0; i < d.length; i += 97) h = (h * 31 + d.charCodeAt(i)) | 0; return h; }
        """;

    private static Task<int> HashAsync(IPage p) => p.EvaluateAsync<int>(HashFn);

    [Fact]
    public async Task A_tap_activates_on_release_not_on_touch_down()
    {
        var page = await host.PhoneAsync();
        var before = await HashAsync(page);

        // A real finger: down, held still, then lifted.
        await page.Touchscreen.TapAsync(30, 200);        // warm the path, then measure a held press
        await page.WaitForTimeoutAsync(400);

        var settled = await HashAsync(page);
        await page.Mouse.MoveAsync(0, 0);                 // ensure no hover state confuses the read

        // Now the real assertion, with an explicit down/hold/up so the two halves are separable.
        var start = await HashAsync(page);
        await page.EvaluateAsync("""
            () => { const c = document.getElementById('cupri');
              c.dispatchEvent(new PointerEvent('pointerdown', { pointerId: 21, pointerType: 'touch',
                clientX: 30, clientY: 320, bubbles: true, cancelable: true, isPrimary: true, buttons: 1 })); }
            """);
        await page.WaitForTimeoutAsync(300);
        var held = await HashAsync(page);

        await page.EvaluateAsync("""
            () => { const c = document.getElementById('cupri');
              c.dispatchEvent(new PointerEvent('pointerup', { pointerId: 21, pointerType: 'touch',
                clientX: 30, clientY: 320, bubbles: true, cancelable: true, isPrimary: true, buttons: 0 })); }
            """);
        await page.WaitForTimeoutAsync(500);
        var after = await HashAsync(page);

        Assert.True(start == held,
            $"[{host.Host}] holding a finger down changed the screen — activation on touch-down, the desktop contract");
        Assert.True(held != after,
            $"[{host.Host}] releasing the finger changed nothing — the tap never activated");
    }

    [Fact]
    public async Task A_drag_scrolls_and_then_coasts()
    {
        var page = await host.PhoneAsync();

        // A fast upward swipe over the content column, well clear of the sidebar rail.
        var moved = await page.EvaluateAsync<bool>("""
            async () => {
              const c = document.getElementById('cupri');
              const hash = () => { const d = c.toDataURL('image/png'); let h = 0;
                for (let i = 0; i < d.length; i += 97) h = (h * 31 + d.charCodeAt(i)) | 0; return h; };
              const wait = ms => new Promise(r => setTimeout(r, ms));
              // EVERY EVENT CARRIES ITS OWN TIMELINE, 14ms apart, rather than whatever the
              // scheduler managed. The recogniser keeps a velocity ring of the last 0.1s and needs
              // two samples in it to fling at all (TouchInput.Prune). A synthetic PointerEvent takes
              // its timeStamp when it is CONSTRUCTED, so on a host whose frames block the main
              // thread — Mono paints in ~90ms — `await wait(14)` lands ~90-100ms late, consecutive
              // moves fall outside the ring window, and the fling branch is skipped. The drag still
              // scrolls, so the failure reads as "the fling never ran" (#223).
              //
              // A REAL finger does not have this problem: hardware events are stamped when the touch
              // happened, and a busy thread only batches their delivery. Pinning the timeline gives
              // the synthetic gesture the property a real one already has, which is why this is a
              // fix and not a fudge. Measured: unpinned, this swipe failed to coast on a developer
              // machine even with no artificial load; pinned, it coasts with a 60ms busy-wait
              // deliberately stalled between every move.
              let clock = performance.now();
              const ev = (t, y) => {
                const e = new PointerEvent(t, { pointerId: 31, pointerType: 'touch',
                  clientX: 200, clientY: y, bubbles: true, cancelable: true, isPrimary: true,
                  buttons: t === 'pointerup' ? 0 : 1 });
                clock += 14;
                Object.defineProperty(e, 'timeStamp', { value: clock });
                c.dispatchEvent(e);
              };
              const start = hash();
              // The swipe must be SHORTER than the shortest page's scroll range (~535px: the
              // controls section at this width). A swipe longer than the range exhausts the scroll
              // while the finger is still down, and the only post-release motion is a rubber-band
              // settle that is over before the first hash sample lands (a hash is toDataURL on a
              // 1082x2202 canvas plus a Playwright round-trip) — which the momentum assertion
              // below then misreads as "the fling never ran". ~196px of leftover range is what
              // this gate provably passes with; 320px of travel leaves ~215px on the shortest page.
              ev('pointerdown', 760); await wait(16);
              for (let y = 760; y >= 440; y -= 35) { ev('pointermove', y); await wait(14); }
              ev('pointerup', 440);
              return start !== hash();
            }
            """);
        Assert.True(moved, $"[{host.Host}] a finger dragged across the page and nothing scrolled");

        // Momentum: the frame must keep changing AFTER the finger is gone.
        var atRelease = await HashAsync(page);
        await page.WaitForTimeoutAsync(180);
        var t180 = await HashAsync(page);
        await page.WaitForTimeoutAsync(500);
        var t680 = await HashAsync(page);

        Assert.True(atRelease != t180 || t180 != t680,
            $"[{host.Host}] the list stopped dead where the thumb left it — the fling never ran");
    }

    [Fact]
    public async Task The_capability_signal_follows_the_pointer_in_use()
    {
        var page = await host.PhoneAsync();
        var coarse = () => page.EvaluateAsync<bool>("() => globalThis.__cupri.isCoarse()");

        // A touch device reports coarse before anything is touched (the opening guess), and any
        // real pointer corrects it — a laptop with a touchscreen is honestly both.
        Assert.True(await coarse(), $"[{host.Host}] a touch device did not report a coarse pointer at boot");

        await page.EvaluateAsync("""
            () => { const c = document.getElementById('cupri');
              for (const t of ['pointerdown', 'pointerup'])
                c.dispatchEvent(new PointerEvent(t, { pointerId: 41, pointerType: 'mouse',
                  clientX: 200, clientY: 400, bubbles: true, cancelable: true, isPrimary: true,
                  buttons: t === 'pointerup' ? 0 : 1 })); }
            """);
        await page.WaitForTimeoutAsync(200);
        Assert.False(await coarse(), $"[{host.Host}] a mouse arrived and the app still called itself coarse");

        await page.EvaluateAsync("""
            () => { const c = document.getElementById('cupri');
              for (const t of ['pointerdown', 'pointerup'])
                c.dispatchEvent(new PointerEvent(t, { pointerId: 42, pointerType: 'touch',
                  clientX: 200, clientY: 400, bubbles: true, cancelable: true, isPrimary: true,
                  buttons: t === 'pointerup' ? 0 : 1 })); }
            """);
        await page.WaitForTimeoutAsync(200);
        Assert.True(await coarse(), $"[{host.Host}] a finger came back and the app stayed fine");
    }

    [Fact]
    public async Task The_canvas_refuses_the_browsers_own_gestures()
    {
        // touch-action:none is what makes every test above possible: without it the browser keeps
        // the gesture for its own scrolling and pointermove stops arriving mid-drag.
        var page = await host.PhoneAsync();
        var ta = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.getElementById('cupri')).touchAction");
        Assert.Equal("none", ta);
    }
}
