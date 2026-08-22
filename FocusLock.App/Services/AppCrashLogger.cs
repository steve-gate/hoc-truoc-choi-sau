using System.Text;

namespace FocusLock.App.Services;

public static class AppCrashLogger
{
    private static readonly object Gate = new();

    public static string LogDirectory
    {
        get
        {
            var baseDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            var root = baseDir.Name.Equals("App", StringComparison.OrdinalIgnoreCase) && baseDir.Parent is not null
                ? baseDir.Parent.FullName
                : baseDir.FullName;
            return Path.Combine(root, "Logs");
        }
    }

    public static string CrashLogPath => Path.Combine(LogDirectory, "crash.log");
    public static string StartupLogPath => Path.Combine(LogDirectory, "startup.log");

    public static void Info(string message) => Write(StartupLogPath, "INFO", message, null);

    public static void Exception(string context, Exception exception) =>
        Write(CrashLogPath, "ERROR", context, exception);

    private static void Write(string file, string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                    .Append(level).Append(" | ").AppendLine(message);
                if (exception is not null) sb.AppendLine(exception.ToString());
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never be able to crash FocusLock.
        }
    }
}
