using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core; // Added for UIUtils
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins.Native
{
    public static class CityCodeResolver
    {
        // TODO: 用户需要将生成的 city_code_v8.json 上传到 Cloudflare Pages，并替换此处的 URL
        private const string JSON_DB_URL = "https://litemonitor.cn/update/CityCode.json";
        
        // Memory Cache
        // 只在构建完整后一次性赋值；volatile 保证其他线程读到的是完整字典
        private static volatile Dictionary<string, List<List<string>>>? _db;
        private static readonly SemaphoreSlim _loadGate = new(1, 1);

        public static async Task<string> ResolveAsync(string province, string city, string district)
        {
            await EnsureDbLoadedAsync();

            // 取一次引用，整个查询只用这份快照
            var db = _db;
            if (db == null) throw new Exception("CityCode DB failed to load");

            // 1. 参数清洗
            province = (province ?? "").Replace("省", "").Replace("市", "").Replace("自治区", "").Replace("壮族", "").Replace("回族", "").Replace("维吾尔", "");
            city = (city ?? "").Trim();
            district = (district ?? "").Trim();

            if (string.IsNullOrEmpty(district) && string.IsNullOrEmpty(city))
            {
                // [Fix] Throw exception instead of returning error JSON, so that PluginExecutor can catch it and retry properly
                // Returning a valid JSON with "error" field might be parsed as success by generic JSON extractors if they don't check for "error"
                throw new ArgumentException("CityCodeResolver: Empty query parameters (province/city/district are all empty).");
            }

            // 2. 查找逻辑
            List<List<string>> candidates = null;
            string targetName = "";

            // 3.1 优先查 District
            if (!string.IsNullOrEmpty(district))
            {
                candidates = Lookup(db, district);
                targetName = district;
            }

            // 3.2 降级查 City
            if (candidates == null && !string.IsNullOrEmpty(city))
            {
                candidates = Lookup(db, city);
                targetName = city;
            }

            if (candidates == null)
            {
                // [Fix] Throw exception to trigger retry mechanism
                throw new KeyNotFoundException($"CityCodeResolver: No matching city found for query: {province} {city} {district}");
            }

            // 4. 评分排序
            List<string> bestMatch = candidates[0];
            int maxScore = -1000;

            if (candidates.Count > 1)
            {
                foreach (var item in candidates)
                {
                    // item: [code, name, province]
                    string code = item[0];
                    string name = item[1];
                    string prov = item.Count > 2 ? item[2] : "";

                    int score = 0;
                    if (!string.IsNullOrEmpty(province) && prov.Contains(province)) score += 100;
                    if (name == targetName) score += 50;
                    score -= name.Length;

                    if (score > maxScore)
                    {
                        maxScore = score;
                        bestMatch = item;
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                code = bestMatch[0],
                name = bestMatch[1],
                province = bestMatch.Count > 2 ? bestMatch[2] : ""
            });
        }

        private static List<List<string>> Lookup(Dictionary<string, List<List<string>>> db, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (db.TryGetValue(key, out var res)) return res;

            // 处理后缀
            string root = System.Text.RegularExpressions.Regex.Replace(key, "(自治州|自治县|地区|盟|市|区|县|旗)$", "");
            if (root.Length < 2 && key.Length > 1) root = key;

            if (db.TryGetValue(root, out var resRoot)) return resRoot;

            return null;
        }

        public static void ResetClient()
        {
            // 网络重置只影响 HttpClient；这里每次加载都新建 HttpClient，城市码库本身不需要丢弃。
            // 保留方法签名，避免改调用方。
        }

        private static async Task EnsureDbLoadedAsync()
        {
            if (_db != null) return;

            // 同一时刻只让一个调用方去下载；其余调用方等它结束后复查 _db
            await _loadGate.WaitAsync();
            try
            {
                if (_db != null) return;

                // [Optimization] Use SocketsHttpHandler with Proxy support and Decompression
                var handler = new SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    UseProxy = true,
                    // Use system proxy
                    Proxy = System.Net.WebRequest.GetSystemWebProxy(),
                    // [Fix] Bypass SSL validation for users with problematic certificates/proxies
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions 
                    {
                        RemoteCertificateValidationCallback = delegate { return true; }
                    }
                };

                using (var client = new HttpClient(handler))
                {
                    // 设置超时
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
                    
                    var json = await client.GetStringAsync(JSON_DB_URL);
                    var rawDb = JsonSerializer.Deserialize<Dictionary<string, List<List<string>>>>(json);

                    // [Optimization] Intern strings in the DB to reduce memory usage
                    // Many cities share the same province name, and many districts share the same city name
                    // 先建到局部变量，建完整后再一次性赋给 _db，避免其他线程读到半成品
                    var db = new Dictionary<string, List<List<string>>>(rawDb.Count);

                    foreach (var kvp in rawDb)
                    {
                        // Key (district name) can also be interned
                        string key = UIUtils.Intern(kvp.Key);
                        var lists = new List<List<string>>(kvp.Value.Count);

                        foreach (var item in kvp.Value)
                        {
                            // item: [code, name, province]
                            var newItem = new List<string>(item.Count);
                            foreach (var str in item)
                            {
                                newItem.Add(UIUtils.Intern(str));
                            }
                            lists.Add(newItem);
                        }
                        db[key] = lists;
                    }

                    _db = db;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CityCodeResolver] Load DB Failed: {ex.Message}");
                AppLog.Warn("Plugin.CityCode", "城市码库加载失败", ex);
            }
            finally
            {
                _loadGate.Release();
            }
        }
    }
}
