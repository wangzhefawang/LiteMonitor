using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// Release 版诊断日志，写入 exe 目录下的 LiteMonitor_Error.log。
    /// 同一 source 在 60 秒内最多落盘一条，文件超过 1 MB 时轮转为 LiteMonitor_Error.log.1。
    /// source 应是有限的固定标识（如 "UI_Thread"），每个 source 在内存里只占一条限流记录。
    /// </summary>
    public static class AppLog
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
        private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "LiteMonitor_Error.log");

        private static readonly object _lock = new();
        // source -> (上次落盘时间, 此后被抑制的条数)
        private static readonly Dictionary<string, (DateTime LastWritten, int Suppressed)> _state = new();

        public static void Warn(string source, string message, Exception? ex = null) => Write("WARN", source, message, ex);

        public static void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);

        private static void Write(string level, string source, string message, Exception? ex)
        {
            try
            {
                lock (_lock)
                {
                    // 限流窗口用 UTC 计算，避免夏令时或时区变化打乱窗口；只在写入文本时转本地时间
                    DateTime now = DateTime.UtcNow;
                    int suppressed = 0;

                    if (_state.TryGetValue(source, out var st))
                    {
                        if (now - st.LastWritten < Window)
                        {
                            _state[source] = (st.LastWritten, st.Suppressed + 1);
                            return;
                        }
                        suppressed = st.Suppressed;
                    }

                    // 先记账再写盘：写盘失败时同源记录也按窗口限速，不会每次调用都重试
                    _state[source] = (now, 0);

                    var sb = new StringBuilder();
                    sb.Append('[').Append(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                      .Append("] [").Append(level).Append("] [").Append(source).Append("] ").Append(message);
                    if (suppressed > 0) sb.Append($"（此前 60 秒内抑制了 {suppressed} 条同源记录）");
                    sb.AppendLine();
                    if (ex != null) sb.AppendLine(ex.ToString());
                    sb.AppendLine();

                    var file = new FileInfo(LogPath);
                    if (file.Exists && file.Length > MaxBytes)
                        File.Move(LogPath, LogPath + ".1", overwrite: true);

                    File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // 有意吞掉所有异常（其余代码应让错误直接抛出，这里是例外）：日志自身绝不能抛异常，写不进去就放弃这条记录
            }
        }
    }
}
