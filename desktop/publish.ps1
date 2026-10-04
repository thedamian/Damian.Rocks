# Builds a self-contained, single-file DamianRocks.exe (no .NET install needed on the target PC).
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'
dotnet publish (Join-Path $PSScriptRoot 'DamianRocks\DamianRocks.csproj') -c Release -o $out -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nPublished: $(Join-Path $out 'DamianRocks.exe')" -ForegroundColor Green
