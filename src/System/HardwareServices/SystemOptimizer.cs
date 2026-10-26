using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 系统优化器：负责内存清理、性能修剪及后台维护任务
    /// </summary>
    public static class SystemOptimizer
    {
        #region Native Methods
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);
        #endregion

        // 边沿触发的周期门：每个周期只执行一次，与刷新间隔无关
        private static readonly IntervalGate _trafficGate = CreateGate(60, 5);

        /// <summary>
        /// 创建保留错峰的周期门：偏移 = 周期 - 余数，使 seconds % 周期 到达余数时进入新周期，
        /// 首个触发秒与原先的 "seconds % 周期 == 余数" 一致。
        /// </summary>
        private static IntervalGate CreateGate(int period, int remainder)
        {
            var gate = new IntervalGate(period, period - remainder);
            gate.Due(0); // 消耗首次调用必然触发的那一次，避免启动时立刻执行
            return gate;
        }

        /// <summary>
        /// 执行深度内存清理 (针对所有进程)
        /// </summary>
        public static void CleanMemory(Action<int>? onProgress = null)
        {
            // 1. 自身 GC (0-5%)
            onProgress?.Invoke(0);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            onProgress?.Invoke(5);

            // 2. 全局清理 (5-100%)
            try
            {
                var procs = Process.GetProcesses();
                int total = procs.Length;
                int current = 0;

                if (total == 0) { onProgress?.Invoke(100); return; }

                foreach (var proc in procs)
                {
                    try
                    {
                        using (proc)
                        {
                            if (!proc.HasExited) EmptyWorkingSet(proc.Handle);
                        }
                    }
                    catch 
                    {
                        // 忽略无权限访问的系统进程
                    }
                    finally
                    {
                        current++;
                        int p = 5 + (int)((double)current / total * 95);
                        onProgress?.Invoke(Math.Min(p, 100));
                    }
                }
            }
            catch { }
            onProgress?.Invoke(100);
        }

        /// <summary>
        /// 修剪当前进程的工作集内存
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        /// <summary>
        /// 执行定时维护任务 (流量保存)
        /// </summary>
        /// <param name="secondsCounter">系统运行秒数计数器</param>
        public static void RunMaintenanceTasks(long secondsCounter)
        {
            // 1. 流量保存: 每 60 秒 (Offset 5s: 避开整点)
            if (_trafficGate.Due(secondsCounter))
            {
                TrafficLogger.Save();
            }
        }
    }
}
