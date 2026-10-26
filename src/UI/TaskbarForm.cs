using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor
{
    public class TaskbarForm : Form
    {
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainForm _mainForm;
        private readonly System.Windows.Forms.Timer _timer = new();

        // ★★★ 双助手架构 ★★★
        private readonly TaskbarWinHelper _winHelper;
        private readonly TaskbarBizHelper _bizHelper;
        
        private HorizontalLayout _layout;
        private List<Column>? _cols;
        private ContextMenuStrip? _currentMenu;
        private DateTime _lastFindHandleTime = DateTime.MinValue;
        private string _lastLayoutSignature = "";
        private long _lastThemeCheckTick;
        private int _lastPaintSignature;
        private readonly TaskbarTooltipHelper _tooltipHelper;
        
        // 公开属性
        public string TargetDevice { get; private set; } = "";

        // 判断菜单是否打开
        public bool IsMenuOpen => _currentMenu != null && !_currentMenu.IsDisposed && _currentMenu.Visible;
        
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private bool _isWin11;

        public TaskbarForm(Settings cfg, UIController ui, MainForm mainForm)
        {
            _cfg = cfg;
            _ui = ui;
            _mainForm = mainForm;
            TargetDevice = _cfg.TaskbarMonitorDevice;

            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);

            // 初始化组件
            _winHelper = new TaskbarWinHelper(this);
            _bizHelper = new TaskbarBizHelper(this, _cfg, _winHelper);

            // 窗体属性
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = false;
            DoubleBuffered = true;

            ReloadLayout();

            _bizHelper.CheckTheme(true);
            _bizHelper.FindHandles();
            
            _bizHelper.AttachToTaskbar();
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);

            // 巡检句柄、父窗口、任务栏矩形、位置和主题，与刷新间隔无关；重画由 UIController.FrameUpdated 驱动
            _timer.Interval = 1000;
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            // 鼠标悬浮提示初始化
            _tooltipHelper = new TaskbarTooltipHelper(this, _cfg, _ui);

            _ui.FrameUpdated += OnFrameUpdated;

            Tick();
        }

        public void ReloadLayout()
        {
            _layout = new HorizontalLayout(ThemeManager.Current, 300, LayoutMode.Taskbar, _cfg);
            _lastLayoutSignature = ""; // 重置签名，强制重算
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);
            _bizHelper.CheckTheme(true);

            // 更新悬浮窗模式 (支持热切换)
            _tooltipHelper?.ReloadMode();

            // 注意：这里仍然可能因为 _cols 为空而暂时不 Build，
            // 但随后的 Tick 会在获取到新数据后自动 Build
            if (_cols != null && _cols.Count > 0)
            {
                _layout.Build(_cols, _bizHelper.Height);
                Width = _layout.PanelWidth;
                _bizHelper.UpdatePlacement(Width);
            }

            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ui.FrameUpdated -= OnFrameUpdated;
                _winHelper?.RestoreTaskbar();
                _timer.Stop();
                _timer.Dispose();
                _currentMenu?.Dispose();
                _tooltipHelper?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            // [Fix] 兼容性修复：在 Win11 25H2 + StartAllBack 环境下，
            // 右键事件会穿透到原生任务栏。
            // 因此不再区分系统版本，统一拦截右键按下和抬起消息。
            if (m.Msg == WM_RBUTTONDOWN)
            {
                return; // 吞掉按下事件，防止穿透
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                this.BeginInvoke(new Action(ShowContextMenu));
                return; 
            }

            // [Fix] 强制拦截双击事件
            // 当悬浮窗(Tooltip)显示时，WinForms 的标准双击事件可能因为焦点/激活状态的微妙变化而失效。
            // 这里直接在消息层处理 WM_LBUTTONDBLCLK，确保双击动作始终能被触发。
            if (m.Msg == WM_LBUTTONDBLCLK)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
                return;
            }

            base.WndProc(ref m);
        }

        private void ShowContextMenu()
        {
            if (_currentMenu != null)
            {
                _currentMenu.Dispose();
                _currentMenu = null;
            }

            _currentMenu = MenuManager.Build(_mainForm, _cfg, _ui, "Taskbar");
            
            TaskbarWinHelper.ActivateWindow(this.Handle);
            _currentMenu.Show(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
            }
        }

        private void Tick()
        {
            // [Fix] 周期性检查句柄，防止 Explorer 重启后句柄失效
            // 优化：仅在重试期或句柄无效时调用 FindHandles，且限制调用频率
            bool isHandleInvalid = !_bizHelper.IsTaskbarValid();
            
            // 如果处于重试期，或者句柄无效且距离上次查找超过2秒(防止无Explorer时高频空转)
            if (isHandleInvalid && (DateTime.Now - _lastFindHandleTime).TotalSeconds > 2)
            {
                _bizHelper.FindHandles();
                _lastFindHandleTime = DateTime.Now;
            }

            // 按真实时间每 5 秒一次，原来的取模节流在刷新间隔大于 1 秒时几乎每次都读注册表
            long now = Environment.TickCount64;
            if (now - _lastThemeCheckTick >= 5000)
            {
                _lastThemeCheckTick = now;
                _bizHelper.CheckTheme();
            }

            RefreshView(patrol: true);
        }

        private void OnFrameUpdated(bool isDataTick)
        {
            RefreshView(patrol: false);

            // 悬浮提示读的是数值，只在数据刷新后更新；动画帧不刷新提示框
            if (isDataTick) _tooltipHelper.UpdateContent();
        }

        private void RefreshView(bool patrol)
        {
            // [Fix Part 1] 防空数据保护
            // 使用临时变量接收，先判断数据有效性，再赋值给成员变量 _cols
            // 防止在 UI 重建期间(RebuildLayout)获取到空列表导致任务栏闪烁或清空
            var nextCols = _ui.GetTaskbarColumns();
            if (nextCols == null || nextCols.Count == 0) return;
            
            _cols = nextCols; // 确认有效后再更新引用

            // 任务栏矩形只在巡检时查，动画帧用缓存的高度
            if (patrol) _bizHelper.UpdateTaskbarRect();

            bool layoutChanged = false;

            // [Fix Part 2] 布局变更检测
            if (_bizHelper.IsVertical())
            {
                // 垂直模式逻辑简单且无测量开销，直接重算即可
                var before = Size;
                _bizHelper.BuildVerticalLayout(_cols);
                _lastLayoutSignature = "vertical";
                layoutChanged = Size != before;
            }
            else
            {
                // [优化] 智能判断更新条件
                // 1. 必须检查：如果列表是新生成的（坐标还没算过，Bounds为空），必须重算！
                // 这解决了“主界面显隐导致任务栏消失”的问题
                bool isUninitialized = (_cols.Count > 0 && _cols[0].Bounds.IsEmpty);

                // 2. 常规检查：如果内容长度/结构变了（签名变了），也要重算
                string currentSig = _layout.GetLayoutSignature(_cols) + "_" + _bizHelper.Height;
                bool isContentChanged = (currentSig != _lastLayoutSignature);

                if (isUninitialized || isContentChanged)
                {
                    _layout.Build(_cols, _bizHelper.Height);
                    Width = _layout.PanelWidth;
                    Height = _bizHelper.Height;
                    
                    _lastLayoutSignature = currentSig;
                    layoutChanged = true;
                }
            }

            // 巡检每秒至少定位一次，SetWindowPosMemo 的 5 秒强制重设依赖这一点；宽度变化时立即定位
            if (patrol || layoutChanged) _bizHelper.UpdatePlacement(Width);

            int sig = ComputePaintSignature();
            if (layoutChanged || sig != _lastPaintSignature)
            {
                _lastPaintSignature = sig;
                Invalidate();
            }
        }

        // 与 TaskbarRenderer 绘制时读取的量一致，签名不变则像素不变
        private int ComputePaintSignature()
        {
            if (_cols == null) return 0;

            var h = new HashCode();
            h.Add(_bizHelper.LastIsLightTheme);

            foreach (var col in _cols)
            {
                AddItemToSignature(ref h, col.Top);
                AddItemToSignature(ref h, col.Bottom);
            }

            return h.ToHashCode();
        }

        private static void AddItemToSignature(ref HashCode h, MetricItem? item)
        {
            if (item == null) return;

            // GetFormattedText 会更新 CachedColorState，必须先调用
            string text = item.GetFormattedText(true);
            h.Add(item.ShortLabel);
            h.Add(text);
            h.Add(item.CachedColorState);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_bizHelper.TransparentKey);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 调试验证用：如果消失时出现了一个红块，说明 OnPaint 被调用但 _cols 为空
            // e.Graphics.FillRectangle(Brushes.Red, 0, 0, 20, 20); 

            if (_cols == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TaskbarRenderer.Render(g, _cols, _bizHelper.LastIsLightTheme);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (防止点击激活窗口，避免抢占焦点)
                if (_cfg != null && _cfg.TaskbarClickThrough)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                }
                return cp;
            }
        }
    }
}
