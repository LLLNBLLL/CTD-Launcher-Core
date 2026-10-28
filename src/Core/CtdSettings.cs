using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CTD.Core
{
    public enum AccountType { Offline, Authlib, Microsoft }

    public class Account
    {
        public string Type { get; set; } = "offline";
        public string Name { get; set; } = "Player";
        public string Uuid { get; set; } = "";
        public string AccessToken { get; set; } = "";
        /// <summary>外置登录的 Yggdrasil API 根地址</summary>
        public string ServerUrl { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        /// <summary>微软 access token 的过期时间（本地时间）；用于启动前判断是否需要刷新。</summary>
        public DateTime TokenExpiresAt { get; set; }
        /// <summary>刷新失败后的退避时间，避免每次启动都卡在同一个网络错误上。</summary>
        public DateTime TokenRefreshAfter { get; set; }
        public string SkinModel { get; set; } = "steve";
        /// <summary>这个账户用的离线皮肤 id（data\skins\ 下的文件名，不含 .png）；空 = 官方默认皮肤。</summary>
        public string SkinId { get; set; } = "";
        /// <summary>已同步到 Mojang 的正版皮肤 id/模型；用来避免每次启动重复上传。</summary>
        public string UploadedSkinId { get; set; } = "";
        public string UploadedSkinModel { get; set; } = "";
        public DateTime LastLogin { get; set; }

        [JsonIgnore]
        public AccountType Kind
        {
            get
            {
                if (string.Equals(Type, "authlib", StringComparison.OrdinalIgnoreCase)) return AccountType.Authlib;
                if (string.Equals(Type, "msa", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Type, "microsoft", StringComparison.OrdinalIgnoreCase)) return AccountType.Microsoft;
                return AccountType.Offline;
            }
        }

        [JsonIgnore]
        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case AccountType.Authlib: return "外置登录";
                    case AccountType.Microsoft: return "微软账号";
                    default: return "离线";
                }
            }
        }

        public Account Clone() { return (Account)MemberwiseClone(); }
    }

    public class CtdSettings
    {
        // ---- 游戏 ----
        public string GameDir { get; set; } = Path.Combine(CtdPaths.Root, ".minecraft");
        public string SelectedVersionId { get; set; } = "";
        public string Name { get; set; } = "";
        public int MemoryMb { get; set; } = 4096;
        public bool AutoMemory { get; set; } = true;
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public bool Fullscreen { get; set; } = false;
        public bool VersionIsolation { get; set; } = true;
        public string JvmArgs { get; set; } = "";
        public string GameArgs { get; set; } = "";
        public bool CloseAfterLaunch { get; set; } = false;
        public bool VerifyBeforeLaunch { get; set; } = true;
        /// <summary>游戏启动加速：默认关闭；开启后追加 JVM 参数，并为模组版本准备性能组件。</summary>
        public bool GameAcceleration { get; set; } = false;
        /// <summary>服务器防误封模式：开启后本次启动不加载任何 CTD 游戏内功能，但不改变下面各开关的值。</summary>
        public bool ServerSafeMode { get; set; } = false;
        /// <summary>不等待启动前的 IMBlocker 检查/下载，先进游戏，后台继续。</summary>
        public bool SkipImeWait { get; set; } = false;
        /// <summary>启动时跳过离线皮肤 jar 写入，本次可能显示旧皮肤。</summary>
        public bool DeferSkin { get; set; } = false;
        /// <summary>JVM 快速启动：-XX:TieredStopAtLevel=1，启动快但长时间性能下降。</summary>
        public bool FastJit { get; set; } = false;
        /// <summary>跳过启动前的 mods 依赖/未完成下载检查，缺前置时可能直接崩溃。</summary>
        public bool SkipModCheck { get; set; } = false;
        /// <summary>跳过字节码校验（-Xverify:none），高风险，可能被新 Java 忽略。</summary>
        public bool XverifyNone { get; set; } = false;
        /// <summary>版本启动缓存：后台为所有版本预生成版本装配/classpath/natives 缓存，启动时直接读取。</summary>
        public bool VersionStartupCache { get; set; } = false;
        /// <summary>激进渲染裁剪：默认关闭，关闭时只裁 96 格外的实体、不做方块实体裁剪。</summary>
        public bool AccelAggressive { get; set; } = false;

        /// <summary>C 键平滑放大：CTD 通用模块，不依赖模组加载器。</summary>
        public bool CKeyZoom { get; set; } = false;

        /// <summary>同一台机器/同一个 Java 实测出来的预估加速百分比；-1 = 还没测过。</summary>
        public int AccelPercent { get; set; } = -1;
        /// <summary>缓存键（Java 路径 + JVM 参数），变了就重新测。</summary>
        public string AccelPercentKey { get; set; } = "";
        public DateTime AccelPercentAt { get; set; }

        // ===== 进游戏时顺手把游戏内设置改好 =====
        /// <summary>启动前把游戏语言设为简体中文（写 options.txt 的 lang:zh_cn）。</summary>
        public bool AutoChinese { get; set; } = true;
        /// <summary>启动前把游戏窗口大小/全屏写进 options.txt。</summary>
        public bool AutoWindow { get; set; } = false;
        /// <summary>进游戏后自动连接到的服务器地址（写 options.txt 的 server-ip）。</summary>
        public string AutoServerIp { get; set; } = "";
        /// <summary>游戏内 FOV，0 表示不修改。</summary>
        public int AutoFov { get; set; } = 0;
        /// <summary>资源包里只有一个材质包时，启动前自动写进 options.txt 启用。</summary>
        public bool AutoResourcePack { get; set; } = true;
        /// <summary>防输入法冲突总开关：关闭后 IMBlocker / CTD 焦点保护都不启用。</summary>
        public bool ImeEnabled { get; set; } = true;
        /// <summary>启动前自动给当前版本装「防输入法冲突」模组（IMBlocker）。</summary>
        public bool AutoImeFixMod { get; set; } = true;
        /// <summary>使用 CTD Agent 的文本框焦点输入法保护；与 AutoImeFixMod 二选一。</summary>
        public bool CtdIme { get; set; } = false;
        /// <summary>游戏内输入法：system = 跟随系统，off = 关掉（英文），on = 启用（中文）。</summary>
        public string GameIme { get; set; } = "system";

        // ---- Java ----
        public string JavaPath { get; set; } = "";
        /// <summary>按当前版本需要自动选择 Java；关闭后固定使用 JavaPath。</summary>
        public bool AutoJava { get; set; } = true;
        public List<string> ExtraJavaPaths { get; set; } = new List<string>();
        public bool AutoDownloadJava { get; set; } = true;

        // ---- 下载 ----
        public string DownloadSource { get; set; } = "Auto";
        public int DownloadThreads { get; set; } = 8;
        public int DownloadRetry { get; set; } = 3;

        /// <summary>微软登录用的 Azure 公共客户端 ID（留空则使用内置值）</summary>
        public string MsaClientId { get; set; } = "";

        // ---- 账户 ----
        public List<Account> Accounts { get; set; } = new List<Account>();
        public int SelectedAccount { get; set; } = 0;

        // ---- 皮肤 ----
        /// <summary>全局皮肤 id：设了就所有账户都用它（覆盖各账户自己的选择）；空 = 各用各的。</summary>
        public string GlobalSkinId { get; set; } = "";

        // ---- 外观 ----
        public string ThemeName { get; set; } = "银霜";

        /// <summary>是否已经做过"银色成为默认主题"的一次性迁移。</summary>
        public bool SilverDefault { get; set; } = false;
        public bool DarkMode { get; set; } = false;
        public bool Animations { get; set; } = true;

        /// <summary>人物头像是否使用圆角（设置 → 外观）。默认打开；关闭后头像一律直角。</summary>
        public bool AvatarRoundCorner { get; set; } = true;

        // ---- 界面音效 ----
        /// <summary>启动器界面音效总开关；不包含游戏内声音。</summary>
        public bool SoundEnabled { get; set; } = true;
        /// <summary>界面音效音量 0–100；0 等同静音，但保留开关状态。</summary>
        public int SoundVolume { get; set; } = 70;

        // ---- 联网更新 ----
        /// <summary>启动时后台检查更新；更新源为空时不联网。</summary>
        public bool AutoCheckUpdates { get; set; } = true;
        /// <summary>远程更新清单 URL（HTTPS）。</summary>
        public string UpdateFeedUrl { get; set; } = "https://github.com/LLLNBLLL/CTD-Launcher-Releases/releases/latest/download/update.json";

        // ---- 统计 ----
        public Dictionary<string, DateTime> LastPlayed { get; set; } = new Dictionary<string, DateTime>();
        public int TotalLaunchCount { get; set; }
        public long TotalPlaySeconds { get; set; }

        // ================= 运行时单例 =================
        private static CtdSettings _current;
        public static CtdSettings Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        [JsonIgnore]
        public int EffectiveMemoryMb
        {
            get
            {
                if (!AutoMemory && MemoryMb > 0) return MemoryMb;
                return AutoSuggestion();
            }
        }

        public static int AutoSuggestion()
        {
            try
            {
                long totalMb = GetTotalRamMb();
                long mb = totalMb / 8;
                mb = (mb / 512) * 512;
                if (mb < 1024) mb = 1024;
                if (mb > 8192) mb = 8192;
                return (int)mb;
            }
            catch { return 4096; }
        }

        private static long GetTotalRamMb()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (var o in searcher.Get())
                    {
                        var v = o["TotalPhysicalMemory"];
                        if (v != null) return Convert.ToInt64(v) / 1024 / 1024;
                    }
                }
            }
            catch { }
            return 8192;
        }

        [JsonIgnore]
        public Account CurrentAccount
        {
            get
            {
                if (Accounts == null || Accounts.Count == 0) return new Account();
                if (SelectedAccount < 0 || SelectedAccount >= Accounts.Count) SelectedAccount = 0;
                return Accounts[SelectedAccount];
            }
        }

        // ================= 读写 =================
        public static CtdSettings Load()
        {
            CtdPaths.MigratePortableConfig();
            try
            {
                if (File.Exists(CtdPaths.ConfigFile))
                {
                    var text = File.ReadAllText(CtdPaths.ConfigFile, Encoding.UTF8);
                    var s = JsonConvert.DeserializeObject<CtdSettings>(text);
                    if (s != null)
                    {
                        s.Normalize();
                        // 账户和设置放在用户目录，复制启动器文件夹不会带走。
                        // 只在 JSON 里完全没有账号时，做一次旧注册表账号迁移。
                        if (!SuppressSave && (s.Accounts == null || s.Accounts.Count == 0) &&
                            TryLoadAccountsFromRegistry(s))
                        {
                            CtdPaths.EnsureDir(CtdPaths.UserConfigDir);
                            CtdPaths.WriteAllText(CtdPaths.ConfigFile,
                                JsonConvert.SerializeObject(s, Formatting.Indented));
                        }
                        s.Normalize();
                        return s;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("读取配置失败，尝试使用上一份备份", e);
                try
                {
                    var bak = CtdPaths.ConfigFile + ".bak";
                    if (File.Exists(CtdPaths.Long(bak)))
                    {
                        var s = JsonConvert.DeserializeObject<CtdSettings>(
                            File.ReadAllText(CtdPaths.Long(bak), Encoding.UTF8));
                        if (s != null)
                        {
                            s.Normalize();
                            return s;
                        }
                    }
                }
                catch { }
                try { File.Copy(CtdPaths.ConfigFile, CtdPaths.ConfigFile + ".broken", true); } catch { }
            }
            var fresh = new CtdSettings();
            fresh.Normalize();
            return fresh;
        }

        private void Normalize()
        {
            if (Accounts == null) Accounts = new List<Account>();
            if (ExtraJavaPaths == null) ExtraJavaPaths = new List<string>();
            if (LastPlayed == null) LastPlayed = new Dictionary<string, DateTime>();
            // 两种防输入法冲突方案互斥；旧配置哪怕两个都为 true，也以 CTD 焦点保护为准。
            if (ImeEnabled)
            {
                if (CtdIme) AutoImeFixMod = false;
                else if (!AutoImeFixMod) AutoImeFixMod = true;
            }
            // 便携化：整个启动器（游戏目录、Java、缓存、日志）都必须在 CTD 文件夹里。
            GameDir = NormalizeInsideRoot(GameDir, Path.Combine(CtdPaths.Root, ".minecraft"));
            JavaPath = NormalizeJavaPath(JavaPath);
            if (ExtraJavaPaths != null)
            {
                var kept = new List<string>();
                foreach (var p in ExtraJavaPaths)
                {
                    var n = NormalizeJavaPath(p);
                    if (!string.IsNullOrEmpty(n) && !kept.Contains(n, StringComparer.OrdinalIgnoreCase)) kept.Add(n);
                }
                ExtraJavaPaths = kept;
            }
            if (DownloadThreads < 1) DownloadThreads = 8;
            if (DownloadThreads > 32) DownloadThreads = 32;
            if (DownloadRetry < 0) DownloadRetry = 3;
            if (WindowWidth < 854) WindowWidth = 1280;
            if (WindowHeight < 480) WindowHeight = 720;
            if (MemoryMb < 512) MemoryMb = 4096;
            if (SoundVolume < 0) SoundVolume = 0;
            if (SoundVolume > 100) SoundVolume = 100;
            DedupeMicrosoftAccounts();
            if (SelectedAccount < 0) SelectedAccount = 0;
            if (string.IsNullOrEmpty(ThemeName)) ThemeName = "银霜";
            if (!SilverDefault)
            {
                ThemeName = "银霜";
                SilverDefault = true;
            }
            if (string.IsNullOrEmpty(DownloadSource)) DownloadSource = "Auto";
        }

        private void DedupeMicrosoftAccounts()
        {
            if (Accounts == null || Accounts.Count < 2) return;
            var selected = SelectedAccount >= 0 && SelectedAccount < Accounts.Count
                ? Accounts[SelectedAccount] : null;
            var kept = new List<Account>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Accounts)
            {
                if (a == null) continue;
                if (a.Kind != AccountType.Microsoft) { kept.Add(a); continue; }
                string key = !string.IsNullOrEmpty(a.Uuid) ? "u:" + a.Uuid : "n:" + (a.Name ?? "").Trim();
                int at;
                if (seen.TryGetValue(key, out at))
                {
                    if (ReferenceEquals(a, selected)) { kept[at] = a; selected = a; }
                    continue;
                }
                seen[key] = kept.Count;
                kept.Add(a);
            }
            if (kept.Count == Accounts.Count) return;
            Accounts = kept;
            SelectedAccount = selected == null ? 0 : Math.Max(0, Accounts.IndexOf(selected));
            Name = Accounts.Count > 0 ? Accounts[SelectedAccount].Name : "";
        }

        private static bool IsInsideRoot(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                var full = Path.GetFullPath(path);
                var root = Path.GetFullPath(CtdPaths.Root).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string RemapDataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                var full = Path.GetFullPath(path);
                int i = full.IndexOf(@"\.minecraft\", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                    return Path.Combine(CtdPaths.Root, ".minecraft", full.Substring(i + @"\.minecraft\".Length));
                i = full.IndexOf(@"\data\", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                    return Path.Combine(CtdPaths.Root, full.Substring(i + 1));
            }
            catch { }
            return "";
        }

        private static string NormalizeInsideRoot(string path, string fallback)
        {
            if (string.IsNullOrWhiteSpace(path)) return fallback;
            try
            {
                if (!Path.IsPathRooted(path)) path = Path.Combine(CtdPaths.Root, path);
                path = Path.GetFullPath(path);
                if (IsInsideRoot(path)) return path;
                var remapped = RemapDataPath(path);
                if (!string.IsNullOrEmpty(remapped) && IsInsideRoot(remapped)) return remapped;
            }
            catch { }
            return fallback;
        }

        private static string NormalizeJavaPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                if (!Path.IsPathRooted(path)) path = Path.Combine(CtdPaths.Root, path);
                path = Path.GetFullPath(path);
                if (IsInsideRoot(path))
                    return File.Exists(CtdPaths.Long(path)) ? path : "";
                var remapped = RemapDataPath(path);
                if (!string.IsNullOrEmpty(remapped) && IsInsideRoot(remapped) &&
                    File.Exists(CtdPaths.Long(remapped))) return remapped;
            }
            catch { }
            return "";
        }

        private int _savePending = 0;
        /// <summary>截图/自检模式禁止写配置，避免污染用户设置。</summary>
        public static bool SuppressSave { get; set; }
        private static readonly object _saveGate = new object();

        /// <summary>
        /// 是否仍是出厂默认设置。默认设置不落盘：用户端首次启动不应产生 CTD.json。
        /// LastPlayed / 启动计数 / 加速估算属于运行统计，不因为统计存在就写配置文件。
        /// SilverDefault 是旧主题迁移标记，也不参与默认判断。
        /// </summary>
        [JsonIgnore]
        public bool IsDefaultSettings
        {
            get
            {
                try
                {
                    var fresh = new CtdSettings();
                    fresh.Normalize();
                    var a = JObject.FromObject(this);
                    var b = JObject.FromObject(fresh);
                    foreach (var key in new[]
                    {
                        "SilverDefault", "LastPlayed", "TotalLaunchCount", "TotalPlaySeconds",
                        "AccelPercent", "AccelPercentKey", "AccelPercentAt"
                    })
                    {
                        a.Remove(key);
                        b.Remove(key);
                    }
                    return JToken.DeepEquals(a, b);
                }
                catch { return false; }
            }
        }

        public void Save()
        {
            if (SuppressSave) return;
            try
            {
                lock (_saveGate)
                {
                    if (IsDefaultSettings)
                    {
                        DeleteConfigFiles();
                        return;
                    }
                    // 账号、皮肤选择和所有设置写用户目录，避免复制启动器时泄露配置。
                    // 先写临时文件再替换，避免异常退出或多实例同时写时留下半个 JSON。
                    CtdPaths.EnsureDir(CtdPaths.UserConfigDir);
                    string json = null;
                    // 后台保存线程可能和 UI 修改账号列表同时发生；遇到集合变更时稍等重试，
                    // 不把“半份配置”写进磁盘。
                    for (int attempt = 0; attempt < 3 && json == null; attempt++)
                    {
                        try { json = JsonConvert.SerializeObject(this, Formatting.Indented); }
                        catch (InvalidOperationException) { Thread.Sleep(30); }
                    }
                    if (json == null)
                    {
                        Log.Warn("保存配置跳过：设置正在被修改，下一次保存会再试");
                        return;
                    }
                    var tmp = CtdPaths.ConfigFile + ".tmp";
                    CtdPaths.WriteAllText(tmp, json);
                    try
                    {
                        if (File.Exists(CtdPaths.Long(CtdPaths.ConfigFile)))
                            File.Replace(CtdPaths.Long(tmp), CtdPaths.Long(CtdPaths.ConfigFile),
                                CtdPaths.Long(CtdPaths.ConfigFile + ".bak"), true);
                        else
                            File.Move(CtdPaths.Long(tmp), CtdPaths.Long(CtdPaths.ConfigFile));
                    }
                    catch
                    {
                        CtdPaths.WriteAllText(CtdPaths.ConfigFile, json);
                        try { File.Delete(CtdPaths.Long(tmp)); } catch { }
                    }
                }
            }
            catch (Exception e) { Log.Error("保存配置失败", e); }
        }

        /// <summary>恢复默认后删除用户配置文件；没有自定义设置时不应留下 CTD.json。</summary>
        private static void DeleteConfigFiles()
        {
            foreach (var path in new[]
            {
                CtdPaths.ConfigFile,
                CtdPaths.ConfigFile + ".bak",
                CtdPaths.ConfigFile + ".tmp"
            })
            {
                try { if (File.Exists(CtdPaths.Long(path))) File.Delete(CtdPaths.Long(path)); }
                catch { }
            }
        }

        private const string RegistryPath = @"Software\CTD";

        private static bool TryLoadAccountsFromRegistry(CtdSettings s)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null) return false;
                    var raw = key.GetValue("Accounts") as string;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        var accounts = JsonConvert.DeserializeObject<List<Account>>(raw);
                        if (accounts != null) s.Accounts = accounts;
                    }
                    var selected = key.GetValue("SelectedAccount");
                    if (selected != null) s.SelectedAccount = Convert.ToInt32(selected);
                    var name = key.GetValue("Name") as string;
                    if (!string.IsNullOrEmpty(name)) s.Name = name;
                    var globalSkin = key.GetValue("GlobalSkinId") as string;
                    if (globalSkin != null) s.GlobalSkinId = globalSkin;
                    s.Normalize();
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Warn("读取注册表账号失败：" + e.Message);
                return false;
            }
        }

        /// <summary>延迟批量保存，避免频繁写盘。</summary>
        public void RequestSave()
        {
            if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
            var t = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(600);
                    Save();
                }
                finally { Interlocked.Exchange(ref _savePending, 0); }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void MarkPlayed(string versionId, TimeSpan duration)
        {
            if (LastPlayed == null) LastPlayed = new Dictionary<string, DateTime>();
            LastPlayed[versionId] = DateTime.Now;
            TotalLaunchCount++;
            TotalPlaySeconds += (long)duration.TotalSeconds;
            RequestSave();
        }

        public DateTime? GetLastPlayed(string versionId)
        {
            DateTime d;
            if (LastPlayed != null && LastPlayed.TryGetValue(versionId, out d)) return d;
            return null;
        }
    }
}
