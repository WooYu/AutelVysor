# CI-ONLY FAKE ADB. NO PHYSICAL ANDROID DEVICES.
# The company E2E suite must not be marked physically validated by this file.
$ErrorActionPreference='Stop'
$p=@($args)
if(-not $env:AUTEL_FAKE_ADB_STATE) {throw 'CI fixture directory not set.'}
$state=$env:AUTEL_FAKE_ADB_STATE
New-Item -ItemType Directory -Force -Path $state | Out-Null

if($p.Count -ge 2 -and $p[0] -eq '-s') {
  if($p[1] -ne 'CI-MOCK-DEVICE') {throw 'Unexpected fake serial.'}
  $p=@($p | Select-Object -Skip 2)
}
$cmd=$p -join ' '
Add-Content -Path (Join-Path $state 'adb-calls.txt') -Value $cmd
if($cmd -eq 'devices -l') {
  Write-Output "List of devices attached"
  Write-Output "CI-MOCK-DEVICE    device product:autel_ci model:mock_device"
}
elseif($cmd -eq 'get-state') {Write-Output 'device'}
elseif($cmd -like 'shell getprop*') {
  if($cmd -like '*ro.kernel.qemu*' -or $cmd -like '*ro.boot.qemu*') {Write-Output '0'}
  elseif($cmd -like '*ro.build.version.sdk*') {Write-Output '36'}
  elseif($cmd -like '*ro.build.version.release*') {Write-Output '16'}
  elseif($cmd -like '*ro.product.model*') {Write-Output 'CI-MOCK-ANDROID'}
  elseif($cmd -like '*ro.product.cpu.abi*') {Write-Output 'arm64-v8a'}
  elseif($cmd -like '*net.dns1*') {Write-Output '192.0.2.53'}
  else {Write-Output 'mock'}
}
elseif($cmd -eq 'shell wm size') {Write-Output 'Physical size: 1080x2400'}
elseif($cmd -eq 'shell wm density') {Write-Output 'Physical density: 420'}
elseif($cmd -eq 'shell dumpsys battery') {Write-Output 'Current Battery Service state: level: 90; status: 2; temperature: 300; health: 2; voltage: 4300'}
elseif($cmd -eq 'shell cat /proc/meminfo') {Write-Output 'MemTotal: 4096000 kB'}
elseif($cmd -eq 'shell df /data') {Write-Output '/data 12000000 3000000 9000000'}
elseif($cmd -like 'shell pm list packages*') {Write-Output 'package:android';Write-Output 'package:com.android.settings'}
elseif($cmd -eq 'shell dumpsys package android') {Write-Output ('Package [android] versionName=16 versionCode=36 requested permissions dataDir system flags '.PadRight(200,'x'))}
elseif($cmd -like 'shell ps*') {Write-Output 'USER PID PPID VSIZE RSS WCHAN PC NAME';Write-Output 'root 1 0 1000 1000 0 0 init'}
elseif($cmd -eq 'shell ip addr') {Write-Output '1: lo: LOOPBACK UP inet 127.0.0.1/8 scope host'}
elseif($cmd -eq 'shell ip route') {Write-Output 'default via 192.0.2.1 dev wlan0'}
elseif($cmd -eq 'shell ss -tuna' -or $cmd -eq 'shell netstat -an') {Write-Output 'Netid State Recv-Q Send-Q Local Address:Port Peer Address:Port';Write-Output 'tcp ESTAB 0 0 127.0.0.1:5555 127.0.0.1:1234'}
elseif($cmd -like 'shell ping*') {Write-Output '1 packets transmitted, 1 received, 0% packet loss'}
elseif($cmd -like 'shell mkdir*') {Write-Output ''}
elseif($cmd -like 'push *') {
  Copy-Item -LiteralPath $p[1] -Destination (Join-Path $state 'transfer') -Force
  Write-Output '1 file pushed'
}
elseif($cmd -like 'pull *') {
  $dest=$p[2]
  if($p[1] -like '*.png') {
    Add-Type -AssemblyName System.Drawing
    $bmp=New-Object System.Drawing.Bitmap(320,640)
    try {$bmp.Save($dest,[System.Drawing.Imaging.ImageFormat]::Png)}
    finally {$bmp.Dispose()}
  }
  else {Copy-Item -LiteralPath (Join-Path $state 'transfer') -Destination $dest -Force}
  Write-Output '1 file pulled'
}
elseif($cmd -like 'shell ls -l *') {Write-Output '-rw-rw---- 1 shell shell 12 2026-10-21 input.txt'}
elseif($cmd -like 'shell screencap*') {Write-Output ''}
elseif($cmd -like 'shell rm *') {
  Add-Content -Path (Join-Path $state 'cleanups.txt') -Value $cmd
}
elseif($cmd -like 'shell log -p*') {
  $marker=$p[-1]
  [IO.File]::WriteAllText((Join-Path $state 'logmarker.txt'),$marker)
}
elseif($cmd -like 'logcat -d*') {
  $marker=[IO.File]::ReadAllText((Join-Path $state 'logmarker.txt'))
  Write-Output ("10-21 10:00:00.000 100 100 I AutelE2E: "+$marker)
}
elseif($cmd -eq 'shell dumpsys input') {Write-Output 'SurfaceOrientation: 0'}
elseif($cmd -eq 'shell dumpsys audio') {Write-Output ('Audio service: bluetooth output device speaker route available .'.PadRight(120,'x'))}
elseif($cmd -like 'shell settings get system user_rotation') {Write-Output '0'}
elseif($cmd -eq 'kill-server' -or $cmd -eq 'start-server' -or $cmd -eq 'devices') {Write-Output 'List of devices attached'}
else {Write-Output 'ok'}
exit 0
