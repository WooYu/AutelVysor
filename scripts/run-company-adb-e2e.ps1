<#
Autel Vysor company-only REAL Android smoke suite.
Requirements: existing company-approved adb.exe + Windows PowerShell 5.1.
No downloaded binaries, GitHub runner, scrcpy, Android APK or new EXE.
Reports: local HTML, CSV and JSON. Backend PASS != Windows GUI PASS.
#>
[CmdletBinding()]
param(
  [string]$AdbPath='',
  [string[]]$Serials=@(),
  [string]$OutputRoot='',
  [switch]$IncludeRotation,
  [switch]$IncludeRecording,
  [switch]$IncludeBugreport,
  [switch]$IncludeReboot,
  [switch]$IncludeAdbRestart,
  [string]$FixturePackage='',
  [switch]$TestFixtureInput,
  [switch]$IncludeFixtureMutations
)

$ErrorActionPreference='Stop'
$script:Results=New-Object 'System.Collections.Generic.List[object]'
$script:RunId=[guid]::NewGuid().ToString('N').Substring(0,12)
$script:NewLine=[Environment]::NewLine
$script:StartTime=(Get-Date).ToUniversalTime().ToString('o')
$repo=Split-Path -Parent $PSScriptRoot
if(-not $OutputRoot) {
  $OutputRoot=Join-Path $repo ('test-results\company-adb-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot=(Resolve-Path -LiteralPath $OutputRoot).Path

if(-not $AdbPath) {
  $tool=Get-Command adb.exe -ErrorAction SilentlyContinue
  if($tool) {$AdbPath=$tool.Source}
}
if(-not $AdbPath -or -not (Test-Path -LiteralPath $AdbPath -PathType Leaf)) {
  throw 'Company-approved adb.exe not found. Pass -AdbPath pointing to the existing corporate ADB executable.'
}
$script:AdbPath=(Resolve-Path -LiteralPath $AdbPath).Path
if($FixturePackage -and $FixturePackage -notmatch '^com\.autel\.vysor\.fixture(?:\.[A-Za-z0-9_]+)*
if(($TestFixtureInput -or $IncludeFixtureMutations) -and -not $FixturePackage) {
  throw 'Provide the already approved/installed -FixturePackage to enable app input or mutations.'
}
if($IncludeFixtureMutations -and -not $TestFixtureInput) {
  throw 'To clear data of the disposable fixture, specify BOTH -TestFixtureInput and -IncludeFixtureMutations.'
}

function A {
  param([string[]]$Arguments,[switch]$AllowFailure)
  $before=$ErrorActionPreference
  $ErrorActionPreference='Continue'
  try {
    $stdout=(& $script:AdbPath @Arguments 2>&1 | Out-String).Trim()
    $code=$LASTEXITCODE
  } finally {$ErrorActionPreference=$before}
  if($code -ne 0 -and -not $AllowFailure) {throw ("adb failed (exit {0}): {1}" -f $code,$stdout)}
  [pscustomobject]@{ Code=[int]$code; Text=[string]$stdout }
}
function D {
  param([string]$Serial,[string[]]$Arguments,[switch]$AllowFailure)
  A -Arguments (@('-s',$Serial)+$Arguments) -AllowFailure:$AllowFailure
}
function Assert {
  param([bool]$Condition,[string]$Message)
  if(-not $Condition) {throw $Message}
}
function Result {
  param([string]$Device,[string]$Feature,[string]$Status,[string]$Details)
  $script:Results.Add([pscustomobject]@{
    Device=$Device; Feature=$Feature; Status=$Status
    Details=$Details; Time=(Get-Date).ToUniversalTime().ToString('o')
  })
  Write-Host ("[{0}] {1} / {2}: {3}" -f $Status,$Device,$Feature,$Details)
}
function Test {
  param([string]$Device,[string]$Feature,[scriptblock]$Action)
  try {
    $answer=& $Action
    Result $Device $Feature 'PASS' ([string]$answer)
  } catch {
    Result $Device $Feature 'FAIL' $_.Exception.Message
  }
}
function Skip {
  param([string]$Device,[string]$Feature,[string]$Why)
  Result $Device $Feature 'SKIP' $Why
}
function Unavailable {
  param([string]$Device,[string]$Feature,[string]$Why,[string]$State='BLOCKED_POLICY')
  Result $Device $Feature $State $Why
}
function Save {
  param([string]$Folder,[string]$Name,[string]$Value)
  [IO.File]::WriteAllText((Join-Path $Folder $Name),$Value,[Text.Encoding]::UTF8)
}
function Rotation {
  param([string]$Serial)
  $raw=(D $Serial @('shell','dumpsys','input') -AllowFailure).Text
  if($raw -match 'SurfaceOrientation:\s*([0-3])') {
    return [pscustomobject]@{ Degrees=[int]$Matches[1]*90; Source='dumpsys input'; Physical=$true }
  }
  $window=(D $Serial @('shell','dumpsys','window') -AllowFailure).Text
  if($window -match '\bmRotation\s*=\s*([0-3])\b') {
    return [pscustomobject]@{ Degrees=[int]$Matches[1]*90; Source='dumpsys window'; Physical=$true }
  }
  $setting=(D $Serial @('shell','settings','get','system','user_rotation') -AllowFailure).Text
  if($setting -match '^\s*([0-3])\s*$') {
    return [pscustomobject]@{ Degrees=[int]$Matches[1]*90; Source='settings only'; Physical=$false }
  }
  throw 'This ROM does not expose a parseable rotation value.'
}
function VerifyPng {
  param([string]$File)
  $stream=[IO.File]::OpenRead($File)
  try {
    Assert ($stream.Length -gt 24) 'Screenshot is empty.'
    $b=New-Object byte[] 24
    [void]$stream.Read($b,0,24)
    $signature=@(137,80,78,71,13,10,26,10)
    for($i=0;$i -lt 8;$i++) {Assert ([int]$b[$i] -eq $signature[$i]) 'PNG signature does not match.'}
    $width=([int]$b[16] -shl 24) -bor ([int]$b[17] -shl 16) -bor ([int]$b[18] -shl 8) -bor [int]$b[19]
    $height=([int]$b[20] -shl 24) -bor ([int]$b[21] -shl 16) -bor ([int]$b[22] -shl 8) -bor [int]$b[23]
    Assert ($width -gt 100 -and $height -gt 100) 'Unexpected PNG dimensions.'
    "$width x $height ($($stream.Length) bytes)"
  } finally {$stream.Dispose()}
}
function WaitDevice {
  param([string]$Serial,[int]$Seconds)
  $end=(Get-Date).AddSeconds($Seconds)
  while((Get-Date) -lt $end) {
    $state=D $Serial @('get-state') -AllowFailure
    if($state.Code -eq 0 -and $state.Text -eq 'device') {return $true}
    Start-Sleep -Seconds 3
  }
  $false
}

Write-Host 'Autel Vysor: REAL devices, approved ADB + PowerShell ONLY.'
Write-Host "Using existing ADB: $script:AdbPath"
Write-Host "Local report folder: $OutputRoot"
$listed=(A @('devices','-l')).Text
Save $OutputRoot 'adb-devices.txt' $listed
$detected=@($listed -split "\r?\n" | ForEach-Object {
  if($_ -match '^\s*(\S+)\s+device(?:\s|$)') {$Matches[1]}
} | Where-Object {$_})
if($Serials.Count -gt 0) {
  foreach($serial in $Serials) {
    if($detected -notcontains $serial) {throw "Requested authorized device not found: $serial"}
  }
  $detected=@($Serials)
}
if($detected.Count -eq 0) {throw 'No authorized ADB devices. Confirm USB debugging and approved adb devices -l.'}

try {
foreach($serial in $detected) {
  $folder=Join-Path $OutputRoot ("device-"+($serial -replace '[^A-Za-z0-9_.-]','_'))
  New-Item -ItemType Directory -Force -Path $folder | Out-Null
  $qemu=(D $serial @('shell','getprop','ro.kernel.qemu') -AllowFailure).Text
  $qemu2=(D $serial @('shell','getprop','ro.boot.qemu') -AllowFailure).Text
  if($serial.StartsWith('emulator-',[StringComparison]::OrdinalIgnoreCase) -or $qemu -eq '1' -or $qemu2 -eq '1') {
    Result $serial 'device.physical' 'FAIL' 'Emulator detected; emulator results never count as physical testing.'
    continue
  }
  Test $serial 'device.connected' {
    Assert ((D $serial @('get-state')).Text -eq 'device') 'ADB device is not online.'
    'Physical device is online and authorized.'
  }
  try {
    $api=[int](D $serial @('shell','getprop','ro.build.version.sdk')).Text
    Assert ($api -ge 19) "Unsupported API level $api."
  } catch {
    Result $serial 'device.api' 'FAIL' $_.Exception.Message
    continue
  }

  Test $serial 'device.model-battery-memory-storage-display' {
    $model=(D $serial @('shell','getprop','ro.product.model')).Text
    $release=(D $serial @('shell','getprop','ro.build.version.release')).Text
    $abi=(D $serial @('shell','getprop','ro.product.cpu.abi')).Text
    $size=(D $serial @('shell','wm','size') -AllowFailure).Text
    $density=(D $serial @('shell','wm','density') -AllowFailure).Text
    $battery=(D $serial @('shell','dumpsys','battery')).Text
    $mem=(D $serial @('shell','cat','/proc/meminfo')).Text
    $storage=(D $serial @('shell','df','/data') -AllowFailure).Text
    Assert ($model.Length -gt 0 -and $mem -match 'MemTotal' -and $battery.Length -gt 30) 'Device metadata incomplete.'
    Save $folder 'device.txt' ((@("Model: $model","Android: $release (API $api)","ABI: $abi",$size,$density,$battery,$mem,$storage)) -join $script:NewLine)
    "$model, Android $release, API $api"
  }
  Test $serial 'shell.exec' {
    Assert ((D $serial @('shell','getprop','ro.build.version.sdk')).Text -eq "$api") 'Android shell getprop mismatch.'
    'Real shell command executed.'
  }
  Test $serial 'apps.list-system-user-search' {
    $all=(D $serial @('shell','pm','list','packages')).Text
    $system=(D $serial @('shell','pm','list','packages','-s')).Text
    $user=(D $serial @('shell','pm','list','packages','-3')).Text
    Assert ($all -match 'package:' -and $system -match 'package:') 'Package listing returned no packages.'
    "System=$(@($system -split "\r?\n" | Where-Object {$_ -match '^package:'}).Count); User=$(@($user -split "\r?\n" | Where-Object {$_ -match '^package:'}).Count)"
  }
  Test $serial 'apps.dumpsys-package' {
    $info=(D $serial @('shell','dumpsys','package','android')).Text
    Assert ($info.Length -gt 100) 'System package dump is missing.'
    'Package metadata readable.'
  }
  Test $serial 'processes.list' {
    $processes=D $serial @('shell','ps','-A') -AllowFailure
    if($processes.Code -ne 0 -or $processes.Text.Length -lt 20) {$processes=D $serial @('shell','ps')}
    Assert ($processes.Text -match '(?i)\bPID\b') 'No PID column found.'
    'Android processes readable.'
  }
  Test $serial 'network.interfaces-DNS-routes' {
    $ip=D $serial @('shell','ip','addr') -AllowFailure
    if($ip.Code -ne 0 -or $ip.Text.Length -lt 15) {$ip=D $serial @('shell','ifconfig') -AllowFailure}
    $routes=(D $serial @('shell','ip','route') -AllowFailure).Text
    $dns=(D $serial @('shell','getprop','net.dns1') -AllowFailure).Text
    Assert ($ip.Text.Length -gt 15) 'Unable to inspect an Android network interface.'
    Save $folder 'network.txt' ((@($ip.Text,$routes,"DNS hint: $dns")) -join $script:NewLine)
    'Interfaces, routes and DNS indicators collected.'
  }
  Test $serial 'network.tcp-udp-sockets' {
    $socket=D $serial @('shell','ss','-tuna') -AllowFailure
    if($socket.Code -ne 0 -or $socket.Text.Length -lt 10) {
      $socket=D $serial @('shell','netstat','-an') -AllowFailure
    }
    Assert ($socket.Code -eq 0 -and $socket.Text.Length -gt 10) 'No Android socket listing available. ROM restrictions may apply.'
    Save $folder 'sockets.txt' $socket.Text
    'Read real Android socket/port listing (no network traffic sent).'
  }
  Test $serial 'network.loopback-ping' {
    $response=D $serial @('shell','ping','-c','1','-W','2','127.0.0.1') -AllowFailure
    Assert ($response.Code -eq 0 -and $response.Text -match '(?i)(1 received|1 packets received)') "Loopback ping failed: $($response.Text)"
    'Physical Android loopback ping succeeded.'
  }
  Test $serial 'files.mkdir-push-pull-sha256-delete' {
    $remote="/sdcard/Download/autel-vysor-$($script:RunId)"
    $local=Join-Path $folder 'input.txt'
    $received=Join-Path $folder 'downloaded.txt'
    [IO.File]::WriteAllText($local,("Company ADB test "+$script:RunId),[Text.Encoding]::UTF8)
    $made=$false
    try {
      [void](D $serial @('shell','mkdir','-p',$remote))
      $made=$true
      [void](D $serial @('push',$local,($remote+'/input.txt')))
      $remoteListing=(D $serial @('shell','ls','-l',($remote+'/input.txt'))).Text
      Assert ($remoteListing -match 'input.txt') 'Uploaded file not listed.'
      [void](D $serial @('pull',($remote+'/input.txt'),$received))
      Assert ((Get-FileHash $local -Algorithm SHA256).Hash -eq (Get-FileHash $received -Algorithm SHA256).Hash) 'SHA256 mismatch on roundtrip.'
      'Created temporary folder; upload / download / content SHA256 verified.'
    } finally {
      if($made) {
        $cleanup=D $serial @('shell','rm','-rf',$remote) -AllowFailure
        if($cleanup.Code -ne 0){Result $serial 'files.cleanup' 'FAIL' 'Temporary test folder removal failed.'}
      }
    }
  }
  Test $serial 'captures.screenshot-PNG' {
    $remote="/sdcard/Download/autel-vysor-$($script:RunId)-screenshot.png"
    $local=Join-Path $folder 'screenshot.png'
    try {
      [void](D $serial @('shell','screencap','-p',$remote))
      [void](D $serial @('pull',$remote,$local))
      $png=VerifyPng $local
      "Real Android screenshot: $png"
    } finally {
      $cleanup=D $serial @('shell','rm','-f',$remote) -AllowFailure
      if($cleanup.Code -ne 0){Result $serial 'captures.cleanup' 'FAIL' 'Temporary PNG removal failed.'}
    }
  }
  Test $serial 'logcat.tag-level' {
    $tag='AutelE2E'+$script:RunId
    $marker='VERIFIED_'+$script:RunId
    [void](D $serial @('shell','log','-p','i','-t',$tag,$marker))
    Start-Sleep -Milliseconds 500
    $data=(D $serial @('logcat','-d','-s',($tag+':I'),'*:S') -AllowFailure).Text
    Assert ($data -match [regex]::Escape($marker)) 'Logcat did not contain the test-generated marker.'
    'Real Info-level / Tag logcat filtering verified.'
  }
  Test $serial 'rotation.read-physical-state' {
    $r=Rotation $serial
    Assert ($r.Physical) "ROM only returns rotation setting, not physical surface state: $($r.Source)"
    "Observed $($r.Degrees) degrees from $($r.Source)."
  }
  if($IncludeRotation) {
    Test $serial 'rotation.actual-lock-and-restore' {
      $oldSensor=(D $serial @('shell','settings','get','system','accelerometer_rotation') -AllowFailure).Text
      $oldUser=(D $serial @('shell','settings','get','system','user_rotation') -AllowFailure).Text
      $oldWm=(D $serial @('shell','wm','user-rotation') -AllowFailure).Text
      $before=Rotation $serial
      Assert ($before.Physical) 'Cannot verify real rotation on this ROM.'
      $target=if($before.Degrees -in @(90,270)){'0'}else{'1'}
      try {
        [void](D $serial @('shell','settings','put','system','accelerometer_rotation','0'))
        $command=D $serial @('shell','wm','user-rotation','lock',$target) -AllowFailure
        if($command.Code -ne 0 -or $command.Text -match '(?i)error|unknown|unsupported') {
          [void](D $serial @('shell','settings','put','system','user_rotation',$target))
        }
        Start-Sleep -Seconds 2
        $after=Rotation $serial
        Assert ($after.Physical -and $after.Degrees -eq ([int]$target*90)) "Screen orientation did not change. Target=$target Observed=$($after.Degrees)."
        "Real Android orientation changed $($before.Degrees) -> $($after.Degrees)."
      } finally {
        if($oldWm -match 'lock\s+([0-3])') {
          $saved=$Matches[1]
          [void](D $serial @('shell','wm','user-rotation','lock',$saved) -AllowFailure)
        } else {
          [void](D $serial @('shell','wm','user-rotation','free') -AllowFailure)
        }
        if($oldUser -match '^[0-3]$') {[void](D $serial @('shell','settings','put','system','user_rotation',$oldUser) -AllowFailure)}
        if($oldSensor -match '^[01]$') {
          [void](D $serial @('shell','settings','put','system','accelerometer_rotation',$oldSensor) -AllowFailure)
          Assert ((D $serial @('shell','settings','get','system','accelerometer_rotation') -AllowFailure).Text -eq $oldSensor) 'Could not restore sensor rotation mode.'
        }
      }
    }
  } else {Skip $serial 'rotation.actual-lock-and-restore' 'Opt-in -IncludeRotation changes real device orientation temporarily.'}
  Test $serial 'audio.route-metadata' {
    $a=D $serial @('shell','dumpsys','audio') -AllowFailure
    Assert ($a.Code -eq 0 -and $a.Text.Length -gt 40) 'Audio dumpsys unavailable.'
    Save $folder 'audio-route.txt' $a.Text
    'Audio metadata captured (NOT proof of audible Bluetooth sound).'
  }
  if($IncludeRecording) {
    Test $serial 'captures.Android-system-recording' {
      $remote="/sdcard/Download/autel-vysor-$($script:RunId).mp4"
      $local=Join-Path $folder 'recording.mp4'
      try {
        $video=D $serial @('shell','screenrecord','--time-limit','3',$remote) -AllowFailure
        Assert ($video.Code -eq 0) "Android screenrecord failed: $($video.Text)"
        [void](D $serial @('pull',$remote,$local))
        $bytes=[IO.File]::ReadAllBytes($local)
        Assert ($bytes.Length -gt 8192) 'MP4 recording is too small.'
        Assert ([Text.Encoding]::ASCII.GetString($bytes,4,4) -eq 'ftyp') 'MP4 ftyp header missing.'
        'Real Android screenrecord saved a valid MP4 container.'
      } finally {
        [void](D $serial @('shell','rm','-f',$remote) -AllowFailure)
      }
    }
  } else {Skip $serial 'captures.Android-system-recording' 'Opt-in -IncludeRecording records the real Android screen using the existing ADB shell.'}

  if($FixturePackage) {
    $installed=D $serial @('shell','pm','path',$FixturePackage) -AllowFailure
    if($installed.Code -ne 0 -or $installed.Text -notmatch 'package:') {
      Skip $serial 'fixture.input-and-permissions' 'Approved test APK is not already installed; no APK is downloaded.'
    } else {
      Test $serial 'fixture.inspect' {
        $data=(D $serial @('shell','dumpsys','package',$FixturePackage)).Text
        Assert ($data -match '(?i)versionName|versionCode') 'Fixture package dump not available.'
        'Installed approved test fixture inspected.'
      }
      Test $serial 'fixture.permission-and-appops-inspection' {
        $dump=(D $serial @('shell','dumpsys','package',$FixturePackage')).Text
        $ops=D $serial @('shell','appops','get',$FixturePackage) -AllowFailure
        Assert ($dump -match 'permission' -and $ops.Code -eq 0) 'Fixture permission/AppOps not readable on this ROM.'
        Save $folder 'fixture-permissions.txt' ((@($dump.Substring(0,[Math]::Min(5000,$dump.Length)),$ops.Text)) -join $script:NewLine)
        'Runtime permission metadata and AppOps state inspected on approved fixture only.'
      }
      if($TestFixtureInput) {
        Test $serial 'fixture.ADB-text-entry-to-real-EditText' {
          [void](D $serial @('shell','am','start','-n',($FixturePackage+'/.MainActivity')))
          Start-Sleep -Seconds 1
          $marker='AUTEL'+$script:RunId
          [void](D $serial @('shell','input','text',$marker))
          Start-Sleep -Milliseconds 700
          $input=D $serial @('shell','run-as',$FixturePackage,'cat','files/input.txt') -AllowFailure
          Assert ($input.Code -eq 0 -and $input.Text.Contains($marker)) 'Approved fixture input.txt did not contain actual Android EditText text.'
          'Real Android EditText received ADB text through installed test fixture.'
        }
      } else {Skip $serial 'fixture.ADB-text-entry-to-real-EditText' 'Opt-in -TestFixtureInput on an existing company-approved fixture.'}
      if($IncludeFixtureMutations) {
        Test $serial 'fixture.stop-clear-restart' {
          [void](D $serial @('shell','am','force-stop',$FixturePackage))
          $cleared=(D $serial @('shell','pm','clear',$FixturePackage)).Text
          Assert ($cleared -match '(?i)success') 'Fixture data clearing failed.'
          [void](D $serial @('shell','am','start','-n',($FixturePackage+'/.MainActivity')))
          'Only explicitly approved disposable fixture data cleared; fixture restarted.'
        }
      } else {Skip $serial 'fixture.stop-clear-restart' 'Requires explicit -IncludeFixtureMutations; clears test-app data.'}
      if($IncludeFixtureMutations -and $api -ge 23) {
        Test $serial 'fixture.CAMERA-permission-grant-and-restore' {
          $dump=(D $serial @('shell','dumpsys','package',$FixturePackage')).Text
          $match=[regex]::Match($dump,'(?m)^\s*android\.permission\.CAMERA:\s+granted=(true|false)')
          Assert ($match.Success) 'Fixture does not expose restorable CAMERA runtime permission state.'
          $previous=$match.Groups[1].Value
          try {
            [void](D $serial @('shell','pm','grant',$FixturePackage,'android.permission.CAMERA'))
            $fresh=(D $serial @('shell','dumpsys','package',$FixturePackage')).Text
            Assert ($fresh -match '(?m)^\s*android\.permission\.CAMERA:\s+granted=true') 'CAMERA grant not reflected by Android.'
            'CAMERA granted and verified on disposable fixture; original state restored.'
          } finally {
            $verb=if($previous -eq 'true'){'grant'}else{'revoke'}
            [void](D $serial @('shell','pm',$verb,$FixturePackage,'android.permission.CAMERA') -AllowFailure)
            $restored=(D $serial @('shell','dumpsys','package',$FixturePackage') -AllowFailure).Text
            Assert ($restored -match ('(?m)^\s*android\.permission\.CAMERA:\s+granted='+$previous)) 'Failed to restore initial CAMERA permission state.'
          }
        }
        Test $serial 'fixture.CAMERA-appops-set-and-restore' {
          $previous=(D $serial @('shell','appops','get',$FixturePackage,'CAMERA') -AllowFailure).Text
          $mode='default'
          if($previous -match '(?i)CAMERA:\s*(allow|ignore|deny|default|foreground)') {
            $mode=$Matches[1].ToLowerInvariant()
          } elseif($previous -notmatch '(?i)no operations|no ops|not found') {
            throw 'Cannot safely determine original CAMERA AppOps mode; leaving it unchanged.'
          }
          try {
            [void](D $serial @('shell','appops','set',$FixturePackage,'CAMERA','allow'))
            $new=(D $serial @('shell','appops','get',$FixturePackage,'CAMERA')).Text
            Assert ($new -match '(?i)CAMERA:\s*allow') 'AppOps CAMERA allow not confirmed.'
            'Camera AppOps set and verified; original mode restored.'
          } finally {
            [void](D $serial @('shell','appops','set',$FixturePackage,'CAMERA',$mode) -AllowFailure)
          }
        }
      } else {
        Skip $serial 'fixture.CAMERA-grant-revoke-AppOps' 'Requires Android API 23+ and explicit -IncludeFixtureMutations on dedicated fixture.'
      }
    }
  } else {Skip $serial 'fixture.input-permission-and-app-management' 'Company-approved test fixture not supplied. Business apps will not be modified.'}

  if($IncludeBugreport) {
    Test $serial 'diagnostics.bugreport' {
      $file=Join-Path $folder 'bugreport.zip'
      $result=D $serial @('bugreport',$file) -AllowFailure
      Assert ($result.Code -eq 0 -and (Test-Path $file) -and (Get-Item $file).Length -gt 1024) 'Bugreport ZIP unavailable.'
      'Bugreport saved locally (may contain confidential device data).'
    }
  } else {Skip $serial 'diagnostics.bugreport' 'Enable -IncludeBugreport only on authorized lab hardware.'}
  if($IncludeReboot) {
    Test $serial 'device.reboot-return-online' {
      [void](D $serial @('reboot'))
      Start-Sleep -Seconds 12
      Assert (WaitDevice $serial 150) 'Device did not reconnect within 150 seconds.'
      'Physical Android rebooted and returned to ADB.'
    }
  } else {Skip $serial 'device.reboot-return-online' 'Enable -IncludeReboot only on dedicated lab hardware.'}

  $reason='Autel Vysor / scrcpy executable is not company-approved. ADB success does not verify PC Windows UI.'
  foreach($feature in @(
    'mirror.WPF-stream-Fit-1to1-fullscreen-sidebar',
    'mirror.PC-keyboard-focus-Chinese-clipboard',
    'mirror.mouse-touch-controls',
    'mirror.real-rotation-buttons',
    'mirror.screen-off-and-live-image',
    'mirror.disconnection-autorecovery',
    'mirror.PC-audio-duplication',
    'files.WPF-dragdrop',
    'shell.WPF-terminal-and-history',
    'logcat.WPF-tag-PID-level-pause',
    'apps.WPF-install-uninstall-permissions',
    'network.WPF-controls',
    'capture.WPF-video-playback',
    'batch.WPF-multiple-devices',
    'GitHub.WPF-update-check'
  )) {Unavailable $serial $feature $reason}
  Unavailable $serial 'Bluetooth.real-headset-sound' 'Requires audible recording hardware or human listening, not dumpsys.' 'EXTERNAL_VALIDATION'
  Unavailable $serial 'sensor.physical-tilt' 'Requires physical device tilt / actuator and video observation.' 'EXTERNAL_VALIDATION'
  Unavailable $serial 'USB.physical-disconnect' 'Requires an approved USB relay or human-controlled cable disconnect.' 'EXTERNAL_VALIDATION'
}
if($IncludeAdbRestart) {
  Test 'HOST' 'ADB.kill-and-start-server' {
    [void](A @('kill-server'))
    [void](A @('start-server'))
    Assert ((A @('devices')).Text -match 'List of devices') 'Host ADB could not restart.'
    'Existing company ADB server restarted.'
  }
} else {Skip 'HOST' 'ADB.kill-and-start-server' 'Only with -IncludeAdbRestart: disrupts every active ADB session on PC.'}
} finally {
  $counts=@{}
  foreach($status in @('PASS','FAIL','SKIP','BLOCKED_POLICY','EXTERNAL_VALIDATION')) {
    $counts[$status]=@($script:Results | Where-Object {$_.Status -eq $status}).Count
  }
  $summary=[pscustomobject]@{
    Suite='Autel Vysor - company ADB/PowerShell only'
    ExecutionType='REAL_PHYSICAL_ADB_NO_EXTERNAL_EXE'
    StartedUtc=$script:StartTime
    CompletedUtc=(Get-Date).ToUniversalTime().ToString('o')
    Devices=@($detected)
    Counts=$counts
    Results=@($script:Results.ToArray())
    Warning='ADB backend PASS does NOT mean Windows UI, Bluetooth sound or PC keyboard is validated.'
  }
  $jsonPath=Join-Path $OutputRoot 'summary.json'
  $csvPath=Join-Path $OutputRoot 'results.csv'
  $htmlPath=Join-Path $OutputRoot 'report.html'
  $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $jsonPath
  $script:Results | Export-Csv -Encoding UTF8 -NoTypeInformation -Path $csvPath
  $rows=@($script:Results | ForEach-Object {
    $d=[Net.WebUtility]::HtmlEncode($_.Device)
    $f=[Net.WebUtility]::HtmlEncode($_.Feature)
    $s=[Net.WebUtility]::HtmlEncode($_.Status)
    $n=[Net.WebUtility]::HtmlEncode($_.Details)
    "<tr><td>$d</td><td>$f</td><td class='$s'>$s</td><td>$n</td></tr>"
  }) -join [Environment]::NewLine
  $html="<html lang='zh'><head><meta charset='utf-8'><title>Autel Vysor E2E</title><style>body{font:14px Arial;background:#101827;color:#e5efff;padding:24px}td,th{border:1px solid #435065;padding:8px;text-align:left}table{border-collapse:collapse;width:100%}.PASS{color:#70e19e}.FAIL{color:#ff8585}.SKIP,.BLOCKED_POLICY,.EXTERNAL_VALIDATION{color:#ffd27d}</style></head><body><h2>Autel Vysor real-device ADB report</h2><p>PASS=$($counts.PASS) FAIL=$($counts.FAIL) SKIP=$($counts.SKIP) BLOCKED=$($counts.BLOCKED_POLICY) EXTERNAL=$($counts.EXTERNAL_VALIDATION)</p><p>ADB backend results do NOT validate blocked Windows UI / PC keyboard / audible Bluetooth.</p><table><tr><th>Device</th><th>Feature</th><th>Status</th><th>Details</th></tr>$rows</table></body></html>"
  [IO.File]::WriteAllText($htmlPath,$html,[Text.Encoding]::UTF8)
  Write-Host "Real-device report: $htmlPath"
  Write-Host "JSON: $jsonPath"
  Write-Host "PASS=$($counts.PASS) FAIL=$($counts.FAIL) SKIP=$($counts.SKIP) BLOCKED=$($counts.BLOCKED_POLICY) EXTERNAL=$($counts.EXTERNAL_VALIDATION)"
}
if($counts.FAIL -gt 0) {exit 1}
exit 0
) {
  throw 'FixturePackage must use the dedicated com.autel.vysor.fixture package prefix. Business App packages are never modified.'
}
if(($TestFixtureInput -or $IncludeFixtureMutations) -and -not $FixturePackage) {
  throw 'Provide the already approved/installed -FixturePackage to enable app input or mutations.'
}
if($IncludeFixtureMutations -and -not $TestFixtureInput) {
  throw 'To clear data of the disposable fixture, specify BOTH -TestFixtureInput and -IncludeFixtureMutations.'
}

function A {
  param([string[]]$Arguments,[switch]$AllowFailure)
  $before=$ErrorActionPreference
  $ErrorActionPreference='Continue'
  try {
    $stdout=(& $script:AdbPath @Arguments 2>&1 | Out-String).Trim()
    $code=$LASTEXITCODE
  } finally {$ErrorActionPreference=$before}
  if($code -ne 0 -and -not $AllowFailure) {throw ("adb failed (exit {0}): {1}" -f $code,$stdout)}
  [pscustomobject]@{ Code=[int]$code; Text=[string]$stdout }
}
function D {
  param([string]$Serial,[string[]]$Arguments,[switch]$AllowFailure)
  A -Arguments (@('-s',$Serial)+$Arguments) -AllowFailure:$AllowFailure
}
function Assert {
  param([bool]$Condition,[string]$Message)
  if(-not $Condition) {throw $Message}
}
function Result {
  param([string]$Device,[string]$Feature,[string]$Status,[string]$Details)
  $script:Results.Add([pscustomobject]@{
    Device=$Device; Feature=$Feature; Status=$Status
    Details=$Details; Time=(Get-Date).ToUniversalTime().ToString('o')
  })
  Write-Host ("[{0}] {1} / {2}: {3}" -f $Status,$Device,$Feature,$Details)
}
function Test {
  param([string]$Device,[string]$Feature,[scriptblock]$Action)
  try {
    $answer=& $Action
    Result $Device $Feature 'PASS' ([string]$answer)
  } catch {
    Result $Device $Feature 'FAIL' $_.Exception.Message
  }
}
function Skip {
  param([string]$Device,[string]$Feature,[string]$Why)
  Result $Device $Feature 'SKIP' $Why
}
function Unavailable {
  param([string]$Device,[string]$Feature,[string]$Why,[string]$State='BLOCKED_POLICY')
  Result $Device $Feature $State $Why
}
function Save {
  param([string]$Folder,[string]$Name,[string]$Value)
  [IO.File]::WriteAllText((Join-Path $Folder $Name),$Value,[Text.Encoding]::UTF8)
}
function Rotation {
  param([string]$Serial)
  $raw=(D $Serial @('shell','dumpsys','input') -AllowFailure).Text
  if($raw -match 'SurfaceOrientation:\s*([0-3])') {
    return [pscustomobject]@{ Degrees=[int]$Matches[1]*90; Source='dumpsys input'; Physical=$true }
  }
  $window=(D $Serial @('shell','dumpsys','window') -AllowFailure).Text
  if($window -match '\bmRotation\s*=\s*([0-3])\b') {
    return [pscustomobject]@{ Degrees=[int]$Matches[1]*90; Source='dumpsys window'; Physical=$true }
  }
  $setting=(D $Serial @('shell','settings','get','system','user_rotation') -AllowFailure).Text
  if($setting -match '^\s*([0-3])\s*$') {
    return [pscustomobject]@{ Degrees=[int]$Matches[1]*90; Source='settings only'; Physical=$false }
  }
  throw 'This ROM does not expose a parseable rotation value.'
}
function VerifyPng {
  param([string]$File)
  $stream=[IO.File]::OpenRead($File)
  try {
    Assert ($stream.Length -gt 24) 'Screenshot is empty.'
    $b=New-Object byte[] 24
    [void]$stream.Read($b,0,24)
    $signature=@(137,80,78,71,13,10,26,10)
    for($i=0;$i -lt 8;$i++) {Assert ([int]$b[$i] -eq $signature[$i]) 'PNG signature does not match.'}
    $width=([int]$b[16] -shl 24) -bor ([int]$b[17] -shl 16) -bor ([int]$b[18] -shl 8) -bor [int]$b[19]
    $height=([int]$b[20] -shl 24) -bor ([int]$b[21] -shl 16) -bor ([int]$b[22] -shl 8) -bor [int]$b[23]
    Assert ($width -gt 100 -and $height -gt 100) 'Unexpected PNG dimensions.'
    "$width x $height ($($stream.Length) bytes)"
  } finally {$stream.Dispose()}
}
function WaitDevice {
  param([string]$Serial,[int]$Seconds)
  $end=(Get-Date).AddSeconds($Seconds)
  while((Get-Date) -lt $end) {
    $state=D $Serial @('get-state') -AllowFailure
    if($state.Code -eq 0 -and $state.Text -eq 'device') {return $true}
    Start-Sleep -Seconds 3
  }
  $false
}

Write-Host 'Autel Vysor: REAL devices, approved ADB + PowerShell ONLY.'
Write-Host "Using existing ADB: $script:AdbPath"
Write-Host "Local report folder: $OutputRoot"
$listed=(A @('devices','-l')).Text
Save $OutputRoot 'adb-devices.txt' $listed
$detected=@($listed -split "\r?\n" | ForEach-Object {
  if($_ -match '^\s*(\S+)\s+device(?:\s|$)') {$Matches[1]}
} | Where-Object {$_})
if($Serials.Count -gt 0) {
  foreach($serial in $Serials) {
    if($detected -notcontains $serial) {throw "Requested authorized device not found: $serial"}
  }
  $detected=@($Serials)
}
if($detected.Count -eq 0) {throw 'No authorized ADB devices. Confirm USB debugging and approved adb devices -l.'}

try {
foreach($serial in $detected) {
  $folder=Join-Path $OutputRoot ("device-"+($serial -replace '[^A-Za-z0-9_.-]','_'))
  New-Item -ItemType Directory -Force -Path $folder | Out-Null
  $qemu=(D $serial @('shell','getprop','ro.kernel.qemu') -AllowFailure).Text
  $qemu2=(D $serial @('shell','getprop','ro.boot.qemu') -AllowFailure).Text
  if($serial.StartsWith('emulator-',[StringComparison]::OrdinalIgnoreCase) -or $qemu -eq '1' -or $qemu2 -eq '1') {
    Result $serial 'device.physical' 'FAIL' 'Emulator detected; emulator results never count as physical testing.'
    continue
  }
  Test $serial 'device.connected' {
    Assert ((D $serial @('get-state')).Text -eq 'device') 'ADB device is not online.'
    'Physical device is online and authorized.'
  }
  try {
    $api=[int](D $serial @('shell','getprop','ro.build.version.sdk')).Text
    Assert ($api -ge 19) "Unsupported API level $api."
  } catch {
    Result $serial 'device.api' 'FAIL' $_.Exception.Message
    continue
  }

  Test $serial 'device.model-battery-memory-storage-display' {
    $model=(D $serial @('shell','getprop','ro.product.model')).Text
    $release=(D $serial @('shell','getprop','ro.build.version.release')).Text
    $abi=(D $serial @('shell','getprop','ro.product.cpu.abi')).Text
    $size=(D $serial @('shell','wm','size') -AllowFailure).Text
    $density=(D $serial @('shell','wm','density') -AllowFailure).Text
    $battery=(D $serial @('shell','dumpsys','battery')).Text
    $mem=(D $serial @('shell','cat','/proc/meminfo')).Text
    $storage=(D $serial @('shell','df','/data') -AllowFailure).Text
    Assert ($model.Length -gt 0 -and $mem -match 'MemTotal' -and $battery.Length -gt 30) 'Device metadata incomplete.'
    Save $folder 'device.txt' ((@("Model: $model","Android: $release (API $api)","ABI: $abi",$size,$density,$battery,$mem,$storage)) -join $script:NewLine)
    "$model, Android $release, API $api"
  }
  Test $serial 'shell.exec' {
    Assert ((D $serial @('shell','getprop','ro.build.version.sdk')).Text -eq "$api") 'Android shell getprop mismatch.'
    'Real shell command executed.'
  }
  Test $serial 'apps.list-system-user-search' {
    $all=(D $serial @('shell','pm','list','packages')).Text
    $system=(D $serial @('shell','pm','list','packages','-s')).Text
    $user=(D $serial @('shell','pm','list','packages','-3')).Text
    Assert ($all -match 'package:' -and $system -match 'package:') 'Package listing returned no packages.'
    "System=$(@($system -split "\r?\n" | Where-Object {$_ -match '^package:'}).Count); User=$(@($user -split "\r?\n" | Where-Object {$_ -match '^package:'}).Count)"
  }
  Test $serial 'apps.dumpsys-package' {
    $info=(D $serial @('shell','dumpsys','package','android')).Text
    Assert ($info.Length -gt 100) 'System package dump is missing.'
    'Package metadata readable.'
  }
  Test $serial 'processes.list' {
    $processes=D $serial @('shell','ps','-A') -AllowFailure
    if($processes.Code -ne 0 -or $processes.Text.Length -lt 20) {$processes=D $serial @('shell','ps')}
    Assert ($processes.Text -match '(?i)\bPID\b') 'No PID column found.'
    'Android processes readable.'
  }
  Test $serial 'network.interfaces-DNS-routes' {
    $ip=D $serial @('shell','ip','addr') -AllowFailure
    if($ip.Code -ne 0 -or $ip.Text.Length -lt 15) {$ip=D $serial @('shell','ifconfig') -AllowFailure}
    $routes=(D $serial @('shell','ip','route') -AllowFailure).Text
    $dns=(D $serial @('shell','getprop','net.dns1') -AllowFailure).Text
    Assert ($ip.Text.Length -gt 15) 'Unable to inspect an Android network interface.'
    Save $folder 'network.txt' ((@($ip.Text,$routes,"DNS hint: $dns")) -join $script:NewLine)
    'Interfaces, routes and DNS indicators collected.'
  }
  Test $serial 'network.loopback-ping' {
    $response=D $serial @('shell','ping','-c','1','-W','2','127.0.0.1') -AllowFailure
    Assert ($response.Code -eq 0 -and $response.Text -match '(?i)(1 received|1 packets received)') "Loopback ping failed: $($response.Text)"
    'Physical Android loopback ping succeeded.'
  }
  Test $serial 'files.mkdir-push-pull-sha256-delete' {
    $remote="/sdcard/Download/autel-vysor-$($script:RunId)"
    $local=Join-Path $folder 'input.txt'
    $received=Join-Path $folder 'downloaded.txt'
    [IO.File]::WriteAllText($local,("Company ADB test "+$script:RunId),[Text.Encoding]::UTF8)
    $made=$false
    try {
      [void](D $serial @('shell','mkdir','-p',$remote))
      $made=$true
      [void](D $serial @('push',$local,($remote+'/input.txt')))
      $remoteListing=(D $serial @('shell','ls','-l',($remote+'/input.txt'))).Text
      Assert ($remoteListing -match 'input.txt') 'Uploaded file not listed.'
      [void](D $serial @('pull',($remote+'/input.txt'),$received))
      Assert ((Get-FileHash $local -Algorithm SHA256).Hash -eq (Get-FileHash $received -Algorithm SHA256).Hash) 'SHA256 mismatch on roundtrip.'
      'Created temporary folder; upload / download / content SHA256 verified.'
    } finally {
      if($made) {
        $cleanup=D $serial @('shell','rm','-rf',$remote) -AllowFailure
        if($cleanup.Code -ne 0){Result $serial 'files.cleanup' 'FAIL' 'Temporary test folder removal failed.'}
      }
    }
  }
  Test $serial 'captures.screenshot-PNG' {
    $remote="/sdcard/Download/autel-vysor-$($script:RunId)-screenshot.png"
    $local=Join-Path $folder 'screenshot.png'
    try {
      [void](D $serial @('shell','screencap','-p',$remote))
      [void](D $serial @('pull',$remote,$local))
      $png=VerifyPng $local
      "Real Android screenshot: $png"
    } finally {
      $cleanup=D $serial @('shell','rm','-f',$remote) -AllowFailure
      if($cleanup.Code -ne 0){Result $serial 'captures.cleanup' 'FAIL' 'Temporary PNG removal failed.'}
    }
  }
  Test $serial 'logcat.tag-level' {
    $tag='AutelE2E'+$script:RunId
    $marker='VERIFIED_'+$script:RunId
    [void](D $serial @('shell','log','-p','i','-t',$tag,$marker))
    Start-Sleep -Milliseconds 500
    $data=(D $serial @('logcat','-d','-s',($tag+':I'),'*:S') -AllowFailure).Text
    Assert ($data -match [regex]::Escape($marker)) 'Logcat did not contain the test-generated marker.'
    'Real Info-level / Tag logcat filtering verified.'
  }
  Test $serial 'rotation.read-physical-state' {
    $r=Rotation $serial
    Assert ($r.Physical) "ROM only returns rotation setting, not physical surface state: $($r.Source)"
    "Observed $($r.Degrees) degrees from $($r.Source)."
  }
  if($IncludeRotation) {
    Test $serial 'rotation.actual-lock-and-restore' {
      $oldSensor=(D $serial @('shell','settings','get','system','accelerometer_rotation') -AllowFailure).Text
      $oldUser=(D $serial @('shell','settings','get','system','user_rotation') -AllowFailure).Text
      $oldWm=(D $serial @('shell','wm','user-rotation') -AllowFailure).Text
      $before=Rotation $serial
      Assert ($before.Physical) 'Cannot verify real rotation on this ROM.'
      $target=if($before.Degrees -in @(90,270)){'0'}else{'1'}
      try {
        [void](D $serial @('shell','settings','put','system','accelerometer_rotation','0'))
        $command=D $serial @('shell','wm','user-rotation','lock',$target) -AllowFailure
        if($command.Code -ne 0 -or $command.Text -match '(?i)error|unknown|unsupported') {
          [void](D $serial @('shell','settings','put','system','user_rotation',$target))
        }
        Start-Sleep -Seconds 2
        $after=Rotation $serial
        Assert ($after.Physical -and $after.Degrees -eq ([int]$target*90)) "Screen orientation did not change. Target=$target Observed=$($after.Degrees)."
        "Real Android orientation changed $($before.Degrees) -> $($after.Degrees)."
      } finally {
        if($oldWm -match 'lock\s+([0-3])') {
          $saved=$Matches[1]
          [void](D $serial @('shell','wm','user-rotation','lock',$saved) -AllowFailure)
        } else {
          [void](D $serial @('shell','wm','user-rotation','free') -AllowFailure)
        }
        if($oldUser -match '^[0-3]$') {[void](D $serial @('shell','settings','put','system','user_rotation',$oldUser) -AllowFailure)}
        if($oldSensor -match '^[01]$') {
          [void](D $serial @('shell','settings','put','system','accelerometer_rotation',$oldSensor) -AllowFailure)
          Assert ((D $serial @('shell','settings','get','system','accelerometer_rotation') -AllowFailure).Text -eq $oldSensor) 'Could not restore sensor rotation mode.'
        }
      }
    }
  } else {Skip $serial 'rotation.actual-lock-and-restore' 'Opt-in -IncludeRotation changes real device orientation temporarily.'}
  Test $serial 'audio.route-metadata' {
    $a=D $serial @('shell','dumpsys','audio') -AllowFailure
    Assert ($a.Code -eq 0 -and $a.Text.Length -gt 40) 'Audio dumpsys unavailable.'
    Save $folder 'audio-route.txt' $a.Text
    'Audio metadata captured (NOT proof of audible Bluetooth sound).'
  }
  if($IncludeRecording) {
    Test $serial 'captures.Android-system-recording' {
      $remote="/sdcard/Download/autel-vysor-$($script:RunId).mp4"
      $local=Join-Path $folder 'recording.mp4'
      try {
        $video=D $serial @('shell','screenrecord','--time-limit','3',$remote) -AllowFailure
        Assert ($video.Code -eq 0) "Android screenrecord failed: $($video.Text)"
        [void](D $serial @('pull',$remote,$local))
        $bytes=[IO.File]::ReadAllBytes($local)
        Assert ($bytes.Length -gt 8192) 'MP4 recording is too small.'
        Assert ([Text.Encoding]::ASCII.GetString($bytes,4,4) -eq 'ftyp') 'MP4 ftyp header missing.'
        'Real Android screenrecord saved a valid MP4 container.'
      } finally {
        [void](D $serial @('shell','rm','-f',$remote) -AllowFailure)
      }
    }
  } else {Skip $serial 'captures.Android-system-recording' 'Opt-in -IncludeRecording records the real Android screen using the existing ADB shell.'}

  if($FixturePackage) {
    $installed=D $serial @('shell','pm','path',$FixturePackage) -AllowFailure
    if($installed.Code -ne 0 -or $installed.Text -notmatch 'package:') {
      Skip $serial 'fixture.input-and-permissions' 'Approved test APK is not already installed; no APK is downloaded.'
    } else {
      Test $serial 'fixture.inspect' {
        $data=(D $serial @('shell','dumpsys','package',$FixturePackage)).Text
        Assert ($data -match '(?i)versionName|versionCode') 'Fixture package dump not available.'
        'Installed approved test fixture inspected.'
      }
      if($TestFixtureInput) {
        Test $serial 'fixture.ADB-text-entry-to-real-EditText' {
          [void](D $serial @('shell','am','start','-n',($FixturePackage+'/.MainActivity')))
          Start-Sleep -Seconds 1
          $marker='AUTEL'+$script:RunId
          [void](D $serial @('shell','input','text',$marker))
          Start-Sleep -Milliseconds 700
          $input=D $serial @('shell','run-as',$FixturePackage,'cat','files/input.txt') -AllowFailure
          Assert ($input.Code -eq 0 -and $input.Text.Contains($marker)) 'Approved fixture input.txt did not contain actual Android EditText text.'
          'Real Android EditText received ADB text through installed test fixture.'
        }
      } else {Skip $serial 'fixture.ADB-text-entry-to-real-EditText' 'Opt-in -TestFixtureInput on an existing company-approved fixture.'}
      if($IncludeFixtureMutations) {
        Test $serial 'fixture.stop-clear-restart' {
          [void](D $serial @('shell','am','force-stop',$FixturePackage))
          $cleared=(D $serial @('shell','pm','clear',$FixturePackage)).Text
          Assert ($cleared -match '(?i)success') 'Fixture data clearing failed.'
          [void](D $serial @('shell','am','start','-n',($FixturePackage+'/.MainActivity')))
          'Only explicitly approved disposable fixture data cleared; fixture restarted.'
        }
      } else {Skip $serial 'fixture.stop-clear-restart' 'Requires explicit -IncludeFixtureMutations; clears test-app data.'}
    }
  } else {Skip $serial 'fixture.input-permission-and-app-management' 'Company-approved test fixture not supplied. Business apps will not be modified.'}

  if($IncludeBugreport) {
    Test $serial 'diagnostics.bugreport' {
      $file=Join-Path $folder 'bugreport.zip'
      $result=D $serial @('bugreport',$file) -AllowFailure
      Assert ($result.Code -eq 0 -and (Test-Path $file) -and (Get-Item $file).Length -gt 1024) 'Bugreport ZIP unavailable.'
      'Bugreport saved locally (may contain confidential device data).'
    }
  } else {Skip $serial 'diagnostics.bugreport' 'Enable -IncludeBugreport only on authorized lab hardware.'}
  if($IncludeReboot) {
    Test $serial 'device.reboot-return-online' {
      [void](D $serial @('reboot'))
      Start-Sleep -Seconds 12
      Assert (WaitDevice $serial 150) 'Device did not reconnect within 150 seconds.'
      'Physical Android rebooted and returned to ADB.'
    }
  } else {Skip $serial 'device.reboot-return-online' 'Enable -IncludeReboot only on dedicated lab hardware.'}

  $reason='Autel Vysor / scrcpy executable is not company-approved. ADB success does not verify PC Windows UI.'
  foreach($feature in @(
    'mirror.WPF-stream-Fit-1to1-fullscreen-sidebar',
    'mirror.PC-keyboard-focus-Chinese-clipboard',
    'mirror.mouse-touch-controls',
    'mirror.real-rotation-buttons',
    'mirror.screen-off-and-live-image',
    'mirror.disconnection-autorecovery',
    'mirror.PC-audio-duplication',
    'files.WPF-dragdrop',
    'shell.WPF-terminal-and-history',
    'logcat.WPF-tag-PID-level-pause',
    'apps.WPF-install-uninstall-permissions',
    'network.WPF-controls',
    'capture.WPF-video-playback',
    'batch.WPF-multiple-devices',
    'GitHub.WPF-update-check'
  )) {Unavailable $serial $feature $reason}
  Unavailable $serial 'Bluetooth.real-headset-sound' 'Requires audible recording hardware or human listening, not dumpsys.' 'EXTERNAL_VALIDATION'
  Unavailable $serial 'sensor.physical-tilt' 'Requires physical device tilt / actuator and video observation.' 'EXTERNAL_VALIDATION'
  Unavailable $serial 'USB.physical-disconnect' 'Requires an approved USB relay or human-controlled cable disconnect.' 'EXTERNAL_VALIDATION'
}
if($IncludeAdbRestart) {
  Test 'HOST' 'ADB.kill-and-start-server' {
    [void](A @('kill-server'))
    [void](A @('start-server'))
    Assert ((A @('devices')).Text -match 'List of devices') 'Host ADB could not restart.'
    'Existing company ADB server restarted.'
  }
} else {Skip 'HOST' 'ADB.kill-and-start-server' 'Only with -IncludeAdbRestart: disrupts every active ADB session on PC.'}
} finally {
  $counts=@{}
  foreach($status in @('PASS','FAIL','SKIP','BLOCKED_POLICY','EXTERNAL_VALIDATION')) {
    $counts[$status]=@($script:Results | Where-Object {$_.Status -eq $status}).Count
  }
  $summary=[pscustomobject]@{
    Suite='Autel Vysor - company ADB/PowerShell only'
    ExecutionType='REAL_PHYSICAL_ADB_NO_EXTERNAL_EXE'
    StartedUtc=$script:StartTime
    CompletedUtc=(Get-Date).ToUniversalTime().ToString('o')
    Devices=@($detected)
    Counts=$counts
    Results=@($script:Results.ToArray())
    Warning='ADB backend PASS does NOT mean Windows UI, Bluetooth sound or PC keyboard is validated.'
  }
  $jsonPath=Join-Path $OutputRoot 'summary.json'
  $csvPath=Join-Path $OutputRoot 'results.csv'
  $htmlPath=Join-Path $OutputRoot 'report.html'
  $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $jsonPath
  $script:Results | Export-Csv -Encoding UTF8 -NoTypeInformation -Path $csvPath
  $rows=@($script:Results | ForEach-Object {
    $d=[Net.WebUtility]::HtmlEncode($_.Device)
    $f=[Net.WebUtility]::HtmlEncode($_.Feature)
    $s=[Net.WebUtility]::HtmlEncode($_.Status)
    $n=[Net.WebUtility]::HtmlEncode($_.Details)
    "<tr><td>$d</td><td>$f</td><td class='$s'>$s</td><td>$n</td></tr>"
  }) -join [Environment]::NewLine
  $html="<html lang='zh'><head><meta charset='utf-8'><title>Autel Vysor E2E</title><style>body{font:14px Arial;background:#101827;color:#e5efff;padding:24px}td,th{border:1px solid #435065;padding:8px;text-align:left}table{border-collapse:collapse;width:100%}.PASS{color:#70e19e}.FAIL{color:#ff8585}.SKIP,.BLOCKED_POLICY,.EXTERNAL_VALIDATION{color:#ffd27d}</style></head><body><h2>Autel Vysor real-device ADB report</h2><p>PASS=$($counts.PASS) FAIL=$($counts.FAIL) SKIP=$($counts.SKIP) BLOCKED=$($counts.BLOCKED_POLICY) EXTERNAL=$($counts.EXTERNAL_VALIDATION)</p><p>ADB backend results do NOT validate blocked Windows UI / PC keyboard / audible Bluetooth.</p><table><tr><th>Device</th><th>Feature</th><th>Status</th><th>Details</th></tr>$rows</table></body></html>"
  [IO.File]::WriteAllText($htmlPath,$html,[Text.Encoding]::UTF8)
  Write-Host "Real-device report: $htmlPath"
  Write-Host "JSON: $jsonPath"
  Write-Host "PASS=$($counts.PASS) FAIL=$($counts.FAIL) SKIP=$($counts.SKIP) BLOCKED=$($counts.BLOCKED_POLICY) EXTERNAL=$($counts.EXTERNAL_VALIDATION)"
}
if($counts.FAIL -gt 0) {exit 1}
exit 0
