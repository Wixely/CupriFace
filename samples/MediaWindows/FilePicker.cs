using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CupriFace.Samples.MediaWindows;

/// <summary>
/// The OS open-file dialog, so this sample can be pointed at a real MKV without restarting.
///
/// <para>A sample reaching for Win32 directly is deliberate and worth stating: CupriFace does not
/// offer a file dialog and should not. A dialog is a native, modal, per-platform window — the
/// engine draws into ONE surface and owns no window at all, which is the whole layering law
/// (DESIGN.md). So picking a file is the application's business, exactly as it is in any other
/// toolkit-free app, and this is what that looks like. The engine's own route for getting a file
/// IN is the opposite direction: a drop, which <c>DesktopHost</c> already delivers through
/// <c>DroppedFile</c> and needs no dialog.</para>
///
/// <para><c>LibraryImport</c> rather than <c>DllImport</c>, per CLAUDE.md: the Viewer publishes
/// AOT and #131 was the UIA bridge going silently dead under exactly that.</para>
///
/// <para>The import was checked without showing a dialog, by calling it with a deliberately zero
/// <c>StructSize</c> — the field the dialog validates first. It returns false with
/// <c>CommDlgExtendedError() == CDERR_STRUCTSIZE (0x0001)</c>, which only happens if the entry
/// point resolved and the struct reached it. Opening the dialog for real needs somebody to click
/// the button.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class FilePicker
{
    private const int MaxPath = 32768;                 // long-path capable; the dialog writes into this
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnExplorer      = 0x00080000;
    private const int OfnNoChangeDir   = 0x00000008;   // never move the process's working directory

    /// <summary>
    /// Show the dialog and return the chosen path, or null when the user cancelled.
    ///
    /// <para>This BLOCKS the calling thread, which is the UI thread: it is called from a click
    /// handler inside the host's loop, so the window stops drawing while the dialog is up and
    /// resumes when it closes. That is what a modal dialog means, and it is the right behaviour
    /// here — there is nothing to animate behind it that the user can reach.</para>
    /// </summary>
    internal static string? Open(string title = "Open a media file")
    {
        // Double-null-terminated pairs of label\0pattern\0 … \0 — the Win32 filter format.
        const string filter = "Media files\0*.mkv;*.mp4;*.m4v;*.mov;*.webm;*.avi;*.ts;*.mp3;*.m4a;*.flac\0" +
                              "Matroska (*.mkv)\0*.mkv\0" +
                              "All files\0*.*\0\0";

        var file = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        var filterPtr = Marshal.StringToHGlobalUni(filter);
        var titlePtr = Marshal.StringToHGlobalUni(title);
        try
        {
            // The buffer is the INPUT as well as the output (an initial filename), so it must start
            // as an empty string rather than whatever the allocation happened to contain.
            Marshal.WriteInt16(file, 0, 0);

            var args = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                // Owned by the foreground window, so the dialog is modal to OUR window and cannot
                // end up behind it. Zero is a valid fallback: an unowned dialog still works.
                HwndOwner = GetForegroundWindow(),
                Filter = filterPtr,
                FilterIndex = 1,
                File = file,
                MaxFile = MaxPath,
                Title = titlePtr,
                Flags = OfnFileMustExist | OfnPathMustExist | OfnExplorer | OfnNoChangeDir,
            };

            if (GetOpenFileNameW(ref args)) return Marshal.PtrToStringUni(file);

            // Zero means the user cancelled, which is not a failure. Anything else is, and saying
            // which beats a silently dead button.
            var error = CommDlgExtendedError();
            if (error != 0) Console.WriteLine($"picker  : dialog failed (CDERR 0x{error:X4})");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(titlePtr);
        }
    }

    // Blittable throughout (nint/int/short only), so no marshalling is generated and LibraryImport
    // can take it by ref. Field ORDER is the ABI — do not reorder.
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint HwndOwner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDir;
        public nint Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public nint DefExt;
        public nint CustData;
        public nint Hook;
        public nint TemplateName;
        public nint Reserved;
        public int Reserved2;
        public int FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(ref OpenFileName args);

    [LibraryImport("comdlg32.dll")]
    private static partial int CommDlgExtendedError();

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
