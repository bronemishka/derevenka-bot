using System.Security.Cryptography;
using System.Text;
using DerevenkaBot.Core.Application;

namespace DerevenkaBot.Core.Infrastructure;

public static class SafetySaltStore
{
    public static void Initialize(ImageOptions options)
    {
        if (!options.Enabled || options.UseFakeProvider || !string.IsNullOrWhiteSpace(options.SafetySalt)) return;
        var path = Path.Combine(options.StoragePath, "safety-salt");
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(options.StoragePath);
            if (!File.Exists(path))
            {
                temporaryPath = Path.Combine(options.StoragePath, $".safety-salt-{Guid.NewGuid():N}.tmp");
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporaryPath, fileOptions))
                {
                    var bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                // Publish a complete file atomically. Concurrent instances all use the winning salt.
                try { File.Move(temporaryPath, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            if (new FileInfo(path).Length > 1024) throw new InvalidDataException();
            var salt = File.ReadAllText(path).Trim();
            if (salt.Length < 16) throw new InvalidDataException();
            options.SafetySalt = salt;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Не удалось прочитать или сохранить SafetySalt. Проверьте доступ к StoragePath и файл safety-salt либо задайте SafetySalt длиной от 16 символов вручную.");
        }
        finally
        {
            if (temporaryPath != null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
