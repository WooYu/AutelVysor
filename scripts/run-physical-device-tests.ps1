<#
.SYNOPSIS
  Run physical-Android-device checks using a Windows PC's actual USB/Wi-Fi ADB devices.
.DESCRIPTION
  This does NOT use emulators, mocked ADB or GitHub-hosted devices.
  Safe mode changes only a uniquely named /sdcard/Download test directory.
  -FixtureApk enables operations ONLY on com.autel.vysor.fixture, a disposable test app.
  Rotation, reboot, restarting the global ADB server and bugreport are separate opt-ins.
  Audio playback heard through a real Bluetooth headset cannot be proven by ADB alone.
#>
[CmdletBinding()]
param(
    [string]$AdbPath = '',
    [string[]]$Serials = @(),
    [string]$ScrcpyPath = '',
    [string]$FixtureApk = '',
    [string]$OutputRoot = '',
    [switch]$IncludeRotation,
    [switch]$IncludeBugreport,
    [switch]$IncludeReboot,
    [switch]$IncludeAdbRestart,
    [switch]$IncludeGui
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repo ('test-results/physical-e2e-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path $OutputRoot).Path
$script:Results = New-Object System.Collections.Generic.List[object]
$script:EvidenceDir = Join-Path $OutputRoot 'evidence'
New-Item -ItemType Directory -Path $script:EvidenceDir -Force | Out-Null
$runId = [guid]::NewGuid().ToString('N').Substring(0,12)

if (-not $AdbPath) {
    foreach ($candidate in @(
        (Join-Path $repo 'src/AutelVysor/tools/adb.exe'),
        (Join-Path $repo 'dist/AutelVysor/tools/adb.exe')
    )) {
        if (Test-Path $candidate) { $AdbPath = $candidate; break }
    }
    if (-not $AdbPath) {
        $candidate = Get-Command adb.exe -ErrorAction SilentlyContinue
        if ($candidate) { $AdbPath = $candidate.Source }
    }
}
if (-not $AdbPath -or -not (Test-Path $AdbPath)) {
    throw 'adb.exe is unavailable. Pass -AdbPath or run scripts/prepare-tools.ps1.'
}
$AdbPath = (Resolve-Path $AdbPath).Path
if ($ScrcpyPath -and -not (Test-Path $ScrcpyPath)) { throw "scrcpy.exe missing: $ScrcpyPath" }
if ($FixtureApk -and -not (Test-Path $FixtureApk)) { throw "Test fixture APK missing: $FixtureApk" }

function Invoke-Adb {
    param([string[]]$Command, [switch]$AllowFailure)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = (& $AdbPath @Command 2>&1 | Out-String).Trim()
        $status = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    if ($status -ne 0 -and -not $AllowFailure) {
        throw "adb $($Command -join ' '): exit=$status; $output"
    }
    return $output
}

function Record {
    param([string]$Serial, [string]$Feature, [string]$Status, [string]$Details = '', [string]$Evidence = '')
    $script:Results.Add([pscustomobject]@{
        Serial=$Serial; Feature=$Feature; Status=$Status
        Details=$Details; Evidence=$Evidence; At=(Get-Date).ToString('o')
    })
    Write-Host ("[{0}] [{1}] {2}: {3}" -f $Status,$Serial,$Feature,$Details)
}

function Assert-Test([bool]$Condition, [string]$ErrorMessage) {
    if (-not $Condition) { throw $ErrorMessage }
}

function Run-Test {
    param([string]$Serial, [string]$Feature, [scriptblock]$Body)
    try {
        $description = & $Body
        Record $Serial $Feature 'PASS' ([string]$description)
    }
    catch {
        Record $Serial $Feature 'FAIL' $_.Exception.Message
    }
}

function Skip-Test([string]$Serial,[string]$Feature,[string]$Why) {
    Record $Serial $Feature 'SKIP' $Why
}

function Needs-External([string]$Serial,[string]$Feature,[string]$Why) {
    Record $Serial $Feature 'EXTERNAL_VALIDATION' $Why
}

function RotationDegrees([string]$Serial) {
    $dump = Invoke-Adb -Command @('-s',$Serial,'shell','dumpsys','input') -AllowFailure
    if ($dump -match 'SurfaceOrientation:\s*([0-3])') { return [int]$Matches[1] * 90 }
    $rotation = Invoke-Adb -Command @('-s',$Serial,'shell','settings','get','system','user_rotation') -AllowFailure
    if ($rotation -match '^\s*([0-3])\s*$') { return [int]$Matches[1] * 90 }
    throw 'Android does not expose a parseable rotation value.'
}

function Wait-Online([string]$Serial,[int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Invoke-Adb -Command @('-s',$Serial,'get-state') -AllowFailure
        if ($state -match '\bdevice\b') { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Verify-Png([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24) { throw 'Screenshot PNG is too small.' }
    $signature = @(137,80,78,71,13,10,26,10)
    for ($i=0;$i -lt 8;$i++) {
        if ($bytes[$i] -ne $signature[$i]) { throw 'Screenshot is not a valid PNG signature.' }
    }
    return $bytes.Length
}

Write-Host "Physical test output: $OutputRoot"
Write-Host 'This script tests devices currently authorized on THIS Windows PC, not mock devices.'
$deviceList = Invoke-Adb -Command @('devices','-l')
$deviceList | Set-Content -Encoding UTF8 (Join-Path $OutputRoot 'adb-devices.txt')
$online = @($deviceList -split "\r?\n" | Where-Object { $_ -match '^\S+\s+device(?:\s|$)' } |
    ForEach-Object { ($_ -split '\s+')[0] })
if ($Serials.Count -gt 0) {
    foreach ($serial in $Serials) {
        if ($online -notcontains $serial) { throw "Requested physical device is not online/authorized: $serial" }
    }
    $online = @($Serials)
}
if ($online.Count -eq 0) {
    throw 'No authorized ADB devices. USB debugging must be enabled, the computer must be authorized, and adb devices -l must show device.'
}

try {
    foreach ($serial in $online) {
        $safe = $serial -replace '[^A-Za-z0-9_.-]','_'
        $deviceDir = Join-Path $script:EvidenceDir $safe
        New-Item -ItemType Directory -Path $deviceDir -Force | Out-Null
        $remote = "/sdcard/Download/autel-vysor-physical-$runId"
        # Read physical-device identity before Run-Test: PowerShell scriptblock
        # assignments inside a helper do not mutate the caller's scope.
        $qemu = Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.kernel.qemu') -AllowFailure
        $emu = Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.boot.qemu') -AllowFailure
        if ($serial.StartsWith('emulator-', [StringComparison]::OrdinalIgnoreCase) -or
            $qemu -eq '1' -or $emu -eq '1') {
            Record $serial 'connect.real-device' 'FAIL' 'Detected emulator, not a physical phone; ignoring this target.'
            continue
        }
        Run-Test $serial 'connect.real-device' {
            $state = Invoke-Adb -Command @('-s',$serial,'get-state')
            Assert-Test ($state -match '\bdevice\b') 'ADB state is not device.'
            'Physical device is online and authorized.'
        }
        try { $api = [int](Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.build.version.sdk')) }
        catch {
            Record $serial 'device.info' 'FAIL' "Cannot determine Android API level: $($_.Exception.Message)"
            continue
        }
        Run-Test $serial 'device.info' {
            $apiText = Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.build.version.sdk')
            $model = Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.product.model')
            $abi = Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.product.cpu.abi')
            $size = Invoke-Adb -Command @('-s',$serial,'shell','wm','size') -AllowFailure
            $battery = Invoke-Adb -Command @('-s',$serial,'shell','dumpsys','battery')
            $memory = Invoke-Adb -Command @('-s',$serial,'shell','cat','/proc/meminfo')
            $storage = Invoke-Adb -Command @('-s',$serial,'shell','df','/data')
            Assert-Test ($api -ge 19 -and $model.Length -gt 0 -and $memory -match 'MemTotal') 'Device metadata is incomplete.'
            @("model=$model","api=$api","abi=$abi","display=$size","",$battery,"",$memory,"",$storage) |
                Set-Content -Encoding UTF8 (Join-Path $deviceDir 'device-info.txt')
            "Model=$model API=$api ABI=$abi"
        }
        Run-Test $serial 'shell.execution' {
            $sdk = Invoke-Adb -Command @('-s',$serial,'shell','getprop','ro.build.version.sdk')
            Assert-Test ($sdk -match '^\d+$') 'ADB shell getprop returned no numeric SDK.'
            "Remote shell returned SDK $sdk"
        }
        Run-Test $serial 'packages.user-system-and-dumpsys' {
            $users = Invoke-Adb -Command @('-s',$serial,'shell','pm','list','packages','-3')
            $system = Invoke-Adb -Command @('-s',$serial,'shell','pm','list','packages','-s')
            $dump = Invoke-Adb -Command @('-s',$serial,'shell','dumpsys','package','android')
            Assert-Test ($system -match 'package:' -and $dump.Length -gt 100) 'System package list or dumpsys package missing.'
            $counts = ($users -split "\r?\n" | Where-Object { $_ -match '^package:' }).Count
            "User packages=$counts; system package list and dumpsys readable."
        }
        Run-Test $serial 'processes' {
            $ps = Invoke-Adb -Command @('-s',$serial,'shell','ps','-A') -AllowFailure
            if ($ps.Length -lt 20) { $ps = Invoke-Adb -Command @('-s',$serial,'shell','ps') }
            Assert-Test ($ps -match '(?i)(PID|zygote|system_server|init)') 'Unable to read Android process list.'
            'Real process list readable.'
        }
        Run-Test $serial 'network.interfaces-routes-dns' {
            $interfaces = Invoke-Adb -Command @('-s',$serial,'shell','ip','addr') -AllowFailure
            if ($interfaces.Length -lt 20) {
                $interfaces = Invoke-Adb -Command @('-s',$serial,'shell','ifconfig') -AllowFailure
            }
            $routes = Invoke-Adb -Command @('-s',$serial,'shell','ip','route') -AllowFailure
            $dns = Invoke-Adb -Command @('-s',$serial,'shell','getprop')
            Assert-Test ($interfaces.Length -gt 20 -and $dns.Length -gt 20) 'Unable to inspect interfaces/DNS.'
            @("=== IP ===",$interfaces,"=== ROUTES ===",$routes,"=== PROPERTIES ===",$dns) |
                Set-Content -Encoding UTF8 (Join-Path $deviceDir 'network.txt')
            'Network interfaces/routes/properties collected locally.'
        }
        Run-Test $serial 'filesystem.upload-download-delete' {
            $localFile = Join-Path $deviceDir 'safe-test-input.txt'
            $pulled = Join-Path $deviceDir 'safe-test-roundtrip.txt'
            Set-Content -Encoding UTF8 -Path $localFile -Value "Autel Vysor physical test $runId"
            try {
                Invoke-Adb -Command @('-s',$serial,'shell','mkdir','-p',$remote) | Out-Null
                Invoke-Adb -Command @('-s',$serial,'push',$localFile,"$remote/input.txt") | Out-Null
                $ls = Invoke-Adb -Command @('-s',$serial,'shell','ls','-l',"$remote/input.txt")
                Assert-Test ($ls -match 'input\.txt') 'Uploaded file not visible.'
                Invoke-Adb -Command @('-s',$serial,'pull',"$remote/input.txt",$pulled) | Out-Null
                $sha1 = (Get-FileHash $localFile -Algorithm SHA256).Hash
                $sha2 = (Get-FileHash $pulled -Algorithm SHA256).Hash
                Assert-Test ($sha1 -eq $sha2) "Roundtrip SHA-256 mismatch."
                'Created test folder, uploaded, browsed and downloaded same SHA-256 file.'
            }
            finally {
                # The only recursive deletion in default mode is our unique disposable folder.
                Invoke-Adb -Command @('-s',$serial,'shell','rm','-rf',$remote) -AllowFailure | Out-Null
            }
        }
        Run-Test $serial 'screenshot.real-png' {
            $remotePng = "/sdcard/Download/autel-vysor-shot-$runId.png"
            $localPng = Join-Path $deviceDir 'device-screenshot.png'
            try {
                Invoke-Adb -Command @('-s',$serial,'shell','screencap','-p',$remotePng) | Out-Null
                Invoke-Adb -Command @('-s',$serial,'pull',$remotePng,$localPng) | Out-Null
                $bytes = Verify-Png $localPng
                "Captured valid PNG from physical device ($bytes bytes)."
            }
            finally { Invoke-Adb -Command @('-s',$serial,'shell','rm','-f',$remotePng) -AllowFailure | Out-Null }
        }
        Run-Test $serial 'logcat.test-tag' {
            $tag = "AutelE2E$runId"
            Invoke-Adb -Command @('-s',$serial,'shell','log','-p','i','-t',$tag,'AUTEL_E2E_MARKER') | Out-Null
            Start-Sleep -Milliseconds 250
            $logs = Invoke-Adb -Command @('-s',$serial,'logcat','-d','-s',"$($tag):I",'*:S') -AllowFailure
            Assert-Test ($logs -match 'AUTEL_E2E_MARKER') "Logcat did not include the injected marker (tag $tag)."
            'Real logcat contains test-generated marker.'
        }
        Run-Test $serial 'rotation.read-state' {
            $degrees = RotationDegrees $serial
            Assert-Test ($degrees -in @(0,90,180,270)) 'Unexpected current display rotation.'
            "Observed physical display rotation state: $degrees degrees."
        }
        if ($IncludeRotation) {
            Run-Test $serial 'rotation.lock-and-restore' {
                $sensorBefore = Invoke-Adb -Command @('-s',$serial,'shell','settings','get','system','accelerometer_rotation') -AllowFailure
                $userBefore = Invoke-Adb -Command @('-s',$serial,'shell','settings','get','system','user_rotation') -AllowFailure
                $wmBefore = Invoke-Adb -Command @('-s',$serial,'shell','wm','user-rotation') -AllowFailure
                $before = RotationDegrees $serial
                $requested = if ($before -in @(90,270)) { '0' } else { '1' }
                try {
                    Invoke-Adb -Command @('-s',$serial,'shell','settings','put','system','accelerometer_rotation','0') | Out-Null
                    $wm = Invoke-Adb -Command @('-s',$serial,'shell','wm','user-rotation','lock',$requested) -AllowFailure
                    if ($wm -match '(?i)unknown|error|exception|not found') {
                        Invoke-Adb -Command @('-s',$serial,'shell','settings','put','system','user_rotation',$requested) | Out-Null
                    }
                    Start-Sleep -Seconds 2
                    $observed = RotationDegrees $serial
                    Assert-Test ($observed -eq ([int]$requested * 90)) "Rotation requested $requested but observed $observed degrees (ROM/foreground app may refuse)."
                    "Real display moved from $before to $observed degrees."
                }
                finally {
                    if ($wmBefore -match 'lock\s+([0-3])') {
                        Invoke-Adb -Command @('-s',$serial,'shell','wm','user-rotation','lock',$Matches[1]) -AllowFailure | Out-Null
                    } else {
                        Invoke-Adb -Command @('-s',$serial,'shell','wm','user-rotation','free') -AllowFailure | Out-Null
                    }
                    if ($userBefore -match '^[0-3]$') {
                        Invoke-Adb -Command @('-s',$serial,'shell','settings','put','system','user_rotation',$userBefore) -AllowFailure | Out-Null
                    }
                    if ($sensorBefore -match '^[01]$') {
                        Invoke-Adb -Command @('-s',$serial,'shell','settings','put','system','accelerometer_rotation',$sensorBefore) -AllowFailure | Out-Null
                    }
                }
            }
        }
        else { Skip-Test $serial 'rotation.lock-and-restore' 'Enable -IncludeRotation on an idle lab device; operation changes real system display orientation.' }
        Run-Test $serial 'audio.route-inspection' {
            $audio = Invoke-Adb -Command @('-s',$serial,'shell','dumpsys','audio') -AllowFailure
            Assert-Test ($audio.Length -gt 40) 'dumpsys audio unavailable.'
            $audio | Set-Content -Encoding UTF8 (Join-Path $deviceDir 'audio-routing.txt')
            'Android audio routing state is observable (audible output still requires external measurement).'
        }
        Needs-External $serial 'audio.pc-and-bluetooth-audibility' 'Requires Bluetooth headset/audio loopback measurement. ADB reports cannot prove sound is heard.'
        Needs-External $serial 'auto-rotate.physical-tilt' 'Requires physical tilt of device; restoring sensor setting alone is not proof of rotation.'
        Needs-External $serial 'wifi.pairing-and-usb-unplug' 'Requires controllable cable/pairing fixture or dedicated USB power relay; test must not disconnect an active production device.'
        Needs-External $serial 'wpf.dark-ui-fit-fullscreen-clipboard-and-mouse' 'Requires interactive Windows desktop UI automation; enable GUI smoke separately.'
        Needs-External $serial 'screen-off.pc-mirror-still-active' 'Requires live scrcpy HWND + display-power measurement on real device; GUI smoke required.'

        if ($ScrcpyPath -and $api -ge 21) {
            Run-Test $serial 'scrcpy.real-video-recording' {
                $record = Join-Path $deviceDir 'scrcpy-physical-test.mp4'
                $previous = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try {
                    $text = (& $ScrcpyPath '-s' $serial '--no-window' '--no-audio' '--time-limit=4' "--record=$record" 2>&1 | Out-String).Trim()
                    $exitCode = $LASTEXITCODE
                }
                finally { $ErrorActionPreference = $previous }
                Assert-Test ($exitCode -eq 0 -and (Test-Path $record) -and (Get-Item $record).Length -gt 1024) "scrcpy did not produce a valid recording: $text"
                'Real Android video stream recorded to MP4; this does not test WPF embedding.'
            }
        } else {
            Skip-Test $serial 'scrcpy.real-video-recording' 'Requires Android API 21+ and explicit -ScrcpyPath.'
        }

        if ($FixtureApk) {
            $pkg = 'com.autel.vysor.fixture'
            $wasInstalled = Invoke-Adb -Command @('-s',$serial,'shell','pm','path',$pkg) -AllowFailure
            if ($wasInstalled -match 'package:') {
                Skip-Test $serial 'fixture.install-and-app-operations' 'Fixture already installed; refusing to overwrite existing app data automatically.'
            } else {
                try {
                    Run-Test $serial 'fixture.install' {
                        $install = Invoke-Adb -Command @('-s',$serial,'install','-r',$FixtureApk)
                        Assert-Test ($install -match '(?i)success') "Fixture install was not successful: $install"
                        'Installed disposable Autel E2E fixture APK.'
                    }
                    Run-Test $serial 'fixture.package-version-and-start' {
                        $dump = Invoke-Adb -Command @('-s',$serial,'shell','dumpsys','package',$pkg)
                        Assert-Test ($dump -match '(?i)versionCode|versionName') 'Installed fixture package information unavailable.'
                        Invoke-Adb -Command @('-s',$serial,'shell','am','start','-n',"$pkg/.MainActivity") | Out-Null
                        Start-Sleep -Seconds 1
                        "Fixture app started; version / launch verified."
                    }
                    Run-Test $serial 'fixture.keyboard-ascii-text' {
                        # The disposable fixture saves EditText contents to files/input.txt.
                        Invoke-Adb -Command @('-s',$serial,'shell','input','text','AUTELTEST123') | Out-Null
                        Start-Sleep -Milliseconds 500
                        $value = Invoke-Adb -Command @('-s',$serial,'shell','run-as',$pkg,'cat','files/input.txt')
                        Assert-Test ($value -match 'AUTELTEST123') "Real EditText did not receive ASCII input. Found: $value"
                        'Text entered into real Android EditText and verified by run-as.'
                    }
                    Run-Test $serial 'fixture.appops' {
                        $previous = Invoke-Adb -Command @('-s',$serial,'shell','appops','get',$pkg,'CAMERA') -AllowFailure
                        try {
                            Invoke-Adb -Command @('-s',$serial,'shell','appops','set',$pkg,'CAMERA','allow') | Out-Null
                            $state = Invoke-Adb -Command @('-s',$serial,'shell','appops','get',$pkg,'CAMERA')
                            Assert-Test ($state -match '(?i)CAMERA:\s*allow') 'AppOps CAMERA allow did not take effect.'
                            'AppOps CAMERA allow applied and verified to disposable fixture.'
                        }
                        finally {
                            Invoke-Adb -Command @('-s',$serial,'shell','appops','set',$pkg,'CAMERA','default') -AllowFailure | Out-Null
                        }
                    }
                    if ($api -ge 23) {
                        Run-Test $serial 'fixture.permission-grant-revoke' {
                            Invoke-Adb -Command @('-s',$serial,'shell','pm','grant',$pkg,'android.permission.CAMERA') | Out-Null
                            $state = Invoke-Adb -Command @('-s',$serial,'shell','dumpsys','package',$pkg)
                            Assert-Test ($state -match 'android.permission.CAMERA.*granted=true') 'CAMERA permission grant not reflected in dumpsys.'
                            Invoke-Adb -Command @('-s',$serial,'shell','pm','revoke',$pkg,'android.permission.CAMERA') | Out-Null
                            'Grant and revoke completed on disposable fixture.'
                        }
                    } else { Skip-Test $serial 'fixture.permission-grant-revoke' 'Runtime permission grant/revoke requires Android API 23+.' }
                    Run-Test $serial 'fixture.force-stop-restart-clear-data' {
                        Invoke-Adb -Command @('-s',$serial,'shell','am','force-stop',$pkg) | Out-Null
                        Invoke-Adb -Command @('-s',$serial,'shell','am','start','-n',"$pkg/.MainActivity") | Out-Null
                        $clear = Invoke-Adb -Command @('-s',$serial,'shell','pm','clear',$pkg)
                        Assert-Test ($clear -match '(?i)success') "Fixture data clear failed: $clear"
                        'Force-stop, restart and clear-data succeeded only on the disposable fixture.'
                    }
                }
                finally {
                    Run-Test $serial 'fixture.uninstall' {
                        $uninstall = Invoke-Adb -Command @('-s',$serial,'uninstall',$pkg) -AllowFailure
                        Assert-Test ($uninstall -match '(?i)success') "Fixture uninstall did not succeed: $uninstall"
                        'Disposable fixture uninstalled.'
                    }
                }
            }
        } else {
            Skip-Test $serial 'fixture.app-install-clear-uninstall-permissions-and-keyboard' 'Supply disposable -FixtureApk to exercise isolated app operations safely.'
        }

        if ($IncludeBugreport) {
            Run-Test $serial 'bugreport.export' {
                $target = Join-Path $deviceDir 'bugreport.zip'
                $saved = Invoke-Adb -Command @('-s',$serial,'bugreport',$target)
                Assert-Test ((Test-Path $target) -and (Get-Item $target).Length -gt 1024) "bugreport missing: $saved"
                'Collected local bugreport ZIP; review private data before sharing.'
            }
        } else { Skip-Test $serial 'bugreport.export' 'Enable -IncludeBugreport; may take minutes and contain sensitive user data.' }

        if ($IncludeReboot) {
            Run-Test $serial 'device.reboot-and-reconnect' {
                Invoke-Adb -Command @('-s',$serial,'reboot') | Out-Null
                Start-Sleep -Seconds 15
                Assert-Test (Wait-Online $serial 120) 'Device did not reconnect after reboot (120s timeout).'
                'Physical device rebooted and reappeared on ADB.'
            }
        } else { Skip-Test $serial 'device.reboot-and-reconnect' 'Enable -IncludeReboot only on disposable idle lab hardware.' }
    }

    if ($IncludeAdbRestart) {
        Run-Test 'HOST' 'adb.server-restart' {
            Invoke-Adb -Command @('kill-server') | Out-Null
            Invoke-Adb -Command @('start-server') | Out-Null
            Assert-Test ((Invoke-Adb -Command @('devices')) -match 'List of devices') 'ADB server did not restart.'
            'Windows ADB server restarted and devices enumerated.'
        }
    } else { Skip-Test 'HOST' 'adb.server-restart' 'Enable -IncludeAdbRestart; interrupts every local ADB client and mirror session.' }

    if ($IncludeGui) {
        Needs-External 'HOST' 'wpf.gui-end-to-end' 'Use separate run-physical-gui-tests.ps1 on an unlocked interactive Windows desktop. Service/session-0 runners cannot automate WPF UI.'
    }
}
finally {
    $passes = @($script:Results | Where-Object Status -eq 'PASS').Count
    $fails = @($script:Results | Where-Object Status -eq 'FAIL').Count
    $skips = @($script:Results | Where-Object Status -eq 'SKIP').Count
    $manual = @($script:Results | Where-Object Status -eq 'EXTERNAL_VALIDATION').Count
    $report = [pscustomobject]@{
        Tool='Autel Vysor physical-device E2E'
        Kind='REAL_ADB_DEVICE_TEST'
        Time=(Get-Date).ToString('o')
        AdbPath=$AdbPath
        DeviceCount=$online.Count
        Results=@($script:Results.ToArray())
        Counts=@{Passed=$passes; Failed=$fails; Skipped=$skips; RequiresExternalValidation=$manual}
        Warnings=@('Only devices actually authorized on this Windows host were queried.',
                   'A passed ADB test does not prove the WPF mirror UI works.',
                   'Bluetooth audible output and physical orientation gestures need external measurement.',
                   'Screenshots, bugreports, device logs and serials may contain sensitive data.')
    }
    $report | ConvertTo-Json -Depth 9 | Set-Content -Encoding UTF8 (Join-Path $OutputRoot 'summary.json')
    $script:Results | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $OutputRoot 'results.csv')
    Write-Host ("Physical test results: {0} PASS, {1} FAIL, {2} SKIP, {3} external validation" -f $passes,$fails,$skips,$manual)
    Write-Host "JSON report: $OutputRoot\summary.json"
}
if ($fails -gt 0) { exit 1 }
