using System.Diagnostics;
using DeleteAnything.Core;

namespace DeleteAnything.Tests;

public sealed class DeleteAnythingTests : IDisposable
{
    private readonly string _testRoot;

    public DeleteAnythingTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "DeleteAnything_Tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        string lp = PathNormalizer.ToLongPath(_testRoot);
        if (DeleteEngine.Exists(lp))
            DeleteEngine.DeleteTree(lp);
    }

    [Fact]
    public void PathNormalizer_PreservesTrailingDotAndAddsLongPrefix()
    {
        string raw = Path.Combine(_testRoot, "bozuk_isim.");
        string longPath = PathNormalizer.ToLongPath(raw);

        Assert.StartsWith(@"\\?\", longPath);
        Assert.EndsWith("bozuk_isim.", longPath);
    }

    [Fact]
    public void SafetyGuard_ProtectsCriticalSystemProcessesAndWindowsDir()
    {
        Assert.True(SafetyGuard.IsCriticalProcess(4, "System"));
        Assert.True(SafetyGuard.IsCriticalProcess(500, "csrss.exe"));
        Assert.True(SafetyGuard.IsCriticalProcess(600, "lsass.exe"));
        Assert.False(SafetyGuard.IsCriticalProcess(99999, "notepad.exe"));

        Assert.NotNull(SafetyGuard.GetProtectedPathReason(@"C:\"));
        Assert.NotNull(SafetyGuard.GetProtectedPathReason(@"C:\Windows\System32"));
        Assert.Null(SafetyGuard.GetProtectedPathReason(_testRoot));
    }

    [Fact]
    public void DeleteEngine_DeletesReadOnlyHiddenAndLongPaths()
    {
        string deepDir = PathNormalizer.ToLongPath(_testRoot);
        for (int i = 0; i < 12; i++)
            deepDir = Path.Combine(deepDir, $"uzun_alt_klasor_adi_numara_{i}");

        Directory.CreateDirectory(deepDir);
        string deepFile = Path.Combine(deepDir, "salt_okunur_dosya.txt");
        File.WriteAllText(deepFile, "icerik");
        File.SetAttributes(deepFile, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

        var orchestrator = new DeleteOrchestrator();
        var report = orchestrator.Delete(new[] { _testRoot });

        Assert.True(report.Success);
        Assert.False(DeleteEngine.Exists(PathNormalizer.ToLongPath(_testRoot)));
    }

    [Fact]
    public void Orchestrator_CloseHandles_DeletesLockedFileWithoutKillingProcess()
    {
        string lockedFile = Path.Combine(_testRoot, "kilitli_handle_test.txt");
        File.WriteAllText(lockedFile, "kilitli");

        using var holder = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -Command \"$f = [System.IO.File]::Open('{lockedFile}', 'Open', 'ReadWrite', 'None'); [Console]::WriteLine('LOCKED'); Start-Sleep 60\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;

        try
        {
            string? line = holder.StandardOutput.ReadLine();
            Assert.Equal("LOCKED", line?.Trim());

            var orchestrator = new DeleteOrchestrator(new DeleteOptions
            {
                DefaultAction = UnlockAction.CloseHandles,
                ScheduleRemainingOnReboot = false,
            });

            var report = orchestrator.Delete(new[] { lockedFile });

            Assert.True(report.Success, "Dosya handle kapatılarak silinmeliydi.");
            Assert.False(File.Exists(lockedFile), "Dosya diskten silinmiş olmalı.");
            Assert.False(holder.HasExited, "Kilitleyen process sonlandırılmamalı, açık kalmalıydı.");
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill();
        }
    }
}
