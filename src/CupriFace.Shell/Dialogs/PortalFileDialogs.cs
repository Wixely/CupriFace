using System.Runtime.Versioning;
using System.Text;
using CupriFace.Platform;
using Tmds.DBus.Protocol;

namespace CupriFace.Shell.Dialogs;

/// <summary>
/// Open / Save / folder on Linux: the xdg-desktop-portal <c>FileChooser</c>, over the session bus.
///
/// <para>The portal rather than GTK or Qt directly because it is the one interface every desktop
/// answers — GNOME, KDE, wlroots compositors, and Flatpak and Snap sandboxes, where it is the ONLY
/// way out — and it is D-Bus, which this host already speaks (the AT-SPI bridge), so it costs no
/// native toolkit at all. The desktop draws its own dialog in its own process; nothing here blocks.</para>
///
/// <para>The portal answers asynchronously: the call returns a REQUEST object path, and the user's
/// answer arrives later as that object's <c>Response</c> signal. The path is predictable from a
/// token we choose, so the signal is subscribed BEFORE the call — otherwise a fast answer could
/// arrive before anyone was listening for it.</para>
///
/// <para>No portal on the session (a bare X11 box, a minimal container) faults the request with a
/// <see cref="NotSupportedException"/> that says so; there is no fallback dialog to fake.</para>
///
/// <para><b>UNVERIFIED</b>: written against the portal specification
/// (org.freedesktop.portal.FileChooser, version 3+), never run — the development machine is Windows.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class PortalFileDialogs(Action<Action> post) : FileDialogs(post)
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string ChooserInterface = "org.freedesktop.portal.FileChooser";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    private DBusConnection? _bus;

    /// <summary>A portal is only worth offering on a machine with a session bus to find it on.</summary>
    public static bool Available => DBusAddress.Session is not null;

    protected override async Task<string[]?> ShowOpenAsync(OpenFileOptions options, CancellationToken cancellationToken) =>
        await RequestAsync("OpenFile", options.Title ?? "Open", new Request
        {
            Multiple = options.AllowMultiple,
            Filters = options.Filters,
            Folder = options.InitialDirectory,
        }, cancellationToken).ConfigureAwait(false);

    protected override async Task<string?> ShowSaveAsync(SaveFileOptions options, CancellationToken cancellationToken)
    {
        var uris = await RequestAsync("SaveFile", options.Title ?? "Save", new Request
        {
            Filters = options.Filters,
            Folder = options.InitialDirectory,
            Name = options.DefaultName,
        }, cancellationToken).ConfigureAwait(false);
        return uris is { Length: > 0 } ? uris[0] : null;
    }

    protected override async Task<string?> ShowFolderAsync(PickFolderOptions options, CancellationToken cancellationToken)
    {
        var uris = await RequestAsync("OpenFile", options.Title ?? "Choose a folder", new Request
        {
            Directory = true,
            Folder = options.InitialDirectory,
        }, cancellationToken).ConfigureAwait(false);
        return uris is { Length: > 0 } ? uris[0] : null;
    }

    private sealed class Request
    {
        public bool Multiple;
        public bool Directory;
        public IReadOnlyList<FileFilter> Filters = [];
        public string? Folder;
        public string? Name;
    }

    private async Task<string[]?> RequestAsync(string method, string title, Request request, CancellationToken cancellationToken)
    {
        DBusConnection bus;
        try { bus = await ConnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { throw new NotSupportedException($"No session bus for the file chooser portal ({ex.Message}).", ex); }

        // The request object's path is /org/freedesktop/portal/desktop/request/SENDER/TOKEN, SENDER
        // being our unique name without its ':' and with '.' as '_' (the portal specification).
        var token = "cupri" + Guid.NewGuid().ToString("N");
        var sender = (bus.UniqueName ?? "").TrimStart(':').Replace('.', '_');
        var requestPath = $"{ObjectPath}/request/{sender}/{token}";

        var answer = new TaskCompletionSource<(uint Code, string[]? Uris)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await bus.WatchSignalAsync(null, requestPath, RequestInterface, "Response",
            static (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                var code = r.ReadUInt32();
                var results = r.ReadDictionaryOfStringToVariantValue();
                string[]? uris = results.TryGetValue("uris", out var v) ? v.GetArray<string>() : null;
                return (code, uris);
            },
            (Notification<(uint, string[]?)> n) =>
            {
                if (n.Exception is { } ex) answer.TrySetException(ex);
                else if (n.HasValue) answer.TrySetResult(n.Value);
            }, ObserverFlags.None, false, null).ConfigureAwait(false);

        MessageBuffer Call()
        {
            // Not a `using`: WriteFilter takes the writer by ref, which a using variable cannot be.
            var w = bus.GetMessageWriter();
            try
            {
            w.WriteMethodCallHeader(Service, ObjectPath, ChooserInterface, method, "ssa{sv}");
            w.WriteString("");          // parent window: none we can name (an X11 id or a Wayland export)
            w.WriteString(title);
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart(); w.WriteString("handle_token"); w.WriteVariantString(token);
            if (request.Multiple) { w.WriteDictionaryEntryStart(); w.WriteString("multiple"); w.WriteVariantBool(true); }
            if (request.Directory) { w.WriteDictionaryEntryStart(); w.WriteString("directory"); w.WriteVariantBool(true); }
            if (request.Name is { } name) { w.WriteDictionaryEntryStart(); w.WriteString("current_name"); w.WriteVariantString(name); }
            if (request.Folder is { } folder)
            {
                // A byte string, NUL-terminated, because a path need not be valid UTF-8.
                w.WriteDictionaryEntryStart(); w.WriteString("current_folder");
                w.WriteSignature("ay");
                w.WriteArray(Encoding.UTF8.GetBytes(folder + "\0"));
            }
            if (request.Filters.Count > 0)
            {
                w.WriteDictionaryEntryStart(); w.WriteString("filters");
                w.WriteSignature("a(sa(us))");
                var filters = w.WriteArrayStart(DBusType.Struct);
                foreach (var f in request.Filters) WriteFilter(ref w, f);
                w.WriteArrayEnd(filters);
                w.WriteDictionaryEntryStart(); w.WriteString("current_filter");
                w.WriteSignature("(sa(us))");
                WriteFilter(ref w, request.Filters[0]);
            }
            w.WriteDictionaryEnd(dict);
            return w.CreateMessage();
            }
            finally { w.Dispose(); }
        }

        try
        {
            await bus.CallMethodAsync(Call(), static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null)
                .ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            throw new NotSupportedException($"The desktop has no file chooser portal ({ex.ErrorName}: {ex.Message}).", ex);
        }

        // Cancelling asks the portal to close its dialog; the Response that follows (or the token's
        // own cancellation, if none comes) ends the wait.
        using var closing = cancellationToken.Register(() =>
        {
            answer.TrySetCanceled(cancellationToken);
            using var w = bus.GetMessageWriter();
            w.WriteMethodCallHeader(Service, requestPath, RequestInterface, "Close", null);
            bus.TrySendMessage(w.CreateMessage());
        });

        var (code, uris) = await answer.Task.ConfigureAwait(false);
        // 0 = chosen; 1 = the user cancelled; 2 = ended some other way. Only 0 carries an answer.
        if (code != 0 || uris is null) return null;
        return uris.Select(ToLocalPath).Where(p => p is not null).Select(p => p!).ToArray();
    }

    // One (name, [(0, glob)…]) entry. Globs are case-sensitive on Linux, so "png" becomes
    // "*.[pP][nN][gG]" — otherwise PHOTO.PNG from a camera card would be hidden by an Images filter.
    private static void WriteFilter(ref MessageWriter w, FileFilter f)
    {
        w.WriteStructureStart();
        w.WriteString(f.Name);
        var globs = w.WriteArrayStart(DBusType.Struct);
        foreach (var ext in f.Extensions)
        {
            w.WriteStructureStart();
            w.WriteUInt32(0);
            w.WriteString(ext == "*" ? "*" : "*." + CaseInsensitive(ext));
        }
        w.WriteArrayEnd(globs);
    }

    private static string CaseInsensitive(string ext)
    {
        var sb = new StringBuilder(ext.Length * 4);
        foreach (var c in ext)
        {
            var lo = char.ToLowerInvariant(c);
            var up = char.ToUpperInvariant(c);
            if (lo == up) sb.Append(c);
            else sb.Append('[').Append(lo).Append(up).Append(']');
        }
        return sb.ToString();
    }

    private static string? ToLocalPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;

    private async Task<DBusConnection> ConnectAsync()
    {
        if (_bus is { } existing) return existing;
        var address = DBusAddress.Session ?? throw new InvalidOperationException("DBUS_SESSION_BUS_ADDRESS is not set");
        var bus = new DBusConnection(address);
        await bus.ConnectAsync().ConfigureAwait(false);
        _bus = bus;
        return bus;
    }
}
