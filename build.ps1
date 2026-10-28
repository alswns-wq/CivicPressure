param(
    [string]$GamePath,
    [string]$HarmonyPath,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Dev
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $GamePath) {
    $steam = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if (-not $steam) { throw 'Steam not found. Supply -GamePath pointing to the CS1 installation.' }
    $libraries = @($steam.SteamPath)
    $libraryFile = Join-Path $steam.SteamPath 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $libraryFile) {
        $content = Get-Content -LiteralPath $libraryFile -Raw
        foreach ($match in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
            $libraries += $match.Groups[1].Value.Replace('\\', '\')
        }
    }
    foreach ($library in ($libraries | Select-Object -Unique)) {
        $manifest = Join-Path $library 'steamapps\appmanifest_255710.acf'
        if (-not (Test-Path -LiteralPath $manifest)) { continue }
        $match = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"')
        if ($match.Success) {
            $candidate = Join-Path $library ('steamapps\common\' + $match.Groups[1].Value)
            if (Test-Path -LiteralPath (Join-Path $candidate 'Cities_Data\Managed\ICities.dll')) {
                $GamePath = $candidate
                break
            }
        }
    }
}
if (-not $GamePath) { throw 'Cities: Skylines 1 (Steam app 255710) not found. Supply -GamePath.' }
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
$managed = Join-Path $GamePath 'Cities_Data\Managed'
foreach ($file in @('Cities.exe','Cities_Data\Managed\ICities.dll','Cities_Data\Managed\UnityEngine.dll','Cities_Data\Managed\Assembly-CSharp.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $GamePath $file))) { throw "Missing CS1 file: $file" }
}
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v3.5\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw '.NET Framework 3.5 MSBuild is required. A modern dotnet SDK alone is insufficient for this build script.'
}
Write-Output "CS1 references: $managed"
if (-not $HarmonyPath) {
    $HarmonyPath = Join-Path (Split-Path (Split-Path $GamePath -Parent) -Parent) 'workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll'
}
if (-not (Test-Path -LiteralPath $HarmonyPath)) { throw 'CitiesHarmony.Harmony.dll is required to build phase 3. Supply -HarmonyPath.' }
# -Dev builds the local developer copy (CivicPressureDev.dll) into dist\CivicPressureDev; the Workshop build is untouched.
if ($Dev) { $edition = 'Dev'; $assembly = 'CivicPressureDev'; $binary = "src\bin\Dev\$Configuration" }
else { $edition = 'Workshop'; $assembly = 'CivicPressure'; $binary = "src\bin\$Configuration" }
& $msbuild (Join-Path $PSScriptRoot 'src\CivicPressure.csproj') /nologo /t:Rebuild "/p:Configuration=$Configuration" "/p:Edition=$edition" "/p:GameManagedPath=$managed" "/p:HarmonyPath=$HarmonyPath" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE. Package not updated." }
$package = Join-Path $PSScriptRoot "dist\$assembly"
New-Item -ItemType Directory -Path $package -Force | Out-Null
$source = Join-Path $PSScriptRoot "$binary\$assembly.dll"
Copy-Item -LiteralPath $source -Destination (Join-Path $package "$assembly.dll") -Force
Write-Output "Built ($edition): $package\$assembly.dll"
