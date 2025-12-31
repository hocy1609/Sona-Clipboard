using System;
using System.IO;
using System.Text;

namespace Sona_Clipboard.Services
{
    public enum LogLevel { Debug, Info, Warning, Error }

    public static class LogService
    {
        private static readonly string _logPath;
        private static readonly object _lock = new object();
        private static LogLevel _minLevel = LogLevel.Info;

        static LogService()
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SonaClipboard", "Logs");
            
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, $"sona_{DateTime.Now:yyyyMMdd}.log");
        }

        public static void SetMinLevel(LogLevel level) => _minLevel = level;

        public static void Debug(string message) => Log(LogLevel.Debug, message);
        public static void Info(string message) => Log(LogLevel.Info, message);
        public static void Warning(string message) => Log(LogLevel.Warning, message);
        public static void Error(string message, Exception? ex = null)
        {
            string full = ex != null ? $"{message}: {ex.Message}\n{ex.StackTrace}" : message;
            Log(LogLevel.Error, full);
        }

        private static void Log(LogLevel level, string message)
        {
            if (level < _minLevel) return;
            
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
                System.Diagnostics.Debug.WriteLine(line);
                
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* Logging should never crash the app */ }
        }

        public static void CleanOldLogs(int keepDays = 7)
        {
            try
            {
                string logDir = Path.GetDirectoryName(_logPath)!;
                foreach (var file in Directory.GetFiles(logDir, "sona_*.log"))
                {
                    if (File.GetCreationTime(file) < DateTime.Now.AddDays(-keepDays))
                        File.Delete(file);
                }
            }
            catch { }
        }
    }
}
