using System;
using System.IO;
using System.Text;

namespace ReCord.Services
{
    public enum AppLogLevel
    {
        Info,
        Warning,
        Error,
        Record
    }

    public sealed class AppLogEntry
    {
        public DateTime Timestamp { get; set; }
        public AppLogLevel Level { get; set; }
        public string Message { get; set; }
    }

    public static class AppLog
    {
        private static readonly object Sync = new object();
        private static readonly string LogDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Log");

        public static event Action<AppLogEntry> LineWritten;

        /// <summary>写入一条日志并通知日志事件订阅者。</summary>
        public static void Write(AppLogLevel level, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            AppLogEntry entry = new AppLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message
            };

            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    string file = Path.Combine(LogDirectory,
                        "ReCord-" + entry.Timestamp.ToString("yyyy-MM-dd") + ".log");
                    File.AppendAllText(file,
                        "[" + entry.Timestamp.ToString("HH:mm:ss.fff") + "][" +
                        entry.Level + "] " + entry.Message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never stop recording.
                }
            }

            Action<AppLogEntry> handler = LineWritten;
            if (handler != null)
            {
                try { handler(entry); } catch { }
            }
        }

        /// <summary>写入普通信息日志。</summary>
        public static void Info(string message) { Write(AppLogLevel.Info, message); }

        /// <summary>写入警告日志。</summary>
        public static void Warning(string message) { Write(AppLogLevel.Warning, message); }

        /// <summary>写入错误日志。</summary>
        public static void Error(string message) { Write(AppLogLevel.Error, message); }

        /// <summary>写入录制事件日志。</summary>
        public static void Record(string message) { Write(AppLogLevel.Record, message); }
    }
}
