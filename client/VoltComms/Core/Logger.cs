using System.IO;

namespace VoltComms.Core;

/// <summary>exe 옆 logs/ 폴더에 일자별 로그 파일을 남기는 간단한 로거.</summary>
public sealed class Logger
{
    private readonly object _lock = new();
    private readonly string _dir;

    public Logger()
    {
        _dir = Path.Combine(AppConfig.BaseDir, "logs");
        Directory.CreateDirectory(_dir);
    }

    public void Info(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(_dir, $"volt-{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine);
            }
            catch (IOException)
            {
                // 로그 실패가 앱을 죽이면 안 된다.
            }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
