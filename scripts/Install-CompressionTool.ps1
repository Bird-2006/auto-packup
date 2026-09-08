$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root 'artifacts\compression'
$destination = Join-Path $root 'tools\7zip'
New-Item -ItemType Directory -Path $cache,$destination -Force | Out-Null
$assets = @{
    '7zr.exe' = 'AD4C82FADCBDF93C03B4FC440F300509C7D60C5C2F4D183E35D9D70D6957037D'
    '7z2603-extra.7z' = '191894E6ACB3647FFB69CE630479FF318523B2E2B9890AA7F05C1127C2E59B8F'
}
foreach ($name in $assets.Keys) {
    $file = Join-Path $cache $name
    if (!(Test-Path -LiteralPath $file)) {
        Invoke-WebRequest -UseBasicParsing "https://github.com/ip7z/7zip/releases/download/26.03/$name" -OutFile $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $assets[$name]) { throw "Checksum mismatch: $name" }
}
& (Join-Path $cache '7zr.exe') x (Join-Path $cache '7z2603-extra.7z') "-o$cache\extra" -y
if ($LASTEXITCODE -ne 0) { throw 'Could not extract 7-Zip.' }
Copy-Item -LiteralPath (Join-Path $cache 'extra\x64\7za.exe') -Destination $destination
Copy-Item -LiteralPath (Join-Path $cache 'extra\License.txt') -Destination $destination
Write-Output "7-Zip 26.03 ready: $destination"
