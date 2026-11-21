param([switch]$ReuseFirmware, [switch]$Installer)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Split-Path -Parent $PSScriptRoot
$firmwareProject = Join-Path $project 'CameraCue'
$firmwareBuild = Join-Path $firmwareProject '.pio/pilot-build'
$bundle = Join-Path $PSScriptRoot 'obj/bundle'
$firmwarePackage = Join-Path $PSScriptRoot 'obj/firmware-package'
$tools = Join-Path $PSScriptRoot 'obj/esptool'
$version = '0.11.0'
$output = Join-Path $project "dist/$version"
$zip = Join-Path $tools 'esptool-v5.1.0-windows-amd64.zip'
$platformHome = if ($env:PLATFORMIO_CORE_DIR) { $env:PLATFORMIO_CORE_DIR } else { Join-Path $env:USERPROFILE '.platformio' }

New-Item -ItemType Directory -Force -Path $bundle,$tools,$output,$firmwarePackage | Out-Null
if (-not $ReuseFirmware) {
    $env:PLATFORMIO_BUILD_DIR = $firmwareBuild
    $pio = Get-Command pio -ErrorAction SilentlyContinue
    $command = if ($pio) { $pio.Source } else { Join-Path $platformHome 'penv/Scripts/platformio.exe' }
    & $command run --project-dir $firmwareProject -e tallypager-pilot -e tallypager-esp8266-base -e tallypager-hotspot
    if ($LASTEXITCODE -ne 0) { throw 'Firmware-Build fehlgeschlagen.' }
}

if (-not (Test-Path -LiteralPath $zip)) {
    $url = 'https://github.com/espressif/esptool/releases/download/v5.1.0/esptool-v5.1.0-windows-amd64.zip'
    Invoke-WebRequest -Uri $url -OutFile $zip
}
$expected = 'f68a8f7728adfc59cd60f9424928199e76eac66372c7bdc23898aa32753a437a'
if ((Get-FileHash -LiteralPath $zip).Hash -ne $expected) { throw 'Prüfsumme des esptool-Archivs stimmt nicht.' }
Expand-Archive -LiteralPath $zip -DestinationPath $tools -Force
$esptool = Join-Path $tools 'esptool-windows-amd64'

$images = @('bootloader.bin', 'partitions.bin', 'firmware.bin', 'boot_app0.bin')
Copy-Item -LiteralPath (Join-Path $platformHome 'packages/framework-arduinoespressif32/tools/partitions/boot_app0.bin') -Destination $firmwarePackage
Copy-Item -LiteralPath (Join-Path $esptool 'esptool.exe') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $esptool 'LICENSE') -Destination (Join-Path $output 'esptool-LICENSE.txt')
@'
LibAtem 1.0.0 — LGPL-3.0-or-later — https://github.com/LibAtem/LibAtem
log4net 3.6.0 — Apache-2.0 — https://logging.apache.org/log4net/
esptool 5.1.0 — see esptool-LICENSE.txt — https://github.com/espressif/esptool/tree/v5.1.0
'@ | Set-Content -LiteralPath (Join-Path $output 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
$firmwareZip = Join-Path $output "TallyPager-Firmware-M5StickC-Plus2-$version.zip"
$hotspotZip = Join-Path $output "TallyPager-Firmware-WLAN-Bruecke-M5StickC-Plus2-$version.zip"
$manifest = Join-Path $firmwarePackage 'manifest.json'

function Write-M5FirmwarePackage($environment, $board, $destination) {
    $build = Join-Path $firmwareBuild $environment
    foreach ($name in $images[0..2]) {
        Copy-Item -LiteralPath (Join-Path $build $name) -Destination $firmwarePackage
    }
    $sha256 = [ordered]@{}
    foreach ($name in $images) {
        $sha256[$name] = (Get-FileHash -LiteralPath (Join-Path $firmwarePackage $name)).Hash.ToLowerInvariant()
    }
    [ordered]@{board = $board; version = $version; sha256 = $sha256} |
        ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
    $files = @($images | ForEach-Object { Join-Path $firmwarePackage $_ }) + $manifest
    Compress-Archive -LiteralPath $files -DestinationPath $destination -Force
}
Write-M5FirmwarePackage 'tallypager-pilot' 'm5stick-cplus2' $firmwareZip
Write-M5FirmwarePackage 'tallypager-hotspot' 'm5stick-cplus2-hotspot' $hotspotZip

$esp8266Image = Join-Path $firmwareBuild 'tallypager-esp8266-base/firmware.bin'
$esp8266Zip = Join-Path $output "TallyPager-Firmware-ESP8266-$version.zip"
[ordered]@{
    board = 'esp8266'
    version = $version
    sha256 = [ordered]@{ 'firmware.bin' = (Get-FileHash -LiteralPath $esp8266Image).Hash.ToLowerInvariant() }
} | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
Compress-Archive -LiteralPath $esp8266Image,$manifest -DestinationPath $esp8266Zip -Force

$publish = Join-Path $PSScriptRoot 'obj/portable'
$publishArgs = @(
    (Join-Path $PSScriptRoot 'TallyPager.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', $publish,
    '-p:SelfContained=true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:TargetLatestRuntimePatch=true'
)
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Windows-Build fehlgeschlagen.' }

$exe = Join-Path $output 'TallyPager-Windows-x64.exe'
Copy-Item -LiteralPath (Join-Path $publish 'TallyPager.exe') -Destination $exe
$releaseFiles = @($exe, $firmwareZip, $esp8266Zip, $hotspotZip)
if ($Installer) {
    $compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $compiler) {
        $compiler = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) |
            ForEach-Object { Join-Path $_ 'Inno Setup 6/ISCC.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $compiler) { throw 'Inno Setup 6 (ISCC.exe) fehlt.' }
    & $compiler "/DAppVersion=$version" "/DArtifacts=$output" (Join-Path $PSScriptRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer-Build fehlgeschlagen.' }
    $releaseFiles += Join-Path $output 'TallyPager-Setup-Windows-x64.exe'
}
foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $file
    Set-Content -LiteralPath ($file + '.sha256') -Value "$hash  $name" -Encoding ascii
}
Get-Item -LiteralPath $releaseFiles | Select-Object FullName,Length
