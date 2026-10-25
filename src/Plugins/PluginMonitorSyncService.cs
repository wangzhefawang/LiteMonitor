using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件监控项同步服务
    /// 负责将插件配置同步到全局 MonitorItems 设置中
    /// </summary>
    public class PluginMonitorSyncService
    {
        private static PluginMonitorSyncService _instance;
        public static PluginMonitorSyncService Instance => _instance ??= new PluginMonitorSyncService();

        private PluginMonitorSyncService() { }

        /// <summary>
        /// 同步指定插件实例的监控项到全局设置
        /// </summary>
        /// <param name="inst">插件实例配置</param>
        /// <param name="tmpl">插件模板</param>
        public bool SyncMonitorItem(PluginInstanceConfig inst, PluginTemplate tmpl, bool saveIfChanged = true)
        {
            if (inst == null || tmpl == null) return false;

            // 优化 1: 正则预编译 (Static Readonly)
            // 移至类级别定义
            
            var settings = Settings.Load();
            bool changed = false;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };
            var validKeys = new HashSet<string>();

            // 规则：运行期不得原地增删已发布的 MonitorItems 列表，复制后整体替换（其他线程可能正在枚举它）
            // 已存在项的属性（SortIndex、DynamicLabel 等）仍在共享对象上原地修改
            var items = new List<MonitorItemConfig>(settings.MonitorItems);
            bool structureChanged = false;

            for (int i = 0; i < targets.Count; i++)
            {
                var targetInputs = targets[i];
                var mergedInputs = new Dictionary<string, string>(inst.InputValues);

                // Merge Target Inputs
                foreach (var kv in targetInputs) mergedInputs[kv.Key] = kv.Value;

                // Apply Defaults
                if (tmpl.Inputs != null)
                {
                    foreach (var input in tmpl.Inputs)
                    {
                        if (input.DefaultValue != null && (!mergedInputs.ContainsKey(input.Key) || string.IsNullOrEmpty(mergedInputs[input.Key])))
                        {
                            mergedInputs[input.Key] = input.DefaultValue;
                        }
                    }
                }

                string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{i}" : "";

                if (tmpl.Outputs != null)
                {
                    foreach (var output in tmpl.Outputs)
                    {
                        string itemKey = PluginConstants.DASH_PREFIX + inst.Id + keySuffix + "." + output.Key;
                        validKeys.Add(itemKey);

                        // 注入 Loading 状态 (仅当当前无值时)
                        if (string.IsNullOrEmpty(InfoService.Instance.GetValue(itemKey)))
                        {
                            InfoService.Instance.InjectValue(itemKey, PluginConstants.STATUS_LOADING);
                        }
                        // ★★★ [新增] 顺便注入默认单位，这样 MetricUtils 就能查到了 ★★★
                        InfoService.Instance.InjectValue(itemKey + ".Unit", output.Unit);

                        var item = items.FirstOrDefault(x => x.Key == itemKey);

                        string labelPattern = output.Label;
                        if (string.IsNullOrEmpty(labelPattern)) labelPattern = (tmpl.Meta.Name) + " " + output.Key;

                        string finalName = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);
                        string finalShort = PluginProcessor.ResolveTemplate(output.ShortLabel, mergedInputs);

                        if (item == null)
                        {
                            // Create New Item
                            string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (tmpl.Meta.Name + " " + output.Key);
                            string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                            // [Optimization] Smart Insert: Follow last sibling based on SortIndex
                            string prefix = PluginConstants.DASH_PREFIX + inst.Id + ".";
                            var siblings = items.Where(x => x.Key.StartsWith(prefix)).ToList();

                            var lastSortPeer = siblings.OrderByDescending(x => x.SortIndex).FirstOrDefault();
                            var lastTbSortPeer = siblings.OrderByDescending(x => x.TaskbarSortIndex).FirstOrDefault();

                            int nextSort = (lastSortPeer?.SortIndex ?? (items.Any() ? items.Max(x => x.SortIndex) : 0)) + 1;
                            int nextTbSort = (lastTbSortPeer?.TaskbarSortIndex ?? (items.Any() ? items.Max(x => x.TaskbarSortIndex) : 0)) + 1;

                            // Shift others to make room
                            foreach (var m in items)
                            {
                                if (m.SortIndex >= nextSort) m.SortIndex++;
                                if (m.TaskbarSortIndex >= nextTbSort) m.TaskbarSortIndex++;
                            }

                            item = new MonitorItemConfig
                            {
                                Key = itemKey,
                                UserLabel = "", // Auto mode
                                DynamicLabel = safeLabel,
                                TaskbarLabel = "", // Auto mode
                                DynamicTaskbarLabel = safeShort,
                                UnitPanel = output.Unit,
                                UnitTaskbar = output.Unit, // [Fix] Sync Unit to Taskbar as well
                                VisibleInPanel = true,
                                SortIndex = nextSort,
                                TaskbarSortIndex = nextTbSort,
                            };
                            items.Add(item);
                            structureChanged = true;
                            changed = true;
                        }
                        else
                        {
                            // Update Existing Item
                            string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (tmpl.Meta.Name + " " + output.Key);
                            string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                            if (item.DynamicLabel != safeLabel) { item.DynamicLabel = safeLabel; changed = true; }
                            if (item.DynamicTaskbarLabel != safeShort) { item.DynamicTaskbarLabel = safeShort; changed = true; }
                            // ★★★ [建议] 注释掉下面这两行，防止重启后强制覆盖用户的设置 ★★★
                            // if (item.UnitPanel != output.Unit) { item.UnitPanel = output.Unit; changed = true; }
                            // if (item.UnitTaskbar != output.Unit) { item.UnitTaskbar = output.Unit; changed = true; } // [Fix] Sync Unit to Taskbar as well
                        }
                    }
                }
            }

            // Cleanup Orphaned Items for this Instance
            var toRemove = items
                .Where(x => x.Key.StartsWith(PluginConstants.DASH_PREFIX + inst.Id + ".") && !validKeys.Contains(x.Key))
                .ToList();

            foreach (var item in toRemove)
            {
                items.Remove(item);
                structureChanged = true;
                changed = true;
            }

            if (structureChanged) settings.MonitorItems = items;

            if (changed && saveIfChanged) settings.Save();
            return changed;
        }

        /// <summary>
        /// 清理指定插件实例的所有监控项
        /// </summary>
        public void RemoveMonitorItems(string instanceId, bool saveIfChanged = true)
        {
            var settings = Settings.Load();
            string mainKey = PluginConstants.DASH_PREFIX + instanceId;
            bool IsMatch(MonitorItemConfig x) => x.Key == mainKey || x.Key.StartsWith(mainKey + ".");

            if (settings.MonitorItems.Any(IsMatch))
            {
                // 写时复制：过滤出新列表后整体替换，不原地删除已发布的列表
                settings.MonitorItems = settings.MonitorItems.Where(x => !IsMatch(x)).ToList();
                if (saveIfChanged) settings.Save();
            }
        }

        /// <summary>
        /// 尝试根据 Key 反向推断 Label (UI 辅助方法)
        /// </summary>
        public string TryGetSmartLabel(string itemKey, List<PluginTemplate> templates, string targetField = "label")
        {
            try
            {
                if (string.IsNullOrEmpty(itemKey) || !itemKey.StartsWith(PluginConstants.DASH_PREFIX)) return "";

                var settings = Settings.Load();

                foreach (var inst in settings.PluginInstances)
                {
                    string prefix = PluginConstants.DASH_PREFIX + inst.Id + ".";
                    if (itemKey.StartsWith(prefix) || itemKey == PluginConstants.DASH_PREFIX + inst.Id)
                    {
                        var tmpl = templates.FirstOrDefault(t => t.Id == inst.TemplateId);
                        if (tmpl == null) continue;

                        string suffix = itemKey.Substring(prefix.Length);
                        
                        if (tmpl.Outputs != null)
                        {
                            foreach (var output in tmpl.Outputs)
                            {
                                if (suffix == output.Key || suffix.EndsWith("." + output.Key))
                                {
                                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                                    if (tmpl.Inputs != null)
                                    {
                                        foreach (var input in tmpl.Inputs)
                                            if (!mergedInputs.ContainsKey(input.Key))
                                                mergedInputs[input.Key] = input.DefaultValue;
                                    }

                                    string labelPattern;
                                    if (targetField == "short_label")
                                        labelPattern = output.ShortLabel;
                                    else
                                        labelPattern = output.Label;

                                    if (string.IsNullOrEmpty(labelPattern)) labelPattern = tmpl.Meta.Name;

                                    string resolved = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);

                                    if (string.IsNullOrEmpty(resolved) || resolved.Contains("{{"))
                                        return tmpl.Meta.Name + " " + output.Key;

                                    return resolved;
                                }
                            }
                        }
                        return tmpl.Meta.Name;
                    }
                }
            }
            catch { }
            return "";
        }

        private static readonly Regex _varsRegex = new Regex(@"\{\{(.+?)\}\}", RegexOptions.Compiled);

        private bool CanResolveAllVars(string pattern, Dictionary<string, string> inputs)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (!pattern.Contains("{{")) return true;

            bool success = true;
            var matches = _varsRegex.Matches(pattern);
            foreach (Match m in matches)
            {
                string content = m.Groups[1].Value.Trim();
                bool resolved = false;

                if (content.Contains("??"))
                {
                    var parts = content.Split(new[] { "??" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string key = part.Trim();
                        if (inputs.TryGetValue(key, out string val) && !string.IsNullOrEmpty(val))
                        {
                            resolved = true;
                            break;
                        }
                    }
                }
                else
                {
                    if (inputs.ContainsKey(content) && !string.IsNullOrEmpty(inputs[content])) resolved = true;
                }

                if (!resolved)
                {
                    success = false;
                    break;
                }
            }
            return success;
        }
    }
}
