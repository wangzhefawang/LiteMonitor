using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32; // ★★★ 新增引用 ★★★

// LiteMonitor 系统服务命名空间
namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// FPS 计数器类，用于监控系统中各进程的帧率
    /// 使用 PresentMon 收集帧率数据，并通过多层算法平滑处理确保数据稳定
    /// 实现 IDisposable 接口以确保资源正确释放
    /// </summary>
    public class FpsCounter : IDisposable
    {
        // 状态标志
        private bool _isRunning = false;     // FPS 计数服务运行状态
        private bool _isRestarting = false;  // 服务重启状态
        
        // ★★★ [新增] 静态关机标志 ★★★
        private static bool _isSystemShuttingDown = false;

        static FpsCounter()
        {
            // 订阅系统关机事件
            try { SystemEvents.SessionEnding += (s, e) => _isSystemShuttingDown = true; } catch { }
        }

        // ★★★ [新增] 启动锁和最后 activity 时间，用于控制进程生命周期 ★★★
        private bool _isStarting = false;    // 防止重复启动的标志
        private DateTime _lastAccessTime = DateTime.MinValue; // 最后一次被 UI 请求数据的时间
        private volatile bool _downloadPrompted = false;      // 本轮是否已提示过下载 FPS 组件（FPS 项关闭 5 秒后重置）

        private Process? _presentMonProc;     // PresentMon 进程实例
        private DateTime _lastDataTime = DateTime.MinValue; // 最后一次收到数据的时间
        
        // ★★★ [新增] 引用 DriverInstaller ★★★
        private readonly DriverInstaller _driverInstaller;

        // 秒表计时，用于计算采样周期
        private Stopwatch _cycleTimer = new Stopwatch();
        
        // 原始数据累加：Key=PID，Value=本周期内的帧数
        private readonly ConcurrentDictionary<int, int> _processFrameCounts = new();
        
        // 第一层：滑动累计窗口（解决管道拥堵导致的 600~1200 FPS 跳动）
        // Key: PID, Value: 过去 N 次采样的记录
        private readonly ConcurrentDictionary<int, Queue<FrameSample>> _accumulatorHistory = new();

        // 第二层：奥运会平滑队列（解决数值毛刺）
        private readonly ConcurrentDictionary<int, Queue<float>> _olympicHistory = new();
        
        // 最终算出来的各进程稳定 FPS
        private readonly ConcurrentDictionary<int, float> _calculatedProcessFps = new();
        
        // ★★★ [新增] 进程名缓存，减少 GetProcessById 的 CPU 消耗 ★★★
        private readonly ConcurrentDictionary<int, string> _processNameCache = new();

        // ★★★ [新增] 排除进程列表，减少字符串比较开销 ★★★
        private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase) 
        { 
            "LiteMonitor", "LiteMonitorFPS", "PresentMon", "Unknown" 
        };
        
        // 锁定机制变量，用于实现粘性锁定逻辑
        private int _currentFocusPid = 0;       // 当前聚焦的进程 PID
        private int _pendingPid = 0;            // 待切换的进程 PID
        private int _pendingCount = 0;          // 待切换进程的连续领先周期数

        // PresentMon 会话名称
        private const string SESSION_NAME = "LiteMonitor_Golden_Session";
        
        // 累计窗口大小：4次采样 = 2.0秒（抹平 GpuTest 波动）
        private const int ACCUMULATOR_SIZE = 4;
        
        // 奥运会窗口大小：6次采样（平滑微小抖动）
        private const int OLYMPIC_SIZE = 6;

        // PresentMon 进程活着但连续没有输出超过此秒数才重启（进程已退出则立即重启）
        private const int SILENT_RESTART_SECONDS = 60;

        // 文件路径常量
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory; // 使用 AppDomain 确保准确
        // ★★★ [修改] 统一路径到 resources/assets ★★★
        private static readonly string AssetDir = Path.Combine(BaseDir, "resources");
        private static readonly string ExePath = Path.Combine(AssetDir, "LiteMonitorFPS.exe");
        private static readonly string LogPath = Path.Combine(BaseDir, "fps_debug.log");
        
        /// <summary>
        /// 内部结构体：一次采样的记录
        /// 存储某段时间内的帧数和持续时间
        /// </summary>
        private struct FrameSample
        {
            public int Count;       // 这一段收到的帧数
            public double Duration; // 这一段花费的时间(秒)
        }

        /// <summary>
        /// FpsCounter 构造函数
        /// 初始化环境并启动后台任务
        /// </summary>
        /// <param name="installer">注入 DriverInstaller 以复用下载逻辑</param>
        public FpsCounter(DriverInstaller installer)
        {
            _driverInstaller = installer;

            // [修改] 确保目录存在 (DriverInstaller 也会做，这里双重保险)
            try { if (!Directory.Exists(AssetDir)) Directory.CreateDirectory(AssetDir); } catch { }
            
            // 清除旧日志文件
            try { File.Delete(LogPath); } catch { }

            // 500ms 刷新一次 FPS 计算（稳定版频率）
            Task.Run(async () =>
            {
                _cycleTimer.Start(); 
                while (true)
                {
                    await Task.Delay(500); 
                    CalculateFps();
                }
            });

            // 3秒检查一次服务健康状态和生命周期
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(3000);
                    CheckHealth();
                }
            });
            
            // ★★★ [修改] 构造函数中不再自动启动服务，改为按需启动 ★★★
        }

        private void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        /// <summary>
        /// 获取当前聚焦进程的 FPS 值
        /// 实现了粘性锁定逻辑，避免频繁切换焦点
        /// </summary>
        /// <returns>当前聚焦进程的 FPS 值，无数据时返回 0</returns>
        public float? GetFps()
        {
            // FPS 项关闭超过 5 秒后再次开启，允许重新提示下载（与 CheckHealth 的自动关闭阈值一致）
            if ((DateTime.Now - _lastAccessTime).TotalSeconds > 5) _downloadPrompted = false;

            // ★★★ [修改] 记录本次访问时间，并触发惰性启动 ★★★
            _lastAccessTime = DateTime.Now;

            // 如果没运行且没在启动中，尝试启动服务
            if (!_isRunning && !_isStarting && !_isRestarting)
            {
                _isStarting = true; // 简单锁
                Task.Run(() => StartService());
            }

            if (!_isRunning) return 0f;
            if (_calculatedProcessFps.IsEmpty) 
            {
                _currentFocusPid = 0;
                return 0f;
            }

            // 粘性锁定逻辑：找出全场 FPS 最高的进程（优化版，避免 OrderBy 全量排序）
            int challengerPid = 0;
            float challengerFps = -1f;
            foreach (var pair in _calculatedProcessFps)
            {
                if (pair.Value > challengerFps)
                {
                    challengerFps = pair.Value;
                    challengerPid = pair.Key;
                }
            }

            if (challengerPid == 0) return 0f;

            // 焦点进程仲裁逻辑
            // 只查一次字典：焦点进程可能在两次查找之间被后台线程移除
            if (_currentFocusPid == 0 || !_calculatedProcessFps.TryGetValue(_currentFocusPid, out float currentFps))
            {
                // 当前无焦点或焦点进程已退出，直接切换
                _currentFocusPid = challengerPid;
                _pendingCount = 0;
            }
            else if (challengerPid != _currentFocusPid)
            {
                // 有新的挑战者进程

                // 判断是否是 DWM 进程（桌面窗口管理器）
                bool isChallengerDwm = IsDwm(challengerPid);

                // DWM 进程需要更高的阈值才能切换（1.2倍），普通进程只需 1.1倍
                float thresholdRatio = isChallengerDwm ? 1.2f : 1.1f;

                if (challengerFps > currentFps * thresholdRatio)
                {
                    if (_pendingPid == challengerPid)
                    {
                        // 同一挑战者连续领先，增加计数
                        _pendingCount++;
                        // 碾压式超越（2倍）：直接切换
                        if (challengerFps > currentFps * 2.0f) _pendingCount += 10;

                        // 连续 4 个周期（约2秒）保持领先 -> 切换焦点
                        if (_pendingCount >= 2) 
                        {
                            _currentFocusPid = challengerPid;
                            _pendingCount = 0;
                        }
                    }
                    else
                    {
                        // 新挑战者出现，初始化计数
                        _pendingPid = challengerPid;
                        _pendingCount = 1;
                    }
                }
                else
                {
                    // 挑战者未达到切换阈值，重置计数
                    _pendingCount = 0;
                }
            }
            else
            {
                // 当前焦点仍是最高 FPS 进程，重置计数
                _pendingCount = 0;
            }

            // 返回当前焦点进程的 FPS 值
            if (_calculatedProcessFps.TryGetValue(_currentFocusPid, out float val))
            {
                return (float)Math.Round(val);
            }
            
            return 0f;
        }

        /// <summary>
        /// 判断是否是 DWM 进程（桌面窗口管理器）
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <returns>是 DWM 进程返回 true，否则返回 false</returns>
        private bool IsDwm(int pid)
        {
            return GetProcessName(pid).Equals("dwm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 检查服务健康状态
        /// </summary>
        private void CheckHealth()
        {
            if (_isRestarting || _isStarting) return;

            // ★★★ [新增] 自动关闭逻辑：如果超过 5 秒没有 UI 请求 FPS 数据，关闭进程 ★★★
            if (_isRunning && (DateTime.Now - _lastAccessTime).TotalSeconds > 5)
            {
                Dispose(); // 销毁进程
                return;
            }

            // 只有在应该运行（有请求）的情况下才检查僵死状态
            if (_isRunning && (DateTime.Now - _lastAccessTime).TotalSeconds <= 5)
            {
                // 判断服务是否异常：进程已退出立即重启；进程活着但连续 60 秒没有输出才算僵死
                // （桌面静止时 PresentMon 本来就没有输出，阈值太短会白白反复重启）
                var proc = _presentMonProc;
                bool isDead = (DateTime.Now - _lastDataTime).TotalSeconds > SILENT_RESTART_SECONDS;
                if (proc == null || proc.HasExited) isDead = true;
                
                // 异常时重启服务
                if (isDead) Task.Run(() => RestartService());
            }
        }

        /// <summary>
        /// 重启 PresentMon 服务
        /// </summary>
        private async Task RestartService()
        {
            _isRestarting = true;
            try
            {
                // 释放当前资源
                Dispose();
                // 短暂延迟确保资源完全释放
                await Task.Delay(1000);
                // 重新启动服务
                StartService();
            }
            finally { _isRestarting = false; }
        }

        /// <summary>
        /// 启动 PresentMon 服务
        /// 初始化 PresentMon 进程并设置数据接收
        /// </summary>
        private void StartService()
        {
            try
            {
                _isStarting = true;

                // 检查是否有管理员权限（PresentMon 需要管理员权限）
                if (!IsAdministrator()) return;
                
                // ★★★ [修改] 如果 PresentMon 不存在，每轮只提示一次下载，且不阻塞等待 ★★★
                // 下载成功后，下一次 GetFps 会自然重新走到这里并启动服务；用户取消则本轮不再弹窗
                if (!File.Exists(ExePath))
                {
                    if (!_downloadPrompted)
                    {
                        _downloadPrompted = true;
                        _driverInstaller.CheckAndDownloadPresentMon(silent: true).ContinueWith(
                            t => AppLog.Warn("FPS.Download", "FPS 组件下载失败", t.Exception),
                            TaskContinuationOptions.OnlyOnFaulted);
                    }
                    return;
                }

                // 清理可能存在的僵尸进程
                ForceKillZombies();

                // 配置 PresentMon 进程启动信息
                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"-session_name {SESSION_NAME} -stop_existing_session -output_stdout",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, 
                    CreateNoWindow = true
                };

                // 启动 PresentMon 进程
                var proc = Process.Start(psi);
                if (proc == null) return;

                // 释放上一个（已退出或已被终止的）进程对象，再换上新的
                _presentMonProc?.Dispose();
                _presentMonProc = proc;

                // 更新服务状态
                _isRunning = true;
                _lastDataTime = DateTime.Now;
                // 忽略错误输出
                proc.ErrorDataReceived += (s, e) => { };
                proc.BeginErrorReadLine();

                // 监控进程退出状态（捕获本次启动的进程实例，重启后不会误读新进程）
                Task.Run(async () => { await proc.WaitForExitAsync(); _isRunning = false; });

                // 异步处理 PresentMon 输出数据（同样只读本次启动的进程实例）
                Task.Run(async () =>
                {
                    try
                    {
                        var reader = proc.StandardOutput;
                        while (!reader.EndOfStream)
                        {
                            string? line = await reader.ReadLineAsync();
                            if (!string.IsNullOrWhiteSpace(line)) 
                            {
                                // 解析输出数据
                                ParseLine(line);
                                // 更新最后数据时间
                                _lastDataTime = DateTime.Now;
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
            finally
            {
                _isStarting = false;
            }
        }

        /// <summary>
        /// 解析 PresentMon 输出的一行数据
        /// 提取进程 PID 和帧数信息
        /// </summary>
        /// <param name="line">PresentMon 输出的一行文本</param>
        private void ParseLine(string line)
        {
            try
            {
                // 跳过表头行
                if (line.Length == 0 || line[0] == 'A') return; // 快速检查 "Application"

                // 优化解析：手动查找第一个和第二个逗号，避免 Split(',') 产生大量字符串碎片的 GC 压力
                int firstComma = line.IndexOf(',');
                if (firstComma == -1) return;

                int secondComma = line.IndexOf(',', firstComma + 1);
                int length = (secondComma == -1) ? line.Length - firstComma - 1 : secondComma - firstComma - 1;

                if (length > 0)
                {
                    // 使用 ReadOnlySpan 避免 Substring 内存分配
                    ReadOnlySpan<char> pidSpan = line.AsMemory(firstComma + 1, length).Span;
                    if (int.TryParse(pidSpan, out int pid))
                    {
                        _processFrameCounts.AddOrUpdate(pid, 1, (k, v) => v + 1);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 计算各进程的 FPS 值
        /// 使用双层算法：滑动窗口累加 + 奥运会平滑
        /// </summary>
        private void CalculateFps()
        {
            if (!_isRunning) return;

            // 获取本周期的精确时间（约 0.5s）
            double elapsedSeconds = _cycleTimer.Elapsed.TotalSeconds;
            _cycleTimer.Restart(); 

            if (elapsedSeconds <= 0) return;

            // 清理逻辑：如果没有原始数据，清空计算结果
            if (_processFrameCounts.IsEmpty)
            {
                 if (!_calculatedProcessFps.IsEmpty) _calculatedProcessFps.Clear();
                 return;
            }

            // 遍历所有收集到的进程帧数据
            foreach (var kv in _processFrameCounts)
            {
                int pid = kv.Key;
                int currentCount = kv.Value;
                _processFrameCounts[pid] = 0; // 读后归零，准备下一个周期

                // 获取进程名称，过滤不需要监控的进程
                if (!TryGetProcessName(pid, out string pName) && currentCount == 0)
                {
                    // 进程已退出且本周期没有新帧：清掉该 PID 的全部记录，不再每周期重复查询已死的 PID
                    _calculatedProcessFps.TryRemove(pid, out _);
                    _accumulatorHistory.TryRemove(pid, out _);
                    _olympicHistory.TryRemove(pid, out _);
                    _processNameCache.TryRemove(pid, out _);
                    _processFrameCounts.TryRemove(new KeyValuePair<int, int>(pid, 0)); // 仅当值仍为 0 才移除，避免丢掉刚到的帧
                    continue;
                }
                if (ExcludedProcesses.Contains(pName))
                {
                    _calculatedProcessFps.TryRemove(pid, out _);
                    continue;
                }

                // 第一层：滑动累计算法（解决 600~1200 FPS 波动）
                var accHistory = _accumulatorHistory.GetOrAdd(pid, new Queue<FrameSample>());
                
                // 入队新的采样记录（保持最近 4 次采样）
                accHistory.Enqueue(new FrameSample { Count = currentCount, Duration = elapsedSeconds });
                while (accHistory.Count > ACCUMULATOR_SIZE) accHistory.Dequeue();

                // 累计求和：计算最近 2 秒内的总帧数和总时间
                long totalCount = 0;
                double totalTime = 0;
                foreach (var sample in accHistory)
                {
                    totalCount += sample.Count;
                    totalTime += sample.Duration;
                }

                // 计算初步稳定的 FPS（基于累计数据）
                float rawStableFps = 0;
                if (totalTime > 0) rawStableFps = (float)(totalCount / totalTime);
                
                // 瞬时响应优化：对于刚启动的进程，如果帧率很高，直接使用瞬时值
                if (accHistory.Count < 2 && (currentCount / elapsedSeconds) > 100)
                {
                    rawStableFps = (float)(currentCount / elapsedSeconds);
                }

                // 第二层：奥运会平滑算法（解决数值毛刺）
                var olyHistory = _olympicHistory.GetOrAdd(pid, new Queue<float>());
                olyHistory.Enqueue(rawStableFps);
                while (olyHistory.Count > OLYMPIC_SIZE) olyHistory.Dequeue();

                // 计算最终平滑的 FPS：去头去尾取平均（奥运会算法优化版）
                float finalFps = 0;
                int count = olyHistory.Count;
                if (count >= 4)
                {
                    float min = float.MaxValue;
                    float max = float.MinValue;
                    float sum = 0;
                    foreach (var f in olyHistory)
                    {
                        if (f < min) min = f;
                        if (f > max) max = f;
                        sum += f;
                    }
                    // 去掉一个最高值和一个最低值，计算剩余值的平均值
                    finalFps = (sum - min - max) / (count - 2);
                }
                else
                {
                    // 数据不足时直接取平均
                    float sum = 0;
                    foreach (var f in olyHistory) sum += f;
                    finalFps = sum / count;
                }

                // 结果存储与清理
                if (finalFps < 1.0f) 
                {
                    // FPS 过低，移除该进程
                    _calculatedProcessFps.TryRemove(pid, out _);
                    _processNameCache.TryRemove(pid, out _); // ★★★ 同步清理进程名缓存 ★★★
                    if (currentCount == 0 && totalCount == 0) 
                    {
                        // 完全没有帧数据，清理历史记录和帧计数（再出帧时 ParseLine 会重新登记）
                        _accumulatorHistory.TryRemove(pid, out _);
                        _olympicHistory.TryRemove(pid, out _);
                        _processFrameCounts.TryRemove(new KeyValuePair<int, int>(pid, 0));
                    }
                }
                else 
                {
                    // 更新该进程的最终 FPS
                    _calculatedProcessFps[pid] = finalFps;
                }
            }
        }

        /// <summary>
        /// 获取进程名称（带缓存优化）
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <returns>进程名称，失败时返回 "Unknown"</returns>
        private string GetProcessName(int pid)
        {
            TryGetProcessName(pid, out string name);
            return name;
        }

        /// <summary>
        /// 获取进程名称（带缓存），并告知查询是否成功
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <param name="name">进程名称，失败时为 "Unknown"</param>
        /// <returns>查询成功返回 true；进程已不存在等失败情况返回 false</returns>
        private bool TryGetProcessName(int pid, out string name)
        {
            if (_processNameCache.TryGetValue(pid, out string? cachedName))
            {
                name = cachedName;
                return true;
            }

            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
                _processNameCache.TryAdd(pid, name);
                return true;
            }
            catch
            {
                name = "Unknown";
                return false;
            }
        }

        /// <summary>
        /// 清理 PresentMon 僵尸进程和会话
        /// </summary>
        public static void ForceKillZombies()
        {
            // 如果是系统关机，跳过清理以避免 logman.exe 报错 (0xc0000142)
            if (_isSystemShuttingDown) return;

            try
            {
                // 1. 优先终止所有名为 LiteMonitorFPS 的进程 (最重要)
                foreach (var p in Process.GetProcessesByName("LiteMonitorFPS")) { using (p) { try { p.Kill(); } catch { } } }

                // 2. 停止 LiteMonitorFPS 会话
                // 仅在 CLR 未关闭时尝试调用 logman，避免在程序退出极晚期引发异常
                if (!Environment.HasShutdownStarted)
                {
                    using var logman = Process.Start(new ProcessStartInfo { FileName = "logman", Arguments = $"stop {SESSION_NAME} -ets", UseShellExecute = false, CreateNoWindow = true });
                    logman?.WaitForExit(100);
                }
            }
            catch { }
        }

        /// <summary>
        /// 检查当前进程是否有管理员权限
        /// </summary>
        /// <returns>有管理员权限返回 true，否则返回 false</returns>
        public static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent()) {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            // 三步各自处理：Kill 失败（进程已退出等）不能跳过后面的释放和清理
            var proc = _presentMonProc;
            try { proc?.Kill(); } catch { }

            // 释放进程对象并清空字段，避免重启后残留旧实例
            proc?.Dispose();
            _presentMonProc = null;

            // 清理僵尸进程
            ForceKillZombies();
            _isRunning = false; // 同步重置状态
        }
    }
}