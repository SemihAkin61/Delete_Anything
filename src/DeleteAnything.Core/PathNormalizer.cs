namespace DeleteAnything.Core;

/// <summary>
/// Yolları uzun-yol formatına (\\?\) çevirir. Böylece 260 karakter sınırı aşılır ve
/// "CON", "NUL", sonu nokta/boşlukla biten isimler gibi sorunlu dosyalar da silinebilir.
/// </summary>
public static class PathNormalizer
{
    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";

    /// <summary>Tam yolu \\?\ önekli hale getirir.</summary>
    public static string ToLongPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Yol boş olamaz.", nameof(path));

        path = path.Trim().Trim('"');

        if (path.StartsWith(LongPrefix, StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;

        // GetFullPath sondaki nokta/boşlukları kırpabildiği için önce \\?\ olmadan çözüp
        // sonra orijinal dosya adını koruyacak şekilde birleştiriyoruz.
        string full = Path.GetFullPath(path);
        string originalName = Path.GetFileName(path.TrimEnd('\\', '/'));
        string fullName = Path.GetFileName(full.TrimEnd('\\'));
        if (originalName.Length > 0 && fullName != originalName && originalName.TrimEnd('.', ' ') == fullName)
            full = Path.Combine(Path.GetDirectoryName(full.TrimEnd('\\'))!, originalName);

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return LongUncPrefix + full[2..];

        return LongPrefix + full;
    }

    /// <summary>\\?\ önekini kaldırır (kullanıcıya gösterim ve bazı API'ler için).</summary>
    public static string ToDisplayPath(string path)
    {
        if (path.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[LongUncPrefix.Length..];
        if (path.StartsWith(LongPrefix, StringComparison.Ordinal))
            return path[LongPrefix.Length..];
        return path;
    }

    /// <summary>Yolun sürücü kökü olup olmadığını kontrol eder (ör. C:\).</summary>
    public static bool IsDriveRoot(string path)
    {
        string p = ToDisplayPath(path).TrimEnd('\\');
        return p.Length == 2 && p[1] == ':';
    }
}
