# Сборка Volumix и установщиков: .uild.ps1 — оба (Windows 11 и Windows 10); .uild.ps1 -Only Win10 — один
# Нужны: .NET 8 SDK и Inno Setup 6 (winget install JRSoftware.InnoSetup)
param([ValidateSet('All', 'Win11', 'Win10')] [string]$Only = 'All')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.6.6'

# WPF в Release молча теряет ресурсы, если пути в obj длиннее 260 символов
if ($root.Length -gt 140) {
    Write-Warning "Путь к проекту очень длинный ($($root.Length) символов). Перенесите папку поближе к корню диска, например C:\Projects\Volumix."
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 не найден. Установите: winget install JRSoftware.InnoSetup' }

# Сборка для Windows 10 (константа WIN10): островок с плеером не вшивается в панель задач, а встаёт поверх неё
$targets = @(
    @{ Name = 'Win11'; Win10 = 'false'; Setup = "Volumix-Setup-$version" },
    @{ Name = 'Win10'; Win10 = 'true'; Setup = "Volumix-Setup-$version-Win10" }
) | Where-Object { $Only -eq 'All' -or $_.Name -eq $Only }

foreach ($t in $targets) {
    Write-Host "==> Публикация приложения ($($t.Name), self-contained)" -ForegroundColor Cyan
    if (Test-Path "$root\publish") { Remove-Item -Recurse -Force "$root\publish" }
    dotnet publish "$root\src\Volumix\Volumix.csproj" -c Release -p:Version=$version "-p:Win10Build=$($t.Win10)" -o "$root\publish"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }

    Write-Host "==> Сборка установщика ($($t.Name))" -ForegroundColor Cyan
    & $iscc /Qp "/DAppVersion=$version" "/DSetupName=$($t.Setup)" "$root\installer\Volumix.iss"
    if ($LASTEXITCODE -ne 0) { throw 'ISCC завершился с ошибкой' }
}

$targets | ForEach-Object { Get-Item "$root\dist\$($_.Setup).exe" } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
