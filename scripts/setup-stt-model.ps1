# Download and verify Vosk small Russian model
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'ALXPRGS\Notify\models\vosk-model-small-ru-0.22')
)
$ErrorActionPreference = 'Stop'
$zipSha256 = '961D5FF98A17F4AA6DE69864D0AA71FA5BAC682301D2B5D17A3F24C5C99A46D4'
$url = 'https://github.com/kercre123/vosk-models/raw/main/vosk-model-small-ru-0.22.zip'
$root = Split-Path -Parent $Destination
$archive = Join-Path ([IO.Path]::GetTempPath()) ('vosk-small-ru-' + [guid]::NewGuid().ToString('N') + '.zip')
$stage = Join-Path ([IO.Path]::GetTempPath()) ('vosk-small-ru-' + [guid]::NewGuid().ToString('N'))
$manifest = Join-Path $PSScriptRoot '..\deploy\stt\model.sha256'
try {
    New-Item -ItemType Directory -Force $root | Out-Null
    if (Test-Path $Destination) {
        Write-Host "Vosk model already exists: $Destination"
        exit 0
    }
    Write-Host 'Downloading Vosk small Russian model (44 MB, Apache-2.0)…'
    curl.exe -L --fail --retry 3 --max-time 300 -o $archive $url
    if ($LASTEXITCODE -ne 0) { throw 'Vosk model download failed' }
    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($actual -ne $zipSha256) { throw 'Vosk model archive checksum mismatch' }
    Expand-Archive -Path $archive -DestinationPath $stage -Force
    $source = Join-Path $stage 'vosk-model-small-ru-0.22'
    if (-not (Test-Path $source)) { throw 'Unexpected Vosk archive layout' }
    foreach ($line in Get-Content $manifest) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid model manifest' }
        $expected = $Matches[1]
        $relative = $Matches[2]
        $file = Join-Path $source ($relative -replace '/', '\')
        if (-not (Test-Path $file)) { throw "Missing model file: $relative" }
        if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Model checksum mismatch: $relative" }
    }
    Move-Item -Path $source -Destination $Destination
    Write-Host "Vosk model installed: $Destination"
} finally {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
