// Can the macOS accessibility bridge be written in PURE C#, with no native shim?
//
// This is the question that decides the design. NSAccessibility is not a protocol you implement
// by answering messages on a socket (the way AT-SPI is) — the OS calls Objective-C methods on
// your view. To serve it from C# we must build an Objective-C class AT RUNTIME and give it method
// implementations that are managed function pointers.
//
// If that works through P/Invoke into libobjc, the macOS bridge is ONE quarantined interop file,
// exactly like UiaInterop.cs on Windows. If it needs a compiled Objective-C shim, then macOS would
// be the first platform to force a hand-written native library into the project, and the design
// has to change. So: find out first.
//
// Every step prints; nothing asserts except the final tally, because the point is to learn.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    if (!ok) failures++;
}

Console.WriteLine($"runtime: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}");

// Foundation has to be resident before NSString exists to look up.
var foundation = NativeLibrary.Load(Foundation);
Check("Foundation loads", foundation != IntPtr.Zero, Foundation);

// ---- 1. reach the runtime at all -----------------------------------------------------------
var nsObject = Native.objc_getClass("NSObject");
Check("objc_getClass finds NSObject", nsObject != IntPtr.Zero, $"0x{nsObject:x}");

// ---- 2. build a brand-new class at runtime --------------------------------------------------
// This is the move the bridge depends on: the OS will send accessibility messages to an object
// whose class did not exist when the binary was compiled.
var probeClass = Native.objc_allocateClassPair(nsObject, "CupriFaceAccessibilityProbe", IntPtr.Zero);
Check("objc_allocateClassPair creates a subclass", probeClass != IntPtr.Zero, $"0x{probeClass:x}");

// ---- 3. attach managed code as method implementations ---------------------------------------
// [UnmanagedCallersOnly] gives a real, callable native entry point with no delegate lifetime to
// babysit — the detail that makes this safe to hand to a runtime that will call it forever.
unsafe
{
    var isElementSel = Native.sel_registerName("isAccessibilityElement");
    var addedBool = Native.class_addMethod(probeClass, isElementSel,
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&Callbacks.IsAccessibilityElement, "c@:");
    Check("class_addMethod attaches a BOOL-returning method", addedBool, "isAccessibilityElement");

    var roleSel = Native.sel_registerName("accessibilityRole");
    var addedObj = Native.class_addMethod(probeClass, roleSel,
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&Callbacks.AccessibilityRole, "@@:");
    Check("class_addMethod attaches an object-returning method", addedObj, "accessibilityRole");
}

Native.objc_registerClassPair(probeClass);
Check("objc_registerClassPair registers it", Native.objc_getClass("CupriFaceAccessibilityProbe") != IntPtr.Zero);

// ---- 4. instantiate it and call back into managed code --------------------------------------
var instance = Native.objc_msgSend(Native.objc_msgSend(probeClass, Native.sel_registerName("alloc")),
                                   Native.sel_registerName("init"));
Check("the class instantiates", instance != IntPtr.Zero, $"0x{instance:x}");

var isElement = Native.objc_msgSend_bool(instance, Native.sel_registerName("isAccessibilityElement"));
Check("a BOOL method dispatches into C#", isElement, "isAccessibilityElement -> YES");

// ---- 5. the round trip that actually matters: returning a string to the OS -------------------
// Every interesting accessibility answer is a string or an array of objects. If C# can hand back
// an NSString the OS reads correctly, the rest is vocabulary.
var rolePtr = Native.objc_msgSend(instance, Native.sel_registerName("accessibilityRole"));
var role = NSStringToManaged(rolePtr);
Check("an NSString method round-trips through C#", role == "AXButton", $"accessibilityRole -> {role ?? "(null)"}");

// ---- 6. and the OS's own accessibility constants are reachable --------------------------------
var axApi = NativeLibrary.TryLoad("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
    out var appServices);
Check("ApplicationServices (the AX client API) loads", axApi, appServices != IntPtr.Zero ? "ok" : "missing");

Console.WriteLine(failures == 0
    ? "\nVERDICT: a macOS bridge can be pure C# — runtime class creation, managed IMPs and NSString\n"
      + "         round trips all work through libobjc. No native shim required."
    : $"\nVERDICT: {failures} step(s) failed — see above. A pure-C# bridge is NOT established.");
return failures;

static string? NSStringToManaged(IntPtr nsString)
{
    if (nsString == IntPtr.Zero) return null;
    var utf8 = Native.objc_msgSend(nsString, Native.sel_registerName("UTF8String"));
    return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
}

/// <summary>The managed implementations the Objective-C runtime will call directly.</summary>
static class Callbacks
{
    [UnmanagedCallersOnly]
    internal static byte IsAccessibilityElement(IntPtr self, IntPtr sel) => 1;

    [UnmanagedCallersOnly]
    internal static IntPtr AccessibilityRole(IntPtr self, IntPtr sel) => Native.NSString("AXButton");
}

static class Native
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)] internal static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] internal static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] internal static extern IntPtr objc_allocateClassPair(IntPtr super,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr extraBytes);
    [DllImport(Objc)] internal static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Objc)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    // objc_msgSend is variadic in the headers but must be called through a signature that matches
    // the ACTUAL method, or the ABI disagrees about registers. One typed import per shape.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr objc_msgSend_ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>An autoreleased NSString from a managed string — the currency of every
    /// accessibility answer.</summary>
    internal static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return objc_msgSend_ptr(objc_getClass("NSString"),
                sel_registerName("stringWithUTF8String:"), utf8);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }
}
