using System.Security.Principal;
using System.Text;
using DeleteAnything.Core;

Console.OutputEncoding = Encoding.UTF8;

var paths = new List<string>();
var options = new DeleteOptions();
bool listOnly = false;

foreach (var arg in args)
{
    switch (arg.ToLowerInvariant())
    {
        case "-h" or "--help" or "/?":
            PrintHelp();
            return 0;
        case "--unlock": options.DefaultAction = UnlockAction.CloseHandles; break;
        case "--close": options.DefaultAction = UnlockAction.CloseThenKill; break;
        case "--kill": options.DefaultAction = UnlockAction.Kill; break;
        case "--reboot": options.DefaultAction = UnlockAction.ScheduleOnReboot; break;
        case "-y" or "--yes": options.DefaultAction ??= UnlockAction.CloseThenKill; options.AllowProtectedPaths = false; break;
        case "--no-reboot-fallback": options.ScheduleRemainingOnReboot = false; break;
        case "--allow-system": options.AllowProtectedPaths = true; break;
        case "--list": listOnly = true; break;
        default:
            if (arg.StartsWith("--"))
            {
                Console.Error.WriteLine($"Bilinmeyen seçenek: {arg}");
                return 2;
            }
            paths.Add(arg);
            break;
    }
}

if (paths.Count == 0)
{
    PrintHelp();
    return 2;
}

if (!IsAdmin())
    WriteColor("Uyarı: Yönetici olarak çalışmıyor. Başka kullanıcıların/servislerin kilitleri kaldırılamayabilir.", ConsoleColor.Yellow);

// --list: sadece kim kilitliyor göster
if (listOnly)
{
    foreach (var p in paths)
    {
        var lp = PathNormalizer.ToLongPath(p);
        var services = ServiceUnlocker.FindServicesInPath(lp);
        var scan = LockDetector.Scan(lp, DeleteEngine.EnumerateFiles(lp));
        Console.WriteLine(PathNormalizer.ToDisplayPath(lp));
        if (!scan.HasLocks && services.Count == 0) Console.WriteLine("  (kilitleyen program veya sürücü yok)");
        foreach (var s in services)
            Console.WriteLine($"  ⚙ {s.ServiceName} [{(s.IsKernelDriver ? "KERNEL SÜRÜCÜSÜ" : "WINDOWS SERVİSİ")}] -> {s.ImagePath}");
        foreach (var l in scan.Processes)
            Console.WriteLine($"  - {l}{(l.HandleCount > 0 ? $"  [{l.HandleCount} kilit]" : "")}{(l.IsCritical ? "  [KRİTİK]" : "")}");
        foreach (var h in scan.Handles)
            Console.WriteLine($"      PID {h.ProcessId}: {h.Path}");
    }
    return 0;
}

var orchestrator = new DeleteOrchestrator(options);
orchestrator.Log += msg => Console.WriteLine("  " + msg);

orchestrator.ConfirmProtectedPath = (path, reason) =>
{
    WriteColor($"\n⚠ {path}\n  {reason}", ConsoleColor.Yellow);
    Console.Write("  Yine de silinsin mi? (e/H): ");
    return ReadInput()?.ToLowerInvariant() is "e" or "evet" or "y";
};

orchestrator.DecideOnLock = prompt =>
{
    WriteColor($"\n🔒 {prompt.Path} şu programlar tarafından kullanılıyor{(prompt.Round > 1 ? " (hâlâ)" : "")}:", ConsoleColor.Cyan);
    foreach (var l in prompt.Processes)
        Console.WriteLine($"   - {l}{(l.HandleCount > 0 ? $"  [{l.HandleCount} kilit]" : "")}{(l.IsCritical ? "  [KRİTİK - dokunulamaz]" : "")}");

    string def = prompt.CanCloseHandles ? "1" : "2";
    Console.WriteLine();
    if (prompt.CanCloseHandles)
        Console.WriteLine("   [1] Sadece kilidi aç ve sil (programlar açık kalır)");
    Console.WriteLine("   [2] Programları kapat ve sil (önce düzgünce, olmazsa zorla)");
    Console.WriteLine("   [3] Programları direkt sonlandır ve sil");
    Console.WriteLine("   [4] Yeniden başlatınca sil");
    Console.WriteLine("   [0] İptal");

    while (true)
    {
        Console.Write($"   Seçim (Enter = {def}): ");
        string? input = ReadInput();
        if (input == "") input = def;
        switch (input)
        {
            case null: return UnlockAction.Cancel; // girdi akışı bitti → güvenli taraf
            case "1" when prompt.CanCloseHandles: return UnlockAction.CloseHandles;
            case "2": return UnlockAction.CloseThenKill;
            case "3": return UnlockAction.Kill;
            case "4": return UnlockAction.ScheduleOnReboot;
            case "0": return UnlockAction.Cancel;
            default: Console.WriteLine("   Geçersiz seçim."); break;
        }
    }
};

var report = orchestrator.Delete(paths);

Console.WriteLine();
WriteColor($"Silindi: {report.DeletedCount}   Yeniden başlatmada: {report.ScheduledCount}   Başarısız: {report.FailedCount}",
    report.FailedCount > 0 ? ConsoleColor.Red : ConsoleColor.Green);

foreach (var item in report.Items.Where(i => i.Status is ItemStatus.Failed or ItemStatus.Skipped))
    Console.WriteLine($"  ✗ {item.Path}: {item.Message}");

return report.FailedCount > 0 ? 1 : 0;

static bool IsAdmin()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>Satır okur; yönlendirilmiş girdideki BOM ve boşlukları temizler. null = girdi bitti.</summary>
static string? ReadInput() => Console.ReadLine()?.Trim().Trim('\uFEFF').Trim();

static void WriteColor(string text, ConsoleColor color)
{
    var old = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = old;
}

static void PrintHelp()
{
    Console.WriteLine("""
        fdel - Delete Anything komut satırı aracı

        Kullanım:  fdel <yol> [<yol> ...] [seçenekler]

        Seçenekler:
          --unlock              Programları kapatmadan sadece dosyanın kilidini aç (sormadan)
          --close               Kilitleyen programları sormadan kapat (önce düzgünce, olmazsa zorla)
          --kill                Kilitleyen programları sormadan direkt sonlandır
          --reboot              Kilitli dosyaları sormadan yeniden başlatmada silinmek üzere planla
          -y, --yes             Sorma; varsayılan olarak --close uygula
          --list                Silme; sadece dosyayı kimin kilitlediğini göster
          --no-reboot-fallback  Silinemeyenleri otomatik olarak yeniden başlatmaya planlama
          --allow-system        Windows klasörü / sürücü kökü gibi korumalı yollar için onay sorma

        Örnekler:
          fdel "C:\dosyalar\rapor.xlsx"
          fdel D:\eski_proje --kill
          fdel kilitli.dll --list
        """);
}
