param([Parameter(Mandatory=$true)][string]$Exe, [switch]$GlBaseline, [switch]$LayeredGpu)
# Run in an unlocked Windows desktop with Windows PowerShell 5.1. Uses only a synthetic
# backdrop; no screenshots or personal desktop content are written to disk.
$ErrorActionPreference = 'Stop'
if($LayeredGpu -and $GlBaseline){throw 'Choose either the layered GPU path or the default GL baseline.'}
if(($LayeredGpu -or $GlBaseline) -and $env:CUPRIFACE_SOFTWARE -in @('1','true','TRUE')){throw 'Unset CUPRIFACE_SOFTWARE to test GPU rendering.'}
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AlphaProbe {
 public delegate bool EnumProc(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int w,int z,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h,int index);
 public struct Rect { public int Left,Top,Right,Bottom; }
 public static IntPtr Find(int id) {
  IntPtr found=IntPtr.Zero;
  EnumWindows(delegate(IntPtr h,IntPtr p) { uint pid; GetWindowThreadProcessId(h,out pid); var s=new StringBuilder(64); GetClassName(h,s,64); if(pid==id && s.ToString()=="CupriFaceAlphaWindow"){found=h;return false;}if(pid==id && s.ToString()=="GLFW30"){found=h;}return true;},IntPtr.Zero);
  return found;
 }
}
'@
$oldDpi=[AlphaProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
function Pump([int]$milliseconds=300) {
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while($timer.ElapsedMilliseconds -lt $milliseconds){[Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 10}
}
function Capture($r) {
 $b=New-Object Drawing.Bitmap ($r.Right-$r.Left),($r.Bottom-$r.Top)
 $g=[Drawing.Graphics]::FromImage($b)
 try{$g.CopyFromScreen($r.Left,$r.Top,0,0,$b.Size)}finally{$g.Dispose()}
 return $b
}
function Measure-Alpha($hwnd,[string]$stage) {
 [AlphaProbe]::ShowWindow($hwnd,5)|Out-Null
 [AlphaProbe]::SetWindowPos($hwnd,[IntPtr]::Zero,0,0,0,0,0x53)|Out-Null
 Pump
 $r=New-Object AlphaProbe+Rect;[AlphaProbe]::GetWindowRect($hwnd,[ref]$r)|Out-Null
 $a=Capture $r
 [AlphaProbe]::ShowWindow($hwnd,0)|Out-Null
 Pump
 $b=Capture $r
 try {
  $corners=@(@(2,2),@(($a.Width-3),2),@(2,($a.Height-3)),@(($a.Width-3),($a.Height-3)))
  $match=$true
  foreach($point in $corners){if($a.GetPixel($point[0],$point[1]).ToArgb() -ne $b.GetPixel($point[0],$point[1]).ToArgb()){$match=$false}}
  $black=0
  for($y=0;$y -lt $a.Height;$y++){for($x=0;$x -lt $a.Width;$x++){if(($a.GetPixel($x,$y).ToArgb() -band 0xffffff) -eq 0){$black++}}}
  $d=[AlphaProbe]::GetDpiForWindow($hwnd)/96.0
  $card=$a.GetPixel([int](25*$d),[int](100*$d))
  $behind=$b.GetPixel([int](25*$d),[int](100*$d))
  # Derive the expected blend from the hidden capture, so foreground changes that obscure
  # the synthetic backdrop cannot be mistaken for an alpha failure.
  $expectedR=(18*217+$behind.R*38)/255.0
  $expectedG=(20*217+$behind.G*38)/255.0
  $expectedB=(26*217+$behind.B*38)/255.0
  $blended=([Math]::Abs($card.R-$expectedR) -le 2 -and [Math]::Abs($card.G-$expectedG) -le 2 -and [Math]::Abs($card.B-$expectedB) -le 2)
  [pscustomobject]@{Stage=$stage;Size="$($a.Width)x$($a.Height)";CornersMatch=$match;BlackFraction=[Math]::Round($black/($a.Width*$a.Height),4);TranslucentCardBlends=$blended;Card="$($card.R),$($card.G),$($card.B)";TopMost=([AlphaProbe]::GetWindowLongW($hwnd,-20) -band 8)-ne 0}
  if(!$GlBaseline -and (!$match -or $black -ne 0 -or !$blended)){throw "Alpha acceptance failed at $stage"}
 } finally {$a.Dispose();$b.Dispose()}
}
$backdrop=New-Object Windows.Forms.Form
$backdrop.FormBorderStyle='None'
$backdrop.StartPosition='Manual'
$backdrop.Location=New-Object Drawing.Point 40,40
$backdrop.Size=New-Object Drawing.Size 1100,950
$backdrop.BackColor=[Drawing.Color]::FromArgb(64,128,192)
$exePath=(Resolve-Path $Exe).Path
try {
 $backdrop.Show();Pump
 foreach($topmost in @($true,$false)) {
  $arguments=@()
  if($LayeredGpu){$arguments+='--layered-gpu'}elseif(!$GlBaseline){$arguments+='--software'}
  if(!$topmost){$arguments+='--no-topmost'}
  $launch=@{FilePath=$exePath;WindowStyle='Hidden';PassThru=$true}
  if($arguments.Count){$launch.ArgumentList=$arguments}
  $p=Start-Process @launch
  $null=$p.Handle # retain the process handle so ExitCode remains available after exit
  $hwnd=[IntPtr]::Zero
  try {
   for($i=0;$i -lt 80 -and $hwnd -eq [IntPtr]::Zero;$i++){Pump 100;$hwnd=[AlphaProbe]::Find($p.Id)}
   if($hwnd -eq [IntPtr]::Zero){throw 'Sample window not found'}
   [AlphaProbe]::SetWindowPos($hwnd,[IntPtr]::Zero,100,100,0,0,0x51)|Out-Null
   Pump 1200
   if($p.HasExited){throw "Sample exited before capture ($($p.ExitCode))"}
   Measure-Alpha $hwnd "initial-topmost=$topmost"
   [AlphaProbe]::SetWindowPos($hwnd,[IntPtr](-2),130,130,620,400,0x50)|Out-Null
   Measure-Alpha $hwnd 'demoted-moved-resized'
   [AlphaProbe]::SetWindowPos($hwnd,[IntPtr](-1),0,0,0,0,0x53)|Out-Null
   Measure-Alpha $hwnd 'promoted'
   [AlphaProbe]::ShowWindow($hwnd,6)|Out-Null
   Pump
   [AlphaProbe]::ShowWindow($hwnd,9)|Out-Null
   Measure-Alpha $hwnd 'restored'
  } finally {
   if($hwnd -ne [IntPtr]::Zero){[AlphaProbe]::PostMessage($hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null}
   if(!$p.WaitForExit(3000)){Stop-Process -Id $p.Id -Force;throw 'Sample did not close cleanly'}
   if($p.ExitCode -ne 0){throw "Sample exited with $($p.ExitCode)"}
   $p.Dispose()
  }
 }
} finally {$backdrop.Dispose();[AlphaProbe]::SetThreadDpiAwarenessContext($oldDpi)|Out-Null}
