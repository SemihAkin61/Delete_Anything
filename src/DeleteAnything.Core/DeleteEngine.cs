using System.ComponentModel;
using System.Runtime.InteropServices;
using static DeleteAnything.Core.Native.NativeMethods;

namespace DeleteAnything.Core;

/// <summary>Bir silme denemesinde silinemeyen öğe.</summary>
public sealed record FailedItem(string Path, bool IsDirectory, int ErrorCode)
{
    public string ErrorMessage => new Win32Exception(ErrorCode).Message;
    public bool IsLockError => ErrorCode is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION;
}

/// <summary>Düşük seviye silme işlemleri. Tüm yollar \\?\ formatında beklenir.</summary>
public static class DeleteEngine
{
    private const FileAttributes ReparsePoint = FileAttributes.ReparsePoint;

    public static bool Exists(string longPath) => GetFileAttributesW(longPath) != INVALID_FILE_ATTRIBUTES;

    /// <summary>
    /// Dosya/klasörü (alt öğeleriyle) silmeye çalışır; silinemeyenleri döndürür.
    /// Sembolik link / junction'ların İÇİNE girilmez, sadece linkin kendisi silinir.
    /// </summary>
    public static List<FailedItem> DeleteTree(string longPath)
    {
        var failed = new List<FailedItem>();
        DeleteRecursive(longPath, failed);
        return failed;
    }

    /// <summary>Dosyaların listesini (silinmeyi bekleyen) toplar; kilit tespiti için kullanılır.</summary>
    public static List<string> EnumerateFiles(string longPath)
    {
        var files = new List<string>();
        uint a = GetFileAttributesW(longPath);
        if (a == INVALID_FILE_ATTRIBUTES) return files;

        var attrs = (FileAttributes)a;
        if (!attrs.HasFlag(FileAttributes.Directory) || attrs.HasFlag(ReparsePoint))
        {
            files.Add(longPath);
            return files;
        }

        foreach (var child in SafeEnumerate(longPath))
            files.AddRange(EnumerateFiles(child));
        return files;
    }

    private static void DeleteRecursive(string path, List<FailedItem> failed)
    {
        uint a = GetFileAttributesW(path);
        if (a == INVALID_FILE_ATTRIBUTES) return; // zaten yok

        var attrs = (FileAttributes)a;
        bool isDir = attrs.HasFlag(FileAttributes.Directory);

        if (isDir && !attrs.HasFlag(ReparsePoint))
        {
            foreach (var child in SafeEnumerate(path))
                DeleteRecursive(child, failed);
        }

        int err = isDir ? TryRemoveDirectory(path, attrs) : TryDeleteFile(path, attrs);
        if (err != ERROR_SUCCESS && err != ERROR_FILE_NOT_FOUND && err != ERROR_PATH_NOT_FOUND)
        {
            // İçinde silinemeyen öğe olduğu için boşalmayan klasörleri ayrıca raporlamaya gerek yok;
            // ama yine de "yeniden başlatmada sil" listesine girebilmeleri için kaydediyoruz.
            failed.Add(new FailedItem(path, isDir, err));
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,          // gizli/sistem dosyaları da dahil
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            return Directory.EnumerateFileSystemEntries(dir, "*", options).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Tek bir dosyayı silmeyi dener. 0 = başarılı, aksi halde Win32 hata kodu.</summary>
    public static int TryDeleteFile(string path, FileAttributes attrs)
    {
        ClearBlockingAttributes(path, attrs);

        if (DeleteFileW(path)) return ERROR_SUCCESS;
        int err = Marshal.GetLastWin32Error();

        // Dosya "silme paylaşımına izin vererek" açıldıysa POSIX silme ile isim hemen kaldırılabilir.
        if (err is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION && TryPosixDelete(path, isDirectory: false))
            return ERROR_SUCCESS;

        return err;
    }

    /// <summary>Boş bir klasörü (veya link'i) silmeyi dener.</summary>
    public static int TryRemoveDirectory(string path, FileAttributes attrs)
    {
        ClearBlockingAttributes(path, attrs);

        if (RemoveDirectoryW(path)) return ERROR_SUCCESS;
        int err = Marshal.GetLastWin32Error();

        if (err is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION && TryPosixDelete(path, isDirectory: true))
            return ERROR_SUCCESS;

        return err;
    }

    private static void ClearBlockingAttributes(string path, FileAttributes attrs)
    {
        const FileAttributes blocking = FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden;
        if ((attrs & blocking) != 0)
            SetFileAttributesW(path, FILE_ATTRIBUTE_NORMAL); // zaten silinecek, tüm özellikleri temizle
    }

    /// <summary>
    /// Handle üzerinden silme. Windows 10 1709+ POSIX semantiği ile, başka programlar dosyayı
    /// FILE_SHARE_DELETE ile açmışsa isim anında kaybolur.
    /// </summary>
    private static bool TryPosixDelete(string path, bool isDirectory)
    {
        using var h = CreateFileW(path, DELETE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return false;

        var ex = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
                    FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        };
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>()))
            return true;

        // Eski sistemler / FAT sürücüler için klasik yöntem (handle kapanınca silinir).
        var classic = new FILE_DISPOSITION_INFO { DeleteFile = true };
        return SetFileInformationByHandle(h, FileDispositionInfo, ref classic, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>());
    }

    /// <summary>Öğeyi bilgisayar yeniden başladığında silinmek üzere planlar (yönetici yetkisi gerekir).</summary>
    public static int ScheduleDeleteOnReboot(string path)
    {
        return MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT) ? ERROR_SUCCESS : Marshal.GetLastWin32Error();
    }

    /// <summary>
    /// Bellekte yüklü olduğu için (ör. çalışan .sys kernel driver veya .dll) DeleteFileW ile doğrudan
    /// silinemeyen dosyaları aynı sürücüdeki geçici çöp klasörüne taşır ve yeniden başlatmada silinmek
    /// üzere işaretler. Böylece dosyanın bulunduğu klasör hemen boşalır ve anında silinebilir!
    /// </summary>
    public static bool TryEvacuateLockedFile(string longFilePath, out string? evacuatedPath)
    {
        evacuatedPath = null;
        try
        {
            string display = PathNormalizer.ToDisplayPath(longFilePath);
            string? root = Path.GetPathRoot(display);
            if (string.IsNullOrEmpty(root)) return false;

            string trashDir = Path.Combine(root, "Windows", "Temp", "DeleteAnything_Trash");
            if (!Directory.Exists( Path.Combine(root, "Windows", "Temp")))
                trashDir = Path.Combine(root, "$DeleteAnything_Trash");

            Directory.CreateDirectory(trashDir);
            string destDisplay = Path.Combine(trashDir, $"{Guid.NewGuid():N}_{Path.GetFileName(display)}.del");
            string destLong = PathNormalizer.ToLongPath(destDisplay);

            ClearBlockingAttributes(longFilePath, (FileAttributes)GetFileAttributesW(longFilePath));

            if (MoveFileExW(longFilePath, destLong, 0x1 /* MOVEFILE_REPLACE_EXISTING */))
            {
                evacuatedPath = destDisplay;
                // Taşınan dosyayı hemen silmeyi dene; hâlâ bellekteyse reboot'ta silinmek üzere işaretle
                if (!DeleteFileW(destLong))
                {
                    MoveFileExW(destLong, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    MoveFileExW(PathNormalizer.ToLongPath(trashDir), null, MOVEFILE_DELAY_UNTIL_REBOOT);
                }
                return true;
            }
        }
        catch
        {
            // Yoksay
        }
        return false;
    }
}
