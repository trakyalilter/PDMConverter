# Generates lib\Interop.EdmLib.dll from the PDM type library installed on this PC.
# Needs: SOLIDWORKS PDM client (EdmInterface.dll) and the .NET Framework SDK (TlbImp.exe, ships with Visual Studio).
# The interop assembly is derived from SOLIDWORKS' type library, so it is generated locally and not committed.
$ErrorActionPreference = 'Stop'

$edm = @(
    'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS PDM\EdmInterface.dll',
    'C:\Program Files (x86)\SOLIDWORKS PDM\EdmInterface.dll'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edm) { throw 'EdmInterface.dll not found. Install the SOLIDWORKS PDM client.' }

$tlbimp = Get-ChildItem 'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin' -Recurse -Filter TlbImp.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $tlbimp) { throw 'TlbImp.exe not found. Install the .NET Framework 4.x SDK / targeting tools (Visual Studio).' }

$lib = Join-Path (Split-Path $PSScriptRoot -Parent) 'lib'
New-Item -ItemType Directory -Force $lib | Out-Null
& $tlbimp.FullName $edm "/out:$lib\Interop.EdmLib.dll" /namespace:EdmLib /machine:Agnostic
if ($LASTEXITCODE -ne 0) { throw "TlbImp failed ($LASTEXITCODE)" }
Write-Host "OK: $lib\Interop.EdmLib.dll (from $edm)"
