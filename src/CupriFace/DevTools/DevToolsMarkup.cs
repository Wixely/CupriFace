namespace CupriFace.DevTools;

/// <summary>The dev tools panel's own markup and stylesheet — written for this engine, and checked
/// by the same doctor it shows (DevToolsPanelTests).</summary>
internal static class DevToolsMarkup
{
    public const string Html = """
<body class="dt">
  <div class="dt-bar">
    <div class="dt-title">CupriFace DevTools</div>
    <div class="dt-btn dt-pick {{PickCss}}">Pick</div>
    <div class="dt-btn dt-outlines {{OutlinesCss}}">Outlines</div>
    <div class="dt-btn dt-reload">Reload</div>
    <div class="dt-btn dt-dock" style="display:{{DockDisplay}}">{{DockLabel}}</div>
    <div class="dt-btn dt-close">Close</div>
  </div>
  <div class="dt-status {{StatusCss}}">{{Status}}</div>
  <div class="dt-edits" style="display:{{EditsDisplay}}">
    <div class="dt-edits-note">{{EditsNote}}</div>
    <div class="dt-btn dt-undo" style="display:{{UndoDisplay}}">Undo</div>
    <div class="dt-btn dt-save on" style="display:{{SaveDisplay}}">Save to file</div>
  </div>

  <div class="dt-head">Elements</div>
  <div class="dt-tree">
    <div class="dt-row {{RowCss}}" data-index="{{Index}}" data-repeat="Rows">
      <div class="dt-indent" style="width:{{Indent}}px"></div>
      <div class="dt-toggle">{{Toggle}}</div>
      <div class="dt-label">{{Label}}</div>
      <div class="dt-flag">{{Flag}}</div>
      <div class="dt-size">{{Size}}</div>
    </div>
    <div class="dt-note">{{TreeNote}}</div>
  </div>

  <div class="dt-head">Selected</div>
  <div class="dt-details">
    <div class="dt-empty" style="display:{{EmptyDisplay}}">Press Pick, then click an element in the app — or choose one in the tree.</div>
    <div class="dt-sel" style="display:{{DetailsDisplay}}">
      <div class="dt-sel-name">{{Selected}}</div>
      <div class="dt-sel-classes">{{Classes}}</div>
      <div class="dt-source" style="display:{{SourceDisplay}}">
        <div class="dt-source-text">{{SourceText}}</div>
        <div class="dt-btn dt-open">Open in editor</div>
      </div>
      <div class="dt-actions" style="display:{{EditDisplay}}">
        <div class="dt-btn dt-edit">Edit as HTML</div>
        <div class="dt-btn dt-ins-after">Insert after</div>
        <div class="dt-btn dt-ins-inside">Insert inside</div>
        <div class="dt-btn dt-copy">Copy</div>
        <div class="dt-btn dt-delete">Delete</div>
      </div>
      <div class="dt-edit-target" style="display:{{EditDisplay}}">{{EditTarget}}</div>
      <div class="dt-editor" style="display:{{EditorDisplay}}">
        <div class="dt-editor-title">{{EditorTitle}}</div>
        <cupri-textarea class="dt-editor-text" value="{{EditorText}}"></cupri-textarea>
        <div class="dt-editor-error" style="display:{{EditErrorDisplay}}">{{EditError}}</div>
        <div class="dt-editor-bar">
          <div class="dt-btn dt-apply on">Apply</div>
          <div class="dt-btn dt-cancel">Cancel</div>
          <div class="dt-editor-hint">Ctrl+S applies · Esc cancels</div>
        </div>
      </div>
      <div class="dt-box">
        <div class="dt-kv"><div class="dt-k">box</div><div class="dt-v">{{Box}}</div></div>
        <div class="dt-kv"><div class="dt-k">margin</div><div class="dt-v">{{Margin}}</div></div>
        <div class="dt-kv"><div class="dt-k">border</div><div class="dt-v">{{Border}}</div></div>
        <div class="dt-kv"><div class="dt-k">padding</div><div class="dt-v">{{Padding}}</div></div>
      </div>
      <div class="dt-sub">Computed</div>
      <div class="dt-kv" data-repeat="Computed"><div class="dt-k">{{Name}}</div><div class="dt-v">{{Value}}</div></div>
      <div class="dt-group" style="display:{{BindingsDisplay}}">
        <div class="dt-sub">Bindings</div>
        <div class="dt-kv" data-repeat="Bindings"><div class="dt-k">{{Name}}</div><div class="dt-v">{{Value}}</div></div>
      </div>
      <div class="dt-group" style="display:{{RulesDisplay}}">
        <div class="dt-sub">Matched rules</div>
        <div class="dt-rule {{LineCss}}" data-repeat="Rules"><div class="dt-rule-k">{{Text}}</div><div class="dt-rule-v">{{Value}}</div></div>
      </div>
      <div class="dt-group" style="display:{{AttributesDisplay}}">
        <div class="dt-sub">Attributes</div>
        <div class="dt-kv" data-repeat="Attributes"><div class="dt-k">{{Name}}</div><div class="dt-v">{{Value}}</div></div>
      </div>
    </div>
  </div>

  <div class="dt-problems-wrap {{ProblemsWrapCss}}" style="display:{{ProblemsDisplay}}">
    <div class="dt-head">Problems — {{ProblemSummary}}</div>
    <div class="dt-problems">
      <div class="dt-problem {{ProblemCss}}" data-index="{{Index}}" data-repeat="Problems">
        <div class="dt-sev">{{Severity}}</div>
        <div class="dt-msg">{{Code}} {{Message}}</div>
        <div class="dt-where">{{Where}}</div>
      </div>
    </div>
  </div>
</body>
""";

    public const string Css = """
body.dt { margin:0; height:100vh; display:flex; flex-direction:column; background:#1e1f24; color:#d4d7dd;
          font-family:"Segoe UI", sans-serif; font-size:12px; border-left:1px solid #3a3d45; box-sizing:border-box; }
.dt-bar { display:flex; align-items:center; gap:6px; padding:8px 10px; background:#26282e; border-bottom:1px solid #3a3d45; flex:none; }
.dt-title { flex:1 1 auto; min-width:0; font-weight:600; color:#f0f2f5; white-space:nowrap; overflow:hidden; }
.dt-btn { display:inline-flex; align-items:center; padding:3px 9px; border-radius:4px; background:#33363e; color:#d4d7dd;
          cursor:pointer; white-space:nowrap; flex:none; }
.dt-btn:hover { background:#41454f; }
.dt-btn.on { background:#3b6fd8; color:#ffffff; }
.dt-status { padding:5px 10px; color:#9aa0aa; border-bottom:1px solid #3a3d45; flex:none; white-space:nowrap; overflow:hidden; }
.dt-status.bad { color:#ff8a80; }
.dt-head { padding:4px 10px; background:#26282e; color:#9aa0aa; font-size:11px; font-weight:600; flex:none;
           border-bottom:1px solid #3a3d45; border-top:1px solid #3a3d45; }

.dt-tree { flex:3 1 0; min-height:60px; overflow:scroll; padding:2px 0; }
.dt-row { display:flex; align-items:center; height:20px; padding-right:16px; cursor:pointer; white-space:nowrap; }
.dt-row:hover { background:#2b2e35; }
.dt-row.sel { background:#2f4a7a; }
.dt-row.sel .dt-size, .dt-row.sel .dt-toggle { color:#e6e8eb; }
.dt-indent { flex:none; height:1px; }
.dt-toggle { flex:none; width:16px; color:#8a909b; text-align:center; }
.dt-label { flex:1 1 auto; min-width:0; color:#8fc1ff; overflow:hidden; white-space:nowrap; }
.dt-row.text .dt-label { color:#c9ccd2; }
.dt-flag { flex:none; color:#ffb74d; padding:0 6px; }
.dt-size { flex:none; color:#9aa0aa; font-variant-numeric:tabular-nums; }
.dt-note { padding:4px 10px; color:#9aa0aa; }

.dt-details { flex:4 1 0; min-height:60px; overflow:scroll; padding:6px 10px; }
.dt-empty { color:#9aa0aa; }
.dt-sel { flex-direction:column; gap:2px; }
.dt-sel-name { font-size:13px; font-weight:600; color:#8fc1ff; overflow-wrap:anywhere; }
.dt-sel-classes { color:#9aa0aa; overflow-wrap:anywhere; }
.dt-source { display:flex; align-items:center; gap:8px; margin:4px 0; }
.dt-source-text { flex:1 1 auto; min-width:0; color:#c3e88d; overflow:hidden; white-space:nowrap; }
.dt-box { margin:4px 0; }
.dt-actions { flex-wrap:wrap; gap:6px; margin:6px 0 2px; }
.dt-edit-target { color:#9aa0aa; margin-bottom:4px; }
.dt-editor { flex-direction:column; gap:6px; margin:6px 0; padding:8px; background:#26282e; border:1px solid #3a3d45; border-radius:4px; }
.dt-editor-title { color:#d4d7dd; font-weight:600; }
.dt-editor-text { font-family:"Cascadia Mono", Consolas, monospace; font-size:12px; min-height:160px; max-height:360px; }
.dt-editor-error { color:#ff8a80; overflow-wrap:anywhere; }
.dt-editor-bar { display:flex; align-items:center; gap:6px; }
.dt-editor-hint { flex:1 1 auto; min-width:0; color:#9aa0aa; text-align:right; }
.dt-edits { align-items:center; gap:6px; padding:5px 10px; background:#2b2a1f; border-bottom:1px solid #3a3d45; flex:none; }
.dt-edits-note { flex:1 1 auto; min-width:0; color:#ffd479; overflow-wrap:anywhere; }
.dt-sub { margin-top:8px; padding-bottom:2px; color:#9aa0aa; font-weight:600; border-bottom:1px solid #33363e; }
.dt-group { flex-direction:column; }
.dt-kv { display:flex; align-items:flex-start; gap:8px; padding:1px 0; }
.dt-k { flex:none; width:96px; color:#9aa0aa; overflow:hidden; white-space:nowrap; }
.dt-v { flex:1 1 auto; min-width:0; color:#e6e8eb; overflow-wrap:anywhere; }
.dt-rule { display:flex; align-items:flex-start; gap:8px; padding:1px 0 1px 12px; }
.dt-rule.selector { padding-left:0; margin-top:4px; }
.dt-rule.selector .dt-rule-k { color:#f78c6c; }
.dt-rule-k { flex:none; width:150px; color:#9aa0aa; overflow-wrap:anywhere; }
.dt-rule-v { flex:1 1 auto; min-width:0; color:#e6e8eb; overflow-wrap:anywhere; }

.dt-problems-wrap { flex:2 1 0; min-height:60px; flex-direction:column; }
.dt-problems { flex:1 1 0; min-height:0; overflow:scroll; }
.dt-problems-wrap.clean { flex:none; min-height:0; }
.dt-problems-wrap.clean .dt-problems { display:none; }
.dt-problem { display:flex; align-items:flex-start; gap:8px; padding:3px 10px; cursor:pointer; border-bottom:1px solid #2b2e35; }
.dt-problem:hover { background:#2b2e35; }
.dt-sev { flex:none; width:50px; font-weight:600; }
.dt-problem.error .dt-sev { color:#ff8a80; }
.dt-problem.warning .dt-sev { color:#ffb74d; }
.dt-problem.info .dt-sev { color:#82b1ff; }
.dt-msg { flex:1 1 auto; min-width:0; overflow-wrap:anywhere; }
.dt-where { flex:none; color:#9aa0aa; }
""";
}
