namespace DeleteAnything.Core;

/// <summary>Basit dosya log'u: %LOCALAPPDATA%\DeleteAnything\logs\yyyy-MM-dd.log</summary>
public static class Logger
{
    private static readonly object Sync = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeleteAnything", "logs");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Log yazılamaması silme işlemini engellememeli.
        }
    }
}
