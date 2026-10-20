param(
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $root 'src/AutelVysor/tools'
}

Write-Host 'Resolving latest official scrcpy Windows x64 release...'
$headers = @{ 'User-Agent' = 'AutelVysor-Build' }
$release = Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/repos/Genymobile/scrcpy/releases/latest'
$asset = $release.assets | Where-Object { $_.name -match '^scrcpy-win64-v.*\.zip$' } | Select-Object -First 1
if (-not $asset) {
    throw 'Could not find the official scrcpy win64 ZIP asset in the latest GitHub release.'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('autel-vysor-tools-' + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $tempRoot $asset.name
$extract = Join-Path $tempRoot 'extract'
New-Item -ItemType Directory -Force -Path $tempRoot, $extract | Out-Null

try {
    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile $zip

    if ($asset.digest -and $asset.digest -match '^sha256:(.+)$') {
        $expected = $Matches[1].ToLowerInvariant()
        $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "scrcpy archive SHA-256 verification failed. Expected $expected, got $actual."
        }
        Write-Host 'scrcpy archive SHA-256 verified.'
    }

    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $scrcpy = Get-ChildItem -Path $extract -Filter 'scrcpy.exe' -Recurse | Select-Object -First 1
    if (-not $scrcpy) { throw 'scrcpy.exe was not found in the downloaded archive.' }
    $sourceDir = $scrcpy.Directory.FullName

    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $Destination -Recurse -Force

    foreach ($required in @('scrcpy.exe', 'adb.exe')) {
        if (-not (Test-Path (Join-Path $Destination $required))) {
            throw "Required tool '$required' is missing after extraction."
        }
    }

    # Android 4.4 / API 19 optional accelerated capture backend.
    # Pin DeviceFarmer/minicap to a specific commit so release builds are reproducible.
    $minicapCommit = 'f3d40d65da0cc7168846c4ae7466ada970441d4e'
    $minicapRoot = Join-Path $Destination 'legacy\minicap\api19'
    $legacySpecs = @(
        @{
            Abi = 'armeabi-v7a'
            Binary = 'libs/armeabi-v7a/minicap'
            Shared = 'jni/minicap-shared/aosp/libs/android-19/armeabi-v7a/minicap.so'
        },
        @{
            Abi = 'x86'
            Binary = 'libs/x86/minicap'
            Shared = 'jni/minicap-shared/aosp/libs/android-19/x86/minicap.so'
        }
    )

    foreach ($spec in $legacySpecs) {
        $abiDir = Join-Path $minicapRoot $spec.Abi
        New-Item -ItemType Directory -Force -Path $abiDir | Out-Null
        $base = "https://raw.githubusercontent.com/DeviceFarmer/minicap/$minicapCommit"
        Write-Host "Downloading minicap API 19 assets for $($spec.Abi)..."
        Invoke-WebRequest -Headers $headers -Uri "$base/$($spec.Binary)" -OutFile (Join-Path $abiDir 'minicap')
        Invoke-WebRequest -Headers $headers -Uri "$base/$($spec.Shared)" -OutFile (Join-Path $abiDir 'minicap.so')
    }

    Set-Content -Path (Join-Path $Destination 'LEGACY_TOOLCHAIN_VERSION.txt') -Encoding UTF8 -Value @(
        "minicap_commit=$minicapCommit",
        "source=https://github.com/DeviceFarmer/minicap",
        "api=19",
        "abis=armeabi-v7a,x86",
        "license=Apache-2.0"
    )

    Set-Content -Path (Join-Path $Destination 'AUTEL_TOOLCHAIN_VERSION.txt') -Encoding UTF8 -Value @(
        "scrcpy=$($release.tag_name)",
        "source=https://github.com/Genymobile/scrcpy/releases/latest",
        "prepared=$(Get-Date -Format o)"
    )

    Write-Host "Tools prepared in: $Destination"
    Write-Host "scrcpy release: $($release.tag_name)"
}
finally {
    if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot }
}
