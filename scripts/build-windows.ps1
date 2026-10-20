param(
    [switch]$SkipToolDownload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/AutelVysor/AutelVysor.csproj'
$tools = Join-Path $root 'src/AutelVysor/tools'
$out = Join-Path $root 'dist/AutelVysor'

if (-not $SkipToolDownload) {
    & (Join-Path $PSScriptRoot 'prepare-tools.ps1') -Destination $tools
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host 'Building Autel Vysor for Windows x64...'
dotnet restore $project
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o $out

$exe = Join-Path $out 'AutelVysor.exe'
if (-not (Test-Path $exe)) {
    throw "Build completed without expected executable: $exe"
}

$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($info.FileDescription -ne 'Autel') {
    throw "Unexpected FileDescription '$($info.FileDescription)'. Expected 'Autel'."
}
if ($info.ProductName -ne 'Autel Vysor') {
    throw "Unexpected ProductName '$($info.ProductName)'. Expected 'Autel Vysor'."
}

foreach ($required in @('tools/adb.exe', 'tools/scrcpy.exe')) {
    $path = Join-Path $out $required
    if (-not (Test-Path $path)) {
        throw "Release is missing $required. Run without -SkipToolDownload or prepare src/AutelVysor/tools first."
    }
}

Write-Host 'Running packaged executable self-test...'
$selfTest = Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru
if ($selfTest.ExitCode -ne 0) {
    throw "AutelVysor.exe self-test failed with exit code $($selfTest.ExitCode)."
}
$selfTestReport = Join-Path $out 'AutelVysor-self-test.txt'
if (-not (Test-Path $selfTestReport)) {
    throw 'AutelVysor.exe self-test exited successfully but did not produce its validation report.'
}
Write-Host (Get-Content -Raw $selfTestReport)
Remove-Item -Force $selfTestReport

Write-Host "Build OK: $exe"
Write-Host "FileDescription: $($info.FileDescription)"
Write-Host "ProductName: $($info.ProductName)"
Write-Host "FileVersion: $($info.FileVersion)"
