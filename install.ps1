<#
.SYNOPSIS
    Delete_Anything Akıllı Kurulum Sihirbazı (Canlı İlerleme Çubuklu)
.DESCRIPTION
    Sistem gereksinimlerini kontrol eder, eksik varsa kullanıcıya sorarak canlı ilerleme çubuğuyla indirir,
    uygulamayı derler, masaüstü kısayolunu ve sağ tık menüsünü aktif eder.
#>

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Draw-ProgressBar {
    param(
        [int]$Percent,
        [string]$Status,
        [string]$Spinner = ""
    )
    if ($Percent -lt 0) { $Percent = 0 }
    if ($Percent -gt 100) { $Percent = 100 }

    $barWidth = 28
    $filled = [Math]::Round(($Percent / 100.0) * $barWidth)
    $empty = $barWidth - $filled

    $bar = ("#" * $filled) + ("-" * $empty)
    $pctText = "{0,3}%" -f $Percent
    $line = "`r  [$bar] $pctText $Spinner $Status"

    # Satır sonundaki eski karakterleri temizlemek için boşlukla doldur
    $pad = 95 - $line.Length
    if ($pad -gt 0) { $line += (" " * $pad) }

    Write-Host $line -NoNewline -ForegroundColor Cyan
    Write-Progress -Activity "Delete_Anything Kurulum Sihirbazı" -Status $Status -PercentComplete $Percent
}

function Complete-StepBar {
    param(
        [int]$Percent,
        [string]$DoneMessage
    )
    Draw-ProgressBar -Percent $Percent -Status $DoneMessage -Spinner "[OK]"
    Write-Host ""
}

function Invoke-ProcessWithLiveBar {
    param(
        [string]$FilePath,
        [string]$Arguments,
        [int]$StartPercent,
        [int]$EndPercent,
        [string]$StatusText
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    $frames = @("|", "/", "-", "\")
    $idx = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    while (-not $proc.HasExited) {
        $elapsed = $sw.Elapsed.TotalSeconds
        # Zaman ilerledikçe EndPercent'e yaklaşan yumuşak eğri
        $ratio = 1.0 - [Math]::Exp(-$elapsed / 6.0)
        $currentPct = [int]($StartPercent + (($EndPercent - 1 - $StartPercent) * $ratio))
        $spin = $frames[$idx % $frames.Count]
        $timeStr = "{0:0.0} sn" -f $elapsed

        Draw-ProgressBar -Percent $currentPct -Status "$StatusText ($timeStr)" -Spinner $spin
        $idx++
        Start-Sleep -Milliseconds 120
    }

    $proc.WaitForExit()
    return $proc.ExitCode
}

try { Clear-Host } catch { }
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "             Delete_Anything - Akıllı Kurulum Sihirbazı               " -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ""

$installDir = "$env:LOCALAPPDATA\DeleteAnything\app"
$sourceDir = $PSScriptRoot

# -------------------------------------------------------------------------
# 1. ADIM: .NET SDK KONTROLÜ VE İNDİRME (%0 -> %30)
# -------------------------------------------------------------------------
Write-Host "[Adım 1/4] Sistem gereksinimleri kontrol ediliyor..." -ForegroundColor Yellow
Draw-ProgressBar -Percent 5 -Status ".NET 10 SDK varlığı taranıyor..." -Spinner "|"
Start-Sleep -Milliseconds 250

$dotnetExe = "dotnet"
$hasDotnet = $false

try {
    $sdks = & $dotnetExe --list-sdks 2>$null
    if ($sdks -match "^10\.") {
        $hasDotnet = $true
    }
} catch {
    $hasDotnet = $false
}

if (-not $hasDotnet -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    try {
        $sdks = & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" --list-sdks 2>$null
        if ($sdks -match "^10\.") {
            $dotnetExe = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
            $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
            $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
            $hasDotnet = $true
        }
    } catch {
        $hasDotnet = $false
    }
}

if ($hasDotnet) {
    Complete-StepBar -Percent 30 -DoneMessage "Gerekli .NET 10 SDK sistemde mevcut."
} else {
    Write-Host ""
    Write-Host ""
    Write-Host "  UYARI: Uygulamayı derlemek için gerekli .NET 10 SDK bilgisayarınızda bulunamadı." -ForegroundColor Yellow
    $answer = Read-Host "  Gereksinimleri otomatik olarak indirmek ve kurmak ister misiniz? (E/H)"

    if ($answer -match "^[eEyY]") {
        Draw-ProgressBar -Percent 10 -Status "Microsoft .NET kurulum aracı indiriliyor..." -Spinner "|"
        $installerUrl = "https://dot.net/v1/dotnet-install.ps1"
        $scriptPath = "$env:TEMP\dotnet-install.ps1"
        Invoke-WebRequest -Uri $installerUrl -OutFile $scriptPath -UseBasicParsing

        $exitCode = Invoke-ProcessWithLiveBar `
            -FilePath "powershell.exe" `
            -Arguments "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Channel 10.0 -Architecture x64" `
            -StartPercent 12 `
            -EndPercent 30 `
            -StatusText ".NET 10 SDK indiriliyor ve kuruluyor"

        if ($exitCode -ne 0) {
            Write-Host ""
            Write-Host "  HATA: .NET 10 SDK kurulumu başarısız oldu!" -ForegroundColor Red
            exit 1
        }

        $dotnetExe = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
        $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
        $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
        Complete-StepBar -Percent 30 -DoneMessage ".NET 10 SDK başarıyla indirildi ve kuruldu."
    } else {
        Write-Host "  Kurulum kullanıcı tarafından iptal edildi." -ForegroundColor Red
        exit 1
    }
}

# -------------------------------------------------------------------------
# 2. ADIM: UYGULAMANIN DERLENMESİ (%30 -> %75)
# -------------------------------------------------------------------------
Write-Host ""
Write-Host "[Adım 2/4] Uygulama derleniyor ve optimize ediliyor..." -ForegroundColor Yellow

Get-Process Delete_Anything -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$appProj = Join-Path $sourceDir "src\DeleteAnything.App\DeleteAnything.App.csproj"
$cliProj = Join-Path $sourceDir "src\DeleteAnything.Cli\DeleteAnything.Cli.csproj"

$code1 = Invoke-ProcessWithLiveBar `
    -FilePath $dotnetExe `
    -Arguments "publish `"$appProj`" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -v q -o `"$installDir`"" `
    -StartPercent 30 `
    -EndPercent 60 `
    -StatusText "Delete_Anything masaüstü arayüzü derleniyor"

if ($code1 -ne 0) {
    Write-Host ""
    Write-Host "  HATA: Delete_Anything derlenemedi (Kod: $code1)!" -ForegroundColor Red
    exit 1
}

$code2 = Invoke-ProcessWithLiveBar `
    -FilePath $dotnetExe `
    -Arguments "publish `"$cliProj`" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -v q -o `"$installDir`"" `
    -StartPercent 60 `
    -EndPercent 75 `
    -StatusText "Komut satırı aracı (fdel.exe) paketleniyor"

$appExe = Join-Path $installDir "Delete_Anything.exe"
if (-not (Test-Path $appExe)) {
    Write-Host ""
    Write-Host "  HATA: Uygulama dosyası oluşturulamadı!" -ForegroundColor Red
    exit 1
}

Complete-StepBar -Percent 75 -DoneMessage "Uygulama başarıyla derlendi ($appExe)"

# -------------------------------------------------------------------------
# 3. ADIM: MASAÜSTÜ KISAYOLU (%75 -> %88)
# -------------------------------------------------------------------------
Write-Host ""
Write-Host "[Adım 3/4] Masaüstü kısayolu hazırlanıyor..." -ForegroundColor Yellow
Draw-ProgressBar -Percent 80 -Status "Masaüstüne Delete_Anything kısayolu ekleniyor..." -Spinner "/"

$desktop = [Environment]::GetFolderPath("DesktopDirectory")
$shortcutPath = Join-Path $desktop "Delete_Anything.lnk"

$wscript = New-Object -ComObject WScript.Shell
$shortcut = $wscript.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = "Delete_Anything - Kilitli dosya ve klasörleri zorla silici"
$shortcut.IconLocation = "$appExe,0"
$shortcut.Save()
Start-Sleep -Milliseconds 200

Complete-StepBar -Percent 88 -DoneMessage "Masaüstü kısayolu oluşturuldu ($shortcutPath)"

# -------------------------------------------------------------------------
# 4. ADIM: SAĞ TIK MENÜSÜ (%88 -> %100)
# -------------------------------------------------------------------------
Write-Host ""
Write-Host "[Adım 4/4] Windows sağ tık menüsü yapılandırılıyor..." -ForegroundColor Yellow

$menuTargets = @(
    @{ SubKey = "Software\Classes\*\shell\Delete_Anything"; Label = "Dosya sağ tık menüsü kaydediliyor..."; Pct = 93 },
    @{ SubKey = "Software\Classes\Directory\shell\Delete_Anything"; Label = "Klasör sağ tık menüsü kaydediliyor..."; Pct = 97 }
)

foreach ($item in $menuTargets) {
    Draw-ProgressBar -Percent $item.Pct -Status $item.Label -Spinner "-"
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($item.SubKey)
    $key.SetValue("", "Delete_Anything ile sil")
    $key.SetValue("Icon", "`"$appExe`",0")
    $cmdKey = $key.CreateSubKey("command")
    $cmdKey.SetValue("", "`"$appExe`" `"%1`"")
    $cmdKey.Close()
    $key.Close()
    Start-Sleep -Milliseconds 150
}

Complete-StepBar -Percent 100 -DoneMessage "Sağ tık 'Delete_Anything ile sil' menüsü aktif edildi."
Write-Progress -Activity "Delete_Anything Kurulum Sihirbazı" -Completed

Write-Host ""
Write-Host "======================================================================" -ForegroundColor Green
Write-Host "  [100%] KURULUM BAŞARIYLA TAMAMLANDI!                                " -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
Write-Host "  * Masaüstündeki 'Delete_Anything' kısayolundan açabilirsiniz."
Write-Host "  * Dosya veya klasörlere sağ tıklayıp 'Delete_Anything ile sil' diyebilirsiniz."
Write-Host ""
