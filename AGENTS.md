# Agent instructions

See **[CLAUDE.md](CLAUDE.md)** — the instructions live there in full, and this file exists so that
agents which look for `AGENTS.md` find them too.

The short version, because it is the thing most often missed and it changes the result the most:

**This engine renders HTML to pixels with no browser, and it fails silently by design** — an
unsupported CSS property is ignored, an unknown element lays out and stays blank, and nothing
throws. You cannot find those by reading the markup. Run `CupriDoctor.Check(html, css)`, render the
document with `doc.RenderToImage(w, h)`, and **open the PNG with the Read tool and look at it.**
Both are headless: no window, no GPU, no browser.
