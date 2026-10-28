using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CTD.Core
{
    public enum LogLevel { Debug, Info, Warn, Error, Game }

    public class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public override string ToString()
        {
            return string.Format("[{0:HH:mm:ss}] [{1}] {2}", Time, Level, Message);
        }
    }

    /// <summary>启动器日志：内存环形缓冲 + 按天落盘，供日志页展示。</summary>
    public static class Log
    {
        private static readonly object _lock = new object();
        private static readonly List<LogEntry> _buffer = new List<LogEntry>();
        private const int MaxBuffer = 4000;
        private static string _file;
        private static bool _inited;
        // 游戏 stdout 可能是每帧/每 tick 一行，逐行 AppendAllText 会反复开关文件。
        // 先攒在内存里，1 秒落盘一次；崩溃最多丢最后 1 秒日志。
        private static readonly StringBuilder _pending = new StringBuilder();
        private static Timer _flushTimer;

        public static event Action<LogEntry> OnLog;

        public static void Init()
        {
            lock (_lock)
            {
                if (_inited) return;
                _inited = true;
                try
                {
                    CtdPaths.EnsureDir(CtdPaths.LogDir);
                    _file = Path.Combine(CtdPaths.LogDir, "CTD-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    CleanupOldLogs();
                    _flushTimer = new Timer(_ => FlushPending(), null, 1000, 1000);
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushPending();
                }
                catch { }
            }
        }

        /// <summary>把内存里的日志追加到当天文件；定时器和退出时调用。</summary>
        private static void FlushPending()
        {
            lock (_lock) FlushPendingLocked();
        }

        /// <summary>已持有 _lock 时使用，避免 Write 内部重复加锁。</summary>
        private static void FlushPendingLocked()
        {
            if (_file == null || _pending.Length == 0) return;
            try
            {
                File.AppendAllText(_file, _pending.ToString(), Encoding.UTF8);
                _pending.Clear();
            }
            catch
            {
                // 写失败时保留内容，下一轮再试；但不能无限增长。
                if (_pending.Length > 1024 * 1024) _pending.Clear();
            }
        }

        private static void CleanupOldLogs()
        {
            try
            {
                var files = Directory.GetFiles(CtdPaths.LogDir, "CTD-*.log");
                if (files.Length <= 10) return;
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length - 10; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        public static void Write(LogLevel level, string msg)
        {
            if (msg == null) return;
            if (level == LogLevel.Game) msg = "    " + msg;
            var e = new LogEntry { Time = DateTime.Now, Level = level, Message = msg };
            Action<LogEntry> handler = null;
            lock (_lock)
            {
                _buffer.Add(e);
                if (_buffer.Count > MaxBuffer) _buffer.RemoveRange(0, _buffer.Count - MaxBuffer);
                handler = OnLog;
                if (_file != null)
                {
                    _pending.Append(e).Append(Environment.NewLine);
                    // 单次刷屏也不能把内存撑爆；超过 512KB 立即落盘。
                    if (_pending.Length > 512 * 1024) FlushPendingLocked();
                }
            }
            if (handler != null)
            {
                try { handler(e); } catch { }
            }
        }

        public static void Debug(string m) { Write(LogLevel.Debug, m); }
        public static void Info(string m) { Write(LogLevel.Info, m); }
        public static void Warn(string m) { Write(LogLevel.Warn, m); }
        public static void Error(string m) { Write(LogLevel.Error, m); }
        public static void Game(string m) { Write(LogLevel.Game, m); }

        public static void Error(string m, Exception ex)
        {
            Write(LogLevel.Error, m + (ex == null ? "" : " -> " + ex.GetType().Name + ": " + ex.Message));
            if (ex != null && ex.StackTrace != null)
            {
                foreach (var line in ex.StackTrace.Split('\n'))
                    Write(LogLevel.Error, "    " + line.Trim());
            }
        }

        public static List<LogEntry> Snapshot()
        {
            lock (_lock) return new List<LogEntry>(_buffer);
        }

        public static void Clear()
        {
            lock (_lock) _buffer.Clear();
        }

        public static string ExportText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("CTD 启动器日志导出 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("版本: " + CtdVersion.Full + "  系统: " + Environment.OSVersion.VersionString);
            sb.AppendLine("游戏目录: " + CtdPaths.GameDir);
            sb.AppendLine(new string('-', 60));
            foreach (var e in Snapshot()) sb.AppendLine(e.ToString());
            return sb.ToString();
        }
    }

    public static class CtdVersion
    {
        public const string Name = "CTD启动器";
        public const string CodeName = "Come True Dream";
#if CTD_STABLE
        public const string Version = "1.05.0";
#elif CTD_TEST_1001
        public const string Version = "beta-1.05.1";
#else
        public const string Version = "1.00.0";
#endif
        public static string Full { get { return Name + " " + Version + " (" + CodeName + ")"; } }
    }
}
