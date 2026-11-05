using System.Diagnostics;
using System.Text;
using DeleteAnything.Core.Native;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>
/// "Bu dosyayı kim kullanıyor?" sorusunu Windows Restart Manager API ile cevaplar.
/// Windows Installer ve Explorer'ın kendisi de aynı API'yi kullanır.
/// </summary>
public static class LockDetector
{
    private const int BatchSize = 500;

    /// <summary>
    /// Tam tarama: Restart Manager (dosyalar) + sistem handle listesi (dosya ve klasörler).
    /// </summary>
    /// <param name="targetLongPath">Silinmek istenen ana yol.</param>
    /// <param name="failedFiles">Silinemeyen dosyalar (Restart Manager'a sorulur).</param>
    public static LockScan Scan(string targetLongPath, IEnumerable<string> failedFiles)
    {
        var scan = new LockScan();
        var byPid = FindLockers(failedFiles).ToDictionary(p => p.ProcessId);

        // Restart Manager kilitleyen process'leri bulduysa önce sadece onların handle'larını tara (<10ms).
        // Bulamadıysa (ör. klasör çalışma dizini kilidi) tüm sistemde tara.
        IReadOnlySet<int>? filterPids = byPid.Count > 0 ? byPid.Keys.ToHashSet() : null;
        var handles = HandleScanner.FindHandles(targetLongPath, filterPids);
        if (handles.Count == 0 && filterPids is not null)
            handles = HandleScanner.FindHandles(targetLongPath, filterPids: null);

        foreach (var h in handles)
            scan.Handles.Add(h);

        foreach (var group in scan.Handles.GroupBy(h => h.ProcessId))
        {
            if (byPid.TryGetValue(group.Key, out var existing))
            {
                byPid[group.Key] = existing with { HandleCount = group.Count() };
                continue;
            }

            string name = $"PID {group.Key}";
            try
            {
                using var proc = Process.GetProcessById(group.Key);
                name = proc.ProcessName + ".exe";
            }
            catch { /* kapanmış */ }

            byPid[group.Key] = new LockingProcess(group.Key, name, null,
                SafetyGuard.IsCriticalProcess(group.Key, name), false, group.Count());
        }

        scan.Processes.AddRange(byPid.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        return scan;
    }

    /// <summary>Verilen dosyaları kilitleyen process'leri döndürür.</summary>
    public static IReadOnlyList<LockingProcess> FindLockers(IEnumerable<string> files)
    {
        var result = new Dictionary<int, LockingProcess>();
        var list = files.Select(PathNormalizer.ToDisplayPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        for (int i = 0; i < list.Count; i += BatchSize)
        {
            var batch = list.Skip(i).Take(BatchSize).ToArray();
            var found = QueryBatch(batch);
            if (found is null)
            {
                // Restart Manager, listede tek bir sorunlu yol (çok uzun, sonu noktalı vb.) olsa bile
                // tüm grubu reddeder. Bu durumda dosyaları tek tek sorgularız.
                found = new List<LockingProcess>();
                foreach (var file in batch)
                    found.AddRange(QueryBatch(new[] { file }) ?? QueryBatch(new[] { PathNormalizer.ToLongPath(file) }) ?? new());
            }

            foreach (var p in found)
                result.TryAdd(p.ProcessId, p);
        }

        return result.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>null = Restart Manager yolları kabul etmedi.</summary>
    private static List<LockingProcess>? QueryBatch(string[] files)
    {
        var found = new List<LockingProcess>();
        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);

        if (RmStartSession(out uint session, 0, key) != ERROR_SUCCESS)
            return found;

        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != ERROR_SUCCESS)
                return null;

            uint needed = 0, count = 0, reasons = 0;
            RM_PROCESS_INFO[]? infos = null;
            int rc;
            int attempts = 0;
            do
            {
                rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                if (rc == ERROR_MORE_DATA)
                {
                    infos = new RM_PROCESS_INFO[needed];
                    count = needed;
                }
            } while (rc == ERROR_MORE_DATA && ++attempts < 5);

            if (rc != ERROR_SUCCESS || infos is null)
                return found;

            for (int i = 0; i < count; i++)
            {
                var info = infos[i];
                int pid = info.Process.dwProcessId;
                string name = info.strAppName;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    name = proc.ProcessName + ".exe";
                }
                catch { /* process kapanmış olabilir */ }

                found.Add(new LockingProcess(
                    pid,
                    name,
                    string.IsNullOrEmpty(info.strServiceShortName) ? null : info.strServiceShortName,
                    SafetyGuard.IsCriticalProcess(pid, name),
                    info.bRestartable));
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return found;
    }
}
