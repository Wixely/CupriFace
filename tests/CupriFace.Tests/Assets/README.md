# Test-owned assets

Fixtures that exist **only** to be tested against, and that nothing in `src/` ships.

Most font fixtures do not live here. They live in `src/WebFonts/`, because the tests that use them
compare a decoded face against the very TTF both web hosts embed, and the OFL covering it is already
in that folder — a fixture there adds no new licence surface. See the comment in
`CupriFace.Tests.csproj`.

This folder is for the cases where that reasoning does not apply: a fixture no shipped code has any
use for, carrying a licence of its own.

## Caveat-700-latin.woff2

The font from [#214](https://github.com/Wixely/CupriFace/issues/214), kept because it is the only
thing in the repository that exercises the **short `loca`** path of the WOFF 2 decoder.

`NotoSans-Regular.woff2` — the fixture everything else uses — has `indexFormat = 1`, so for the
decoder's whole life the `indexFormat == 0` branch was never executed by a test. That is the branch
the bug was reported on: the decoder rebuilt `glyf` around 75% larger than the table it came from,
which only ever failed once a font's real `glyf` sat near the 131,070 bytes a short `loca` can
address. Caveat is such a font, and every browser reads it.

Do not replace it with a subset, a re-build, or a different weight without checking the decoder
still fails the old way on the old code — the properties that make it a useful fixture are narrow:

| | |
|---|---|
| `numGlyphs` | 352 |
| `indexFormat` | 0 (short `loca`) |
| declared `glyf` origLength | 87,532 — fits the short form with 43 KB to spare |
| what the old decoder produced | 152,828 — overflowed at glyph 287 |

- **Source:** `https://cdn.jsdelivr.net/fontsource/fonts/caveat@latest/latin-700-normal.woff2`
  (Fontsource's build of Google Fonts Caveat, Bold, latin subset), retrieved 2026-09-27.
- **SHA-256:** `15f9638095ad5ec9816f93d66805ca1a87e71dc5a76b596bfce1c78d4704c405`
- **Licence:** SIL Open Font License 1.1 — `OFL-Caveat.txt`, copied verbatim from
  [googlefonts/caveat](https://github.com/googlefonts/caveat). Copyright 2014 The Caveat Project
  Authors. It is redistributed here unmodified, as the OFL requires, and is not embedded in or
  packed into anything under `src/`.
