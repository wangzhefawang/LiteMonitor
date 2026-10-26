using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.SystemServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class UIController : IDisposable
    {
        private readonly Settings _cfg;
        private readonly Form _form;
        private readonly HardwareMonitor _mon;
        private readonly System.Windows.Forms.Timer _timer;

        // 数值动画独立于数据刷新：有项未收敛时才运行，用真实帧间隔算系数
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Diagnostics.Stopwatch _animClock = new();

        private UILayout _layout;
        private bool _layoutDirty = true;
        private bool _dragging = false;
        private string _lastLayoutSignature = "";
        private int _lastPanelSignature;

        private List<GroupLayoutInfo> _groups = new();
        private List<Column> _hxColsHorizontal = new();
        private List<Column> _hxColsTaskbar = new();
        private HorizontalLayout? _hxLayout;
        private Dictionary<string, DateTime> _overheatStartTimes = new Dictionary<string, DateTime>();
        public MainForm MainForm => (MainForm)_form;
        
        // 辅助判断当前渲染状态是否为横屏 (用于 AppActions 判断是否切换了模式)
        // 只有在 Render 并在横屏分支运行后 _hxLayout 才不为 null
        // 竖屏模式下 ApplyTheme 会将其重置为 null
        public bool IsLayoutHorizontal => _hxLayout != null;

        public List<Column> GetTaskbarColumns() => _hxColsTaskbar;
        public List<GroupLayoutInfo> GetMainGroups() => _groups;

        // 每次数据刷新（true）和动画帧（false）收尾时在 UI 线程触发，任务栏据此重排和重绘
        public event Action<bool>? FrameUpdated;

        public UIController(Settings cfg, Form form)
        {
            _cfg = cfg;
            _form = form;
            _mon = new HardwareMonitor(cfg);

            _layout = new UILayout(ThemeManager.Current);

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(80, _cfg.RefreshMs) };
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            _animTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _animTimer.Tick += (_, __) => AnimateFrame();

            ApplyTheme(_cfg.Skin);
        }

        public float GetCurrentDpiScale()
        {
            using (Graphics g = _form.CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        public void ApplyTheme(string name, bool retainData = false)
        {
            // 1. 先保留旧主题的引用 (为了稍后释放)
            var oldTheme = ThemeManager.Current;

            // 2. 清理全局画刷缓存 (这不会影响 ThemeManager 的字体了，因为解耦了)
            UIRenderer.ClearCache();
            UIUtils.ClearBrushCache();

            // 3. 加载新主题 (Current 指向新对象，包含全新的字体)
            // 如果主题名相同且要求保留数据，可以考虑跳过 Load，但为了应用 Scale 还是重新 Load 比较稳妥
            // 或者优化：ThemeManager.Load 内部判断是否已加载
            ThemeManager.Load(name);
            var t = ThemeManager.Current;

            // 4. 安全释放旧主题的字体
            // 此时 Current 已经是新主题了，Paint 事件只会用新字体，所以释放旧的是安全的
            if (oldTheme != null && oldTheme != t)
            {
                oldTheme.DisposeFonts();
            }

            // ... 后续缩放逻辑保持不变 ...
            float dpiScale = GetCurrentDpiScale();   
            float userScale = (float)_cfg.UIScale;    
            float finalScale = dpiScale * userScale;

            t.Scale(dpiScale, userScale); // Scale 内部现在会自动清理旧缩放字体

            // ... 边距修复逻辑 ...
            if (!_cfg.HorizontalMode)
            {
                t.Layout.Width = (int)(_cfg.PanelWidth * finalScale);
                _form.ClientSize = new Size(t.Layout.Width, _form.ClientSize.Height);
            }

            TaskbarRenderer.ReloadStyle(_cfg);

            _layout = new UILayout(t);
            _hxLayout = null;

            if (!retainData)
            {
                BuildMetrics();
                BuildHorizontalColumns();
            }
            else
            {
                // [Safety Check] Even if retaining data, ensure we have content.
                // This handles cases where we switch modes but data wasn't built for that mode yet.
                if (_groups.Count == 0) BuildMetrics();
                if (_hxColsHorizontal.Count == 0) BuildHorizontalColumns();
            }
            
            _layoutDirty = true;

            _form.BackColor = ThemeManager.ParseColor(t.Color.Background);

            _timer.Interval = Math.Max(80, _cfg.RefreshMs);
            _form.Invalidate();
            _form.Update();
        }

        public void RebuildLayout()
        {
            BuildMetrics();
            BuildHorizontalColumns(); 
            _layoutDirty = true;
            _form.Invalidate();
            _form.Update();
        }

        public void SetDragging(bool dragging) => _dragging = dragging;

        public void Render(Graphics g)
        {
            var t = ThemeManager.Current;
            _layout ??= new UILayout(t);

            // === 横屏模式 ===
            if (_cfg.HorizontalMode)
            {
                _hxLayout ??= new HorizontalLayout(t, _form.Width, LayoutMode.Horizontal);

                // [通用方案] 动态检测是否需要重新计算布局
                // 移至 Tick() 中低频检查，避免 Render 高频调用
                if (_layoutDirty)
                {
                    int h = _hxLayout.Build(_hxColsHorizontal);
                    // 同样建议横屏模式也使用 ClientSize
                    // _form.Width = ... 
                    // _form.Height = h;
                    _form.ClientSize = new Size(_hxLayout.PanelWidth, h);
                    _layoutDirty = false;
                }
                HorizontalRenderer.Render(g, t, _hxColsHorizontal, _hxLayout.PanelWidth);
                return;
            }

            // === 竖屏模式 ===
            if (_layoutDirty)
            {
                int h = _layout.Build(_groups);
                // [修复2补充] 设置高度时也使用 ClientSize，确保高度精准
                _form.ClientSize = new Size(_form.ClientSize.Width, h);
                _layoutDirty = false;
            }

            // === [关键修改] 获取布局中的看板信息 ===
            // var dashboard = _layout.Dashboard; // 已移除
            // ==========================================

            UIRenderer.Render(g, _groups, t);
        }

        private bool _busy = false;

        private async void Tick()
        {
            if (_dragging || _busy) return;
            _busy = true;

            try
            {
                await Task.Run(() => _mon.UpdateAll());

                // 先驱动 InfoService，下面读到的看板文本就属于本轮刷新（时钟类文本不再落后一个刷新周期）
                InfoService.Instance.Update();

                // ① 更新竖屏 items（只设目标值，平滑由动画定时器推进）
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                    {
                        // [新增] Dashboard 实时更新
                        if (it.IsDash)
                        {
                            it.TextValue = InfoService.Instance.GetValue(it.DashKey!);
                        }
                        else
                        {
                            it.Value = _mon.Get(it.Key);
                        }
                    }

                // ② 更新横版 / 任务栏 (清理了冗余代码)
                void UpdateCol(Column col)
                {
                    void UpdateItem(MetricItem it)
                    {
                        if (it == null) return;

                        // [新增] Dashboard 实时更新 (横版/任务栏)
                        if (it.IsDash)
                        {
                            it.TextValue = InfoService.Instance.GetValue(it.DashKey!);
                        }
                        else
                        {
                            it.Value = _mon.Get(it.Key);
                        }
                    }
                    UpdateItem(col.Top);
                    UpdateItem(col.Bottom);
                }
                
                foreach (var col in _hxColsHorizontal) UpdateCol(col);
                foreach (var col in _hxColsTaskbar) UpdateCol(col);

                HardwareHistoryLogger.RecordSnapshot(_cfg, key => _mon.Get(key));

                // 最大值记录去抖保存：在 UI 线程、硬件锁之外落盘，同一份变更最多每 30 秒一次
                if (_cfg.MaxRecordDirty && (DateTime.Now - _cfg.LastAutoSaveTime).TotalSeconds > 30)
                {
                    _cfg.MaxRecordDirty = false;
                    _cfg.LastAutoSaveTime = DateTime.Now;
                    _cfg.Save();
                }

                CheckTemperatureAlert();

                // 目标值已更新：需要时启动动画，再做布局/重绘收尾
                StartAnimationIfNeeded();
                FinishFrame(true);
            }
            catch (Exception ex)
            {
                // async void 里的未处理异常会直接冲到全局处理器，这里记录后继续下一轮刷新
                AppLog.Warn("UI.Tick", "刷新失败", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        // 速度够高时立即吸附到目标；否则启动动画定时器，由第一帧开始推进
        // 即使所有项已收敛也可以启动，第一帧发现没有项在动就会停掉
        private void StartAnimationIfNeeded()
        {
            if (MetricItem.FrameAlpha(_cfg.AnimationSpeed, 100) >= 1)
            {
                StepAll(1);
                return;
            }

            if (!_animTimer.Enabled)
            {
                _animClock.Restart();
                _animTimer.Start();
            }
        }

        private void AnimateFrame()
        {
            try
            {
                double elapsed = _animClock.Elapsed.TotalMilliseconds;
                _animClock.Restart();
                double alpha = MetricItem.FrameAlpha(_cfg.AnimationSpeed, elapsed);

                bool moving = StepAll(alpha);
                if (!moving) _animTimer.Stop();

                FinishFrame(false);
            }
            catch (Exception ex)
            {
                _animTimer.Stop();
                AppLog.Warn("UI.Animate", "动画帧失败", ex);
            }
        }

        // 推进竖屏、横条、任务栏三套条目（悬浮提示读竖屏，任务栏读自己的列），DASH 项没有数值；返回是否仍有项在移动
        private bool StepAll(double alpha)
        {
            bool moving = false;

            foreach (var g in _groups)
                foreach (var it in g.Items)
                    if (it != null && !it.IsDash && it.TickSmooth(alpha)) moving = true;

            foreach (var col in _hxColsHorizontal)
            {
                if (col.Top != null && !col.Top.IsDash && col.Top.TickSmooth(alpha)) moving = true;
                if (col.Bottom != null && !col.Bottom.IsDash && col.Bottom.TickSmooth(alpha)) moving = true;
            }

            foreach (var col in _hxColsTaskbar)
            {
                if (col.Top != null && !col.Top.IsDash && col.Top.TickSmooth(alpha)) moving = true;
                if (col.Bottom != null && !col.Bottom.IsDash && col.Bottom.TickSmooth(alpha)) moving = true;
            }

            return moving;
        }

        // 数据刷新和动画帧共用的收尾：横屏布局检查、主面板按签名决定是否重画、通知任务栏
        private void FinishFrame(bool isDataTick)
        {
            // 只有当充电状态、隐藏标签项文本长度等导致样本变化时，才标记 Dirty
            // 每次数据刷新和动画帧之后都会检查
            if (_cfg.HorizontalMode && _hxLayout != null)
            {
                string currentLayoutSig = _hxLayout.GetLayoutSignature(_hxColsHorizontal);
                if (currentLayoutSig != _lastLayoutSignature)
                {
                    _lastLayoutSignature = currentLayoutSig;
                    _layoutDirty = true;
                }
            }

            int sig = ComputePanelSignature();
            if (_layoutDirty || sig != _lastPanelSignature)
            {
                _lastPanelSignature = sig;
                _form.Invalidate();
            }

            FrameUpdated?.Invoke(isDataTick);
        }

        // 签名覆盖绘制时读取的全部可变量（与 UIRenderer / HorizontalRenderer 一致），签名不变则像素不变
        // GetFormattedText 会顺带更新 CachedColorState / CachedPercent，必须先调它再读这两项
        private int ComputePanelSignature()
        {
            var h = new HashCode();

            if (_cfg.HorizontalMode)
            {
                foreach (var col in _hxColsHorizontal)
                {
                    var top = col.Top;
                    if (top != null)
                    {
                        h.Add(top.ShortLabel);
                        h.Add(top.GetFormattedText(true));
                        h.Add(top.CachedColorState);
                    }

                    var bottom = col.Bottom;
                    if (bottom != null)
                    {
                        h.Add(bottom.ShortLabel);
                        h.Add(bottom.GetFormattedText(true));
                        h.Add(bottom.CachedColorState);
                    }
                }

                return h.ToHashCode();
            }

            var t = ThemeManager.Current;
            bool narrow = t.Layout.Width < 240 * t.Layout.LayoutScale;

            foreach (var g in _groups)
                foreach (var it in g.Items)
                {
                    string text = it.GetFormattedText(it.Style == MetricRenderStyle.TwoColumn && narrow);

                    h.Add(it.Label);
                    h.Add(text);
                    h.Add(it.CachedColorState);
                    if (it.Style == MetricRenderStyle.StandardBar) h.Add(it.GetBarFillWidth());
                }

            return h.ToHashCode();
        }

        private void BuildMetrics()
        {
            _groups = new List<GroupLayoutInfo>();

            var activeItems = _cfg.MonitorItems
                .Where(x => x.VisibleInPanel)
                // ★★★ 终极排序逻辑 ★★★
                // 1. 先按 Group 分组，解决 "分裂问题" (物理聚类)
                .GroupBy(x => x.UIGroup)
                // 2. 组间排序：使用组内最小的 SortIndex 决定组的位置 (保留用户的整体排序意图)
                .OrderBy(g => g.Min(x => x.SortIndex))
                // 3. 组内排序并展平：组内按 SortIndex 排列
                .SelectMany(g => g.OrderBy(x => x.SortIndex))
                .ToList();

            if (activeItems.Count == 0) return;

            string currentGroupKey = "";
            List<MetricItem> currentGroupList = new List<MetricItem>();

            foreach (var cfgItem in activeItems)
            {
                string groupKey = cfgItem.UIGroup;

                if (groupKey != currentGroupKey && currentGroupList.Count > 0)
                {
                    var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                    string gName = LanguageManager.T(UIUtils.Intern("Groups." + currentGroupKey));
                    if (_cfg.GroupAliases.ContainsKey(currentGroupKey)) gName = _cfg.GroupAliases[currentGroupKey];
                    
                    gr.Label = gName;
                    _groups.Add(gr);
                    currentGroupList = new List<MetricItem>();
                }

                currentGroupKey = groupKey;

                string label = LanguageManager.T(UIUtils.Intern("Items." + cfgItem.Key));
                var item = new MetricItem 
                { 
                    Key = cfgItem.Key, 
                    BoundConfig = cfgItem 
                };
                
                // [修复] 初始化默认 Label (作为 Fallback)
                // 如果 BoundConfig.UserLabel 有值，Getter 会优先返回它
                item.Label = label;

                string defShort = LanguageManager.T(UIUtils.Intern("Short." + cfgItem.Key));
                item.ShortLabel = defShort;

                
                // ★★★ [新增] Dashboard 数据源绑定 ★★★
                if (cfgItem.Key.StartsWith("DASH."))
                {
                     string dashKey = cfgItem.Key.Substring(5); // DASH.HOST -> HOST
                     
                     // 直接从服务获取值，不再依赖 WidgetItem 对象
                     string val = InfoService.Instance.GetValue(dashKey);
                     item.TextValue = val;
                     item.Value = null; // Dashboard 项没有数值，只有文本
                }
                else
                {
                    float? val = _mon.Get(item.Key);
                    item.Value = val;
                    if (val.HasValue) item.DisplayValue = val.Value;
                }

                currentGroupList.Add(item);
            }

            if (currentGroupList.Count > 0)
            {
                var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                string gName = LanguageManager.T(UIUtils.Intern("Groups." + currentGroupKey));
                 if (_cfg.GroupAliases.ContainsKey(currentGroupKey)) gName = _cfg.GroupAliases[currentGroupKey];
                
                gr.Label = gName;
                _groups.Add(gr);
            }
        }

        private void BuildHorizontalColumns()
        {
            _hxColsHorizontal = BuildColumnsCore(forTaskbar: false);
            _hxColsTaskbar = BuildColumnsCore(forTaskbar: true);
        }

        private List<Column> BuildColumnsCore(bool forTaskbar)
        {
            var cols = new List<Column>();

            // 1. 筛选
            var query = _cfg.MonitorItems
                .Where(x => forTaskbar ? x.VisibleInTaskbar : x.VisibleInPanel);

            // 2. 排序 (优化：先按组聚类，防止新插件跑到末尾)
            bool useTaskbarSort = forTaskbar || _cfg.HorizontalFollowsTaskbar;
            List<MonitorItemConfig> items;

            if (useTaskbarSort)
            {
                // [Taskbar Mode] Allow cross-group sorting (Flat Sort)
                // 用户要求：任务栏排序不受分组约束，可以跨组混排
                items = query
                    .OrderBy(item => item.TaskbarSortIndex)
                    .ToList();
            }
            else
            {
                // [Panel Mode] Enforce grouping (Grouped Sort)
                // 面板模式默认保持分组聚合的视觉习惯
                items = query
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(item => item.SortIndex))
                    .SelectMany(g => g.OrderBy(item => item.SortIndex))
                    .ToList();
            }

            bool singleLine = (forTaskbar && _cfg.TaskbarSingleLine) || 
                              (!forTaskbar && _cfg.HorizontalMode && _cfg.HorizontalSingleLine);
            int step = singleLine ? 1 : 2;

            for (int i = 0; i < items.Count; i += step)
            {
                var col = new Column();
                col.Top = CreateMetric(items[i]);

                if (!singleLine && i + 1 < items.Count)
                {
                    col.Bottom = CreateMetric(items[i + 1]);
                }
                cols.Add(col);
            }

            return cols;
        }

        private MetricItem CreateMetric(MonitorItemConfig cfg)
        {
            var item = new MetricItem 
            { 
                Key = cfg.Key,
                BoundConfig = cfg // [核心修复] 绑定 Config
            };
            
            // Standard initialization
            // 优先使用用户自定义的 Label/ShortLabel
            // 如果用户未定义 (null/empty)，则使用语言包中的默认值
            // 如果用户定义了 " " (空格)，则保留空格 (意味着隐藏标签)
            
            string defLabel = LanguageManager.T(UIUtils.Intern("Items." + cfg.Key));
            item.Label = defLabel; // 设为默认值，BoundConfig 会负责处理覆盖

            string defShort = LanguageManager.T(UIUtils.Intern("Short." + cfg.Key));
            item.ShortLabel = defShort;


            // [新增] Dashboard 数据源绑定
            if (cfg.Key.StartsWith("DASH."))
            {
                 string dashKey = cfg.Key.Substring(5);
                 // 立即尝试获取一次，如果是 Loading 且 NetworkManager 有缓存，则直接更新
                 // 避免异步等待导致的 "?"
                 if (dashKey == "IP")
                 {
                      string cachedIP = HardwareMonitor.Instance?.GetNetworkIP();
                      if (!string.IsNullOrEmpty(cachedIP) && cachedIP != "?")
                      {
                           InfoService.Instance.InjectIP(cachedIP);
                      }
                 }
                 
                 string val = InfoService.Instance.GetValue(dashKey);   
                 item.TextValue = val;
                 item.Value = null;
            }
            else
            {
                InitMetricValue(item);
            }
            
            return item;
        }

        private void InitMetricValue(MetricItem? item)
        {
            if (item == null) return;
            float? val = _mon.Get(item.Key);
            item.Value = val;
            if (val.HasValue) item.DisplayValue = val.Value;
        }
        
        private void CheckTemperatureAlert()
        {
            if (!_cfg.AlertTempEnabled) 
            {
                _overheatStartTimes.Clear();
                return;
            }

            if ((DateTime.Now - _cfg.LastAlertTime).TotalMinutes < 3) return;

            int globalThreshold = _cfg.AlertTempThreshold; 
            int diskThreshold = Math.Min(globalThreshold - 20, 60); 

            List<string> alertLines = new List<string>();
            string alertTitle = LanguageManager.T("Menu.AlertTemp"); 

            void Check(string key, float? val, int threshold, string label, string msgSuffix = "")
            {
                if (val.HasValue && val.Value >= threshold)
                {
                    if (!_overheatStartTimes.ContainsKey(key))
                        _overheatStartTimes[key] = DateTime.Now;
                    
                    if ((DateTime.Now - _overheatStartTimes[key]).TotalSeconds >= 5)
                    {
                        alertLines.Add($"{label} {alertTitle}: 🔥{val:F0}°C{msgSuffix}");
                    }
                }
                else
                {
                    if (_overheatStartTimes.ContainsKey(key))
                        _overheatStartTimes.Remove(key);
                }
            }

            Check("CPU.Temp", _mon.Get("CPU.Temp"), globalThreshold, LanguageManager.T("Short.CPU.Temp"));
            Check("GPU.Temp", _mon.Get("GPU.Temp"), globalThreshold, LanguageManager.T("Short.GPU.Temp"));
            Check("MOBO.Temp", _mon.Get("MOBO.Temp"), globalThreshold, LanguageManager.T("Short.MOBO.Temp"));
            Check("DISK.Temp", _mon.Get("DISK.Temp"), diskThreshold, LanguageManager.T("Short.DISK.Temp"), $" (>{diskThreshold}°C)");

            if (alertLines.Count > 0)
            {
                string thresholdText = (alertLines.Count == 1 && alertLines[0].StartsWith("DISK")) 
                    ? $"(>{diskThreshold}°C)" 
                    : $"(>{globalThreshold}°C)";

                alertTitle += $" {thresholdText}";
                string bodyText = string.Join("\n", alertLines);
                
                ((MainForm)_form).ShowNotification(alertTitle, bodyText, ToolTipIcon.Warning);
                _cfg.LastAlertTime = DateTime.Now;
            }
        }
        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _animTimer.Stop();
            _animTimer.Dispose();
            _mon.Dispose();
        }
    }
}
