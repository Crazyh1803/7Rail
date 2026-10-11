# Dev loop: build the converter and the mod, convert the local EverRail into the installed mod, copy the mod in.
# usage: powershell -File tools/deploy.ps1 [-GameDir <7 Days To Die folder>] [-NoConvert]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die",
    [switch]$NoConvert
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet-sdk8"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
py -I "$root\tools\gen_xml.py" "$root\mod\Config"
foreach ($proj in "$root\converter\SevenRailConvert", "$root\mod\SevenRail.csproj") {
    $out = dotnet build $proj -c Release -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Select-String "error CS" | Select-Object -First 10; throw "build failed: $proj" }
}
$mod = Join-Path $GameDir "Mods\7Rail"
New-Item -ItemType Directory -Force "$mod\Config" | Out-Null
Copy-Item "$root\mod\bin\Release\7Rail.dll", "$root\mod\ModInfo.xml" $mod -Force
Copy-Item "$root\mod\Config\*" "$mod\Config" -Force
if (-not $NoConvert) {
    $exe = (Get-ChildItem "$root\converter\SevenRailConvert\bin\Release" -Recurse -Filter 7rail-convert.exe | Select-Object -First 1).FullName
    & $exe --out "$mod\everrail_cache" | Select-String "^layout|!!|failed|done"
}
"deployed to $mod"
