using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DeleteAnything.Core;
using MessageBox = System.Windows.MessageBox;

namespace DeleteAnything.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _targets = new();
    private readonly ObservableCollection<string> _lockers = new();
    private readonly bool _launchedWithArgs;
    private bool _isBusy;

    public MainWindow(string[] args)
    {
        InitializeComponent();

        LstTargets.ItemsSource = _targets;
        LstLockers.ItemsSource = _lockers;

        EnsureContextMenuOnStartup();
        UpdateAdminBadge();
        UpdateContextMenuButton();

        foreach (string arg in args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("--")))
            AddTarget(arg);

        _launchedWithArgs = _targets.Count > 0;
        Loaded += MainWindow_Loaded;
    }

    private void EnsureContextMenuOnStartup()
    {
        try
        {
            if (!ShellIntegration.IsContextMenuRegistered())
            {
                string exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe))
                    ShellIntegration.RegisterContextMenu(exe);
            }
        }
        catch
        {
            // Yoksay
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog("Delete_Anything (Yönetici Modu) hazır. Dosya/klasör sürükleyip bırakabilir veya sağ tık menüsünü kullanabilirsiniz.");

        if (_launchedWithArgs && ChkAutoCloseOnRightClick.IsChecked == true)
        {
            AppendLog("Sağ tık / parametre ile başlatıldı → otomatik kilit açma, sürücü durdurma ve silme başlatılıyor...");
            await RunDeleteFlowAsync(UnlockAction.CloseThenKill);
        }
    }

    private void UpdateAdminBadge()
    {
        if (Privileges.IsElevated)
        {
            AdminBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 95, 70));
            AdminBadgeText.Text = "🛡 Yönetici Yetkisi Aktif";
            AdminBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(167, 243, 208));
            BtnElevate.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 58, 138));
            AdminBadgeText.Text = "Standart Yetki";
            AdminBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(147, 197, 253));
            BtnElevate.Visibility = Visibility.Visible;
        }
    }

    private void UpdateContextMenuButton()
    {
        bool registered = ShellIntegration.IsContextMenuRegistered();
        BtnToggleContext.Content = registered
            ? "🖱 Sağ Tık ('Delete_Anything ile sil'): AÇIK"
            : "🖱 Sağ Tık ('Delete_Anything ile sil'): KAPALI";
        BtnToggleContext.BorderBrush = new SolidColorBrush(
            registered ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(71, 85, 105));
    }

    private void AddTarget(string rawPath)
    {
        try
        {
            string display = PathNormalizer.ToDisplayPath(PathNormalizer.ToLongPath(rawPath));
            if (!_targets.Contains(display, StringComparer.OrdinalIgnoreCase))
                _targets.Add(display);
        }
        catch (Exception ex)
        {
            AppendLog($"Geçersiz yol ({rawPath}): {ex.Message}");
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            foreach (string f in files)
                AddTarget(f);
            AppendLog($"{files.Length} öğe listeye eklendi.");
        }
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Silinecek Dosyaları Seçin",
            Multiselect = true,
            CheckFileExists = false,
        };
        if (dlg.ShowDialog(this) == true)
        {
            foreach (string f in dlg.FileNames)
                AddTarget(f);
        }
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Silinecek klasörü seçin",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            AddTarget(dlg.SelectedPath);
    }

    private void BtnClearTargets_Click(object sender, RoutedEventArgs e)
    {
        _targets.Clear();
        _lockers.Clear();
        TxtLockHeader.Text = "🔒 Kilitleyen Programlar & Sürücüler (0)";
    }

    private async void BtnScanLocks_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _targets.Count == 0)
        {
            if (_targets.Count == 0) AppendLog("Önce silinecek veya taranacak bir dosya/klasör ekleyin.");
            return;
        }

        SetBusy(true, "Kilitler ve sürücüler taranıyor...");
        _lockers.Clear();
        var targetsCopy = _targets.ToList();

        try
        {
            var (foundProcs, foundServices) = await Task.Run(() =>
            {
                var procs = new List<LockingProcess>();
                var services = new List<TargetServiceInfo>();
                foreach (var t in targetsCopy)
                {
                    string lp = PathNormalizer.ToLongPath(t);
                    foreach (var s in ServiceUnlocker.FindServicesInPath(lp))
                        if (services.All(x => !x.ServiceName.Equals(s.ServiceName, StringComparison.OrdinalIgnoreCase)))
                            services.Add(s);

                    var scan = LockDetector.Scan(lp, DeleteEngine.EnumerateFiles(lp));
                    foreach (var p in scan.Processes)
                        if (procs.All(x => x.ProcessId != p.ProcessId)) procs.Add(p);
                }
                return (procs, services);
            });

            foreach (var s in foundServices)
            {
                string typeLabel = s.IsKernelDriver ? "KERNEL SÜRÜCÜSÜ (.sys)" : "WINDOWS SERVİSİ";
                _lockers.Add($"⚙ {s.ServiceName} [{typeLabel}] → {s.ImagePath}");
            }

            foreach (var p in foundProcs)
            {
                string badge = p.IsCritical ? " [KRİTİK SİSTEM / KERNEL]" : p.HandleCount > 0 ? $" [{p.HandleCount} açık handle]" : " [Bellekte/Modül]";
                _lockers.Add($"{p}{badge}");
            }

            int total = foundProcs.Count + foundServices.Count;
            TxtLockHeader.Text = $"🔒 Kilitleyen Programlar & Sürücüler ({total})";
            AppendLog(total == 0
                ? "Tarama tamamlandı: Bu öğeleri kilitleyen açık bir program veya sürücü bulunmadı."
                : $"Tarama tamamlandı: {foundServices.Count} servis/kernel sürücüsü ve {foundProcs.Count} süreç tespit edildi.");
        }
        finally
        {
            SetBusy(false, "Hazır");
        }
    }

    private async void BtnUnlockDelete_Click(object sender, RoutedEventArgs e) =>
        await RunDeleteFlowAsync(UnlockAction.CloseHandles);

    private async void BtnKillDelete_Click(object sender, RoutedEventArgs e) =>
        await RunDeleteFlowAsync(UnlockAction.CloseThenKill);

    private async void BtnRebootDelete_Click(object sender, RoutedEventArgs e) =>
        await RunDeleteFlowAsync(UnlockAction.ScheduleOnReboot);

    private async Task RunDeleteFlowAsync(UnlockAction preferredAction)
    {
        if (_isBusy || _targets.Count == 0)
        {
            if (_targets.Count == 0) AppendLog("Önce silinecek bir dosya veya klasör ekleyin.");
            return;
        }

        SetBusy(true, "Silme işlemi çalışıyor...");
        _lockers.Clear();
        var targetsCopy = _targets.ToList();

        try
        {
            var report = await Task.Run(() =>
            {
                var options = new DeleteOptions
                {
                    DefaultAction = preferredAction,
                    ScheduleRemainingOnReboot = preferredAction == UnlockAction.ScheduleOnReboot,
                };
                var orchestrator = new DeleteOrchestrator(options);
                orchestrator.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
                orchestrator.ConfirmProtectedPath = (path, reason) =>
                {
                    bool confirmed = false;
                    Dispatcher.Invoke(() =>
                    {
                        var res = MessageBox.Show(this,
                            $"{path}\n\n{reason}\n\nYine de silmek istediğinize emin misiniz?",
                            "Korumalı Yol Onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        confirmed = res == MessageBoxResult.Yes;
                    });
                    return confirmed;
                };
                return orchestrator.Delete(targetsCopy);
            });

            foreach (var p in report.Lockers)
            {
                string badge = p.IsCritical ? " [KRİTİK / KERNEL]" : p.HandleCount > 0 ? $" [{p.HandleCount} handle]" : "";
                _lockers.Add($"{p}{badge}");
            }
            TxtLockHeader.Text = $"🔒 Kilitleyen Programlar & Sürücüler ({report.Lockers.Count})";

            foreach (var item in report.Items.Where(i => i.Status is ItemStatus.Deleted or ItemStatus.ScheduledForReboot))
            {
                var match = _targets.FirstOrDefault(t => t.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
                if (match is not null) _targets.Remove(match);
            }

            AppendLog($"SONUÇ → Silindi: {report.DeletedCount} | Yeniden Başlatmada: {report.ScheduledCount} | Başarısız: {report.FailedCount}");

            // Silinemeyen öğe kaldıysa kullanıcıya açık ve net bir hata açıklaması göster!
            if (report.FailedCount > 0)
            {
                var failedItems = report.Items
                    .Where(i => i.Status == ItemStatus.Failed)
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("Aşağıdaki öğeler şu anda doğrudan silinemedi:");
                sb.AppendLine();

                foreach (var item in failedItems.Take(6))
                {
                    sb.AppendLine($"• {item.Path}");
                    sb.AppendLine($"  Neden: {item.Message}");
                    sb.AppendLine();
                }

                if (failedItems.Count > 6)
                    sb.AppendLine($"... ve {failedItems.Count - 6} öğe daha.");

                sb.AppendLine("Bu dosyaları bilgisayar yeniden başlatıldığında Windows açılmadan önce otomatik silinmek üzere işaretlemek ister misiniz?");

                var res = MessageBox.Show(this,
                    sb.ToString(),
                    "Silinemeyen Dosya Raporu — Neden Silinemedi?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (res == MessageBoxResult.Yes)
                {
                    SetBusy(false, "Hazır");
                    await RunDeleteFlowAsync(UnlockAction.ScheduleOnReboot);
                    return;
                }
            }
            else if (report.ScheduledCount > 0)
            {
                MessageBox.Show(this,
                    $"{report.ScheduledCount} öğe Windows yeniden başlatıldığında otomatik silinmek üzere işaretlendi.\n\n" +
                    "Bilgisayarınızı yeniden başlattığınızda bu dosyalar tamamen kaldırılmış olacaktır.",
                    "Yeniden Başlatmada Silinecek",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        finally
        {
            SetBusy(false, "Hazır");
        }
    }

    private void BtnElevate_Click(object sender, RoutedEventArgs e) => RestartElevated();

    private void RestartElevated()
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exe)) return;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var t in _targets)
            psi.ArgumentList.Add(t);

        try
        {
            Process.Start(psi);
            Close();
        }
        catch
        {
            AppendLog("Yönetici yetkisi isteği iptal edildi.");
        }
    }

    private void BtnToggleContext_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (ShellIntegration.IsContextMenuRegistered())
            {
                ShellIntegration.UnregisterContextMenu();
                AppendLog("Sağ tık menüsünden 'Delete_Anything ile sil' kaldırıldı.");
            }
            else
            {
                ShellIntegration.RegisterContextMenu(exe);
                AppendLog("Sağ tık menüsüne 'Delete_Anything ile sil' eklendi.");
            }
            UpdateContextMenuButton();
        }
        catch (Exception ex)
        {
            AppendLog($"Sağ tık menüsü güncellenemedi: {ex.Message}");
        }
    }

    private void BtnShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            string lnk = ShellIntegration.CreateDesktopShortcut(exe, "Delete_Anything");
            AppendLog($"Masaüstü kısayolu oluşturuldu: {lnk}");
        }
        catch (Exception ex)
        {
            AppendLog($"Kısayol oluşturulamadı: {ex.Message}");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        TxtStatus.Text = status;
        BtnUnlockDelete.IsEnabled = !busy;
        BtnKillDelete.IsEnabled = !busy;
        BtnRebootDelete.IsEnabled = !busy;
        BtnScanLocks.IsEnabled = !busy;
    }

    private void AppendLog(string message)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
    }
}
