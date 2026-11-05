# 🗑️ Delete_Anything

> **Windows'ta kilitli, arka planda çalışan ve "erişim engellendi" hatası veren inatçı dosya/klasörleri zorla silme aracı.**

Windows'ta **"Dosya başka bir programda açık"**, **"Klasör kullanımda"**, **"Erişim engellendi"** veya **"Yol çok uzun / geçersiz dosya adı"** uyarılarına takılmadan dosya ve klasörleri silebilen lokal Windows uygulaması.

- **Sürekli çalışan arka plan servisi yoktur:** Yalnızca masaüstündeki **Delete_Anything** kısayolundan açtığınızda veya bir dosya/klasöre sağ tıklayıp **"Delete_Anything ile sil"** dediğinizde çalışır, işiniz bitince kapanır.

---

## Özellikler (Tamamlanan Tüm Aşamalar)

1. **Aşama 1 — Çekirdek & CLI (`fdel.exe`):**
   - `\\?\` uzun yol normalizasyonu (32.767 karakter sınırı, `CON`, `NUL`, `aux.txt`, sonu noktalı/boşluklu bozuk isimler)
   - POSIX anlık silme semantiği + `ReadOnly` / `Hidden` / `System` özniteliklerini otomatik temizleme
   - Windows Restart Manager API ile dosyayı hangi programın tuttuğunu tespit etme
   - `MoveFileExW` (`MOVEFILE_DELAY_UNTIL_REBOOT`) ile yeniden başlatmada silme
2. **Aşama 2 — Kilit Açma (`HandleScanner`) & İzin/Sahiplik Düzeltme (`PermissionFixer`):**
   - `NtQuerySystemInformation(SystemExtendedHandleInformation)` + `DuplicateHandle(DUPLICATE_CLOSE_SOURCE)` ile **programı kapatmadan sadece açık dosya/klasör kilidini (handle) kapatma**
   - `SeTakeOwnershipPrivilege`, `SeRestorePrivilege`, `SeBackupPrivilege` ve `SetNamedSecurityInfoW` ile erişim engeli olan öğelerin sahipliğini ve tam yetkisini alma
3. **Aşama 3 — Masaüstü Arayüzü (`Delete_Anything.exe`) & Sağ Tık Entegrasyonu:**
   - Koyu temalı modern WPF arayüzü (sürükle-bırak, dosya/klasör seçimi, kilitleyen süreç listesi, canlı işlem günlüğü)
   - Windows Gezgini sağ tık menüsüne **"Delete_Anything ile sil"** seçeneği (`HKCU\Software\Classes` üzerinden)
   - Masaüstünde **Delete_Anything** kısayolu
4. **Aşama 4 — Testler & Tek Dosya (`PublishSingleFile`) Dağıtım:**
   - `tests/DeleteAnything.Tests` altında otomatik birim ve entegrasyon testleri
   - `publish/` klasöründe bağımsız çalışan `Delete_Anything.exe` ve `fdel.exe`

---

## Kullanım

### 1. Masaüstü Uygulaması (`Delete_Anything.exe`)
- Masaüstündeki **Delete_Anything** kısayoluna çift tıklayın.
- Silmek istediğiniz dosya veya klasörleri pencereye sürükleyip bırakın.
- Seçenekler:
  - **🔓 Kilidi Aç & Sil (Program Kapanmaz):** Dosyayı tutan programı kapatmadan sadece dosya kilidini (handle) koparıp siler.
  - **🛑 Programı Kapat & Zorla Sil:** Kilitleyen programı kapatıp (gerekirse sonlandırıp) siler.
  - **🔄 Yeniden Başlatmada Sil:** Bilgisayar yeniden başladığında silinmek üzere işaretler.
  - **🔍 Kim Kilitliyor?:** Silmeden önce hangi programların tuttuğunu listeler.

### 2. Sağ Tık Menüsü (`Delete_Anything ile sil`)
- Herhangi bir dosya veya klasöre sağ tıklayıp **"Delete_Anything ile sil"** seçeneğine basın.
- Uygulama hedefle birlikte açılır ve otomatik olarak kilidi açıp siler.

### 3. Komut Satırı (`fdel.exe`)
```powershell
fdel "C:\yol\dosya.xlsx"          # Kilitliyse ne yapılacağını sorar
fdel "C:\yol\dosya.xlsx" --unlock # Programı kapatmadan sadece kilidi açar ve siler
fdel "D:\eski_proje" --kill       # Kilitleyen programları sonlandırır ve siler
fdel "kilitli.dll" --list         # Sadece kimin kilitlediğini gösterir
```
