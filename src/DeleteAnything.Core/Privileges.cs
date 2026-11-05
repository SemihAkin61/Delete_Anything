using System.Security.Principal;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>Yönetici durumu ve token yetkileri.</summary>
public static class Privileges
{
    private static bool _enabled;

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Yönetici token'ında var olan ama varsayılan olarak kapalı yetkileri açar:
    /// yedekleme/geri yükleme (ACL'e takılmadan okuma/silme), sahiplik alma,
    /// ve diğer kullanıcıların process'lerine erişim.
    /// </summary>
    public static void EnableAdminPrivileges()
    {
        if (_enabled || !IsElevated) return;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return;

        using (token)
        {
            foreach (var name in new[] { "SeBackupPrivilege", "SeRestorePrivilege", "SeTakeOwnershipPrivilege", "SeDebugPrivilege" })
            {
                if (!LookupPrivilegeValueW(null, name, out var luid)) continue;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        _enabled = true;
    }
}
