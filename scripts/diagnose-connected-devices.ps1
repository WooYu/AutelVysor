param(
    [string]$AdbPath = "",
    [string]$DeviceSerial = "",
    [switch]$TestRotation
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($AdbPath)) {
    $bundled = Join-Path $repo 'src/AutelVysor/tools/adb.exe'
    if (Test-Path $bundled) { $AdbPath = $bundled }
    else {
        $installed = Get-Command 'adb.exe' -ErrorAction SilentlyContinue
        if ($installed) { $AdbPath = $installed.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($AdbPath) -or -not (Test-Path $AdbPath)) {
    throw 'adb.exe not found. Run scripts/prepare-tools.ps1, or pass -AdbPath C:\path\to\adb.exe'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path $repo "test-results/physical-android-$stamp"
New-Item -ItemType Directory -Force -Path $output | Out-Null

function RunAdb {
    param([string]$Serial, [string[]]$Arguments)
    $params = @()
    if ($Serial) { $params += '-s'; $params += $Serial }
    $params += $Arguments
    $text = (& $AdbPath @params 2>&1 | Out-String).Trim()
    return "adb $($params -join ' ')\r\n$text\r\nexit=$LASTEXITCODE"
}

$devices = & $AdbPath devices -l
$devices | Set-Content -Path (Join-Path $output 'adb-devices.txt') -Encoding UTF8
$serials = @(
    $devices | Select-Object -Skip 1 |
        Where-Object { $_ -match '^([^\s]+)\s+device\b' } |
        ForEach-Object { $Matches[1] }
)
if ($DeviceSerial) {
    if ($serials -notcontains $DeviceSerial) {
        throw "Device '$DeviceSerial' is not available (online): $($serials -join ', ')"
    }
    $serials = @($DeviceSerial)
}

if ($serials.Count -eq 0) {
    Write-Host 'No authorized Android devices. Check USB debugging and adb devices -l.'
    Write-Host "Report saved: $output"
    exit 1
}

foreach ($serial in $serials) {
    $safe = $serial -replace '[^a-zA-Z0-9_.-]', '_'
    $reportPath = Join-Path $output "$safe.txt"
    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("Autel Vysor physical Android diagnostics $stamp / $serial")
    foreach ($args in @(
        @('shell', 'getprop', 'ro.build.version.release'),
        @('shell', 'getprop', 'ro.build.version.sdk'),
        @('shell', 'getprop', 'ro.product.model'),
        @('shell', 'wm', 'user-rotation'),
        @('shell', 'settings', 'get', 'system', 'accelerometer_rotation'),
        @('shell', 'settings', 'get', 'system', 'user_rotation'),
        @('shell', 'dumpsys', 'input'),
        @('shell', 'dumpsys', 'window'),
        @('shell', 'settings', 'get', 'secure', 'default_input_method'),
        @('shell', 'ime', 'list', '-s')
    )) {
        $report.Add((RunAdb -Serial $serial -Arguments $args))
    }

    if ($TestRotation) {
        $priorWm = ((& $AdbPath -s $serial shell wm user-rotation 2>&1 | Out-String).Trim())
        $priorSensor = ((& $AdbPath -s $serial shell settings get system accelerometer_rotation 2>&1 | Out-String).Trim())
        $priorRotation = ((& $AdbPath -s $serial shell settings get system user_rotation 2>&1 | Out-String).Trim())
        try {
            $report.Add('=== ROTATION EXERCISE (temporarily changes real device) ===')
            $report.Add((RunAdb -Serial $serial -Arguments @('shell','settings','put','system','accelerometer_rotation','0')))
            $report.Add((RunAdb -Serial $serial -Arguments @('shell','wm','user-rotation','lock','1')))
            Start-Sleep -Seconds 2
            $report.Add((RunAdb -Serial $serial -Arguments @('shell','wm','user-rotation')))
            $report.Add((RunAdb -Serial $serial -Arguments @('shell','dumpsys','input')))
        }
        finally {
            if ($priorWm -match '^lock\s+([0-3])') {
                $report.Add((RunAdb -Serial $serial -Arguments @('shell','wm','user-rotation','lock',$Matches[1])))
            } else {
                $report.Add((RunAdb -Serial $serial -Arguments @('shell','wm','user-rotation','free')))
            }
            if ($priorSensor -match '^[01]$') {
                $report.Add((RunAdb -Serial $serial -Arguments @('shell','settings','put','system','accelerometer_rotation',$priorSensor)))
            }
            if ($priorRotation -match '^[0-3]$') {
                $report.Add((RunAdb -Serial $serial -Arguments @('shell','settings','put','system','user_rotation',$priorRotation)))
            }
        }
    }

    $report | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host "Device diagnostics: $reportPath"
}
Write-Host "Finished. Attach the relevant report from $output if you need device-specific help."
Write-Host 'Reports may contain device identifiers, installed IME details and window/app information. Review before sharing.'
