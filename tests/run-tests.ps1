# End-to-end checks against a fake Photoshop.exe. Touches only a sandbox folder (APPDATA/LOCALAPPDATA are redirected).
# Run: powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $PSScriptRoot 'bin'
$exe = Join-Path $root 'FixPhotoshop.exe'
$script:failed = 0

function Check($name, $ok, $detail) {
    if ($ok) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
New-Item -ItemType Directory $bin | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /codepage:65001 /target:winexe /platform:x64 "/out:$bin\Photoshop.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\FakePhotoshop.cs"
if ($LASTEXITCODE -ne 0) { throw 'cannot compile the fake Photoshop' }
Copy-Item "$env:WINDIR\System32\msvcrt.dll" "$bin\nvoglv64.dll"
Copy-Item "$env:WINDIR\System32\msvcrt.dll" "$bin\Camera Raw.8bi"

& $csc /nologo /codepage:65001 /target:exe /platform:x64 /main:FixPhotoshopTests.UnitTests "/out:$bin\unit.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll "$root\src\*.cs" "$PSScriptRoot\UnitTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'cannot compile the unit tests' }
& "$bin\unit.exe" | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { $script:failed += $LASTEXITCODE }

$sandbox = Join-Path $bin 'sandbox'
$env:APPDATA = Join-Path $sandbox 'Roaming'
$env:LOCALAPPDATA = Join-Path $sandbox 'Local'
$env:ProgramData = Join-Path $sandbox 'ProgramData'
New-Item -ItemType Directory $env:APPDATA, $env:LOCALAPPDATA, $env:ProgramData | Out-Null

function Stop-Fake { Get-Process Photoshop -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500 }

function Run-Scenario($scenario, $timeout) {
    Stop-Fake
    $report = Join-Path $bin "report-$scenario.txt"
    Start-Process (Join-Path $bin 'Photoshop.exe') -ArgumentList $scenario | Out-Null
    $p = Start-Process $exe -ArgumentList '--headless', '--timeout', $timeout, '--report', "`"$report`"" -PassThru -Wait -RedirectStandardOutput (Join-Path $bin "out-$scenario.txt")
    Stop-Fake
    return @{ Code = $p.ExitCode; Report = (Get-Content $report -Raw -Encoding UTF8); Out = (Get-Content (Join-Path $bin "out-$scenario.txt") -Raw -Encoding UTF8) }
}

# 1. Freeze inside the (fake) video driver.
$r = Run-Scenario 'gpu' 120
Check 'gpu: hang captured' ($r.Code -eq 0) "exit=$($r.Code)"
Check 'gpu: verdict names the driver module' ($r.Report -match 'внутри видеодрайвера \(nvoglv64\.dll\)') $r.Out
Check 'gpu: frozen window listed' ($r.Report -match 'ЗАВИС') ''
Check 'gpu: stack shows the module' ($r.Report -match 'nvoglv64\.dll') ''
Check 'gpu: Camera Raw module detected' ($r.Report -match 'Модуль Camera Raw загружен: .*Camera Raw\.8bi') ''
Check 'gpu: stack is reported as still' ($r.Report -match 'Стек окна между снимками: не менялся') ''

# 2. Two threads holding each other's mutex.
$r = Run-Scenario 'deadlock' 120
Check 'deadlock: hang captured' ($r.Code -eq 0) "exit=$($r.Code)"
Check 'deadlock: cycle found through the wait chain' ($r.Report -match 'Взаимная блокировка') $r.Out

# 3. Busy loop on the window thread.
$r = Run-Scenario 'spin' 120
Check 'spin: hang captured' ($r.Code -eq 0) "exit=$($r.Code)"
Check 'spin: reported as busy, not waiting' ($r.Report -match 'занят на полную мощность|не выходит из цикла') $r.Out

# 4. A healthy window must not be reported as a hang.
$r = Run-Scenario 'ok' 45
Check 'ok: no false hang' ($r.Code -eq 3) "exit=$($r.Code)"
Check 'ok: Camera Raw seen working' ($r.Out -match 'Camera Raw открылся и отвечает') $r.Out
Check 'ok: report says no hang' ($r.Report -match 'Зависание ещё не зафиксировано') ''

# 5. An error window is not a hang, but it has to be noticed.
# The log repeats one line thousands of times around the real problem, as seen on a real machine.
$logDir = Join-Path $env:APPDATA 'Adobe\CameraRaw\logs'
New-Item -ItemType Directory $logDir -Force | Out-Null
$spam = @('*** Error: Unexpected re-entrancy in RedrawMonitor -- skipping ***') * 500
[IO.File]::WriteAllLines("$logDir\Camera Raw Log Latest v0.txt", [string[]]($spam + '*** Warning: falling back to baseline get_ml_model_preinstall_root_paths: (depthmap) perhaps models not properly installed ***' + '*** Error: ML model not loaded ***' + $spam))
$r = Run-Scenario 'dialog' 60
Remove-Item "$logDir\Camera Raw Log Latest v0.txt"
Check 'dialog: buried model error becomes the verdict' ($r.Report -match '(?s)=== ГЛАВНОЕ ===\s*\* Camera Raw пишет в журнал, что ИИ-модели не загружены') $r.Out
Check 'dialog: quotes the depth model line' ($r.Report -match '\(depthmap\) perhaps models not properly installed') ''
Check 'dialog: spam folded in the report' ($r.Report -match '\[×1000\] \*\*\* Error: Unexpected re-entrancy') ''
Check 'dialog: captured' ($r.Code -eq 0) "exit=$($r.Code)"
Check 'dialog: verdict quotes the window' ($r.Report -match 'окно с ошибкой: «Произошла ошибка\.»') $r.Out
Check 'dialog: window history recorded' ($r.Report -match '(?s)ОКНА PHOTOSHOP ЗА СЕАНС.*Произошла ошибка') ''

# 6. A document that merely has the word in its name is not an error window.
$r = Run-Scenario 'docname' 12
Check 'docname: no false alarm' ($r.Code -eq 3) "exit=$($r.Code)"

# 7. Fixes rename things aside; undo restores the exact tree.
$acr = Join-Path $env:APPDATA 'Adobe\CameraRaw'
$gpu = Join-Path $acr 'GPU\Adobe Photoshop Camera Raw'
$cache = Join-Path $env:LOCALAPPDATA 'Adobe\CameraRaw\Cache'
$ps = Join-Path $env:APPDATA 'Adobe\Adobe Photoshop 2026\Adobe Photoshop 2026 Settings'
New-Item -ItemType Directory $gpu, "$acr\Settings", "$acr\CameraProfiles", "$acr\logs", $cache, $ps -Force | Out-Null
Set-Content "$gpu\Camera Raw GPU Config.txt" 'config'
Set-Content "$acr\Settings\my preset.xmp" 'preset'
Set-Content "$acr\CameraProfiles\profile.dcp" 'profile'
Set-Content "$acr\logs\log.txt" 'log'
Set-Content "$cache\Cache.dat" 'cache'
Set-Content "$ps\Prefs.psp" 'prefs'
$zooUser = Join-Path $acr 'ModelZoo'
$zooMachine = Join-Path $env:ProgramData 'Adobe\CameraRaw\ModelZoo'
New-Item -ItemType Directory "$zooUser\depth", "$zooMachine\depth" -Force | Out-Null
Set-Content "$zooUser\depth\model.data" 'user model'
Set-Content "$zooMachine\depth\model.data" 'machine model'

function Tree { Get-ChildItem (Join-Path $sandbox 'Roaming\Adobe'), (Join-Path $sandbox 'Local\Adobe'), (Join-Path $sandbox 'ProgramData\Adobe') -Recurse -File | Where-Object { $_.Name -notmatch 'heartbeat|backups' } | ForEach-Object { "$($_.FullName)=$((Get-FileHash $_.FullName).Hash)" } | Sort-Object }
function Invoke-Cli($cliArgs) {
    $out = Join-Path $bin 'cli.txt'
    $p = Start-Process $exe -ArgumentList $cliArgs -PassThru -Wait -RedirectStandardOutput $out
    return @{ Code = $p.ExitCode; Text = (Get-Content $out -Raw -Encoding UTF8) }
}

$before = Tree
$list = Invoke-Cli @('--list')
Check 'fixes: all steps offered' (($list.Text -match 'gpu-reset') -and ($list.Text -match 'gpu-off') -and ($list.Text -match 'acr-cache') -and ($list.Text -match 'acr-userdata') -and ($list.Text -match 'ps-prefs') -and ($list.Text -match 'model-zoo')) $list.Text

foreach ($id in 'gpu-reset', 'gpu-off', 'acr-cache', 'acr-userdata', 'model-zoo', 'ps-prefs') {
    if ($id -eq 'model-zoo') { Check 'acr-userdata: AI models left alone' (Test-Path "$zooUser\depth\model.data") '' }
    $c = Invoke-Cli @('--apply', $id)
    Check "fixes: $id applied" ($c.Code -eq 0) $c.Text
}
Check 'gpu-reset: config set aside' (-not (Test-Path "$gpu\Camera Raw GPU Config.txt")) ''
Check 'gpu-off: marker created' (Test-Path "$gpu\TempDisableGPU3") ''
Check 'backup name cannot look like a TempDisable marker' (-not (Get-ChildItem $gpu -Filter 'TempDisableGPU*' | Where-Object Name -ne 'TempDisableGPU3')) ''
Check 'acr-cache: cache set aside' (-not (Test-Path $cache)) ''
Check 'acr-userdata: presets set aside' (-not (Test-Path "$acr\Settings")) ''
Check 'acr-userdata: logs untouched' (Test-Path "$acr\logs\log.txt") ''
Check 'ps-prefs: settings set aside' (-not (Test-Path $ps)) ''
Check 'model-zoo: per-user folder set aside' (-not (Test-Path $zooUser)) ''
Check 'model-zoo: machine-wide folder set aside' (-not (Test-Path $zooMachine)) ''

# Adobe recreates a folder while the experiment runs; undo must not lose it or fail.
New-Item -ItemType Directory "$acr\Settings" | Out-Null
Set-Content "$acr\Settings\recreated.xmp" 'new'
$u = Invoke-Cli @('--undo')
Check 'undo: finished' ($u.Code -eq 0) $u.Text
Check 'undo: marker removed' (-not (Test-Path "$gpu\TempDisableGPU3")) ''
Check 'undo: recreated data kept aside' ((Get-ChildItem $acr -Directory -Filter 'fixphotoshop-replaced-*').Count -eq 1) ''
Remove-Item (Get-ChildItem $acr -Directory -Filter 'fixphotoshop-replaced-*').FullName -Recurse -Force
$after = Tree
Check 'undo: original tree restored exactly' (($before -join "`n") -eq ($after -join "`n")) (Compare-Object $before $after | Out-String)

# 8. The GPU preference keeps other flags and undo restores it. Uses a made-up program path in the real HKCU key.
$regPath = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
$fakeExe = 'C:\fixphotoshop-test\Photoshop.exe'
$dxPath = 'HKCU:\Software\Microsoft\DirectX'
$hadDx = Test-Path $dxPath
$hadKey = Test-Path $regPath
# -Force creates missing parents; it is only reached when the key does not exist, so nothing can be overwritten.
if (-not $hadKey) { New-Item $regPath -Force | Out-Null }
function GpuValue { (Get-Item $regPath).GetValue($fakeExe) }
try {
    Set-ItemProperty -Path $regPath -Name $fakeExe -Value 'AutoHDREnable=1;GpuPreference=1;'
    [void](Invoke-Cli @('--high-performance', $fakeExe))
    Check 'gpu-high: switched, other flags kept' ((GpuValue) -eq 'AutoHDREnable=1;GpuPreference=2;') (GpuValue)
    [void](Invoke-Cli @('--undo'))
    Check 'gpu-high: undo restores the old value' ((GpuValue) -eq 'AutoHDREnable=1;GpuPreference=1;') (GpuValue)
    Remove-ItemProperty -Path $regPath -Name $fakeExe
    [void](Invoke-Cli @('--high-performance', $fakeExe))
    Check 'gpu-high: created when absent' ((GpuValue) -eq 'GpuPreference=2;') (GpuValue)
    [void](Invoke-Cli @('--undo'))
    Check 'gpu-high: undo removes what it created' ($null -eq (GpuValue)) (GpuValue)
}
finally {
    Remove-ItemProperty -Path $regPath -Name $fakeExe -ErrorAction SilentlyContinue
    if (-not $hadDx) { Remove-Item $dxPath -Recurse }
    elseif (-not $hadKey) { Remove-Item $regPath -Recurse }
}

Write-Host ''
if ($script:failed -eq 0) { Write-Host 'ALL TESTS PASSED' } else { Write-Host "$script:failed TEST(S) FAILED" }
exit $script:failed
