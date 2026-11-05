using System.ComponentModel;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>Kullanıcıya kilit hakkında soru sorulurken verilen bilgiler.</summary>
public sealed record LockPrompt(
    string Path,
    IReadOnlyList<LockingProcess> Processes,
    int Round,
    bool CanCloseHandles);

/// <summary>
/// Silme akışını yönetir:
/// 1) Normal silme
/// 2) Hedef klasör içindeki Windows Servislerini ve Kernel Sürücülerini (.sys) durdurup kaldırma
/// 3) Açık dosya handle'larını kapatma ve gerekirse kilitleyen süreçleri sonlandırma
/// 4) Sahiplik / ACL izinlerini düzeltme (TrustedInstaller / SYSTEM koruması)
/// 5) Bellekte kilitli kalan (.sys/.dll) dosyaları geçici çöpe taşıyıp ana klasörü anında silme
/// 6) Kalanları yeniden başlatmada silmek üzere planlama + detaylı hata açıklaması
/// </summary>
public sealed class DeleteOrchestrator
{
    private const int MaxRounds = 3;
    private readonly DeleteOptions _options;

    /// <summary>Kilit bulunduğunda çağrılır (DefaultAction null ise).</summary>
    public Func<LockPrompt, UnlockAction>? DecideOnLock { get; set; }

    /// <summary>Korumalı yol için onay. Hedef yol + neden → true = devam et.</summary>
    public Func<string, string, bool>? ConfirmProtectedPath { get; set; }

    /// <summary>İlerleme / bilgi mesajları.</summary>
    public event Action<string>? Log;

    public DeleteOrchestrator(DeleteOptions? options = null) => _options = options ?? new DeleteOptions();

    public DeleteReport Delete(IEnumerable<string> paths)
    {
        Privileges.EnableAdminPrivileges();

        var report = new DeleteReport();
        foreach (var path in paths)
        {
            try
            {
                DeleteOne(path, report);
            }
            catch (Exception ex)
            {
                report.Items.Add(new ItemResult(path, ItemStatus.Failed, ex.Message));
                report.FailedRoots.Add(path);
                Emit($"HATA: {path}: {ex.Message}");
            }
        }
        return report;
    }

    private void DeleteOne(string inputPath, DeleteReport report)
    {
        string path = PathNormalizer.ToLongPath(inputPath);
        string display = PathNormalizer.ToDisplayPath(path);

        if (!DeleteEngine.Exists(path))
        {
            report.Items.Add(new ItemResult(display, ItemStatus.Skipped, "Dosya veya klasör zaten mevcut değil."));
            Emit($"Bulunamadı (zaten silinmiş olabilir): {display}");
            return;
        }

        string? reason = SafetyGuard.GetProtectedPathReason(path);
        if (reason is not null && !_options.AllowProtectedPaths &&
            !(ConfirmProtectedPath?.Invoke(display, reason) ?? false))
        {
            report.Items.Add(new ItemResult(display, ItemStatus.Skipped, $"Korumalı yol: {reason}"));
            Emit($"Atlandı (korumalı yol): {display}");
            return;
        }

        // 1) Normal silme (öznitelik temizleme + POSIX silme dahil)
        Emit($"Siliniyor: {display}");
        var failed = DeleteEngine.DeleteTree(path);
        if (failed.Count == 0)
        {
            report.Items.Add(new ItemResult(display, ItemStatus.Deleted));
            Emit($"✓ Tamamen silindi: {display}");
            return;
        }

        // 2) Klasörün içinden çalışan Windows Servisi veya Kernel Sürücüsü (.sys) var mı? (Örn. TxGameAssistant aow_drv)
        var targetServices = ServiceUnlocker.FindServicesInPath(path);
        if (targetServices.Count > 0)
        {
            if (Privileges.IsElevated)
            {
                ServiceUnlocker.StopAndDeleteServices(targetServices, Emit);
                Thread.Sleep(400);
                failed = DeleteEngine.DeleteTree(path);
            }
            else
            {
                Emit($"⚠ Klasör içinde çalışan servis/sürücü bulundu ({string.Join(", ", targetServices.Select(s => s.ServiceName))}), durdurmak için Yönetici yetkisi gerekli!");
                report.NeedsElevation = true;
            }
        }

        // 3) Kilit turları (Restart Manager + HandleScanner)
        for (int round = 1; round <= MaxRounds && failed.Count > 0; round++)
        {
            var scan = LockDetector.Scan(path, failed.Where(f => !f.IsDirectory).Select(f => f.Path));
            if (!scan.HasLocks) break;

            foreach (var l in scan.Processes)
                if (report.Lockers.All(r => r.ProcessId != l.ProcessId)) report.Lockers.Add(l);

            Emit($"Kilitleyen programlar: {string.Join(", ", scan.Processes)}");

            UnlockAction action;
            if (_options.DefaultAction is { } preset)
            {
                if (round == 1)
                {
                    action = preset;
                }
                else if (round == 2 && preset == UnlockAction.CloseHandles && scan.Processes.Any(p => !p.IsCritical))
                {
                    // Sadece handle kapatma yetmediyse 2. turda kilitleyen (kritik olmayan) programı kapatmayı dene
                    Emit("Sadece kilit (handle) kapatmak yetmedi; kilitleyen program kapatılıyor...");
                    action = UnlockAction.CloseThenKill;
                }
                else
                {
                    break;
                }
            }
            else
            {
                action = DecideOnLock?.Invoke(new LockPrompt(display, scan.Processes, round, scan.Handles.Count > 0))
                         ?? UnlockAction.Cancel;
            }

            if (action == UnlockAction.Cancel)
            {
                foreach (var f in failed)
                    report.Items.Add(new ItemResult(PathNormalizer.ToDisplayPath(f.Path), ItemStatus.Failed, "İptal edildi: " + ExplainFailure(f, report.Lockers)));
                report.FailedRoots.Add(display);
                Emit("İptal edildi.");
                return;
            }

            if (action == UnlockAction.ScheduleOnReboot)
            {
                ScheduleOnReboot(failed, report, display);
                return;
            }

            var notReleased = Unlocker.Release(scan, action, _options.GracefulCloseTimeout, Emit);
            foreach (var (p, why) in notReleased) Emit($"Kilit bilgisi: {p}: {why}");

            Thread.Sleep(350);
            failed = DeleteEngine.DeleteTree(path);
        }

        // 4) İzin / sahiplik sorunları
        if (failed.Count > 0)
        {
            if (Privileges.IsElevated)
            {
                Emit("İzinler ve dosya sahiplikleri düzeltiliyor...");
                PermissionFixer.FixTree(path);
                failed = DeleteEngine.DeleteTree(path);
            }
            else
            {
                report.NeedsElevation = true;
            }
        }

        // 5) Bellekte kilitli kalan (.sys driver / .dll / .log) dosyaları geçici çöpe taşıyıp ana klasörü boşaltma
        if (failed.Count > 0 && Privileges.IsElevated)
        {
            int evacuated = 0;
            foreach (var f in failed.Where(f => !f.IsDirectory).ToList())
            {
                if (DeleteEngine.TryEvacuateLockedFile(f.Path, out string? trashPath))
                {
                    evacuated++;
                    Emit($"📦 Bellekte kilitli dosya klasörden çıkarılıp geçici çöpe taşındı (yeniden başlatmada silinecek): {Path.GetFileName(PathNormalizer.ToDisplayPath(f.Path))}");
                }
            }

            if (evacuated > 0)
                failed = DeleteEngine.DeleteTree(path);
        }

        if (failed.Count == 0)
        {
            report.Items.Add(new ItemResult(display, ItemStatus.Deleted));
            Emit($"✓ Tamamen silindi: {display}");
            return;
        }

        // 6) Hâlâ silinemeyenler: Neden silinemediğini detaylı açıkla ve istenirse yeniden başlatmaya planla
        foreach (var f in failed.Where(f => !f.IsDirectory || f.ErrorCode != ERROR_DIR_NOT_EMPTY))
        {
            string fDisplay = PathNormalizer.ToDisplayPath(f.Path);
            string detailedReason = ExplainFailure(f, report.Lockers);
            Emit($"✗ Silinemedi: {fDisplay} → NEDEN: {detailedReason}");
        }

        if (_options.ScheduleRemainingOnReboot)
        {
            ScheduleOnReboot(failed, report, display);
        }
        else
        {
            foreach (var f in failed)
            {
                string fDisplay = PathNormalizer.ToDisplayPath(f.Path);
                report.Items.Add(new ItemResult(fDisplay, ItemStatus.Failed, ExplainFailure(f, report.Lockers)));
            }
            report.FailedRoots.Add(display);
            if (!Privileges.IsElevated) report.NeedsElevation = true;
        }
    }

    private static string ExplainFailure(FailedItem f, List<LockingProcess> lockers)
    {
        bool lockedBySystem = lockers.Any(l => l.ProcessId == 4);
        string ext = Path.GetExtension(f.Path).ToLowerInvariant();

        if (ext == ".sys" && f.ErrorCode is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION)
        {
            return "Bu dosya aktif bir Windows çekirdek sürücüsü (Kernel Driver - .sys) olarak bellekte yüklü. " +
                   "Windows çekirdeği çalışırken bellekten kaldırılamayan sürücüler ancak 'Yeniden Başlatmada Sil' seçeneğiyle silinebilir.";
        }

        if (lockedBySystem && f.ErrorCode is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION or ERROR_ACCESS_DENIED)
        {
            return $"Dosya Windows Çekirdeği (System - PID 4) veya bir çekirdek sürücüsü (.sys) tarafından açık tutuluyor ({f.ErrorMessage}). " +
                   "Çekirdek süreci kapatılamayacağı için 'Yeniden Başlatmada Sil' kullanılmalıdır.";
        }

        if (lockers.Count > 0 && f.IsLockError)
        {
            string names = string.Join(", ", lockers.Select(l => $"{l.Name} (PID {l.ProcessId})"));
            return $"Dosya şu program(lar) tarafından açık tutuluyor: {names}. ({f.ErrorMessage})";
        }

        return $"{f.ErrorMessage} (Win32 Hata Kodu: {f.ErrorCode})";
    }

    private void ScheduleOnReboot(List<FailedItem> items, DeleteReport report, string root)
    {
        bool anyFailed = false;
        foreach (var f in items)
        {
            string display = PathNormalizer.ToDisplayPath(f.Path);
            int err = DeleteEngine.ScheduleDeleteOnReboot(f.Path);
            if (err == 0)
            {
                report.Items.Add(new ItemResult(display, ItemStatus.ScheduledForReboot));
                Emit($"🔄 Yeniden başlatmada silinmek üzere işaretlendi: {display}");
            }
            else
            {
                anyFailed = true;
                string msg = new Win32Exception(err).Message;
                if (err == ERROR_ACCESS_DENIED)
                {
                    msg += " (Yeniden başlatmada silme için Yönetici yetkisi gerekir)";
                    report.NeedsElevation = true;
                }
                report.Items.Add(new ItemResult(display, ItemStatus.Failed, msg));
                Emit($"✗ Planlanamadı: {display}: {msg}");
            }
        }
        if (anyFailed) report.FailedRoots.Add(root);
    }

    private void Emit(string message)
    {
        Logger.Write(message);
        Log?.Invoke(message);
    }
}
