<#
.SYNOPSIS
    Delete_Anything Akıllı Kurulum Scripti
.DESCRIPTION
    Sistem gereksinimlerini kontrol eder, eksik varsa kullanıcıya sorarak tamamlar,
    uygulamayı derler, masaüstü kısayolunu ve sağ tık menüsünü aktif eder.
#>

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "     🗑️ Delete_Anything Kurulum Sihirbazı" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

$installDir = "$env:LOCALAPPDATA\DeleteAnything\app"
$publishDir = Join-Path $PSScriptRoot "publish"
$sourceDir = $PSScriptRoot

# 1. ADIM: .NET SDK / RUNTIME KONTROLÜ
Write-Host "[1/4] Sistem gereksinimleri kontrol ediliyor..." -ForegroundColor Yellow

$hasDotnet = $false
try {
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks -match "10\.") {
        $hasDotnet = $true
        Write-Host "✓ Gerekli .NET SDK sistemde mevcut." -ForegroundColor Green
    }
} catch {
    $hasDotnet = $false
}

if (-not $hasDotnet) {
    Write-Host ""
    Write-Host "UYARI: Uygulamayı derlemek için gerekli .NET SDK bilgisayarınızda bulunamadı." -ForegroundColor Yellow
    Write-Host "Gereksinimleri otomatik olarak indirmek ve kurmak ister misiniz? (E/H): " -NoNewline -ForegroundColor Cyan
    $answer = Read-Host

    if ($answer -match "^[eEyY]") {
        Write-Host "Gerekli bileşen indiriliyor (resmi Microsoft sunucusu)..." -ForegroundColor Yellow
        $installerUrl = "https://dot.net/v1/dotnet-install.ps1"
        $scriptPath = "$env:TEMP\dotnet-install.ps1"
        Invoke-WebRequest -Uri $installerUrl -OutFile $scriptPath
        & $scriptPath -Channel 10.0 -Architecture x64
        $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
        Write-Host "✓ .NET başarıyla kuruldu." -ForegroundColor Green
    } else {
        Write-Host "Kurulum kullanıcı tarafından iptal edildi." -ForegroundColor Red
        Exit 1
    }
}

# 2. ADIM: UYGULAMANIN DERLENMESİ
Write-Host ""
Write-Host "[2/4] Delete_Anything derleniyor ve optimize ediliyor..." -ForegroundColor Yellow

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$buildCmd = "dotnet publish `"$sourceDir\src\DeleteAnything.App\DeleteAnything.App.csproj`" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o `"$installDir`""
Invoke-Expression $buildCmd | Out-Null

$appExe = Join-Path $installDir "Delete_Anything.exe"

if (-not (Test-Path $appExe)) {
    Write-Host "HATA: Uygulama derlenemedi!" -ForegroundColor Red
    Exit 1
}
Write-Host "✓ Uygulama başarıyla oluşturuldu: $appExe" -ForegroundColor Green

# 3. ADIM: MASAÜSTÜ KISAYOLU
Write-Host ""
Write-Host "[3/4] Masaüstü kısayolu oluşturuluyor..." -ForegroundColor Yellow

$desktop = [Environment]::GetFolderPath("DesktopDirectory")
$shortcutPath = Join-Path $desktop "Delete_Anything.lnk"

$wscript = New-Object -ComObject WScript.Shell
$shortcut = $wscript.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = "Delete_Anything - Kilitli dosya ve klasörleri zorla silici"
$shortcut.IconLocation = "$appExe,0"
$shortcut.Save()

Write-Host "✓ Masaüstü kısayolu oluşturuldu." -ForegroundColor Green

# 4. ADIM: SAĞ TIK MENÜSÜ ("Delete_Anything ile sil")
Write-Host ""
Write-Host "[4/4] Sağ tık 'Delete_Anything ile sil' menüsü kaydediliyor..." -ForegroundColor Yellow

$menuTargets = @(
    "HKCU:\Software\Classes\*\shell\Delete_Anything",
    "HKCU:\Software\Classes\Directory\shell\Delete_Anything"
)

foreach ($reg in $menuTargets) {
    New-Item -Path "$reg\command" -Force | Out-Null
    Set-ItemProperty -Path $reg -Name "(Default)" -Value "Delete_Anything ile sil"
    Set-ItemProperty -Path $reg -Name "Icon" -Value "`"$appExe`",0"
    Set-ItemProperty -Path "$reg\command" -Name "(Default)" -Value "`"$appExe`" `"%1`""
}

Write-Host "✓ Sağ tık menüsü aktif edildi." -ForegroundColor Green

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "   🎉 KURULUM BAŞARIYLA TAMAMLANDI!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "- Masaüstündeki 'Delete_Anything' simgesinden açabilirsiniz."
Write-Host "- Herhangi bir dosya/klasöre sağ tıklayıp 'Delete_Anything ile sil' diyebilirsiniz."
Write-Host ""
Write-Host "Devam etmek için bir tuşa basın..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
