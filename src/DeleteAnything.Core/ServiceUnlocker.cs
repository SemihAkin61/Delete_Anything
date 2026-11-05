using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace DeleteAnything.Core;

/// <summary>Hedef klasörün içinden çalışan Windows Servisi veya Kernel Sürücüsü (.sys) bilgisi.</summary>
public sealed record TargetServiceInfo(
    string ServiceName,
    string DisplayName,
    string ImagePath,
    int ServiceType,
    bool IsKernelDriver);

/// <summary>
/// Silinmek istenen klasörün içindeki .exe servislerini ve .sys kernel sürücülerini (ör. aow_drv,
/// QMEmulatorService, anti-cheat / emülatör kalıntı sürücüleri) tespit eder, durdurur ve kaldırır.
/// System (PID 4) tarafından tutulan kilitlerin asıl sebebi bu kernel sürücüleridir.
/// </summary>
public static class ServiceUnlocker
{
    /// <summary>
    /// ImagePath'i hedef yolun (veya alt klasörlerinin) içinde olan tüm servis ve kernel driver'ları bulur.
    /// </summary>
    public static List<TargetServiceInfo> FindServicesInPath(string targetLongPath)
    {
        var result = new List<TargetServiceInfo>();
        string target = PathNormalizer.ToDisplayPath(targetLongPath).TrimEnd('\\');

        using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (servicesKey is null) return result;

        foreach (string subName in servicesKey.GetSubKeyNames())
        {
            try
            {
                using var sub = servicesKey.OpenSubKey(subName);
                if (sub is null) continue;

                string? rawImage = sub.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(rawImage)) continue;

                string normalizedImage = NormalizeServiceImagePath(rawImage);
                if (normalizedImage.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                    normalizedImage.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    int type = Convert.ToInt32(sub.GetValue("Type") ?? 0);
                    bool isKernel = (type & 0x1) != 0 || (type & 0x2) != 0; // SERVICE_KERNEL_DRIVER | SERVICE_FILE_SYSTEM_DRIVER
                    string display = (sub.GetValue("DisplayName") as string) ?? subName;
                    result.Add(new TargetServiceInfo(subName, display, normalizedImage, type, isKernel));
                }
            }
            catch
            {
                // Okunamayan alt anahtarları atla
            }
        }

        return result;
    }

    /// <summary>
    /// Bulunan servis ve kernel sürücülerini durdurur ve silinen klasöre ait oldukları için servis kaydını kaldırır.
    /// </summary>
    public static int StopAndDeleteServices(IEnumerable<TargetServiceInfo> services, Action<string>? log = null)
    {
        int stopped = 0;
        foreach (var svc in services)
        {
            string kind = svc.IsKernelDriver ? "Kernel Sürücüsü (Driver)" : "Windows Servisi";
            log?.Invoke($"⚙ {kind} tespit edildi: {svc.ServiceName} ({svc.ImagePath}) → durduruluyor...");

            // 1) sc.exe stop ile durdur (hem kernel driver hem normal servisler için çalışır)
            RunSc($"stop \"{svc.ServiceName}\"");

            // Durmasını bekle (en fazla 6 saniye)
            if (WaitForServiceStopped(svc.ServiceName, TimeSpan.FromSeconds(6)))
            {
                stopped++;
                log?.Invoke($"✓ {kind} durduruldu: {svc.ServiceName}");
            }
            else
            {
                log?.Invoke($"⚠ {kind} hemen durmadı (bellekte kilitli olabilir): {svc.ServiceName}");
            }

            // 2) Klasörü sildiğimiz için kalıntı servisi/driver'ı sistemden kaldır (bir sonraki açılışta tekrar yüklenmesin)
            RunSc($" config \"{svc.ServiceName}\" start= disabled");
            if (RunSc($"delete \"{svc.ServiceName}\"") == 0)
                log?.Invoke($"✓ Kalıntı {kind} kaydı silindi: {svc.ServiceName}");
        }

        return stopped;
    }

    private static bool WaitForServiceStopped(string serviceName, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            string output = RunScWithOutput($"query \"{serviceName}\"");
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("1060", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static int RunSc(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(5000);
            return p?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string RunScWithOutput(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return "";
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            return stdout;
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeServiceImagePath(string raw)
    {
        string s = Environment.ExpandEnvironmentVariables(raw.Trim());

        if (s.StartsWith(@"\??\", StringComparison.Ordinal))
            s = s[4..];
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
            s = s[4..];
        else if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), s[12..]);
        else if (s.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), s[9..]);

        // Tırnak içinde yol + argüman varsa ("C:\path\svc.exe" -arg) sadece dosya yolunu al
        if (s.StartsWith('"'))
        {
            int endQuote = s.IndexOf('"', 1);
            if (endQuote > 1)
                return s[1..endQuote];
        }

        // Tırnaksız yolda .exe veya .sys sonrasındaki parametreleri kırp
        foreach (string ext in new[] { ".sys", ".exe" })
        {
            int idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return s[..(idx + ext.Length)];
        }

        return s.Trim('"');
    }
}
