namespace DeleteAnything.Core;

/// <summary>
/// Minimal güvenlik katmanı: sadece sistemi çökertecek (mavi ekran) işlemleri engeller
/// ve Windows klasörü / sürücü kökü gibi yollarda ek onay ister.
/// </summary>
public static class SafetyGuard
{
    /// <summary>Sonlandırılırsa Windows'u anında çökerten process'ler.</summary>
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "secure system", "memory compression",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe",
    };

    public static bool IsCriticalProcess(int pid, string name)
    {
        if (pid <= 4) return true; // Idle (0) ve System (4)
        if (pid == Environment.ProcessId) return true; // kendimizi kapatmayalım
        return CriticalProcesses.Contains(name) || CriticalProcesses.Contains(name + ".exe");
    }

    /// <summary>Ek onay gerektiren yollar. Döndürülen metin nedenini açıklar; null = sorun yok.</summary>
    public static string? GetProtectedPathReason(string path)
    {
        string p = PathNormalizer.ToDisplayPath(path).TrimEnd('\\');

        if (PathNormalizer.IsDriveRoot(p))
            return "Bu bir sürücü kökü.";

        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            string r = root.TrimEnd('\\');
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase))
                return $"Bu bir sistem klasörünün kendisi: {r}";
        }

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (p.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase))
            return "Bu yol Windows klasörünün içinde.";

        return null;
    }
}
