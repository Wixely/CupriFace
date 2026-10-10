# CupriFace.Media.Windows

Optional Windows video backend for `<cupri-video>`. It implements CupriFace's portable
`IVideoBackend`/`IVideoPlayer` contract while LibVLC renders into a child HWND managed by the
desktop host-composited-surface lifecycle.

```csharp
using var video = new LibVlcVideoBackend();
DesktopHost.Run(app, document => document.UseVideo(video));
```

The backend requests D3D11VA hardware decoding and enables LibVLCSharp's cross-platform hardware
decode flag. Unsupported codecs, profiles, dimensions, or drivers may still fall back to software;
set `CUPRIFACE_MEDIA_DEBUG=1` to print the selected decoder and D3D presentation path.

Remote HTTPS video locations are passed directly to LibVLC for streaming with a short network
buffer. They are not downloaded into CupriFace's bounded in-memory source loader or written to the
media cache, and their URLs are not included in backend diagnostics. Local and embedded sources
continue to use the bounded loader and temporary media cache.

This is a native child-window presentation path. It is zero-copy after decode, but Win32 child
windows have airspace constraints: CupriFace cannot paint arbitrary translucent content over the
video. The standard 44-logical-pixel video control bar is reserved below the child window. Affine-
transformed placements are hidden rather than rendered in the wrong position. A future shared-
texture backend can remove those constraints without changing the application-facing contract.

Dependencies are pinned by the project:

- LibVLCSharp 3.10.1, LGPL-2.1-or-later
- VideoLAN.LibVLC.Windows 3.0.24, LGPL-2.1-or-later

CupriFace and this adapter remain MIT licensed. The NuGet package carries an explicit third-party
notice, the complete LGPL-2.1 text, and corresponding-source links for the separately distributed
VideoLAN components. Those files are copied into consuming application output under
`THIRD-PARTY-NOTICES/` and `licenses/third-party/`. LibVLCSharp, LibVLC, and its plugins remain
separate, replaceable files even when the application uses .NET single-file publishing.
Each CupriFace GitHub release also attaches the pinned LibVLCSharp, VLC, and native-package build
source archives beside its binary and NuGet assets.

The native runtime adds approximately 128 MB before packaging/compression. Applications choosing
this optional package are responsible for complying with the applicable LGPL notices and source/
relocation requirements when distributing it.
