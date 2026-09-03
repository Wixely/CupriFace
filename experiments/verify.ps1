# Re-establish the whole proof, on demand.
#
# Everything in the README was measured in one sitting. That is the weakest kind of evidence: this
# repo's own history says so — v0.16.0 exists partly because an Android claim went from "measured
# once" to "asserted every run". These probes are deliberately outside CI (they are not product, and
# nothing should gate a release on a GPU), so this script is the substitute: one command that rebuilds
# every leg from what is committed and reports what actually passed.
#
#   pwsh experiments/verify.ps1
#
# Legs whose prerequisites are absent SKIP rather than fail. A machine with no OpenGL, no Android
# device and no browser can still check that everything BUILDS, which is most of what silently rots.
# Exit code is 1 if anything genuinely failed, 0 otherwise.

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$results = @()
function Record($leg, $status, $detail) {
    $script:results += [pscustomobject]@{ Leg = $leg; Status = $status; Detail = $detail }
    $colour = 'Yellow'
    if ($status -eq 'PASS') { $colour = 'Green' }
    if ($status -eq 'FAIL') { $colour = 'Red' }
    Write-Host ("{0,-22} {1,-5} {2}" -f $leg, $status, $detail) -ForegroundColor $colour
}

Write-Host "`n=== building every leg ===" -ForegroundColor Cyan

# --- builds: the cheapest check, and the one that catches the most rot ---------------------------
$buildOnly = @(
    @{ Name = 'GlProbe.Desktop';   Args = @() },
    @{ Name = 'GlProbe.CupriFace'; Args = @() },
    @{ Name = 'GlProbe.Android';   Args = @('-r', 'android-x64') }
)
foreach ($p in $buildOnly) {
    $out = & dotnet build "experiments/$($p.Name)" -c Release --nologo -v q @($p.Args) 2>&1
    if ($LASTEXITCODE -eq 0) { Record $p.Name 'BUILD' 'compiles' }
    else { Record $p.Name 'FAIL' 'build failed'; $out | Select-Object -Last 3 | ForEach-Object { Write-Host "    $_" } }
}

# The wasm legs need a publish, not a build: ILC only runs at publish time, so a `build` that
# succeeds proves nothing about whether the native link resolves.
foreach ($name in @('GlProbe.Web', 'GlProbe.Web.Twin', 'GlProbe.WebHost')) {
    $out = & dotnet publish "experiments/$name" -c Release -o "out/verify-$name" 2>&1
    if ($LASTEXITCODE -eq 0) { Record $name 'BUILD' 'publishes (ILC + emcc link ok)' }
    else { Record $name 'FAIL' 'publish failed'; $out | Select-Object -Last 5 | ForEach-Object { Write-Host "    $_" } }
}

Write-Host "`n=== running what this machine can run ===" -ForegroundColor Cyan

# --- desktop: exits 2 when there is no usable GL, which is an environment fact, not a failure ----
$out = & dotnet run --project experiments/GlProbe.Desktop -c Release --no-build 2>&1
$code = $LASTEXITCODE
$mean = ($out | Select-String 'mean rgb' | Select-Object -First 1).ToString()
if ($code -eq 0) { Record 'desktop (run)' 'PASS' $mean.Trim() }
elseif ($code -eq 2) { Record 'desktop (run)' 'SKIP' 'no usable OpenGL on this machine' }
else { Record 'desktop (run)' 'FAIL' (($out | Select-Object -Last 2) -join ' ') }

# --- inside a CupriFace document ------------------------------------------------------------------
$out = & dotnet run --project experiments/GlProbe.CupriFace -c Release --no-build -- --probe 2>&1
if ($LASTEXITCODE -eq 0) {
    $line = ($out | Select-String 'composited' | Select-Object -First 1).ToString()
    Record 'cupriface (run)' 'PASS' $line.Trim()
} else {
    $noGl = $out | Select-String 'could not create an OpenGL'
    if ($noGl) { Record 'cupriface (run)' 'SKIP' 'no usable OpenGL on this machine' }
    else { Record 'cupriface (run)' 'FAIL' (($out | Select-Object -Last 2) -join ' ') }
}

# --- android: only if something is actually attached ---------------------------------------------
$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
if (-not (Test-Path $adb)) {
    Record 'android (run)' 'SKIP' 'no adb on this machine'
} else {
    $devices = & $adb devices | Select-String '\sdevice$'
    if (-not $devices) {
        Record 'android (run)' 'SKIP' 'no device or emulator attached'
    } else {
        & dotnet publish experiments/GlProbe.Android -c Release -r android-x64 -o out/verify-android --nologo -v q 2>&1 | Out-Null
        $apk = Get-ChildItem out/verify-android -Filter '*-Signed.apk' | Select-Object -First 1
        & $adb logcat -c
        & $adb install -r $apk.FullName | Out-Null
        $activity = (& $adb shell cmd package resolve-activity --brief com.cupriface.glprobe | Select-Object -Last 1).Trim()
        & $adb shell am start -n $activity | Out-Null
        $verdict = $null
        foreach ($i in 1..40) {
            $log = & $adb logcat -d -s glprobe:I
            $verdict = $log | Select-String 'glprobe : (PASS|FAIL)'
            if ($verdict) { break }
            Start-Sleep -Seconds 2
        }
        if ($verdict -and $verdict -match 'PASS') {
            $mean = ($log | Select-String 'mean rgb' | Select-Object -First 1).ToString()
            Record 'android (run)' 'PASS' $mean.Trim()
        } elseif ($verdict) { Record 'android (run)' 'FAIL' $verdict.ToString().Trim() }
        else { Record 'android (run)' 'FAIL' 'no verdict within 80s' }
    }
}

# --- the two browser legs cannot be driven from here ----------------------------------------------
# Deliberately not automated: driving a real browser needs Playwright or a devtools client, and this
# script's job is to be runnable anywhere rather than to reproduce the whole harness. Their PUBLISH
# is checked above, which is what actually rots; the pixel checks are in the README with the numbers
# they produced, and the commands to reproduce them by hand.
Record 'web (browser)' 'MANUAL' 'dotnet run --project tools/Serve -- out/verify-GlProbe.Web 5299'
Record 'webhost (browser)' 'MANUAL' 'dotnet run --project tools/Serve -- out/verify-GlProbe.WebHost 5299'

Write-Host "`n=== summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Status -eq 'FAIL' })
Pop-Location
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) leg(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "nothing failed" -ForegroundColor Green
exit 0
