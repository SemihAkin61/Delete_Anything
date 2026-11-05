using System.Diagnostics;
using Microsoft.Win32;

namespace DeleteAnything.Core;

/// <summary>
/// Sağ tık menüsü ("Delete_Anything ile sil") ve Masaüstü kısayolu yönetimi.
/// </summary>
public static class ShellIntegration
{
    private const string MenuKeyName = "Delete_Anything";
    public const string MenuDisplayText = "Delete_Anything ile sil";

    private static readonly string[] SubTargets =
    {
        @"Software\Classes\*\shell\" + MenuKeyName,
        @"Software\Classes\Directory\shell\" + MenuKeyName,
    };

    /// <summary>Sağ tık menüsünde kayıtlı mı?</summary>
    public static bool IsContextMenuRegistered()
    {
        using var cuKey = Registry.CurrentUser.OpenSubKey(SubTargets[0] + @"\command");
        if (!string.IsNullOrWhiteSpace(cuKey?.GetValue(null) as string))
            return true;

        using var lmKey = Registry.LocalMachine.OpenSubKey(SubTargets[0] + @"\command");
        return !string.IsNullOrWhiteSpace(lmKey?.GetValue(null) as string);
    }

    /// <summary>
    /// Dosya ve klasör sağ tık menüsüne "Delete_Anything ile sil" seçeneğini ekler.
    /// </summary>
    public static void RegisterContextMenu(string appExePath)
    {
        if (string.IsNullOrWhiteSpace(appExePath))
            throw new ArgumentException("Uygulama yolu boş olamaz.", nameof(appExePath));

        appExePath = Path.GetFullPath(appExePath);

        foreach (string regPath in SubTargets)
        {
            WriteMenuKey(Registry.CurrentUser, regPath, appExePath);
            if (Privileges.IsElevated)
            {
                try { WriteMenuKey(Registry.LocalMachine, regPath, appExePath); }
                catch { /* Yoksay */ }
            }
        }
    }

    private static void WriteMenuKey(RegistryKey root, string regPath, string appExePath)
    {
        using var key = root.CreateSubKey(regPath);
        key.SetValue(null, MenuDisplayText);
        key.SetValue("Icon", $"\"{appExePath}\",0");

        using var cmd = key.CreateSubKey("command");
        cmd.SetValue(null, $"\"{appExePath}\" \"%1\"");
    }

    /// <summary>Sağ tık menüsünden "Delete_Anything ile sil" seçeneğini kaldırır.</summary>
    public static void UnregisterContextMenu()
    {
        foreach (string regPath in SubTargets)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false); } catch { }
            if (Privileges.IsElevated)
            {
                try { Registry.LocalMachine.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false); } catch { }
            }
        }
    }

    /// <summary>Masaüstüne kısayol (.lnk) oluşturur.</summary>
    public static string CreateDesktopShortcut(string appExePath, string shortcutName = "Delete_Anything")
    {
        appExePath = Path.GetFullPath(appExePath);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string lnkPath = Path.Combine(desktop, shortcutName + ".lnk");
        string workDir = Path.GetDirectoryName(appExePath) ?? desktop;

        string escapedLnk = lnkPath.Replace("'", "''");
        string escapedExe = appExePath.Replace("'", "''");
        string escapedWork = workDir.Replace("'", "''");

        string psScript =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{escapedLnk}');" +
            $"$s.TargetPath = '{escapedExe}';" +
            $"$s.WorkingDirectory = '{escapedWork}';" +
            $"$s.Description = 'Delete_Anything - Kilitli dosya ve klasörleri zorla silici';" +
            $"$s.IconLocation = '{escapedExe},0';" +
            "$s.Save()";

        using var proc = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", psScript },
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        proc?.WaitForExit(5000);

        return lnkPath;
    }
}
