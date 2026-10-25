# Сборка Volumix и установщика: .\build.ps1
# Нужны: .NET 8 SDK и Inno Setup 6 (winget install JRSoftware.InnoSetup)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.5.4'

# WPF в Release молча теряет ресурсы, если пути в obj длиннее 260 символов
if ($root.Length -gt 140) {
    Write-Warning "Путь к проекту очень длинный ($($root.Length) символов). Перенесите папку поближе к корню диска, например C:\Projects\Volumix."
}

Write-Host '==> Публикация приложения (self-contained)' -ForegroundColor Cyan
if (Test-Path "$root\publish") { Remove-Item -Recurse -Force "$root\publish" }
dotnet publish "$root\src\Volumix\Volumix.csproj" -c Release -p:Version=$version -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 не найден. Установите: winget install JRSoftware.InnoSetup' }

Write-Host '==> Сборка установщика' -ForegroundColor Cyan
& $iscc /Qp "/DAppVersion=$version" "$root\installer\Volumix.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC завершился с ошибкой' }

Get-Item "$root\dist\Volumix-Setup-$version.exe" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
