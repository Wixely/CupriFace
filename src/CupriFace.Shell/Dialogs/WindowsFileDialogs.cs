using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using CupriFace.Platform;

namespace CupriFace.Shell.Dialogs;

/// <summary>
/// Open / Save / folder on Windows: the Common Item Dialog (<c>IFileOpenDialog</c> /
/// <c>IFileSaveDialog</c>), the one Explorer itself uses.
///
/// <para><b>Each dialog runs on a thread of its own.</b> <c>IModalWindow::Show</c> does not return
/// until the user is done — it runs a modal message loop of its own — so calling it on the host's
/// thread would stop the app drawing for as long as the dialog is open, animations and all. On a
/// separate STA thread it blocks only that thread. The dialog is still OWNED by the app's window, so
/// Windows disables that window while it is up (the user cannot click past a modal dialog) and keeps
/// the dialog above it; the app's loop keeps running and painting underneath.</para>
///
/// <para>Owning a window across threads attaches the two threads' input queues, which is only safe
/// while both keep pumping messages. The host's loop always does — it is the render loop, and nothing
/// here ever makes it wait for the dialog — which is the property that keeps this deadlock-free.</para>
///
/// <para>Through the SOURCE-GENERATED COM layer, as the UIA bridge is (Accessibility/UiaInterop.cs
/// explains why): the runtime's built-in COM is switched off for trimmed apps and absent under
/// NativeAOT, and a published app whose Open button quietly did nothing would be the result.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsFileDialogs(Func<nint?> owner, Action<Action> post) : FileDialogs(post)
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    protected override Task<string[]?> ShowOpenAsync(OpenFileOptions options, CancellationToken cancellationToken) =>
        OnDialogThread(cancellationToken, () =>
        {
            var dialog = Create<IFileOpenDialog>(WinDialogs.ClsidFileOpenDialog, WinDialogs.IidFileOpenDialog);
            try
            {
                var fos = WinDialogs.FosForceFileSystem | WinDialogs.FosNoChangeDir | WinDialogs.FosFileMustExist | WinDialogs.FosPathMustExist;
                if (options.AllowMultiple) fos |= WinDialogs.FosAllowMultiSelect;
                dialog.SetOptions(dialog.GetOptions() | fos);
                Prepare(dialog, options.Title, options.Filters, options.InitialDirectory, out var filterMemory);
                try
                {
                    if (!Show(dialog)) return null;
                }
                finally { FreeFilters(filterMemory); }
                var items = dialog.GetResults();
                try
                {
                    var count = items.GetCount();
                    var paths = new string[count];
                    for (uint i = 0; i < count; i++)
                    {
                        var item = items.GetItemAt(i);
                        try { paths[i] = PathOf(item); }
                        finally { Release(item); }
                    }
                    return paths;
                }
                finally { Release(items); }
            }
            finally { Release(dialog); }
        });

    protected override Task<string?> ShowSaveAsync(SaveFileOptions options, CancellationToken cancellationToken) =>
        OnDialogThread(cancellationToken, () =>
        {
            // IFileSaveDialog adds nothing this needs over IFileDialog, so the object is asked for
            // the base interface — one vtable fewer to keep in step with the SDK.
            var dialog = Create<IFileDialog>(WinDialogs.ClsidFileSaveDialog, WinDialogs.IidFileDialog);
            try
            {
                dialog.SetOptions(dialog.GetOptions() | WinDialogs.FosForceFileSystem | WinDialogs.FosNoChangeDir
                                  | WinDialogs.FosOverwritePrompt | WinDialogs.FosPathMustExist);
                Prepare(dialog, options.Title, options.Filters, options.InitialDirectory, out var filterMemory);
                try
                {
                    if (options.DefaultName is { } name) dialog.SetFileName(name);
                    // Typing "report" with "CSV" selected should save report.csv, as every Windows
                    // app does; the dialog only appends an extension when it is given a default one.
                    if (options.Filters.FirstOrDefault(f => !f.MatchesAll) is { } first)
                        dialog.SetDefaultExtension(first.Extensions[0]);
                    if (!Show(dialog)) return null;
                }
                finally { FreeFilters(filterMemory); }
                var item = dialog.GetResult();
                try { return PathOf(item); }
                finally { Release(item); }
            }
            finally { Release(dialog); }
        });

    protected override Task<string?> ShowFolderAsync(PickFolderOptions options, CancellationToken cancellationToken) =>
        OnDialogThread(cancellationToken, () =>
        {
            var dialog = Create<IFileOpenDialog>(WinDialogs.ClsidFileOpenDialog, WinDialogs.IidFileOpenDialog);
            try
            {
                dialog.SetOptions(dialog.GetOptions() | WinDialogs.FosPickFolders | WinDialogs.FosForceFileSystem
                                  | WinDialogs.FosNoChangeDir | WinDialogs.FosPathMustExist);
                Prepare(dialog, options.Title, [], options.InitialDirectory, out _);
                if (!Show(dialog)) return null;
                var item = dialog.GetResult();
                try { return PathOf(item); }
                finally { Release(item); }
            }
            finally { Release(dialog); }
        });

    private bool Show(IFileDialog dialog)
    {
        // The owner is read on the dialog's thread, at the moment of showing: a second window may
        // have closed since the request was made, and a dead owner would fail the Show outright.
        var hwnd = owner() is { } h && WinDialogs.IsWindow(h) ? h : 0;
        var hr = dialog.Show(hwnd);
        if (hr == WinDialogs.HrCancelled) return false;
        Marshal.ThrowExceptionForHR(hr);
        return true;
    }

    private static void Prepare(IFileDialog dialog, string? title, IReadOnlyList<FileFilter> filters, string? initialDirectory,
        out nint filterMemory)
    {
        filterMemory = 0;
        if (title is { Length: > 0 }) dialog.SetTitle(title);
        if (filters.Count > 0)
        {
            filterMemory = WinDialogs.AllocFilterSpecs(filters);
            dialog.SetFileTypes((uint)filters.Count, filterMemory);
            dialog.SetFileTypeIndex(1);   // one-based
        }
        if (initialDirectory is { } dir && Directory.Exists(dir) &&
            WinDialogs.SHCreateItemFromParsingName(dir, 0, WinDialogs.IidShellItem, out var folder) >= 0)
        {
            // SetFolder, not SetDefaultFolder: the app asked for THIS folder, and the default only
            // applies when the dialog has no memory of its own.
            try { dialog.SetFolder(folder); }
            finally { Marshal.Release(folder); }
        }
    }

    private static void FreeFilters(nint memory)
    {
        if (memory != 0) WinDialogs.FreeFilterSpecs(memory);
    }

    private static string PathOf(IShellItem item)
    {
        var p = item.GetDisplayName(WinDialogs.SigdnFileSysPath);
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    private static T Create<T>(Guid clsid, Guid iid) where T : class
    {
        Marshal.ThrowExceptionForHR(WinDialogs.CoCreateInstance(clsid, 0, WinDialogs.ClsctxInprocServer, iid, out var ptr));
        try { return (T)ComWrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(ptr); }   // the wrapper holds its own reference
    }

    // Released on the dialog's own thread, which is the STA the objects live in, rather than
    // whenever the finaliser gets round to it on a thread that is not.
    private static void Release(object o)
    {
        if (o is ComObject c) c.FinalRelease();
    }

    /// <summary>
    /// Run <paramref name="show"/> on a fresh STA thread and complete with its answer. A
    /// <paramref name="cancellationToken"/> that fires closes the dialog: WM_CLOSE to that thread's
    /// windows, which the dialog treats as Cancel. It is posted until the dialog has gone, because a
    /// token can fire before the dialog has finished appearing, and a single post into that gap
    /// would be lost.
    /// </summary>
    private static Task<T?> OnDialogThread<T>(CancellationToken cancellationToken, Func<T?> show)
    {
        var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threadId = 0u;
        var thread = new Thread(() =>
        {
            Volatile.Write(ref threadId, WinDialogs.GetCurrentThreadId());
            var hr = WinDialogs.CoInitializeEx(0, WinDialogs.CoinitApartmentThreaded | WinDialogs.CoinitDisableOle1Dde);
            try
            {
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                cancellationToken.ThrowIfCancellationRequested();
                result.TrySetResult(show());
            }
            catch (OperationCanceledException) { result.TrySetResult(default); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally
            {
                if (hr >= 0) WinDialogs.CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "CupriFace file dialog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => _ = Task.Run(async () =>
            {
                while (!result.Task.IsCompleted)
                {
                    if (Volatile.Read(ref threadId) is var tid and not 0) WinDialogs.CloseThreadWindows(tid);
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }));
            result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return result.Task;
    }
}

/// <summary>The constants and flat imports behind <see cref="WindowsFileDialogs"/>.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinDialogs
{
    public static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    public static readonly Guid ClsidFileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    public static readonly Guid IidFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");
    public static readonly Guid IidFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
    public static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public const uint ClsctxInprocServer = 0x1;
    public const uint CoinitApartmentThreaded = 0x2, CoinitDisableOle1Dde = 0x4;
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED) — what Show returns when the user cancels.</summary>
    public const int HrCancelled = unchecked((int)0x800704C7);
    public const uint SigdnFileSysPath = 0x80058000;

    public const uint FosOverwritePrompt = 0x2, FosNoChangeDir = 0x8, FosPickFolders = 0x20,
        FosForceFileSystem = 0x40, FosAllowMultiSelect = 0x200, FosPathMustExist = 0x800, FosFileMustExist = 0x1000;

    private const uint WmClose = 0x0010;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint outer, uint context, in Guid riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out nint item);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumThreadWindows(uint threadId, delegate* unmanaged<nint, nint, int> callback, nint lParam);

    /// <summary>Ask every visible top-level window of a dialog thread to close — the dialog, and
    /// anything it opened over itself (an overwrite prompt) first.</summary>
    public static void CloseThreadWindows(uint threadId) => EnumThreadWindows(threadId, &CloseOne, 0);

    [UnmanagedCallersOnly]
    private static int CloseOne(nint hwnd, nint lParam)
    {
        if (IsWindowVisible(hwnd)) PostMessage(hwnd, WmClose, 0, 0);
        return 1;   // keep enumerating
    }

    // COMDLG_FILTERSPEC is two LPCWSTRs; the array and its strings are one allocation each, freed
    // together once Show has returned.
    public static nint AllocFilterSpecs(IReadOnlyList<FileFilter> filters)
    {
        // A count word first, so the free knows how many pairs of strings to release.
        var block = (nint*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * (1 + 2 * filters.Count)));
        block[0] = filters.Count;
        for (var i = 0; i < filters.Count; i++)
        {
            block[1 + 2 * i] = Marshal.StringToCoTaskMemUni(filters[i].Name);
            block[2 + 2 * i] = Marshal.StringToCoTaskMemUni(filters[i].ToWindowsPattern());
        }
        return (nint)(block + 1);
    }

    public static void FreeFilterSpecs(nint specs)
    {
        var block = (nint*)specs - 1;
        var count = (int)block[0];
        for (var i = 0; i < 2 * count; i++) Marshal.FreeCoTaskMem(block[1 + i]);
        NativeMemory.Free(block);
    }
}

// The slice of shobjidl_core.idl the dialogs use. The UIA bridge's rules apply (UiaInterop.cs):
// member ORDER is the vtable order, never reorder or insert; GUIDs verbatim from the SDK; methods
// whose HRESULT is an answer rather than a failure (Show's "cancelled") are [PreserveSig].
// Pointers the dialogs only pass through are nint, so no wrapper is built for them.

[GeneratedComInterface, Guid("b4db1657-70d7-485e-8e3e-6fcb5a5c1802")]
[SupportedOSPlatform("windows")]
internal partial interface IModalWindow
{
    [PreserveSig] int Show(nint hwndOwner);
}

[GeneratedComInterface, Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
[SupportedOSPlatform("windows")]
internal partial interface IFileDialog : IModalWindow
{
    void SetFileTypes(uint count, nint filterSpecs);
    void SetFileTypeIndex(uint index);
    uint GetFileTypeIndex();
    uint Advise(nint events);
    void Unadvise(uint cookie);
    void SetOptions(uint options);
    uint GetOptions();
    void SetDefaultFolder(nint shellItem);
    void SetFolder(nint shellItem);
    nint GetFolder();
    nint GetCurrentSelection();
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    nint GetFileName();
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
    IShellItem GetResult();
    void AddPlace(nint shellItem, int place);
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
    void Close(int hr);
    void SetClientGuid(in Guid guid);
    void ClearClientData();
    void SetFilter(nint filter);
}

[GeneratedComInterface, Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
[SupportedOSPlatform("windows")]
internal partial interface IFileOpenDialog : IFileDialog
{
    IShellItemArray GetResults();
    IShellItemArray GetSelectedItems();
}

[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[SupportedOSPlatform("windows")]
internal partial interface IShellItem
{
    nint BindToHandler(nint bindContext, in Guid handler, in Guid riid);
    nint GetParent();
    /// <summary>A CoTaskMem string the caller frees.</summary>
    nint GetDisplayName(uint sigdn);
    uint GetAttributes(uint mask);
    int Compare(nint other, uint hint);
}

[GeneratedComInterface, Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[SupportedOSPlatform("windows")]
internal partial interface IShellItemArray
{
    nint BindToHandler(nint bindContext, in Guid handler, in Guid riid);
    nint GetPropertyStore(int flags, in Guid riid);
    nint GetPropertyDescriptionList(nint keyType, in Guid riid);
    uint GetAttributes(int flags, uint mask);
    uint GetCount();
    IShellItem GetItemAt(uint index);
    nint EnumItems();
}
