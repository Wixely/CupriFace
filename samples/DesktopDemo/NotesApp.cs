using CupriFace.Platform;
using SkiaSharp;

namespace CupriFace.Samples.DesktopDemo;

/// <summary>
/// A tiny notes editor that uses the desktop's own dialogs and opens a second window — the worked
/// example for <see cref="CupriDocument.FileDialogs"/> and <see cref="CupriDocument.Windows"/>.
///
/// <para>Three things to notice, because they are the whole API:</para>
/// <list type="bullet">
/// <item><b>Both services can be null</b> — in a browser, on Android, in a headless test. The app
/// asks each time and says so rather than offering a button that cannot work.</item>
/// <item><b>The code after an <c>await</c> runs on the document's thread</b>, so it can change the
/// model and call <c>Refresh</c> directly, exactly as a click handler does.</item>
/// <item><b>Every window shares that one thread</b>, so the scratch window below writes straight into
/// this window's model and refreshes this window's document — no marshalling, no locks.</item>
/// </list>
/// </summary>
public sealed class NotesApp : CupriApp
{
    private readonly NotesModel _model = new();
    private CupriDocument? _doc;
    private int _scratchCount;

    public override string Title => "CupriFace — desktop integration";
    public override int Width => 720;
    public override int Height => 520;
    public override SKColor Background => new(0xF4, 0xF5, 0xF8);
    public override object? Model => _model;

    public override string Html => """
        <body>
          <div class="head">
            <span class="h">Notes</span>
            <span class="support">{{Support}}</span>
          </div>
          <div class="row">
            <cupri-button class="open">Open…</cupri-button>
            <cupri-button class="save">Save as…</cupri-button>
            <cupri-button class="folder">Choose folder…</cupri-button>
            <cupri-button class="window">New scratch window</cupri-button>
          </div>
          <cupri-textarea class="text" value="{{Text}}" placeholder="Type here, or open a file…"></cupri-textarea>
          <div class="status">{{Status}}</div>
        </body>
        """;

    public override string Css => """
        body { margin:0; padding:20px; box-sizing:border-box; font-family:sans-serif; color:#1E2430;
               display:flex; flex-direction:column; gap:14px; }
        .head { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
        .h { font-size:22px; font-weight:bold; }
        .support { font-size:12px; color:#5F6879; }
        .row { display:flex; align-items:center; gap:10px; flex-wrap:wrap; row-gap:10px; }
        .text { flex:1; min-height:200px; }
        .status { font-size:13px; color:#48505C; min-height:18px; overflow-wrap:anywhere; }
        """;

    public override void Configure(CupriDocument doc)
    {
        _doc = doc;
        // Set by the host AFTER Configure, so this first line is written on the first frame rather
        // than here — read the services when they are needed, never cache them in Configure.
        doc.Post(() =>
        {
            _model.Support = Describe(doc);
            doc.Refresh();
        });

        doc.OnClick(".open", _ => Run(OpenAsync));
        doc.OnClick(".save", _ => Run(SaveAsync));
        doc.OnClick(".folder", _ => Run(FolderAsync));
        doc.OnClick(".window", _ => OpenScratch());
    }

    private static string Describe(CupriDocument doc) =>
        (doc.FileDialogs is null ? "no file dialogs here" : "file dialogs: yes")
        + " · " + (doc.Windows is null ? "no extra windows here" : "extra windows: yes");

    private async Task OpenAsync()
    {
        if (_doc!.FileDialogs is not { } dialogs) { Say("This host has no file dialogs."); return; }
        var path = await dialogs.OpenFileAsync(new OpenFileOptions
        {
            Title = "Open a note",
            Filters = [new FileFilter("Text", "txt", "md"), FileFilter.AllFiles],
        });
        // Back on the document's thread: touch the model, refresh, done.
        if (path is null) { Say("Open cancelled."); return; }
        _model.Text = await File.ReadAllTextAsync(path);
        Say($"Opened {path}");
    }

    private async Task SaveAsync()
    {
        if (_doc!.FileDialogs is not { } dialogs) { Say("This host has no file dialogs."); return; }
        var path = await dialogs.SaveFileAsync(new SaveFileOptions
        {
            Title = "Save the note",
            DefaultName = "note.txt",
            Filters = [new FileFilter("Text", "txt"), new FileFilter("Markdown", "md")],
        });
        if (path is null) { Say("Save cancelled."); return; }
        await File.WriteAllTextAsync(path, _model.Text);
        Say($"Saved {path}");
    }

    private async Task FolderAsync()
    {
        if (_doc!.FileDialogs is not { } dialogs) { Say("This host has no file dialogs."); return; }
        var folder = await dialogs.PickFolderAsync(new PickFolderOptions { Title = "Choose a folder" });
        if (folder is null) { Say("No folder chosen."); return; }
        Say($"{folder} holds {Directory.GetFiles(folder).Length} files.");
    }

    private void OpenScratch()
    {
        if (_doc!.Windows is not { } windows) { Say("This host cannot open another window."); return; }
        var scratch = new ScratchApp(++_scratchCount, text => { _model.Text += text; Say("Text arrived from a scratch window."); });
        var window = windows.Open(scratch);
        if (window is null) { Say("The OS refused a new window."); return; }
        scratch.Window = window;
        window.Closed += why => Say($"Scratch window {scratch.Number} closed ({why}). Open: {windows.OpenWindows.Count}.");
        Say($"Opened scratch window {scratch.Number}. Open: {windows.OpenWindows.Count}.");
    }

    private void Say(string status)
    {
        _model.Status = status;
        _doc?.Refresh();
    }

    // A click handler is not async, so the work is started here and anything it throws is SHOWN
    // rather than lost — an exception in a fire-and-forget task would otherwise vanish.
    private async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { Say($"{ex.GetType().Name}: {ex.Message}"); }
    }
}

public sealed class NotesModel
{
    public string Text { get; set; } = "";
    public string Status { get; set; } = "";
    public string Support { get; set; } = "";
}

/// <summary>A second window: its own document and model, and a button that writes into the first
/// window's model — on the same thread, so it simply does.</summary>
public sealed class ScratchApp(int number, Action<string> send) : CupriApp
{
    private readonly ScratchModel _model = new() { Heading = $"Scratch {number}" };

    public int Number => number;

    /// <summary>The window this app is shown in, so its own button can close it.</summary>
    public AppWindow? Window { get; set; }

    public override string Title => $"Scratch {number}";
    public override int Width => 420;
    public override int Height => 300;
    public override SKColor Background => new(0xFF, 0xF8, 0xE8);
    public override object? Model => _model;

    public override string Html => """
        <body>
          <div class="h">{{Heading}}</div>
          <cupri-textarea class="text" value="{{Text}}" placeholder="Jot something down…"></cupri-textarea>
          <div class="row">
            <cupri-button class="send">Send to notes</cupri-button>
            <cupri-button class="close">Close window</cupri-button>
          </div>
        </body>
        """;

    public override string Css => """
        body { margin:0; padding:16px; box-sizing:border-box; font-family:sans-serif; color:#3A2E12;
               display:flex; flex-direction:column; gap:12px; }
        .h { font-size:18px; font-weight:bold; }
        .text { flex:1; min-height:120px; }
        .row { display:flex; align-items:center; gap:10px; flex-wrap:wrap; row-gap:10px; }
        .row .cupri-button { min-width:140px; }
        """;

    public override void Configure(CupriDocument doc)
    {
        doc.OnClick(".send", _ =>
        {
            if (_model.Text.Length == 0) return;
            send(_model.Text);
            _model.Text = "";
            doc.Refresh();
        });
        // Closing from the window's own handler is safe: the window goes at the end of the tick.
        doc.OnClick(".close", _ => Window?.Close());
    }
}

public sealed class ScratchModel
{
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
}
