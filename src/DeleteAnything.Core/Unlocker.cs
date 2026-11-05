using System.Diagnostics;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>Kilitleri kaldırır: handle kapatma, programı kapatma veya sonlandırma.</summary>
public static class Unlocker
{
    /// <summary>
    /// Seçilen eylemi uygular. Kritik sistem process'lerine dokunmaz.
    /// Dönen liste: kilidi kaldırılamayan process'ler ve nedeni.
    /// </summary>
    public static List<(LockingProcess Process, string Reason)> Release(
        LockScan scan, UnlockAction action, TimeSpan gracefulTimeout, Action<string>? log = null)
    {
        var failed = new List<(LockingProcess, string)>();

        foreach (var critical in scan.Processes.Where(p => p.IsCritical))
            failed.Add((critical, "Kritik sistem process'i; dokunulursa Windows çöker."));

        var targets = scan.Processes.Where(p => !p.IsCritical).ToList();

        if (action == UnlockAction.CloseHandles)
        {
            var critical = scan.Processes.Where(p => p.IsCritical).Select(p => p.ProcessId).ToHashSet();
            int closed = HandleScanner.CloseHandles(scan.Handles, pid => critical.Contains(pid) || SafetyGuard.IsCriticalProcess(pid, ""), log);
            log?.Invoke($"{closed} kilit (handle) kapatıldı; programlar açık kaldı.");

            // Handle'ı bulunmayan process'ler dosyayı belleğe yüklemiş olabilir (çalışan exe/dll).
            foreach (var p in targets.Where(p => scan.Handles.All(h => h.ProcessId != p.ProcessId)))
                failed.Add((p, "Dosya programın belleğine yüklü; programı kapatmadan kilit açılamaz."));
            return failed;
        }

        bool restartExplorer = false;
        foreach (var locker in targets)
        {
            Process proc;
            try { proc = Process.GetProcessById(locker.ProcessId); }
            catch { continue; } // zaten kapanmış

            using (proc)
            {
                bool isExplorer = proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (action == UnlockAction.CloseThenKill && TryCloseGracefully(proc, gracefulTimeout))
                    {
                        log?.Invoke($"Kapatıldı: {locker}");
                        continue;
                    }

                    proc.Kill();
                    proc.WaitForExit(5000);
                    log?.Invoke($"Sonlandırıldı: {locker}");
                    if (isExplorer) restartExplorer = true;
                }
                catch (Exception ex)
                {
                    failed.Add((locker, ex.Message));
                }
            }
        }

        // Masaüstü / görev çubuğu kaybolmasın.
        if (restartExplorer && Process.GetProcessesByName("explorer").Length == 0)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); }
            catch { /* yoksay */ }
        }

        return failed;
    }

    /// <summary>Process'in görünür pencerelerine kapatma mesajı gönderir ve bekler.</summary>
    private static bool TryCloseGracefully(Process proc, TimeSpan timeout)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out int pid);
            if (pid == proc.Id && IsWindowVisible(hWnd)) windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        if (windows.Count == 0) return false;

        foreach (var w in windows) PostMessageW(w, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return proc.WaitForExit((int)timeout.TotalMilliseconds);
    }
}
