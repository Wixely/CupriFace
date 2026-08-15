# MonoVM crash on Android: string content dereferenced as a string reference

The evidence file for the runtime defect the Android probe found, kept beside the probe so the
upstream report can be filed from it verbatim. Status: **worked around** (this app runs CoreCLR,
`<UseMonoRuntime>false</UseMonoRuntime>`); **not yet filed** upstream.

## Summary

A .NET 10 (`net10.0-android`, Android workload 36.1.x, Mono runtime pack 10.0.11) app crashes
during ordinary string-list membership tests (`List<string>.Contains` under an HTML parser's
class-token handling). The failure is a **native fault inside Mono-generated code for the string
comparison path**: a register that should hold a string *reference* instead holds eight bytes of
string *content*, which is then dereferenced. CoreCLR (`UseMonoRuntime=false`) runs the identical
IL correctly on the same devices.

## Where it was pinned: a four-variant experiment

Same app, same device, one variable per leg (CI runs 8 and 9 of `android-probe.yml`):

| runtime | AOT | trimmed | result |
|---|---|---|---|
| Mono | profiled AOT | yes | crash |
| Mono | JIT | yes | crash |
| Mono | JIT | no | crash (same stack, parameter names intact — trimmer exonerated) |
| CoreCLR | — | default | **runs the full app** |

JIT and AOT share Mono's code generator, which is why disabling AOT changed nothing.

## Manifestation 1 — x64 emulator: an impossible managed NRE

android-34 `google_apis;x86_64` emulator (AVX-class host CPU). The fault is converted to a
managed `NullReferenceException` whose top frame is the **static** `String.Equals(String, String)`
— an overload that null-checks both arguments and cannot throw NRE if executed faithfully:

```
System.NullReferenceException: Object reference not set to an instance of an object
   at System.String.Equals(String a, String b)
   at System.Collections.Generic.StringEqualityComparer.Equals(String x, String y)
   at System.Collections.Generic.EqualityComparer`1[String].IndexOf(...)
   at System.Array.IndexOf[String](String[] array, String value, Int32 startIndex, Int32 count)
   at System.Collections.Generic.List`1[String].IndexOf(String item)
   at System.Collections.Generic.List`1[String].Contains(String item)
   at AngleSharp.Dom.TokenList.Add(String[] tokens)
   ...
```

## Manifestation 2 — arm64 hardware: a raw SIGSEGV, and the smoking-gun registers

Real device, Android 16/17-era build (`google/rango`, kernel 6.6.142-android15), SVE-capable CPU
(`vg = 2` in the dump). No managed exception is raised at all — the process dies on the signal,
so no managed `catch` can observe it:

```
Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0067006800000016
    x1  0067006800000006  x2  0067006800000006 ... x24 0067006800000006  x26 0067006800000006
backtrace:
      #00 pc 0000000000011c88  .../lib/arm64/libaot-System.Private.CoreLib.dll.so
```

Decode of the poisoned value `0x0067006800000006`, little-endian:

```
06 00 00 00   int32  6        ← a string's length field
68 00         char  'h'       ← its first character
67 00         char  'g'       ← its second character
```

That is the `[length | char0 | char1]` quadword of a **6-character string beginning "hg"** —
and `"hgroup"` is a six-character HTML tag name interned in AngleSharp's tag tables, precisely
the kind of string a DOM-building comparison loop walks. The faulting read is at **+0x10**, which
is the offset of `length` in Mono's string object layout. So the generated code:

1. loaded eight bytes of string *content* (a legitimate step in a vectorised comparison),
2. left/placed it in a register consumed as a string *reference*,
3. dereferenced it to fetch the "string's" length → SEGV_MAPERR.

Both failing environments die in vectorised-comparison territory (AVX-class on x64, SVE-capable
on arm64); CoreCLR's independent code generator is clean on both.

## Reproduction

Minimal shape: `net10.0-android`, Release, default (Mono) runtime; exercise
`List<string>.Contains` through AngleSharp 1.7.0's `TokenList.Add` by parsing an HTML document
and adding a class token — or run this repo's probe:

```
dotnet publish samples/AndroidProbe/AndroidProbe.csproj -c Release -r android-arm64 \
  -p:UseMonoRuntime=true -o out/mono          # crashes on device
dotnet publish samples/AndroidProbe/AndroidProbe.csproj -c Release -r android-arm64 \
  -o out/coreclr                              # csproj defaults to CoreCLR; runs
```

The CI workflow (`.github/workflows/android-probe.yml`) reproduces the x64 half unattended on a
hosted emulator, all four variants in one run.

## Workaround

`<UseMonoRuntime>false</UseMonoRuntime>` (CoreCLR). Costs APK size today — arm64 20.7 MB vs
Mono's 12.0 MB for this app — and is where Android .NET is heading regardless.
