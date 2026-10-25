using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.Core
{
    public class HardwareHistoryData
    {
        public List<HardwareHistoryPoint> Points { get; set; } = new List<HardwareHistoryPoint>();
    }

    public class HardwareHistoryPoint
    {
        public DateTime Time { get; set; }
        public Dictionary<string, HardwareHistoryValue> Values { get; set; } = new Dictionary<string, HardwareHistoryValue>();
    }

    public class HardwareHistoryValue
    {
        public float Avg { get; set; }
        public float Max { get; set; }
        public float Min { get; set; }
        public int Count { get; set; }
    }

    public static class HardwareHistoryLogger
    {
        public static readonly string[] TemperatureKeys = { "CPU.Temp", "GPU.Temp", "DISK.Temp", "MOBO.Temp" };
        public static readonly string[] LoadKeys = { "CPU.Load", "GPU.Load", "MEM.Load" };
        public static readonly string[] FrequencyKeys = { "CPU.Clock", "GPU.Clock" };
        public static readonly string[] PowerKeys = { "CPU.Power", "GPU.Power", "BAT.Power" };
        public static readonly string[] FpsKeys = { "FPS" };

        // 按天存储：HardwareHistory/yyyy-MM-dd.json，内容是当天的点列表
        private static readonly string _dirPath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory");
        private const string DayFormat = "yyyy-MM-dd";
        // 旧版单文件，仅用于首次启动时迁移
        private static readonly string _legacyPath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory.json");
        private static readonly string _legacyBackupPath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory.json.bak");
        private static readonly string _legacyMigratedPath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory.json.migrated");
        private static readonly object _dataLock = new object();
        private static readonly object _ioLock = new object();
        private static readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _retention = TimeSpan.FromDays(7);
        private static readonly TimeSpan _saveInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan _exitLoadWait = TimeSpan.FromSeconds(5);

        private static readonly HashSet<string> _supportedKeys = new HashSet<string>(
            TemperatureKeys.Concat(LoadKeys).Concat(FrequencyKeys).Concat(PowerKeys).Concat(FpsKeys),
            StringComparer.OrdinalIgnoreCase);

        private static DateTime _lastSampleTime = DateTime.MinValue;
        private static DateTime _currentMinute = DateTime.MinValue;
        private static DateTime _lastSaveTime = DateTime.MinValue;
        private static readonly Dictionary<string, Accumulator> _currentBucket = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        private static bool _saveRunning;

        // 自上次保存以来有新点写入的日期（都是 .Date），受 _dataLock 保护
        private static readonly HashSet<DateTime> _dirtyDays = new HashSet<DateTime>();
        // 后台加载任务；加载成功（已与内存数据合并）后 _loaded 才为 true，保存必须等到那时，否则会用不完整的数据覆盖当天文件
        private static Task? _loadTask;
        private static volatile bool _loaded;

        public static HardwareHistoryData Data { get; } = new HardwareHistoryData();

        public static IEnumerable<string> SupportedKeys => _supportedKeys;

        /// <summary>
        /// 后台加载最近 7 天的历史，并迁移旧版 HardwareHistory.json。fire-and-forget 调用，内部记录所有异常。
        /// </summary>
        public static Task LoadAsync()
        {
            var task = Task.Run(LoadCore);
            _loadTask = task;
            return task;
        }

        private static void LoadCore()
        {
            try
            {
                DateTime now = DateTime.Now;
                DateTime cutoffDay = (now - _retention).Date;

                MigrateLegacyFile(cutoffDay);

                var loaded = new List<HardwareHistoryPoint>();
                foreach (var (day, filePath) in EnumerateDayFiles().OrderBy(f => f.Day))
                {
                    if (day < cutoffDay) continue; // 过期文件由 DeleteExpiredDayFiles 删除
                    var points = TryReadDayFile(filePath);
                    if (points != null) loaded.AddRange(points);
                }

                DeleteExpiredDayFiles(now);

                // 与加载期间新记录的点合并：同一分钟以内存中的为准（更新），其余补进来后按时间排序
                lock (_dataLock)
                {
                    var existing = new HashSet<DateTime>(Data.Points.Select(p => p.Time));
                    Data.Points = loaded
                        .Where(p => !existing.Contains(p.Time))
                        .Concat(Data.Points)
                        .OrderBy(p => p.Time)
                        .ToList();
                    TrimUnsafe(now);
                }

                // 必须是最后一步：之后保存才被放行
                _loaded = true;
            }
            catch (Exception ex)
            {
                // 加载失败时 _loaded 保持 false，本次运行不再写盘，避免用不完整的数据覆盖磁盘上的日文件
                AppLog.Warn("History.Load", "加载硬件历史失败，本次运行不保存历史", ex);
            }
        }

        // 旧版单文件 -> 按天文件。只写磁盘上还没有日文件的那些天；全部写成功后才把旧文件改名为 .migrated
        private static void MigrateLegacyFile(DateTime cutoffDay)
        {
            if (!File.Exists(_legacyPath)) return;

            var legacy = TryLoadLegacyFile(_legacyPath) ?? TryLoadLegacyFile(_legacyBackupPath);
            if (legacy == null)
            {
                AppLog.Warn("History.Migrate", "旧版硬件历史无法解析，保持原样不迁移");
                return;
            }

            var existingDays = new HashSet<DateTime>(EnumerateDayFiles().Select(f => f.Day));
            var legacyPoints = NormalizeLoaded(legacy.Points);
            foreach (var group in legacyPoints.Where(p => p.Time.Date >= cutoffDay).GroupBy(p => p.Time.Date))
            {
                if (existingDays.Contains(group.Key)) continue;
                WriteDayFile(group.Key, group.OrderBy(p => p.Time).ToList());
            }

            File.Move(_legacyPath, _legacyMigratedPath, overwrite: true);
        }

        private static string GetDayFilePath(DateTime day)
        {
            return Path.Combine(_dirPath, day.ToString(DayFormat, CultureInfo.InvariantCulture) + ".json");
        }

        // 枚举目录下文件名能解析成日期的日文件；解析不了的文件忽略
        private static IEnumerable<(DateTime Day, string FilePath)> EnumerateDayFiles()
        {
            if (!Directory.Exists(_dirPath)) yield break;

            foreach (string filePath in Directory.EnumerateFiles(_dirPath, "*.json"))
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(filePath), DayFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    yield return (day, filePath);
                }
            }
        }

        // 删除早于保留期的日文件（尽力而为，失败只记日志）
        private static void DeleteExpiredDayFiles(DateTime now)
        {
            try
            {
                DateTime cutoffDay = (now - _retention).Date;
                foreach (var (day, filePath) in EnumerateDayFiles().ToList())
                {
                    if (day >= cutoffDay) continue;
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("History.Trim", "删除过期硬件历史文件失败", ex);
            }
        }

        // 单日文件损坏只丢这一天：记日志并跳过
        private static List<HardwareHistoryPoint>? TryReadDayFile(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                var points = JsonSerializer.Deserialize<List<HardwareHistoryPoint>>(stream);
                return points == null ? null : NormalizeLoaded(points);
            }
            catch (Exception ex)
            {
                AppLog.Warn("History.Load", $"读取硬件历史失败: {Path.GetFileName(filePath)}", ex);
                return null;
            }
        }

        // 文件是系统边界：去掉 null 项，补齐 null 的 Values
        private static List<HardwareHistoryPoint> NormalizeLoaded(List<HardwareHistoryPoint>? points)
        {
            if (points == null) return new List<HardwareHistoryPoint>();

            points.RemoveAll(p => p == null);
            foreach (var point in points)
            {
                point.Values ??= new Dictionary<string, HardwareHistoryValue>();
            }
            return points;
        }

        // 先写 .tmp 再 Move 覆盖；直接序列化到流，不生成与文件等大的 UTF-16 字符串。System.Text.Json 写流固定为 UTF-8
        private static void WriteDayFile(DateTime day, List<HardwareHistoryPoint> points)
        {
            Directory.CreateDirectory(_dirPath);

            string filePath = GetDayFilePath(day);
            string tempPath = filePath + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, points);
                }
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch
            {
                CleanupTempFile(tempPath);
                throw;
            }
        }

        public static void RecordSnapshot(Settings cfg, Func<string, float?> readValue)
        {
            if (cfg?.MonitorItems == null || readValue == null) return;

            DateTime now = DateTime.Now;
            if (now - _lastSampleTime < _sampleInterval) return;
            _lastSampleTime = now;

            DateTime minute = FloorMinute(now);
            var enabledKeys = GetEnabledSupportedKeys(cfg).ToList();
            if (enabledKeys.Count == 0) return;

            lock (_dataLock)
            {
                if (_currentMinute == DateTime.MinValue)
                {
                    _currentMinute = minute;
                }
                else if (minute > _currentMinute)
                {
                    FlushCurrentBucketUnsafe();
                    _currentMinute = minute;
                }

                foreach (string key in enabledKeys)
                {
                    float? value;
                    try { value = readValue(key); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HardwareHistoryLogger] 读取硬件趋势采样失败: {key}, {ex.Message}");
                        continue;
                    }

                    float rawValue = value.GetValueOrDefault();
                    if (!value.HasValue || !IsValidValue(key, rawValue)) continue;

                    if (!_currentBucket.TryGetValue(key, out var acc))
                    {
                        acc = new Accumulator();
                        _currentBucket[key] = acc;
                    }
                    acc.Add(rawValue);
                }
            }

            if (now - _lastSaveTime >= _saveInterval)
            {
                _lastSaveTime = now;
                SaveAsync();
            }
        }

        public static List<HardwareHistoryPoint> GetPoints(DateTime from)
        {
            lock (_dataLock)
            {
                var result = Data.Points
                    .Where(p => p.Time >= from)
                    .Select(ClonePoint)
                    .ToList();

                var pending = BuildCurrentPointUnsafe();
                if (pending != null && pending.Time >= from)
                {
                    result.Add(pending);
                }

                return result.OrderBy(p => p.Time).ToList();
            }
        }

        // 退出时同步保存。先等后台加载最多 5 秒；仍未完成（或加载失败）就跳过，绝不用不完整的数据覆盖日文件
        public static void Save()
        {
            var loadTask = _loadTask;
            if (loadTask == null || !loadTask.Wait(_exitLoadWait) || !_loaded)
            {
                AppLog.Warn("History.Save", "硬件历史尚未加载完成或加载失败，退出时跳过保存");
                return;
            }

            SaveCore(flushPending: true);
        }

        private static void SaveCore(bool flushPending)
        {
            // 快照与写盘都在 _ioLock 内，保证先取的快照不会晚于后取的快照落盘（锁顺序固定为 _ioLock -> _dataLock）
            lock (_ioLock)
            {
                DateTime now = DateTime.Now;
                Dictionary<DateTime, List<HardwareHistoryPoint>> snapshot;
                lock (_dataLock)
                {
                    if (flushPending)
                    {
                        FlushCurrentBucketUnsafe();
                    }
                    TrimUnsafe(now);
                    snapshot = CloneDirtyDaysUnsafe(includePending: !flushPending);
                    _dirtyDays.Clear();
                }

                foreach (var (day, points) in snapshot)
                {
                    if (points.Count == 0) continue;

                    try
                    {
                        WriteDayFile(day, points);
                    }
                    catch (Exception ex)
                    {
                        // 写失败的那一天留到下次再写
                        lock (_dataLock) _dirtyDays.Add(day);
                        AppLog.Warn("History.Save", $"保存硬件历史失败: {day.ToString(DayFormat, CultureInfo.InvariantCulture)}", ex);
                    }
                }

                DeleteExpiredDayFiles(now);
            }
        }

        private static IEnumerable<string> GetEnabledSupportedKeys(Settings cfg)
        {
            foreach (string key in _supportedKeys)
            {
                bool enabled = cfg.MonitorItems.Any(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));

                if (enabled) yield return key;
            }
        }

        private static bool IsValidValue(string key, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;

            var type = MetricUtils.GetType(key);
            return type switch
            {
                MetricType.Temperature => v > 0 && v <= 125,
                MetricType.Percent => v >= 0 && v <= 100,
                MetricType.Memory => v >= 0 && v <= 100,
                MetricType.Frequency => v > 0 && v <= 10000,
                MetricType.Power => v > 0 && v <= 1000,
                MetricType.FPS => v >= 0 && v <= 1000,
                _ => false
            };
        }

        // 读取旧版单文件（迁移用）；不存在或无法解析返回 null
        private static HardwareHistoryData? TryLoadLegacyFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<HardwareHistoryData>(stream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareHistoryLogger] 读取旧版硬件趋势失败: {path}, {ex.Message}");
                return null;
            }
        }

        private static void FlushCurrentBucketUnsafe()
        {
            var point = BuildCurrentPointUnsafe();
            if (point == null) return;

            Data.Points.Add(point);
            _dirtyDays.Add(point.Time.Date);
            _currentBucket.Clear();
            TrimUnsafe(DateTime.Now);
        }

        private static HardwareHistoryPoint? BuildCurrentPointUnsafe()
        {
            if (_currentMinute == DateTime.MinValue || _currentBucket.Count == 0) return null;

            var point = new HardwareHistoryPoint { Time = _currentMinute };
            foreach (var kv in _currentBucket)
            {
                if (kv.Value.Count <= 0) continue;
                point.Values[kv.Key] = kv.Value.ToValue();
            }

            return point.Values.Count > 0 ? point : null;
        }

        // 只清内存；过期的日文件由 DeleteExpiredDayFiles 在锁外删除
        private static void TrimUnsafe(DateTime now)
        {
            DateTime cutoff = now - _retention;
            Data.Points.RemoveAll(p => p.Time < cutoff);
            _dirtyDays.RemoveWhere(d => d < cutoff.Date);
        }

        // 克隆需要写盘的那几天的点，按日期分组。includePending 时把尚未封口的当前分钟也算进去（它所在的那天一并写）
        private static Dictionary<DateTime, List<HardwareHistoryPoint>> CloneDirtyDaysUnsafe(bool includePending)
        {
            var result = new Dictionary<DateTime, List<HardwareHistoryPoint>>();
            foreach (DateTime day in _dirtyDays)
            {
                result[day] = new List<HardwareHistoryPoint>();
            }

            var pending = includePending ? BuildCurrentPointUnsafe() : null;
            if (pending != null && !result.ContainsKey(pending.Time.Date))
            {
                result[pending.Time.Date] = new List<HardwareHistoryPoint>();
            }

            foreach (var point in Data.Points)
            {
                if (result.TryGetValue(point.Time.Date, out var list))
                {
                    list.Add(ClonePoint(point));
                }
            }

            if (pending != null)
            {
                result[pending.Time.Date].Add(pending);
            }

            return result;
        }

        private static HardwareHistoryPoint ClonePoint(HardwareHistoryPoint p)
        {
            return new HardwareHistoryPoint
            {
                Time = p.Time,
                Values = (p.Values ?? new Dictionary<string, HardwareHistoryValue>()).ToDictionary(
                    kv => kv.Key,
                    kv => new HardwareHistoryValue
                    {
                        Avg = kv.Value.Avg,
                        Max = kv.Value.Max,
                        Min = kv.Value.Min,
                        Count = kv.Value.Count
                    },
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void SaveAsync()
        {
            // 后台加载完成（并与内存数据合并）之前不保存
            if (!_loaded) return;

            lock (_ioLock)
            {
                if (_saveRunning) return;
                _saveRunning = true;
            }

            Task.Run(() =>
            {
                try { SaveCore(flushPending: false); }
                finally
                {
                    lock (_ioLock) _saveRunning = false;
                }
            });
        }

        private static void CleanupTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareHistoryLogger] 清理临时文件失败: {ex.Message}");
            }
        }

        private static DateTime FloorMinute(DateTime time)
        {
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
        }

        private sealed class Accumulator
        {
            private double _sum;
            private float _min = float.MaxValue;
            private float _max = float.MinValue;

            public int Count { get; private set; }

            public void Add(float value)
            {
                _sum += value;
                if (value < _min) _min = value;
                if (value > _max) _max = value;
                Count++;
            }

            public HardwareHistoryValue ToValue()
            {
                return new HardwareHistoryValue
                {
                    Avg = Count > 0 ? (float)(_sum / Count) : 0,
                    Max = Count > 0 ? _max : 0,
                    Min = Count > 0 ? _min : 0,
                    Count = Count
                };
            }
        }
    }
}
