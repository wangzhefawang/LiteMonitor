using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading; // 必须引用：用于 Mutex
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor
{
    internal static class Program
    {
        // 保持 Mutex 引用，防止被 GC 回收
        private static Mutex? _mutex = null;

        // UI 线程崩溃弹窗节流：弹窗期间定时器仍在触发，同一故障会反复进入处理器
        private static readonly TimeSpan CrashDialogInterval = TimeSpan.FromMinutes(10);
        private static DateTime _lastCrashDialog = DateTime.MinValue;

        [STAThread]
        static void Main()
        {
            // =================================================================
            // ★★★ 1. 单实例互斥锁 (基于文件路径的版本) - 修正版 ★★★
            // =================================================================
            bool createNew;
            string mutexName;

            try
            {
                // [修正] 使用 Process 获取真实路径，解决单文件发布路径为空的问题
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

                if (string.IsNullOrEmpty(exePath))
                {
                    mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                }
                else
                {
                    string appFolderPath = Path.GetDirectoryName(exePath);

                    string sanitizedPath = appFolderPath?.ToLower()
                                                        .Replace('\\', '_')
                                                        .Replace(':', '_')
                                                        .Replace('/', '_')
                                                        .Replace(' ', '_');

                    // [建议] 增加哈希或长度截断，防止路径过长导致 Mutex 名称超过系统限制 (260字符) 从而抛出异常进入 catch
                    // 这里简单处理：如果生成的名称太长，就改用路径的 SHA256 哈希（取前 16 字节）
                    string baseName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath}_Mutex";
                    if (baseName.Length > 250) 
                    {
                         // 如果路径太长，使用路径的稳定哈希值来保证唯一性且不超长
                         // 不能用 string.GetHashCode()：.NET Core 下它每个进程随机化，会让单实例保护失效
                         string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appFolderPath!.ToLowerInvariant())), 0, 16);
                         baseName = $"Global\\LiteMonitor_SingleInstance_{pathHash}_Mutex";
                    }
                    
                    mutexName = baseName;
                }

                _mutex = new Mutex(true, mutexName, out createNew);
            }
            catch (Exception ex)
            {
                // 记录一下异常（可选），方便调试为什么会创建失败
                // LogCrash(ex, "Mutex_Creation_Failed"); 
                
                // 回退策略
                mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                _mutex = new Mutex(true, mutexName, out createNew);
            }

            if (!createNew)
            {
                return; 
            }

            // =================================================================
            // ★★★ 2. 注册全局异常捕获事件 (保留你的原始逻辑) ★★★
            // =================================================================
            // 捕获 UI 线程的异常
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            
            // 捕获非 UI 线程（后台线程）的异常
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // =================================================================
            // ★★★ 3. 启动应用 ★★★
            // =================================================================
            try
            {
                // ★★★ 3. 启动应用 ★★★
                ApplicationConfiguration.Initialize();
                if (!DriverInstaller.CheckPawnIOBeforeHardware(Settings.Load()))
                {
                    return;
                }

                Application.Run(new MainForm());
            }
            finally
            {
                // =================================================================
                // ★★★ [新增] 退出时的终极清理 ★★★
                // 无论程序是正常关闭、崩溃还是被强制结束(部分情况)，这里都会尝试执行
                // 确保 FPS 进程被杀掉，且 ETW 会话被停止，防止系统卡顿
                // =================================================================
                try 
                {
                    FpsCounter.ForceKillZombies(); 
                }
                catch { }

                // 显式释放锁
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                }
            }
        }

        // --- 异常处理委托 ---
        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            LogCrash(e.Exception, "UI_Thread", throttleDialog: true);
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash(e.ExceptionObject as Exception, "Background_Thread");
        }

        // --- 写入 crash.log 的核心方法 ---
        static void LogCrash(Exception? ex, string source, bool throttleDialog = false)
        {
            if (ex == null) return;

            try
            {
                // 日志文件保存在程序运行目录下
                string logPath = Path.Combine(AppContext.BaseDirectory, "LiteMonitor_Error.log");

                AppLog.Error(source, "未处理异常", ex);

                // UI 线程异常可能持续触发，弹窗最多每 10 分钟一次；后台线程异常意味着进程即将终止，始终弹窗
                if (throttleDialog)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - _lastCrashDialog < CrashDialogInterval) return;
                    // 必须在 MessageBox.Show 之前更新：模态框打开期间处理器会被重入
                    _lastCrashDialog = now;
                }

                // 只有真的崩了才弹窗提示用户
                MessageBox.Show($"程序遇到致命错误！\n错误日志已保存至：{logPath}\n\n原因：{ex.Message}", 
                                "LiteMonitor Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch 
            {
                // 如果日志都写不进去，通常是磁盘满了或权限极度受限，只能忽略
            }
        }
    }
}
