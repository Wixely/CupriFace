using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CupriFace.Accessibility;
using Tmds.DBus.Protocol;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// The Linux AT-SPI2 bridge: exposes the engine's semantics tree (<see cref="AccessibilityNode"/>)
/// to Orca and any other assistive technology, as an application on the accessibility bus.
/// DESIGN.md §5's Linux leg, and the sibling of <see cref="UiaBridge"/>.
///
/// Threading — deliberately identical to the Windows bridge, because the hazard is identical:
///   - D-Bus method calls arrive on the connection's own thread. They only ever read the current
///     immutable SNAPSHOT (tree + id maps + screen transform), swapped in whole by the UI thread
///     after each drawn frame. No AT thread ever touches the live document.
///   - Actions (DoAction, GrabFocus, Value.Set) are queued and drained by the UI thread's tick,
///     each running through the document's ordinary interaction machinery.
///   - Focus changes are detected while publishing (path diff) and emitted as AT-SPI signals.
///
/// PORTABILITY: unlike the Windows leg, this file needs no interop whatsoever. AT-SPI is not an
/// OS API — it is D-Bus over a Unix socket — so there is no P/Invoke and no native library here,
/// only managed IL that never runs off Linux. The whole tree is served by ONE handler registered
/// with HandlesChildPaths, so no per-node D-Bus object churn as the UI rebuilds.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class AtSpiBridge : IPathMethodHandler
{
    /// <summary>What every AT-facing call reads: an immutable view of one painted frame.</summary>
    internal sealed record Snapshot(
        AccessibilityNode Root,
        IReadOnlyList<AccessibilityNode> ById,          // index = the object's AT-SPI id
        IReadOnlyDictionary<string, int> IdByPath,
        string? FocusedPath,
        float Scale,
        float OriginX,
        float OriginY);

    private readonly CupriFace.CupriDocument _doc;
    private readonly Action _requestFrame;
    private readonly string _appName;
    private readonly ConcurrentQueue<Action<CupriFace.CupriDocument>> _actions = new();

    private DBusConnection? _bus;
    private string _uniqueName = "";
    private string _desktopName = "";                    // the registry's own bus name, from Embed
    private string _desktopPath = AtSpi.NullPath;
    private volatile Snapshot? _snapshot;

    // Ids are handed out per STRUCTURAL PATH and never reused, so an AT that cached an object
    // keeps talking about the same element across the per-keystroke rebuild (the same reason
    // UiaBridge caches providers by path).
    private readonly Dictionary<string, int> _idByPath = new(StringComparer.Ordinal);
    private readonly List<string> _pathById = new();
    private readonly object _idLock = new();

    string IPathMethodHandler.Path => AtSpi.PathPrefix.TrimEnd('/');
    bool IPathMethodHandler.HandlesChildPaths => true;

    internal Snapshot? Current => _snapshot;

    /// <summary>Kill switch, mirroring CUPRIFACE_UIA on the Windows side.</summary>
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("CUPRIFACE_ATSPI") is not ("0" or "false" or "FALSE");

    private AtSpiBridge(CupriFace.CupriDocument doc, Action requestFrame, string appName)
    {
        _doc = doc;
        _requestFrame = requestFrame;
        _appName = appName;
    }

    /// <summary>Connect to the accessibility bus and register as an application, or return null
    /// (with a note on stderr) when AT-SPI isn't available here — a desktop with no accessibility
    /// stack running is the normal case, and the app must not care.</summary>
    public static AtSpiBridge? TryAttach(CupriFace.CupriDocument doc, Action requestFrame, string appName)
    {
        try
        {
            var bridge = new AtSpiBridge(doc, requestFrame, appName);
            // Bounded: a missing or wedged a11y bus must delay startup by seconds, not forever.
            if (!bridge.ConnectAsync().Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("timed out connecting to the accessibility bus");
            return bridge;
        }
        catch (Exception ex)
        {
            var reason = (ex as AggregateException)?.InnerException ?? ex;
            Console.Error.WriteLine($"[CupriFace] AT-SPI bridge unavailable ({reason.GetType().Name}: {reason.Message}); continuing without it.");
            return null;
        }
    }

    private async Task ConnectAsync()
    {
        // The a11y bus is a SEPARATE bus from the session bus; the session bus only tells you
        // where it lives. (Probed on a hosted runner: unix:path=/run/user/<uid>/at-spi/bus.)
        var address = await GetA11yBusAddressAsync().ConfigureAwait(false);
        var bus = new DBusConnection(address);
        await bus.ConnectAsync().ConfigureAwait(false);
        _bus = bus;
        _uniqueName = bus.UniqueName ?? "";
        bus.AddMethodHandler(this);

        // Embed: hand the registry our (bus name, root path); it hands back the desktop object
        // that becomes our parent. This is the moment the app appears to every AT on the box.
        // The writer is a ref struct, so the message is built in its own synchronous scope —
        // it cannot live across the await.
        MessageBuffer EmbedMessage()
        {
            using var writer = bus.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: AtSpi.RegistryName, path: AtSpi.RootPath,
                @interface: AtSpi.IfaceSocket, member: "Embed", signature: "(so)");
            writer.WriteStructureStart();
            writer.WriteString(_uniqueName);
            writer.WriteObjectPath(AtSpi.RootPath);
            return writer.CreateMessage();
        }

        var (name, path) = await bus.CallMethodAsync(EmbedMessage(),
            static (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                r.AlignStruct();
                return (r.ReadString(), r.ReadObjectPath().ToString());
            }, null).ConfigureAwait(false);
        _desktopName = name;
        _desktopPath = path;
    }

    private static async Task<string> GetA11yBusAddressAsync()
    {
        var session = DBusAddress.Session
            ?? throw new InvalidOperationException("no session bus (DBUS_SESSION_BUS_ADDRESS unset)");
        using var sessionBus = new DBusConnection(session);
        await sessionBus.ConnectAsync().ConfigureAwait(false);

        MessageBuffer GetAddressMessage()
        {
            using var writer = sessionBus.GetMessageWriter();
            writer.WriteMethodCallHeader(AtSpi.BusName, AtSpi.BusPath, AtSpi.BusName, "GetAddress", null);
            return writer.CreateMessage();
        }

        return await sessionBus.CallMethodAsync(GetAddressMessage(),
            static (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);
    }

    // ---- UI-thread side (same contract as UiaBridge) -----------------------------------------

    /// <summary>Run queued AT actions on the UI thread. True if any ran (→ mark dirty).</summary>
    public bool DrainActions()
    {
        var any = false;
        while (_actions.TryDequeue(out var action))
        {
            any = true;
            try { action(_doc); }
            catch { /* a stale path or a mid-rebuild race must never take the app down */ }
        }
        return any;
    }

    /// <summary>Publish a fresh snapshot after a drawn frame, and tell ATs if focus moved.</summary>
    public void PublishFrame(float logicalWidth, float logicalHeight, float scale, (int X, int Y) clientOrigin)
    {
        if (_bus is null) return;

        var root = _doc.BuildAccessibilityTree(logicalWidth, logicalHeight);
        var byId = new List<AccessibilityNode>();
        var idByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        string? focusedPath = null;

        void Index(AccessibilityNode n)
        {
            var id = IdFor(n.Path);
            while (byId.Count <= id) byId.Add(root);       // dense list; gaps point at the root
            byId[id] = n;
            idByPath[n.Path] = id;
            if (n.Focused) focusedPath = n.Path;
            foreach (var c in n.Children) Index(c);
        }
        Index(root);

        var previousFocus = _snapshot?.FocusedPath;
        _snapshot = new Snapshot(root, byId, idByPath, focusedPath,
            scale <= 0 ? 1f : scale, clientOrigin.X, clientOrigin.Y);

        if (focusedPath is not null && focusedPath != previousFocus && idByPath.TryGetValue(focusedPath, out var fid))
            EmitFocusChanged(fid);
    }

    private int IdFor(string path)
    {
        lock (_idLock)
        {
            if (_idByPath.TryGetValue(path, out var id)) return id;
            id = _pathById.Count;
            _pathById.Add(path);
            _idByPath[path] = id;
            return id;
        }
    }

    // ---- events -------------------------------------------------------------------------------

    /// <summary>What makes Tab talk: state-changed(focused) plus the legacy focus event, which
    /// older ATs still listen for.</summary>
    private void EmitFocusChanged(int id)
    {
        try
        {
            Emit(AtSpi.EventObject, "StateChanged", ObjectPathFor(id), "focused", 1, 0);
            Emit(AtSpi.EventFocus, "Focus", ObjectPathFor(id), "", 0, 0);
        }
        catch { /* an AT that vanished mid-signal is its problem, not ours */ }
    }

    private void Emit(string iface, string member, string path, string detail, int detail1, int detail2)
    {
        if (_bus is not { } bus) return;
        using var writer = bus.GetMessageWriter();
        // AT-SPI event signature: (s detail, i detail1, i detail2, v any_data, (so) sender-app).
        writer.WriteSignalHeader(destination: null, path: path, @interface: iface, member: member,
            signature: "siiv(so)");
        writer.WriteString(detail);
        writer.WriteInt32(detail1);
        writer.WriteInt32(detail2);
        writer.WriteVariantInt32(0);
        writer.WriteStructureStart();
        writer.WriteString(_uniqueName);
        writer.WriteObjectPath(AtSpi.RootPath);
        bus.TrySendMessage(writer.CreateMessage());
    }

    // ---- D-Bus dispatch (connection thread; reads the snapshot only) --------------------------

    internal static string ObjectPathFor(int id) => AtSpi.PathPrefix + id.ToString();

    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        try { Dispatch(context); }
        catch (Exception ex) { context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message); }
        return default;
    }

    private void Dispatch(MethodContext context)
    {
        var request = context.Request;
        var path = request.PathAsString ?? "";
        var iface = request.InterfaceAsString ?? "";
        var member = request.MemberAsString ?? "";

        var snapshot = _snapshot;
        if (snapshot is null) { context.ReplyError("org.freedesktop.DBus.Error.Failed", "no frame published yet"); return; }

        var isRoot = path == AtSpi.RootPath;
        AccessibilityNode? node = null;
        if (!isRoot)
        {
            var idText = path.Length > AtSpi.PathPrefix.Length ? path[AtSpi.PathPrefix.Length..] : "";
            if (!int.TryParse(idText, out var id) || id < 0 || id >= snapshot.ById.Count)
            { context.ReplyUnknownMethodError(); return; }
            node = snapshot.ById[id];
        }

        switch (iface)
        {
            case AtSpi.IfaceProperties: Properties(context, snapshot, node, isRoot); return;
            case AtSpi.IfaceAccessible: Accessible(context, snapshot, node, isRoot, member); return;
            case AtSpi.IfaceComponent when node is not null: Component(context, snapshot, node, member); return;
            case AtSpi.IfaceAction when node is not null: ActionIface(context, snapshot, node, member); return;
            case AtSpi.IfaceApplication when isRoot: ApplicationIface(context, member); return;
            default: context.ReplyUnknownMethodError(); return;
        }
    }

    // org.freedesktop.DBus.Properties — where ATs read most attributes from.
    private void Properties(MethodContext context, Snapshot snap, AccessibilityNode? node, bool isRoot)
    {
        var reader = context.Request.GetBodyReader();
        var iface = reader.ReadString();
        var member = context.Request.MemberAsString;

        if (member == "GetAll")
        {
            using var w = context.CreateReplyWriter("a{sv}");
            var dict = w.WriteDictionaryStart();
            foreach (var (key, kind, s, i, d, so) in PropertiesOf(iface, snap, node, isRoot))
            {
                w.WriteDictionaryEntryStart();
                w.WriteString(key);
                switch (kind)
                {
                    case 's': w.WriteVariantString(s!); break;
                    case 'i': w.WriteVariantInt32(i); break;
                    case 'u': w.WriteVariantUInt32((uint)i); break;
                    case 'd': w.WriteVariantDouble(d); break;
                    case 'o': w.WriteVariantObjectPath(so!); break;
                }
            }
            w.WriteDictionaryEnd(dict);
            context.Reply(w.CreateMessage());
            return;
        }

        var prop = reader.ReadString();
        if (member == "Set")
        {
            // The only settable property AT-SPI cares about here: Value.CurrentValue (a slider
            // being driven by an AT). Routed through the SAME path a real drag takes.
            if (iface == AtSpi.IfaceValue && prop == "CurrentValue" && node is not null)
            {
                var value = reader.ReadVariantValue().GetDouble();
                var target = node.Path;
                Post(doc => doc.AccessibilitySetValue(target, value));
                using var ok = context.CreateReplyWriter(null);
                context.Reply(ok.CreateMessage());
                return;
            }
            context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", prop);
            return;
        }

        // Get
        foreach (var (key, kind, s, i, d, so) in PropertiesOf(iface, snap, node, isRoot))
        {
            if (key != prop) continue;
            using var w = context.CreateReplyWriter("v");
            switch (kind)
            {
                case 's': w.WriteVariantString(s!); break;
                case 'i': w.WriteVariantInt32(i); break;
                case 'u': w.WriteVariantUInt32((uint)i); break;
                case 'd': w.WriteVariantDouble(d); break;
                case 'o': w.WriteVariantObjectPath(so!); break;
            }
            context.Reply(w.CreateMessage());
            return;
        }
        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", prop);
    }

    // (name, kind, string, int, double, objectPath) — a tiny tagged union beats six overloads.
    private IEnumerable<(string, char, string?, int, double, string?)> PropertiesOf(
        string iface, Snapshot snap, AccessibilityNode? node, bool isRoot)
    {
        if (iface == AtSpi.IfaceAccessible)
        {
            yield return ("Name", 's', isRoot ? _appName : NameOf(node!), 0, 0, null);
            yield return ("Description", 's', "", 0, 0, null);
            yield return ("Locale", 's', "C", 0, 0, null);
            yield return ("AccessibleId", 's', isRoot ? "" : node!.AutomationId ?? "", 0, 0, null);
            yield return ("ChildCount", 'i', null, isRoot ? 1 : node!.Children.Count, 0, null);
            // Parent is (so) — a struct, not a bare path — so it can't ride this table; ATs that
            // ask for it by name get it from Accessible.GetParent below.
        }
        else if (iface == AtSpi.IfaceApplication && isRoot)
        {
            yield return ("ToolkitName", 's', "CupriFace", 0, 0, null);
            yield return ("Version", 's', typeof(AtSpiBridge).Assembly.GetName().Version?.ToString() ?? "1.0", 0, 0, null);
            yield return ("AtspiVersion", 's', "2.1", 0, 0, null);
            yield return ("Id", 'i', null, 0, 0, null);
        }
        else if (iface == AtSpi.IfaceValue && node is not null && AtSpi.HasValue(node))
        {
            yield return ("CurrentValue", 'd', null, 0, node.Now ?? 0, null);
            yield return ("MinimumValue", 'd', null, 0, node.Min ?? 0, null);
            yield return ("MaximumValue", 'd', null, 0, node.Max ?? 100, null);
            yield return ("MinimumIncrement", 'd', null, 0, 1, null);
        }
        else if (iface == AtSpi.IfaceAction && node is not null)
        {
            yield return ("NActions", 'i', null, AtSpi.ActionNameOf(node) is null ? 0 : 1, 0, null);
        }
    }

    private static string NameOf(AccessibilityNode n) => n.Name ?? n.Value ?? "";

    private void Accessible(MethodContext context, Snapshot snap, AccessibilityNode? node, bool isRoot, string member)
    {
        switch (member)
        {
            case "GetRole":
            {
                using var w = context.CreateReplyWriter("u");
                w.WriteUInt32(isRoot ? AtSpi.RoleApplication : AtSpi.RoleOf(node!.Role));
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetRoleName":
            case "GetLocalizedRoleName":
            {
                using var w = context.CreateReplyWriter("s");
                w.WriteString(AtSpi.RoleNameOf(isRoot ? AtSpi.RoleApplication : AtSpi.RoleOf(node!.Role)));
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetState":
            {
                using var w = context.CreateReplyWriter("au");
                var (low, high) = isRoot ? ((uint)((1u << 8) | (1u << 24) | (1u << 25) | (1u << 30)), 0u)
                                         : AtSpi.StatesOf(node!);
                var arr = w.WriteArrayStart(DBusType.UInt32);
                w.WriteUInt32(low);
                w.WriteUInt32(high);
                w.WriteArrayEnd(arr);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetChildAtIndex":
            {
                var index = context.Request.GetBodyReader().ReadInt32();
                var child = ChildOf(snap, node, isRoot, index);
                using var w = context.CreateReplyWriter("(so)");
                WriteRef(w, child);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetChildren":
            {
                using var w = context.CreateReplyWriter("a(so)");
                var arr = w.WriteArrayStart(DBusType.Struct);
                if (isRoot) WriteRef(w, RootChildPath(snap));
                else foreach (var c in node!.Children) WriteRef(w, PathFor(snap, c));
                w.WriteArrayEnd(arr);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetParent":
            {
                using var w = context.CreateReplyWriter("(so)");
                if (isRoot) { w.WriteStructureStart(); w.WriteString(_desktopName); w.WriteObjectPath(_desktopPath); }
                else if (node!.Parent is { } parent) WriteRef(w, PathFor(snap, parent));
                else { w.WriteStructureStart(); w.WriteString(_uniqueName); w.WriteObjectPath(AtSpi.RootPath); }
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetIndexInParent":
            {
                using var w = context.CreateReplyWriter("i");
                w.WriteInt32(isRoot ? 0 : node!.Parent?.Children.IndexOf(node) ?? 0);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetApplication":
            {
                using var w = context.CreateReplyWriter("(so)");
                w.WriteStructureStart(); w.WriteString(_uniqueName); w.WriteObjectPath(AtSpi.RootPath);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetInterfaces":
            {
                using var w = context.CreateReplyWriter("as");
                var arr = w.WriteArrayStart(DBusType.String);
                w.WriteString(AtSpi.IfaceAccessible);
                if (isRoot) w.WriteString(AtSpi.IfaceApplication);
                else
                {
                    w.WriteString(AtSpi.IfaceComponent);
                    if (AtSpi.ActionNameOf(node!) is not null) w.WriteString(AtSpi.IfaceAction);
                    if (AtSpi.HasValue(node!)) w.WriteString(AtSpi.IfaceValue);
                }
                w.WriteArrayEnd(arr);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetAttributes":
            {
                using var w = context.CreateReplyWriter("a{ss}");
                var dict = w.WriteDictionaryStart();
                if (!isRoot)
                {
                    w.WriteDictionaryEntryStart(); w.WriteString("toolkit"); w.WriteString("CupriFace");
                    w.WriteDictionaryEntryStart(); w.WriteString("xml-roles"); w.WriteString(node!.Role);
                }
                w.WriteDictionaryEnd(dict);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetRelationSet":
            {
                using var w = context.CreateReplyWriter("a(ua(so))");
                var arr = w.WriteArrayStart(DBusType.Struct);
                w.WriteArrayEnd(arr);
                context.Reply(w.CreateMessage());
                return;
            }
            default: context.ReplyUnknownMethodError(); return;
        }
    }

    private void Component(MethodContext context, Snapshot snap, AccessibilityNode node, string member)
    {
        switch (member)
        {
            case "GetExtents":
            {
                var (x, y, w2, h) = ToScreen(snap, node.Bounds, context.Request.GetBodyReader().ReadUInt32());
                using var w = context.CreateReplyWriter("(iiii)");
                w.WriteStructureStart();
                w.WriteInt32(x); w.WriteInt32(y); w.WriteInt32(w2); w.WriteInt32(h);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetPosition":
            {
                var (x, y, _, _) = ToScreen(snap, node.Bounds, context.Request.GetBodyReader().ReadUInt32());
                using var w = context.CreateReplyWriter("(ii)");
                w.WriteStructureStart(); w.WriteInt32(x); w.WriteInt32(y);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetSize":
            {
                var (_, _, w2, h) = ToScreen(snap, node.Bounds, 1);
                using var w = context.CreateReplyWriter("(ii)");
                w.WriteStructureStart(); w.WriteInt32(w2); w.WriteInt32(h);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GrabFocus":
            {
                var target = node.Path;
                Post(doc => doc.AccessibilityFocus(target));
                using var w = context.CreateReplyWriter("b");
                w.WriteBool(true);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetLayer":
            {
                using var w = context.CreateReplyWriter("u");
                w.WriteUInt32(3);          // ATSPI_LAYER_WIDGET
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetAlpha":
            {
                using var w = context.CreateReplyWriter("d");
                w.WriteDouble(1.0);
                context.Reply(w.CreateMessage());
                return;
            }
            case "Contains":
            {
                var r = context.Request.GetBodyReader();
                int px = r.ReadInt32(), py = r.ReadInt32();
                var (x, y, w2, h) = ToScreen(snap, node.Bounds, r.ReadUInt32());
                using var w = context.CreateReplyWriter("b");
                w.WriteBool(px >= x && px < x + w2 && py >= y && py < y + h);
                context.Reply(w.CreateMessage());
                return;
            }
            default: context.ReplyUnknownMethodError(); return;
        }
    }

    private void ActionIface(MethodContext context, Snapshot snap, AccessibilityNode node, string member)
    {
        var action = AtSpi.ActionNameOf(node);
        switch (member)
        {
            case "GetNActions":
            {
                using var w = context.CreateReplyWriter("i");
                w.WriteInt32(action is null ? 0 : 1);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetName":
            case "GetLocalizedName":
            {
                using var w = context.CreateReplyWriter("s");
                w.WriteString(action ?? "");
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetDescription":
            case "GetKeyBinding":
            {
                using var w = context.CreateReplyWriter("s");
                w.WriteString("");
                context.Reply(w.CreateMessage());
                return;
            }
            case "DoAction":
            {
                var index = context.Request.GetBodyReader().ReadInt32();
                var ok = action is not null && index == 0;
                if (ok)
                {
                    var target = node.Path;
                    Post(doc => doc.AccessibilityActivate(target));
                }
                using var w = context.CreateReplyWriter("b");
                w.WriteBool(ok);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetActions":
            {
                using var w = context.CreateReplyWriter("a(sss)");
                var arr = w.WriteArrayStart(DBusType.Struct);
                if (action is not null)
                {
                    w.WriteStructureStart();
                    w.WriteString(action); w.WriteString(""); w.WriteString("");
                }
                w.WriteArrayEnd(arr);
                context.Reply(w.CreateMessage());
                return;
            }
            default: context.ReplyUnknownMethodError(); return;
        }
    }

    private void ApplicationIface(MethodContext context, string member)
    {
        switch (member)
        {
            case "GetApplicationBusAddress":
            {
                using var w = context.CreateReplyWriter("s");
                w.WriteString("");
                context.Reply(w.CreateMessage());
                return;
            }
            // RegisterEventListener / DeregisterEventListener are advisory; acknowledging them
            // keeps chatty ATs happy without us tracking per-client interest.
            case "RegisterEventListener":
            case "DeregisterEventListener":
            {
                using var w = context.CreateReplyWriter(null);
                context.Reply(w.CreateMessage());
                return;
            }
            default: context.ReplyUnknownMethodError(); return;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private string PathFor(Snapshot snap, AccessibilityNode node) =>
        snap.IdByPath.TryGetValue(node.Path, out var id) ? ObjectPathFor(id) : AtSpi.NullPath;

    private string RootChildPath(Snapshot snap) => PathFor(snap, snap.Root);

    private string ChildOf(Snapshot snap, AccessibilityNode? node, bool isRoot, int index)
    {
        if (isRoot) return index == 0 ? RootChildPath(snap) : AtSpi.NullPath;
        if (node is null || index < 0 || index >= node.Children.Count) return AtSpi.NullPath;
        return PathFor(snap, node.Children[index]);
    }

    private void WriteRef(MessageWriter w, string objectPath)
    {
        w.WriteStructureStart();
        w.WriteString(_uniqueName);
        w.WriteObjectPath(objectPath);
    }

    /// <summary>CSS px in the window → AT-SPI coordinates. coordType 0 = screen, 1 = window.</summary>
    private static (int X, int Y, int W, int H) ToScreen(Snapshot snap, (float X, float Y, float W, float H) b, uint coordType)
    {
        var scale = snap.Scale;
        var ox = coordType == 0 ? snap.OriginX : 0;
        var oy = coordType == 0 ? snap.OriginY : 0;
        return ((int)(ox + b.X * scale), (int)(oy + b.Y * scale), (int)(b.W * scale), (int)(b.H * scale));
    }

    private void Post(Action<CupriFace.CupriDocument> action)
    {
        _actions.Enqueue(action);
        _requestFrame();   // wake the render loop so the tick drains promptly
    }

    public void Dispose()
    {
        try { _bus?.Dispose(); } catch { /* shutting down */ }
    }
}
