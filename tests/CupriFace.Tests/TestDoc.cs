using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Tests;

/// <summary>
/// Test harness around a <see cref="CupriDocument"/>: build from HTML/CSS, lay out, search the render
/// tree, and drive input the way the hosts do. The hosts lay out every frame before dispatching input,
/// so a rebuild leaves an unlaid tree — the dispatch helpers here re-lay-out (<see cref="Layout"/>)
/// after each state change so the next hit-test sees positioned nodes. These utilities are shared by
/// every feature test (they were near-identical across the original throwaway harnesses).
/// </summary>
public sealed class TestDoc : IDisposable
{
    public CupriDocument Doc { get; }
    public int Width { get; }
    public int Height { get; }

    public TestDoc(string html, string? css = null, object? model = null, int width = 400, int height = 300,
        bool components = false)
    {
        var doc = CupriDocument.Load(html, css);
        if (components) doc.UseComponents(ComponentRegistry.Default());
        if (model is not null) doc.Bind(model);
        Doc = doc;
        Width = width; Height = height;
        Layout();
    }

    /// <summary>Lay out (and paint) one frame — mirrors what a host does before dispatching input.</summary>
    public void Layout() { using var _ = Doc.RenderToImage(Width, Height); }

    /// <summary>Render a frame to a bitmap for pixel assertions (clears to <paramref name="clear"/>).</summary>
    public SKBitmap Render(SKColor? clear = null)
    {
        using var img = Doc.RenderToImage(Width, Height, clear ?? SKColors.White);
        return SKBitmap.FromImage(img);
    }

    public RenderNode Root => Doc.Root;

    public RenderNode? Find(Func<RenderNode, bool> match) => Find(Doc.Root, match);
    public static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
    {
        if (match(n)) return n;
        foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
        return null;
    }

    public RenderNode FindClass(string cls) => Find(n => n.Element?.ClassList.Contains(cls) == true)!;
    public RenderNode FindRole(string role) => Find(n => n.Element?.GetAttribute("role") == role)!;
    public RenderNode? FindText() => Find(n => n.IsText && n.Lines is { Count: > 0 });

    /// <summary>Centre of a node's ON-SCREEN box, for a pointer hit-test. ScreenBox, not
    /// AbsoluteBox: inside a scrolled container only screen coordinates can actually be hit
    /// (identical when nothing is scrolled).</summary>
    public static (float X, float Y) Center(RenderNode n)
    {
        var b = HitTesting.ScreenBox(n);
        return (b.X + b.W / 2f, b.Y + b.H / 2f);
    }

    // Input helpers: dispatch, then re-lay-out so the next hit-test sees the new tree.
    public void Move(float x, float y) { Doc.DispatchPointerMove(x, y); Layout(); }
    public void Click(float x, float y, int clicks = 1) { Doc.DispatchClick(x, y, clicks); Layout(); }
    public void Up(float x, float y) { Doc.DispatchPointerUp(x, y); Layout(); }
    public void Key(EditKey key, KeyMods mods = KeyMods.None) { Doc.DispatchKey(null, key, mods); Layout(); }
    public void Type(string text) { Doc.DispatchKey(text, EditKey.None); Layout(); }

    public void ClickNode(RenderNode n, int clicks = 1) { var (x, y) = Center(n); Click(x, y, clicks); }
    public void ClickMatch(Func<RenderNode, bool> match) => ClickNode(Find(match)!);
    public void HoverClass(string cls) { var (x, y) = Center(FindClass(cls)); Move(x, y); }

    public void Dispose() => Doc.Dispose();
}
