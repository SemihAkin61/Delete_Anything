namespace DeleteAnything.Core;

/// <summary>Bir dosyayı kilitleyen process bilgisi.</summary>
public sealed record LockingProcess(
    int ProcessId,
    string Name,
    string? ServiceName,
    bool IsCritical,
    bool IsRestartable,
    int HandleCount = 0)
{
    public override string ToString() =>
        ServiceName is { Length: > 0 }
            ? $"{Name} (PID {ProcessId}, servis: {ServiceName})"
            : $"{Name} (PID {ProcessId})";
}

/// <summary>Kilit taraması sonucu.</summary>
public sealed class LockScan
{
    public List<LockingProcess> Processes { get; } = new();
    public List<OpenFileHandle> Handles { get; } = new();
    public bool HasLocks => Processes.Count > 0;
}

/// <summary>Kilit bulunduğunda ne yapılacağı.</summary>
public enum UnlockAction
{
    /// <summary>Hiçbir şey yapma, iptal et.</summary>
    Cancel,
    /// <summary>Programı kapatmadan sadece dosyanın kilidini (handle'ı) kapat.</summary>
    CloseHandles,
    /// <summary>Programları düzgünce kapat (pencere kapatma mesajı), olmazsa sonlandır.</summary>
    CloseThenKill,
    /// <summary>Programları direkt sonlandır.</summary>
    Kill,
    /// <summary>Dokunma; bilgisayar yeniden başlayınca sil.</summary>
    ScheduleOnReboot,
}

public enum ItemStatus
{
    Deleted,
    ScheduledForReboot,
    Failed,
    Skipped,
}

public sealed record ItemResult(string Path, ItemStatus Status, string? Message = null);

public sealed class DeleteOptions
{
    /// <summary>Kilit bulunduğunda kullanıcıya sorulmadan uygulanacak eylem. null = sor.</summary>
    public UnlockAction? DefaultAction { get; set; }

    /// <summary>Programın kendiliğinden kapanması için beklenecek süre.</summary>
    public TimeSpan GracefulCloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Kalan dosyalar yeniden başlatmada silinmek üzere otomatik planlansın mı?</summary>
    public bool ScheduleRemainingOnReboot { get; set; } = true;

    /// <summary>Windows klasörü, sürücü kökü gibi korumalı yolların silinmesine izin ver.</summary>
    public bool AllowProtectedPaths { get; set; }
}

public sealed class DeleteReport
{
    public List<ItemResult> Items { get; } = new();
    public List<LockingProcess> Lockers { get; } = new();

    /// <summary>Silinemeyen ana yollar (yönetici olarak tekrar denemek için).</summary>
    public List<string> FailedRoots { get; } = new();

    /// <summary>Başarısızlıkların bir kısmı yönetici yetkisiyle çözülebilir.</summary>
    public bool NeedsElevation { get; set; }

    public int DeletedCount => Items.Count(i => i.Status == ItemStatus.Deleted);
    public int ScheduledCount => Items.Count(i => i.Status == ItemStatus.ScheduledForReboot);
    public int FailedCount => Items.Count(i => i.Status == ItemStatus.Failed);
    public bool Success => FailedCount == 0 && Items.All(i => i.Status != ItemStatus.Skipped);
}
