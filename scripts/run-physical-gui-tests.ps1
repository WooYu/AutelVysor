<#
.SYNOPSIS
  Drive the actual AutelVysor.exe WPF UI on an interactive Windows desktop,
  while verifying input and rotation effects against a REAL authorized Android phone.
.NOTES
  Must run in a logged-in, unlocked desktop session (NOT a Windows service/session 0).
  Install/uninstall uses only the disposable com.autel.vysor.fixture APK when supplied.
  Do not run during customer diagnosis or on production devices.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AppExePath,
    [Parameter(Mandatory=$true)][string]$DeviceSerial,
    [string]$AdbPath = '',
    [string]$FixtureApk = '',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) {
    throw 'No interactive Windows desktop. UI automation cannot work in a service/session 0.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AutelVirtualKeyboard {
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    public static void Press(byte key) {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
    }
}
'@

$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repo ('test-results/physical-gui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path $OutputRoot).Path

if (-not (Test-Path $AppExePath)) { throw "AutelVysor.exe missing: $AppExePath" }
$AppExePath = (Resolve-Path $AppExePath).Path
if (-not $AdbPath) {
    $candidate = Join-Path (Split-Path $AppExePath -Parent) 'tools/adb.exe'
    if (Test-Path $candidate) { $AdbPath = $candidate }
    else {
        $adbCommand = Get-Command adb.exe -ErrorAction SilentlyContinue
        if ($adbCommand) { $AdbPath = $adbCommand.Source }
    }
}
if (-not (Test-Path $AdbPath)) { throw 'adb.exe missing. Pass -AdbPath.' }
if ($FixtureApk -and -not (Test-Path $FixtureApk)) { throw "Fixture APK missing: $FixtureApk" }

$results = New-Object System.Collections.Generic.List[object]
function Record([string]$feature, [string]$status, [string]$details) {
    $results.Add([pscustomobject]@{Feature=$feature;Status=$status;Details=$details;At=(Get-Date).ToString('o')})
    Write-Host "[$status] $feature : $details"
}
function Run-Case([string]$feature,[scriptblock]$body) {
    try { $detail = & $body; Record $feature 'PASS' ([string]$detail) }
    catch { Record $feature 'FAIL' $_.Exception.Message }
}
function Assert([bool]$pass,[string]$message) { if (-not $pass) { throw $message } }

function Adb([string[]]$cmd) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& $AdbPath @cmd 2>&1 | Out-String).Trim()
        $exit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($exit -ne 0) { throw "ADB exit $($exit): $text" }
    return $text
}
function Device([string[]]$cmd) { Adb (@('-s',$DeviceSerial) + $cmd) }
function DeviceRotation {
    $dump = Device @('shell','dumpsys','window','displays')
    $headers = [regex]::Matches($dump, '(?m)^\s*Display:\s*mDisplayId=(?<id>\d+)\b[^\r\n]*')
    for ($i=0; $i -lt $headers.Count; $i++) {
        if ($headers[$i].Groups['id'].Value -ne '0') { continue }
        $end = $dump.Length
        if ($i+1 -lt $headers.Count) { $end = $headers[$i+1].Index }
        $section = $dump.Substring($headers[$i].Index, $end-$headers[$i].Index)
        if ($section -match '\bDisplayFrames\b[^\r\n]*\br=([0-3])\b') { return [int]$Matches[1] * 90 }
        if ($section -match '\bm(?:Current)?Rotation=(?:ROTATION_)?(270|180|90|[0-3])\b') {
            $value = [int]$Matches[1]
            if ($value -le 3) { return $value * 90 }
            return $value
        }
    }
    $dump = Device @('shell','dumpsys','input')
    if ($dump -match 'SurfaceOrientation:\s*([0-3])\b') { return [int]$Matches[1] * 90 }
    throw 'Actual device rotation unavailable; user_rotation is not current display telemetry.'
}

function Find-ByName($root,[string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,$name)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Find-ById($root,[string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Wait-ByName($root,[string]$name,[int]$seconds=20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $element = Find-ByName $root $name
        if ($element) { return $element }
        Start-Sleep -Milliseconds 400
    }
    throw "Timed out waiting for '$name' in actual WPF UI."
}
function Invoke-Button($root,[string]$name) {
    $element = Wait-ByName $root $name
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Text($root,[string]$automationId,[string]$value) {
    $element = Find-ById $root $automationId
    if (-not $element) { throw "Could not locate WPF control '$automationId'." }
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function ReadFixture {
    Device @('shell','run-as','com.autel.vysor.fixture','cat','files/input.txt')
}
function Wait-Input([string]$needle,[int]$seconds=6) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $actual = ''
    while ((Get-Date) -lt $deadline) {
        try { $actual = ReadFixture } catch {}
        if ($actual.Contains($needle)) { return $true }
        Start-Sleep -Milliseconds 350
    }
    return $false
}

$process = $null
$installedFixture = $false
$main = $null
$session = $null
$rotationBefore = $null
$sensorBefore = ''
$userRotationBefore = ''
$wmBefore = ''
try {
    $devices = Adb @('devices','-l')
    Assert ($devices -match "(?m)^$([regex]::Escape($DeviceSerial))\s+device\b") 'Requested real Android device is not online/authorized.'
    $qemu = Device @('shell','getprop','ro.kernel.qemu')
    Assert ($qemu -ne '1' -and -not $DeviceSerial.StartsWith('emulator-')) 'Refusing to label an emulator as a physical device.'

    $rotationBefore = DeviceRotation
    $sensorBefore = Device @('shell','settings','get','system','accelerometer_rotation')
    $userRotationBefore = Device @('shell','settings','get','system','user_rotation')
    try { $wmBefore = Device @('shell','wm','user-rotation') } catch {}

    if ($FixtureApk) {
        $existing = Device @('shell','pm','path','com.autel.vysor.fixture')
        if ($existing -match 'package:') {
            Record 'fixture.isolation' 'SKIP' 'Disposable fixture already installed; will not overwrite existing data.'
        } else {
            Run-Case 'fixture.install' {
                $output = Device @('install','-r',$FixtureApk)
                Assert ($output -match '(?i)success') "Fixture APK installation failed: $output"
                $script:installedFixture = $true
                'Disposable fixture installed on physical device.'
            }
        }
        if ($installedFixture) {
            Device @('shell','am','start','-n','com.autel.vysor.fixture/.MainActivity') | Out-Null
            Start-Sleep -Seconds 2
        }
    }

    Run-Case 'ui.start' {
        $script:process = Start-Process -FilePath $AppExePath -WorkingDirectory (Split-Path $AppExePath -Parent) -PassThru
        $deadline = (Get-Date).AddSeconds(25)
        while ($process.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $process.Refresh()
        }
        Assert ($process.MainWindowHandle -ne [IntPtr]::Zero) 'Autel Vysor did not expose a main HWND.'
        $script:main = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        Assert ($null -ne $main) 'Windows UIAutomation could not access Autel Vysor.'
        'Autel Vysor WPF window is running and accessible by Windows UIAutomation.'
    }
    if ($null -eq $main) { throw 'WPF application startup failed. Additional GUI tests cannot run.' }

    Run-Case 'ui.device-selection-and-open-mirror' {
        Invoke-Button $main 'Refresh devices'
        Start-Sleep -Seconds 2
        $list = Find-ById $main 'DeviceList'
        Assert ($null -ne $list) 'DeviceList is missing.'
        $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        $selected = $null
        foreach ($item in $items) {
            if ($item.Current.Name.Contains($DeviceSerial)) { $selected = $item; break }
            $texts = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($text in $texts) {
                if ($text.Current.Name -eq $DeviceSerial) { $selected = $item; break }
            }
            if ($selected) { break }
        }
        Assert ($null -ne $selected) "DeviceList did not contain serial $DeviceSerial."
        $selected.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Invoke-Button $main 'Open selected'
        $script:session = Wait-ByName $main 'Switch orientation' 30
        'Selected physical serial and opened actual scrcpy/legacy mirror UI.'
    }

    if ($null -ne $session) {
        Run-Case 'ui.fit-1to1' {
            Invoke-Button $main 'Fit'
            Invoke-Button $main '1:1'
            Invoke-Button $main 'Fit'
            'Fit / 1:1 / Fit invoked in real WPF UI (visual calibration still external).'
        }
        Run-Case 'ui.switch-real-android-orientation' {
            $before = DeviceRotation
            Invoke-Button $main 'Switch orientation'
            Start-Sleep -Seconds 3
            $after = DeviceRotation
            Assert ($before -ne $after) "Android actual SurfaceOrientation did not change ($before -> $after); foreground app/OEM may lock rotation."
            "Actual device orientation changed $before -> $after degrees."
        }
        Run-Case 'ui.auto-rotation-setting' {
            Invoke-Button $main 'Auto rotate'
            Start-Sleep -Seconds 1
            $sensor = Device @('shell','settings','get','system','accelerometer_rotation')
            Assert ($sensor -eq '1') 'Auto rotate did not re-enable the real Android sensor mode.'
            $deadline = (Get-Date).AddSeconds(15)
            $synchronized = $false
            while ((Get-Date) -lt $deadline) {
                $actual = DeviceRotation
                $state = Find-ById $main 'RotateStateText'
                if ($state -and $state.Current.Name -eq ("Rotation: {0}° · Auto" -f $actual)) {
                    $synchronized = $true; break
                }
                Start-Sleep -Milliseconds 500
            }
            Assert $synchronized 'Auto status angle differs from actual display (AV-005).'
            'Auto setting and displayed angle match real Android display telemetry. Physical tilt remains external validation.'
        }
        if ($installedFixture) {
            Run-Case 'ui.adb-send-text-and-verified-edittext' {
                Device @('shell','am','start','-n','com.autel.vysor.fixture/.MainActivity') | Out-Null
                Start-Sleep -Milliseconds 700
                Set-Text $main 'AndroidTextBox' 'FROMUI123'
                Invoke-Button $main 'Send text'
                Assert (Wait-Input 'FROMUI123') 'WPF Send text did not enter the fixture EditText on the physical device.'
                'Actual WPF Send text delivered data to physical Android EditText.'
            }
            Run-Case 'ui.virtual-key-digits-and-backspace-AV004' {
                Device @('shell','am','force-stop','com.autel.vysor.fixture') | Out-Null
                Device @('shell','am','start','-n','com.autel.vysor.fixture/.MainActivity') | Out-Null
                Start-Sleep -Milliseconds 700
                Set-Text $main 'AndroidTextBox' '12345'
                Invoke-Button $main 'Send text'
                Assert ((ReadFixture) -eq '12345') 'ADB seed text did not reach fixture.'
                Invoke-Button $main 'Focus keyboard'
                Start-Sleep -Milliseconds 500
                foreach ($key in @(0x08,0x36,0x37)) {
                    [AutelVirtualKeyboard]::Press([byte]$key)
                    Start-Sleep -Milliseconds 150
                }
                Assert (Wait-Input '123467') 'Native Backspace,6,7 did not produce 123467 (AV-004).'
                Assert ((ReadFixture) -eq '123467') 'Unexpected or duplicated characters after virtual-key entry.'
                'OS virtual keys Backspace/6/7 changed physical Android text from 12345 to 123467.'
            }
            Run-Case 'ui.pc-keyboard-through-native-scrcpy' {
                Device @('shell','am','start','-n','com.autel.vysor.fixture/.MainActivity') | Out-Null
                Start-Sleep -Milliseconds 700
                Invoke-Button $main 'Focus keyboard'
                Start-Sleep -Milliseconds 500
                [System.Windows.Forms.SendKeys]::SendWait('PCWINDOWS123')
                Assert (Wait-Input 'PCWINDOWS123') 'PC keyboard input did not reach fixture EditText via embedded scrcpy.'
                'Actual Windows keyboard typed through embedded scrcpy to physical Android EditText.'
            }
        } else {
            Record 'ui.pc-keyboard-and-edittext-verification' 'SKIP' 'Provide a fresh disposable fixture APK to verify end-to-end text entry.'
        }
    } else {
        Record 'ui.mirror-interaction' 'FAIL' 'Could not open selected mirror; orientation/keyboard/UI tests not run.'
    }

    Run-Case 'ui.sidebar' {
        Invoke-Button $main 'Hide sidebar'
        Start-Sleep -Milliseconds 500
        Invoke-Button $main 'Show sidebar'
        'Sidebar hidden and restored by actual WPF button clicks.'
    }
    Run-Case 'ui.fullscreen' {
        Invoke-Button $main 'Full screen'
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait('{F11}')
        'Full-screen toggled and restored through F11.'
    }
    Run-Case 'ui.device-debug-tabs' {
        Invoke-Button $main 'Device debug'
        Start-Sleep -Seconds 2
        $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        $debug = $null
        foreach ($candidate in $roots) {
            if ($candidate.Current.ProcessId -eq $process.Id -and $candidate.Current.Name -like '*Device Debug*') {
                $debug = $candidate; break
            }
        }
        Assert ($null -ne $debug) 'Device Debug WPF window was not created.'
        foreach ($tab in @('App','Processes','Logs','Files','Shell','Network','Device')) {
            $control = Wait-ByName $debug $tab 5
            $control.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        'All seven Device Debug tabs are reachable in real Windows UI.'
    }
    Run-Case 'ui.capture-history-window' {
        Invoke-Button $main 'Captures'
        Start-Sleep -Milliseconds 600
        'Opened capture history window.'
    }
    Run-Case 'ui.batch-operations-window' {
        Invoke-Button $main 'Batch operations'
        Start-Sleep -Milliseconds 600
        'Opened multi-device batch operations UI (destructive commands not invoked).'
    }
    Run-Case 'ui.theme-visual-snapshot' {
        # Captures desktop for later VISUAL review; a valid file is not an assertion that contrast is correct.
        $bounds = $main.Current.BoundingRectangle
        Assert ($bounds.Width -ge 100 -and $bounds.Height -ge 100) 'App bounds are invalid.'
        $bitmap = [System.Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
        $gfx = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $gfx.CopyFromScreen([int]$bounds.X,[int]$bounds.Y,0,0,
                (New-Object System.Drawing.Size ([int]$bounds.Width),([int]$bounds.Height)))
            $file = Join-Path $OutputRoot 'autel-main-window.png'
            $bitmap.Save($file,[System.Drawing.Imaging.ImageFormat]::Png)
            'Saved actual Windows desktop screenshot for visual review; not an automatic contrast pass.'
        }
        finally { $gfx.Dispose(); $bitmap.Dispose() }
    }
}
finally {
    # Restore the Android system policy even if GUI test fails.
    try {
        if ($wmBefore -match 'lock\s+([0-3])') {
            Device @('shell','wm','user-rotation','lock',$Matches[1]) | Out-Null
        } else {
            Device @('shell','wm','user-rotation','free') | Out-Null
        }
        if ($userRotationBefore -match '^[0-3]$') {
            Device @('shell','settings','put','system','user_rotation',$userRotationBefore) | Out-Null
        }
        if ($sensorBefore -match '^[01]$') {
            Device @('shell','settings','put','system','accelerometer_rotation',$sensorBefore) | Out-Null
        }
    } catch { Record 'cleanup.rotation-restore' 'FAIL' $_.Exception.Message }
    if ($installedFixture) {
        try {
            Device @('uninstall','com.autel.vysor.fixture') | Out-Null
            Record 'cleanup.fixture' 'PASS' 'Disposable fixture uninstalled.'
        } catch { Record 'cleanup.fixture' 'FAIL' $_.Exception.Message }
    }
    if ($process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) { $process.Kill() }
        } catch {}
        $process.Dispose()
    }
    $results | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $OutputRoot 'gui-results.json')
    $results | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $OutputRoot 'gui-results.csv')
    Write-Host "Real-device Windows GUI test evidence: $OutputRoot"
}

if (@($results | Where-Object Status -eq 'FAIL').Count -gt 0) { exit 1 }

