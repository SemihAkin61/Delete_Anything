using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>Bir process'in hedef yol üzerinde tuttuğu açık dosya/klasör handle'ı.</summary>
public sealed record OpenFileHandle(int ProcessId, IntPtr Handle, string Path);

/// <summary>
/// Sistemdeki açık handle'ları tarayıp hedef dosya/klasörü tutanları bulur ve
/// istenirse programı kapatmadan sadece o handle'ı kapatır
/// (Process Explorer'daki "Close Handle" ile aynı mantık).
/// </summary>
public static class HandleScanner
{
    /// <summary>
    /// Hedef yolu (klasörse altındakiler dahil) tutan handle'ları bulur.
    /// <paramref name="filterPids"/> verilirse sadece o process'lerin handle'larına bakar (çok hızlı, &lt;10ms).
    /// </summary>
    public static List<OpenFileHandle> FindHandles(string targetLongPath, IReadOnlySet<int>? filterPids = null, TimeSpan? timeout = null)
    {
        var task = Task.Factory.StartNew(() => Scan(targetLongPath, filterPids), TaskCreationOptions.LongRunning);
        try
        {
            return task.Wait(timeout ?? TimeSpan.FromSeconds(3)) ? task.Result : new List<OpenFileHandle>();
        }
        catch
        {
            return new List<OpenFileHandle>();
        }
    }

    /// <summary>Handle'ları sahibi olan process içinde kapatır. Kritik process'lere dokunmaz.</summary>
    public static int CloseHandles(IEnumerable<OpenFileHandle> handles, Func<int, bool> isCritical, Action<string>? log = null)
    {
        int closed = 0;
        foreach (var group in handles.GroupBy(h => h.ProcessId))
        {
            if (isCritical(group.Key)) continue;

            using var proc = OpenProcess(PROCESS_DUP_HANDLE, false, group.Key);
            if (proc.IsInvalid)
            {
                log?.Invoke($"PID {group.Key} açılamadı (yönetici yetkisi gerekebilir).");
                continue;
            }

            foreach (var h in group)
            {
                if (DuplicateHandle(proc, h.Handle, IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE))
                    closed++;
            }
        }
        return closed;
    }

    private static unsafe List<OpenFileHandle> Scan(string targetLongPath, IReadOnlySet<int>? filterPids)
    {
        var result = new List<OpenFileHandle>();
        string target = ResolveFinalPath(targetLongPath).TrimEnd('\\');
        int ownPid = Environment.ProcessId;

        string probePath = Environment.ProcessPath ?? Path.GetTempPath();
        using var probe = File.OpenHandle(probePath,
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        IntPtr probeValue = probe.DangerousGetHandle();

        IntPtr buffer = QuerySystemHandles();
        var processCache = new Dictionary<int, SafeProcessHandle?>();
        try
        {
            long count = (long)Marshal.ReadIntPtr(buffer);
            var entries = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX*)(buffer + 2 * IntPtr.Size);

            int fileType = -1;
            for (long i = 0; i < count; i++)
            {
                if ((int)entries[i].UniqueProcessId == ownPid && entries[i].HandleValue == probeValue)
                {
                    fileType = entries[i].ObjectTypeIndex;
                    break;
                }
            }
            if (fileType < 0) return result;

            IntPtr self = GetCurrentProcess();

            for (long i = 0; i < count; i++)
            {
                ref var e = ref entries[i];
                int pid = (int)e.UniqueProcessId;
                if (e.ObjectTypeIndex != fileType || pid == ownPid || pid <= 4) continue;
                if (filterPids is not null && !filterPids.Contains(pid)) continue;

                // Bazı senkron pipe handle'ları (0x0012019F / 0x00120189 / 0x001A019F) GetFileType'da bloklayabilir.
                if (filterPids is null && (e.GrantedAccess == 0x0012019F || e.GrantedAccess == 0x00120189 || e.GrantedAccess == 0x001A019F))
                    continue;

                if (!processCache.TryGetValue(pid, out var proc))
                {
                    proc = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                    if (proc.IsInvalid) { proc.Dispose(); proc = null; }
                    processCache[pid] = proc;
                }
                if (proc is null) continue;

                if (!DuplicateHandle(proc, e.HandleValue, self, out IntPtr dup, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                string? path = TryQueryHandlePathWithTimeout(dup);
                if (path is not null &&
                    (path.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new OpenFileHandle(pid, e.HandleValue, path));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            foreach (var p in processCache.Values) p?.Dispose();
        }

        return result;
    }

    // Bloklayan pipe/driver handle'larına takılmamak için kalıcı arka plan işçisi kullanırız.
    private static readonly BlockingCollection<(IntPtr Handle, TaskCompletionSource<string?> Tcs)> QueryQueue = new();
    private static Thread? _worker;
    private static readonly object WorkerLock = new();

    private static string? TryQueryHandlePathWithTimeout(IntPtr dupHandle)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureWorker();
        QueryQueue.Add((dupHandle, tcs));

        if (tcs.Task.Wait(40))
            return tcs.Task.Result;

        // İşçi bloklandı; yeni işçi başlat (bloklanan işçi handle'ı kendi kapatacak).
        lock (WorkerLock) { _worker = null; }
        EnsureWorker();
        return null;
    }

    private static void EnsureWorker()
    {
        lock (WorkerLock)
        {
            if (_worker is { IsAlive: true }) return;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "HandlePathQueryWorker" };
            _worker.Start();
        }
    }

    private static void WorkerLoop()
    {
        var nameBuf = new char[4096];
        foreach (var (dup, tcs) in QueryQueue.GetConsumingEnumerable())
        {
            try
            {
                if (GetFileType(dup) != FILE_TYPE_DISK)
                {
                    tcs.TrySetResult(null);
                    continue;
                }

                uint len = GetFinalPathNameByHandleW(dup, nameBuf, (uint)nameBuf.Length, 0);
                if (len == 0 || len >= nameBuf.Length)
                {
                    tcs.TrySetResult(null);
                    continue;
                }

                tcs.TrySetResult(PathNormalizer.ToDisplayPath(new string(nameBuf, 0, (int)len)));
            }
            catch
            {
                tcs.TrySetResult(null);
            }
            finally
            {
                CloseHandle(dup);
            }
        }
    }

    private static IntPtr QuerySystemHandles()
    {
        int size = 8 * 1024 * 1024;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            uint status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
            if (status == STATUS_SUCCESS) return buffer;

            Marshal.FreeHGlobal(buffer);
            if (status != STATUS_INFO_LENGTH_MISMATCH || size > 1024 * 1024 * 1024)
                throw new InvalidOperationException($"Handle listesi alınamadı (0x{status:X8}).");
            size = Math.Max(size * 2, needed + 1024 * 1024);
        }
    }

    private static string ResolveFinalPath(string longPath)
    {
        using SafeFileHandle h = CreateFileW(longPath, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (!h.IsInvalid)
        {
            var buf = new char[4096];
            uint len = GetFinalPathNameByHandleW(h.DangerousGetHandle(), buf, (uint)buf.Length, 0);
            if (len > 0 && len < buf.Length)
                return PathNormalizer.ToDisplayPath(new string(buf, 0, (int)len));
        }
        return PathNormalizer.ToDisplayPath(longPath);
    }
}
