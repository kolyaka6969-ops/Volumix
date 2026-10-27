$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.6.2'

if ($root.Length -gt 140) {
    Write-Warning "Path to project is long ($($root.Length) chars)."
}

Write-Host '==> Publishing app (self-contained)' -ForegroundColor Cyan
if (Test-Path "$root\publish") { Remove-Item -Recurse -Force "$root\publish" }
dotnet publish "$root\src\Volumix\Volumix.csproj" -c Release -p:Version=$version -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found.' }

Write-Host '==> Compiling installer' -ForegroundColor Cyan
& $iscc /Qp "/DAppVersion=$version" "$root\installer\Volumix.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Get-Item "$root\dist\Volumix-Setup-$version.exe" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
