using CupriFace;
using SkiaSharp;

namespace CupriFace.Demo;

/// <summary>
/// A gallery of the first-party control set — icons + v1 controls. A portable
/// <see cref="CupriApp"/>, so the desktop Viewer and the web hosts show the identical set.
/// </summary>
public sealed class ControlsApp : CupriApp
{
    public override string Title => "CupriFace — Controls";
    public override int Width => 800;
    public override int Height => 720;
    public override SKColor Background => new(0xf4, 0xf5, 0xf7);

    public override string Html => """
    <body>
      <div class="page">
        <div class="h">Icons</div>
        <div class="row icons">
          <cupri-icon name="home"></cupri-icon><cupri-icon name="search"></cupri-icon>
          <cupri-icon name="settings"></cupri-icon><cupri-icon name="bell"></cupri-icon>
          <cupri-icon name="user"></cupri-icon><cupri-icon name="heart" style="color:#e0245e"></cupri-icon>
          <cupri-icon name="star" style="color:#f5b301"></cupri-icon><cupri-icon name="download"></cupri-icon>
          <cupri-icon name="trash"></cupri-icon><cupri-icon name="info" style="color:#1a56db"></cupri-icon>
        </div>

        <div class="h">Buttons &amp; selection</div>
        <div class="row">
          <cupri-button>Primary</cupri-button>
          <cupri-button variant="ghost">Ghost</cupri-button>
          <cupri-icon-button icon="settings"></cupri-icon-button>
          <cupri-icon-button icon="trash"></cupri-icon-button>
          <cupri-checkbox checked="true"></cupri-checkbox>
          <cupri-checkbox></cupri-checkbox>
          <cupri-radio checked="true"></cupri-radio>
          <cupri-radio></cupri-radio>
          <cupri-switch checked="true"></cupri-switch>
        </div>

        <div class="h">Chips, avatars, badges</div>
        <div class="row">
          <cupri-chip>Design</cupri-chip>
          <cupri-chip closable="true">Removable</cupri-chip>
          <cupri-avatar initials="AM"></cupri-avatar>
          <cupri-avatar initials="CD"></cupri-avatar>
          <cupri-badge>NEW</cupri-badge>
        </div>

        <div class="h">Feedback</div>
        <div class="row">
          <cupri-spinner></cupri-spinner>
          <cupri-progress value="62" max="100" style="width:220px"></cupri-progress>
          <cupri-skeleton style="width:200px"></cupri-skeleton>
        </div>
        <cupri-alert type="success">Changes saved successfully.</cupri-alert>
        <cupri-alert type="warning">You are running low on disk space.</cupri-alert>
        <cupri-alert type="error">Upload failed — please try again.</cupri-alert>

        <div class="h">Cards &amp; stats</div>
        <div class="row">
          <cupri-card style="width:150px"><cupri-stat label="Active users" value="12.4k"></cupri-stat></cupri-card>
          <cupri-card style="width:150px"><cupri-stat label="Revenue" value="$8.9k"></cupri-stat></cupri-card>
          <cupri-card style="width:150px"><cupri-stat label="Uptime" value="99.9%"></cupri-stat></cupri-card>
        </div>
      </div>
    </body>
    """;

    public override string Css => """
    .page { padding:26px; background:#f4f5f7; font-family:sans-serif; }
    .h { color:#1e2430; font-size:15px; font-weight:bold; margin:18px 0 10px 0; }
    .row { display:flex; align-items:center; flex-wrap:wrap; gap:14px; margin-bottom:8px; }
    .icons { color:#48505c; }
    cupri-alert { margin-bottom:8px; }
    """;
}
