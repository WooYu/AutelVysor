param(
    [Parameter(Mandatory=$true)]
    [string]$FilePath
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $FilePath)) { throw "Signing target missing: $FilePath" }

if ([string]::IsNullOrWhiteSpace($env:AUTEL_SIGN_PFX_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:AUTEL_SIGN_PFX_PASSWORD)) {
    Write-Host "Unsigned internal test build (trusted certificate not configured): $FilePath"
    exit 0
}

$tool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $tool) {
    $sdkBin = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
    $candidates = @(Get-ChildItem -Path $sdkBin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*\x64' } |
        Sort-Object FullName -Descending)
    if ($candidates.Count -gt 0) { $tool = $candidates[0].FullName }
}
if (-not $tool) { throw "signtool.exe not found. Install Windows SDK or use a Windows SDK runner." }

$temp = Join-Path $env:TEMP ("autel-codesign-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [System.IO.File]::WriteAllBytes($temp, [Convert]::FromBase64String($env:AUTEL_SIGN_PFX_BASE64))
    & $tool sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /f $temp /p $env:AUTEL_SIGN_PFX_PASSWORD $FilePath
    if ($LASTEXITCODE -ne 0) { throw "signtool signing failed." }
    & $tool verify /pa /v $FilePath
    if ($LASTEXITCODE -ne 0) { throw "signtool verification failed." }
    Write-Host "Trusted Authenticode signature verified: $FilePath"
}
finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
