using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>
/// "Erişim engellendi" sorunları için: sahipliği Administrators grubuna alır ve
/// Administrators + mevcut kullanıcıya tam yetki verir. Yönetici yetkisi gerekir.
/// Sadece silinecek öğelere uygulanır.
/// </summary>
public static class PermissionFixer
{
    public static int FixedCount { get; private set; }

    /// <summary>Öğeyi (klasörse içindekilerle birlikte) düzeltir.</summary>
    public static void FixTree(string longPath)
    {
        if (!Privileges.IsElevated) return;
        Privileges.EnableAdminPrivileges();
        FixRecursive(longPath, depth: 0);
    }

    private static void FixRecursive(string path, int depth)
    {
        Fix(path);

        uint a = GetFileAttributesW(path);
        if (a == INVALID_FILE_ATTRIBUTES) return;
        var attrs = (FileAttributes)a;
        if (!attrs.HasFlag(FileAttributes.Directory) || attrs.HasFlag(FileAttributes.ReparsePoint) || depth > 256)
            return;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(path, "*",
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList();
        }
        catch { return; }

        foreach (var child in children)
            FixRecursive(child, depth + 1);
    }

    /// <summary>Tek bir öğenin sahibini ve izinlerini ayarlar. true = başarılı.</summary>
    public static bool Fix(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var me = WindowsIdentity.GetCurrent();
        var user = me.User ?? admins;

        byte[] ownerBytes = new byte[admins.BinaryLength];
        admins.GetBinaryForm(ownerBytes, 0);

        // Korumalı DACL: Administrators ve kullanıcı tam yetkili, alt öğelere de miras kalsın.
        var sd = new RawSecurityDescriptor($"D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;{user.Value})");
        byte[] daclBytes = new byte[sd.DiscretionaryAcl!.BinaryLength];
        sd.DiscretionaryAcl.GetBinaryForm(daclBytes, 0);

        var ownerHandle = GCHandle.Alloc(ownerBytes, GCHandleType.Pinned);
        var daclHandle = GCHandle.Alloc(daclBytes, GCHandleType.Pinned);
        try
        {
            uint r1 = SetNamedSecurityInfoW(path, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION,
                ownerHandle.AddrOfPinnedObject(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            uint r2 = SetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, daclHandle.AddrOfPinnedObject(), IntPtr.Zero);

            SetFileAttributesW(path, FILE_ATTRIBUTE_NORMAL);
            bool ok = r1 == 0 && r2 == 0;
            if (ok) FixedCount++;
            return ok;
        }
        finally
        {
            ownerHandle.Free();
            daclHandle.Free();
        }
    }
}
