using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CupriFace.Platform;
using CupriFace.Shell.Accessibility;

namespace CupriFace.Shell.Dialogs;

/// <summary>
/// Open / Save / folder on macOS: <c>NSOpenPanel</c> and <c>NSSavePanel</c>, shown as a SHEET on the
/// window that asked (or as a free-standing panel when it has none to hang from).
///
/// <para><b>Never <c>runModal</c>.</b> That would run AppKit's modal loop on the host's thread —
/// which on macOS is the main thread, the only one AppKit allows — and the app would stop drawing
/// until the panel closed. <c>beginSheetModalForWindow:completionHandler:</c> returns at once; the
/// panel lives on in the run loop the host already pumps, and calls back when the user is done.</para>
///
/// <para>The callback is an Objective-C BLOCK, which C# has no syntax for, so one is laid out by hand
/// (the Block ABI's global-block form: no captures, nothing to copy or dispose) whose invoke pointer
/// is an <see cref="UnmanagedCallersOnlyAttribute"/> function. One block per instance, kept for the
/// life of the process: AppKit copies and later releases it, and a global block's copy is the block
/// itself, so freeing it after the callback would hand AppKit freed memory to release.</para>
///
/// <para><b>UNVERIFIED</b>: written against the AppKit documentation and the Block ABI, never run —
/// the development machine has no Mac. The first macOS run should exercise all three dialogs.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacFileDialogs : FileDialogs
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";
    private const int BlockIsGlobal = 1 << 28;
    private const nint ModalResponseOk = 1;

    // Which request a block's callback belongs to. Touched only on the main thread (AppKit calls
    // completion handlers there, and requests are made there), but locked because a lock is cheaper
    // than proving that.
    private static readonly Dictionary<nint, MacFileDialogs> ByBlock = [];

    private readonly Func<nint?> _window;
    private readonly Action<Action> _post;
    private nint _block;
    private nint _panel;                         // retained while shown
    private bool _openPanel;
    private TaskCompletionSource<string[]?>? _pending;

    public MacFileDialogs(Func<nint?> window, Action<Action> post) : base(post)
    {
        _window = window;
        _post = post;
    }

    protected override Task<string[]?> ShowOpenAsync(OpenFileOptions options, CancellationToken cancellationToken) =>
        Begin(open: true, cancellationToken, panel =>
        {
            SetBool(panel, "setCanChooseFiles:", true);
            SetBool(panel, "setCanChooseDirectories:", false);
            SetBool(panel, "setAllowsMultipleSelection:", options.AllowMultiple);
            Common(panel, options.Title, options.Filters, options.InitialDirectory);
        });

    protected override async Task<string?> ShowSaveAsync(SaveFileOptions options, CancellationToken cancellationToken)
    {
        var paths = await Begin(open: false, cancellationToken, panel =>
        {
            SetBool(panel, "setCanCreateDirectories:", true);
            if (options.DefaultName is { } name)
                ObjC.Send(panel, ObjC.Sel("setNameFieldStringValue:"), ObjC.NSString(name));
            Common(panel, options.Title, options.Filters, options.InitialDirectory);
        }).ConfigureAwait(false);
        return paths is { Length: > 0 } ? paths[0] : null;
    }

    protected override async Task<string?> ShowFolderAsync(PickFolderOptions options, CancellationToken cancellationToken)
    {
        var paths = await Begin(open: true, cancellationToken, panel =>
        {
            SetBool(panel, "setCanChooseFiles:", false);
            SetBool(panel, "setCanChooseDirectories:", true);
            SetBool(panel, "setCanCreateDirectories:", true);
            SetBool(panel, "setAllowsMultipleSelection:", false);
            Common(panel, options.Title, [], options.InitialDirectory);
        }).ConfigureAwait(false);
        return paths is { Length: > 0 } ? paths[0] : null;
    }

    // Called on the host's thread, which on macOS is the main thread — the one AppKit requires.
    private Task<string[]?> Begin(bool open, CancellationToken cancellationToken, Action<nint> configure)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var cls = ObjC.objc_getClass(open ? "NSOpenPanel" : "NSSavePanel");
            var panel = ObjC.Send(cls, ObjC.Sel(open ? "openPanel" : "savePanel"));
            if (panel == 0) return Task.FromException<string[]?>(new InvalidOperationException("AppKit returned no panel."));
            _panel = ObjC.Send(panel, ObjC.Sel("retain"));
            _openPanel = open;
            configure(panel);

            _pending = new TaskCompletionSource<string[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = BlockFor();
            if (_window() is { } window and not 0)
                SendPP(panel, ObjC.Sel("beginSheetModalForWindow:completionHandler:"), window, block);
            else
                ObjC.Send(panel, ObjC.Sel("beginWithCompletionHandler:"), block);

            // Cancelling closes the panel as its own Cancel button would — on the main thread, which
            // is where the token's callback is sent rather than run.
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => _post(() =>
                {
                    if (_panel != 0) ObjC.Send(_panel, ObjC.Sel("cancel:"), (nint)0);
                }));
            return _pending.Task;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private void Completed(nint response)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            string[]? paths = null;
            if (response == ModalResponseOk && _panel != 0)
            {
                if (_openPanel)
                {
                    var urls = ObjC.Send(_panel, ObjC.Sel("URLs"));
                    var count = (int)ObjC.Send(urls, ObjC.Sel("count"));
                    var list = new List<string>(count);
                    for (var i = 0; i < count; i++)
                        if (PathOfUrl(ObjC.Send(urls, ObjC.Sel("objectAtIndex:"), (nint)i)) is { } p) list.Add(p);
                    paths = [.. list];
                }
                else if (PathOfUrl(ObjC.Send(_panel, ObjC.Sel("URL"))) is { } p)
                    paths = [p];
            }
            if (_panel != 0) { ObjC.Send(_panel, ObjC.Sel("release")); _panel = 0; }
            var pending = _pending;
            _pending = null;
            pending?.TrySetResult(paths);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static string? PathOfUrl(nint url) => url == 0 ? null : ObjC.ToManaged(ObjC.Send(url, ObjC.Sel("path")));

    private static void Common(nint panel, string? title, IReadOnlyList<FileFilter> filters, string? initialDirectory)
    {
        if (title is { Length: > 0 })
        {
            // Modern macOS hides a panel's title bar; the message line is what the user reads.
            ObjC.Send(panel, ObjC.Sel("setTitle:"), ObjC.NSString(title));
            ObjC.Send(panel, ObjC.Sel("setMessage:"), ObjC.NSString(title));
        }
        // AppKit has no per-entry type list, only one set of allowed extensions: the union of the
        // filters, unless one of them admits everything.
        if (filters.Count > 0 && !filters.Any(f => f.MatchesAll))
        {
            var types = filters.SelectMany(f => f.Extensions).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ObjC.NSString).ToArray();
            ObjC.Send(panel, ObjC.Sel("setAllowedFileTypes:"), ObjC.NSArray(types));
        }
        if (initialDirectory is { } dir)
        {
            var url = ObjC.Send(ObjC.objc_getClass("NSURL"), ObjC.Sel("fileURLWithPath:"), ObjC.NSString(dir));
            if (url != 0) ObjC.Send(panel, ObjC.Sel("setDirectoryURL:"), url);
        }
    }

    private static void SetBool(nint panel, string selector, bool value) =>
        ObjC.Send(panel, ObjC.Sel(selector), (nint)(value ? 1 : 0));

    // ---- the completion block ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    private static nint _descriptor;

    private unsafe nint BlockFor()
    {
        if (_block != 0) return _block;
        if (_descriptor == 0)
        {
            var d = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            d->Size = (nuint)sizeof(BlockLiteral);
            _descriptor = (nint)d;
        }
        if (!NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out var libSystem) ||
            !NativeLibrary.TryGetExport(libSystem, "_NSConcreteGlobalBlock", out var isa))
            throw new PlatformNotSupportedException("The Objective-C block runtime was not found.");
        var b = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        b->Isa = isa;
        b->Flags = BlockIsGlobal;
        b->Invoke = (nint)(delegate* unmanaged<nint, nint, void>)&Invoke;
        b->Descriptor = _descriptor;
        _block = (nint)b;
        lock (ByBlock) ByBlock[_block] = this;
        return _block;
    }

    // void (^)(NSModalResponse result): the block itself first, then the NSInteger response.
    [UnmanagedCallersOnly]
    private static void Invoke(nint block, nint response)
    {
        MacFileDialogs? owner;
        lock (ByBlock) ByBlock.TryGetValue(block, out owner);
        owner?.Completed(response);
    }

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial nint SendPP(nint self, nint sel, nint a, nint b);

    [LibraryImport(Runtime)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Runtime)]
    private static partial void objc_autoreleasePoolPop(nint pool);
}
