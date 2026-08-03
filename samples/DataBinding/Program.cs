using System.Collections.ObjectModel;
using System.ComponentModel;
using CupriFace;
using CupriFace.Binding;
using SkiaSharp;

// CupriFace M4 demo: bind an HTML template to a C# model ({{interpolation}},
// data-repeat collections, and an attribute-bound progress bar), render, then mutate
// the model and re-render — proving one-way model → view reactivity.

var model = new AppModel
{
    Title = "Sprint board",
    Progress = 40,
    Tasks =
    {
        new TaskItem("Parse HTML + CSS", "done", "#7CFC00"),
        new TaskItem("Flexbox layout", "done", "#7CFC00"),
        new TaskItem("Text shaping", "in progress", "#FFD700"),
    },
};

const string html = """
<body>
  <div class="app">
    <div class="head">
      <span class="h">{{Title}}</span>
      <span class="count">{{Progress}}% complete · {{Tasks.Count}} tasks</span>
    </div>
    <div class="bar"><div class="fill" style="width:{{Progress}}%"></div></div>
    <div class="list">
      <div class="row" data-repeat="Tasks">
        <div class="dot" style="background:{{Color}}"></div>
        <span class="name">{{Name}}</span>
        <span class="status">{{Status}}</span>
      </div>
    </div>
  </div>
</body>
""";

const string css = """
.app { font-family:sans-serif; padding:24px; background:#12141a; height:452px; }
.head { display:flex; align-items:center; justify-content:space-between; margin-bottom:14px; }
.h { color:white; font-size:24px; font-weight:bold; }
.count { color:#8b93a7; font-size:14px; }
.bar { height:14px; background:#262b36; border-radius:7px; margin-bottom:20px; }
.fill { height:14px; background:#B87333; border-radius:7px; }
.list { display:flex; flex-direction:column; gap:8px; }
.row { display:flex; align-items:center; background:#1c2029; border-radius:8px; padding:12px 14px; }
.dot { width:12px; height:12px; border-radius:6px; margin-right:12px; }
.name { color:#e6e9f0; font-size:15px; flex:1; }
.status { color:#8b93a7; font-size:13px; }
""";

using var doc = CupriDocument.Load(html, css).Bind(model);
Save(doc, "m4-bind-a.png");

// Mutate the model and re-render — same template, new data.
model.Progress = 90;
model.Tasks[2].Status = "done";
model.Tasks[2].Color = "#7CFC00";
model.Tasks.Add(new TaskItem("Data binding", "in progress", "#FFD700"));
doc.Refresh();
Save(doc, "m4-bind-b.png");

Console.WriteLine("[CupriFace M4] rendered m4-bind-a.png (before) and m4-bind-b.png (after model change)");

static void Save(CupriDocument doc, string name)
{
    using var image = doc.RenderToImage(760, 452, new SKColor(0x12, 0x14, 0x1a));
    var path = Path.Combine(Environment.CurrentDirectory, name);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(path);
    data.SaveTo(fs);
}

// ---- model ---------------------------------------------------------------------
[CupriBindable]
sealed partial class AppModel
{
    public string Title { get; set; } = "";
    public int Progress { get; set; }
    public ObservableCollection<TaskItem> Tasks { get; } = new();
}

[CupriBindable]
sealed partial class TaskItem : INotifyPropertyChanged
{
    public TaskItem(string name, string status, string color) { Name = name; _status = status; _color = color; }

    public string Name { get; set; }

    private string _status;
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

    private string _color;
    public string Color { get => _color; set { _color = value; Raise(nameof(Color)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
