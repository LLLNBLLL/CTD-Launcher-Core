using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CTD.Services;
using Newtonsoft.Json.Linq;

namespace CTD.Core
{
    public sealed class UpdateInfo
    {
        public string Version = "";
        public string Notes = "";
        public string Url = "";
        public string Sha256 = "";
        public long Size;
        public List<string> Mirrors = new List<string>();
    }

    /// <summary>
    /// 启动器自更新：远程 JSON 清单 → 下载 ZIP → SHA-256 校验 → 退出后用脚本覆盖程序文件。
    /// 只更新程序文件，不碰 .minecraft、data 用户数据或外部配置。
    /// </summary>
    public static class UpdateService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly HttpClient DownloadHttp = CreateDownloadClient();
        private const string BetaFeedUrl =
            "https://github.com/LLLNBLLL/CTD-Launcher-Releases/releases/latest/download/update.json";

        private static string ResolveFeedUrl()
        {
            string configured = (CtdSettings.Current.UpdateFeedUrl ?? "").Trim();
            return string.IsNullOrEmpty(configured) ? BetaFeedUrl : configured;
        }

        private static HttpClient CreateClient()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CTD-Launcher-Updater");
            return c;
        }

        // 大文件下载单独用不超时的客户端，30 秒会把 10 MB 的包截断。
        private static HttpClient CreateDownloadClient()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
            var c = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CTD-Launcher-Updater");
            return c;
        }

        // GitHub 直连在国内经常超时；这些是公共加速前缀，只用于“取到同一个文件”。
        // 完整性仍由清单里的 sha256 保证，镜像无法替换成别的包。
        private static readonly string[] ProxyPrefixes =
        {
            "https://ghfast.top/",
            "https://gh-proxy.com/",
            "https://ghproxy.net/"
        };

        private static bool IsGitHubUrl(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                   url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<string> GetTextAsync(string url)
        {
            using (var resp = await Http.GetAsync(url).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("HTTP " + (int)resp.StatusCode);
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private sealed class ManifestFetch
        {
            public string Text = "";
            public bool ViaProxy;
        }

        /// <summary>取更新清单：先直连，失败再走镜像前缀。</summary>
        private static async Task<ManifestFetch> FetchManifestAsync(string feed)
        {
            try { return new ManifestFetch { Text = await GetTextAsync(feed).ConfigureAwait(false) }; }
            catch (Exception e) { Log.Warn("更新清单直连失败：" + e.Message); }

            if (!IsGitHubUrl(feed)) throw new InvalidOperationException("无法获取更新清单：" + feed);

            foreach (var prefix in ProxyPrefixes)
            {
                string candidate = prefix + feed;
                try
                {
                    string text = await GetTextAsync(candidate).ConfigureAwait(false);
                    Log.Info("更新清单通过镜像获取：" + prefix);
                    return new ManifestFetch { Text = text, ViaProxy = true };
                }
                catch (Exception e) { Log.Warn("更新清单镜像失败 " + prefix + "：" + e.Message); }
            }
            throw new InvalidOperationException("更新清单下载失败（直连与镜像均不可用）");
        }

        /// <summary>候选下载地址：清单 url → 清单 mirrors → GitHub 加速前缀。</summary>
        private static IEnumerable<string> CandidateUrls(UpdateInfo info)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                string u = raw.Trim();
                if (!IsAllowedUrl(u) || !seen.Add(u)) return;
                list.Add(u);
            }

            Add(info.Url);
            if (info.Mirrors != null)
                foreach (var m in info.Mirrors) Add(m);

            if (IsGitHubUrl(info.Url))
                foreach (var prefix in ProxyPrefixes) Add(prefix + info.Url);

            return list;
        }

        public static async Task AutoCheckAsync(Window owner)
        {
            if (!CtdSettings.Current.AutoCheckUpdates) return;
            if (string.IsNullOrWhiteSpace(ResolveFeedUrl())) return;
            try
            {
                var info = await CheckAsync().ConfigureAwait(true);
                if (info == null) return;
                Prompt(owner, info);
            }
            catch (Exception e) { Log.Warn("自动检查更新失败：" + e.Message); }
        }

        public static async Task CheckAndPromptAsync(Window owner)
        {
            if (string.IsNullOrWhiteSpace(ResolveFeedUrl()))
            {
                Ui.Toast("还没有配置更新源，请先在设置里填写更新清单 URL", ToastKind.Info);
                return;
            }
            try
            {
                var info = await CheckAsync().ConfigureAwait(true);
                if (info == null)
                {
                    Ui.Toast("当前已经是最新版本", ToastKind.Success);
                    return;
                }
                Prompt(owner, info);
            }
            catch (Exception e)
            {
                Ui.Error(owner, "检查更新失败", e.Message);
            }
        }

        private static void Prompt(Window owner, UpdateInfo info)
        {
            string size = info.Size > 0 ? "\n\n下载大小：" + FormatSize(info.Size) : "";
            bool go = Ui.Confirm(owner, "发现新版本",
                "当前版本：" + CtdVersion.Version + "\n" +
                "最新版本：" + info.Version + size +
                (string.IsNullOrEmpty(info.Notes) ? "" : "\n\n" + info.Notes) +
                "\n\n更新只替换启动器程序文件，不会改动游戏、存档或账户配置。\n" +
                "更新期间启动器会退出，完成后自动重新打开。",
                "下载并更新", "稍后");
            if (go) _ = ApplyAsync(owner, info);
        }

        public static async Task<UpdateInfo> CheckAsync()
        {
            string feed = ResolveFeedUrl();
            if (!IsAllowedUrl(feed))
                throw new InvalidOperationException("更新源必须使用 HTTPS（本地测试可用 127.0.0.1/localhost）。");

            var fetched = await FetchManifestAsync(feed).ConfigureAwait(false);
            bool viaProxy = fetched.ViaProxy;
            string body = fetched.Text;
            var json = JObject.Parse(body);

            string version = (string)json["version"];
            if (string.IsNullOrWhiteSpace(version)) version = (string)json["tag_name"];
            string url = (string)json["url"];
            string sha = (string)json["sha256"];

            // 兼容 GitHub Releases 风格的 assets。
            if (string.IsNullOrWhiteSpace(url) && json["assets"] is JArray arr && arr.Count > 0)
            {
                url = (string)arr[0]["browser_download_url"];
                sha = (string)arr[0]["digest"];
            }
            if (sha != null && sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = sha.Substring(7);

            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("更新清单缺少 version 或 url");
            if (string.IsNullOrWhiteSpace(sha))
                throw new InvalidOperationException("更新清单缺少 sha256，拒绝下载。");

            long size = 0;
            try { size = (long?)json["size"] ?? 0; } catch { }

            // 清单签名：覆盖 version/url/sha256/size。未签名的清单在过渡期只警告，
            // 签名上线后（发布带 sig 的清单）即可改 STRICT 拒绝。
            string keyid = (string)json["keyid"];
            string sig = (string)json["sig"];
            if (!string.IsNullOrWhiteSpace(sig))
            {
                if (!UpdateSignature.Verify(version, url, sha.Trim(), size, keyid, sig))
                    throw new InvalidOperationException("更新清单签名无效，已拒绝更新。");
            }
            else
            {
                // 经第三方镜像拿到的清单必须带签名，否则镜像可以随意篡改版本与地址。
                if (viaProxy)
                    throw new InvalidOperationException("镜像返回的更新清单未签名，已拒绝更新。");
                Log.Warn("更新清单未签名（过渡期放行）：" + feed);
            }

            if (!IsNewer(version, CtdVersion.Version)) return null;
            if (!IsAllowedUrl(url))
                throw new InvalidOperationException("更新包地址必须使用 HTTPS。");

            var mirrors = new List<string>();
            if (json["mirrors"] is JArray marr)
            {
                foreach (var item in marr)
                {
                    string m = (string)item;
                    if (!string.IsNullOrWhiteSpace(m) && IsAllowedUrl(m.Trim()))
                        mirrors.Add(m.Trim());
                }
            }

            return new UpdateInfo
            {
                Version = version,
                Notes = (string)json["notes"] ?? (string)json["body"] ?? "",
                Url = url,
                Sha256 = sha.Trim(),
                Size = size,
                Mirrors = mirrors
            };
        }

        public static async Task ApplyAsync(Window owner, UpdateInfo info)
        {
            string dir = CtdPaths.UpdateDir;
            string zip = Path.Combine(dir, "CTD-update-" + SafeName(info.Version) + ".zip");
            string staging = Path.Combine(dir, "staging-" + SafeName(info.Version));
            try
            {
                CtdPaths.EnsureDir(dir);
                // 多源：清单 url → 清单 mirrors → GitHub 加速前缀。每个源都校验大小与 SHA-256。
                Exception lastError = null;
                bool ok = false;
                foreach (string candidate in CandidateUrls(info))
                {
                    try
                    {
                        await DownloadPackageAsync(candidate, zip, info.Size).ConfigureAwait(false);

                        long got = new FileInfo(CtdPaths.Long(zip)).Length;
                        if (info.Size > 0 && got != info.Size)
                            throw new InvalidOperationException("更新包大小不符：" + got + " / " + info.Size);

                        string actual = Sha256File(zip);
                        if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("更新包 SHA-256 校验失败，已拒绝安装。");

                        ok = true;
                        Log.Info("更新包下载完成，来源：" + candidate);
                        break;
                    }
                    catch (Exception e)
                    {
                        lastError = e;
                        Log.Warn("更新源不可用：" + candidate + " → " + e.Message);
                        try { if (File.Exists(CtdPaths.Long(zip))) File.Delete(CtdPaths.Long(zip)); } catch { }
                    }
                }
                if (!ok)
                    throw new InvalidOperationException("所有更新源都失败：" +
                                                        (lastError == null ? "" : lastError.Message));

                if (Directory.Exists(CtdPaths.Long(staging))) CtdPaths.DeleteDirSafe(staging);
                Directory.CreateDirectory(CtdPaths.Long(staging));
                ExtractSafe(zip, staging);
                if (!File.Exists(CtdPaths.Long(Path.Combine(staging, "CTD.exe"))))
                    throw new InvalidOperationException("更新包缺少 CTD.exe，拒绝安装。");

                string backup = Path.Combine(dir, "backup-" + SafeName(info.Version));
                BackupProgram(backup);
                string marker = Path.Combine(dir, "ok-" + SafeName(info.Version) + ".marker");
                try { if (File.Exists(CtdPaths.Long(marker))) File.Delete(CtdPaths.Long(marker)); } catch { }
                WritePending(info.Version, backup);
                string script = WriteUpdaterScript(staging, backup, marker);
                Log.Info("准备更新到 " + info.Version + "，更新脚本：" + script);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " +
                                Process.GetCurrentProcess().Id + " \"" + CtdPaths.Root + "\" \"" + staging + "\" \"" +
                                backup + "\" \"" + marker + "\" \"" + PendingPath + "\"",
                    UseShellExecute = true,
                    WorkingDirectory = CtdPaths.Root
                };
                Process.Start(psi);
                ExitToApply();
            }
            catch (Exception e)
            {
                Log.Error("更新失败", e);
                if (owner != null)
                {
                    _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { Ui.Error(owner, "更新失败", e.Message); } catch { }
                    }));
                }
            }
        }

        private static void ExtractSafe(string zip, string dest)
        {
            string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
            using (var z = ZipFile.OpenRead(CtdPaths.Long(zip)))
            {
                foreach (var e in z.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string full = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', '\\')));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包含非法路径：" + e.FullName);
                    if (e.FullName.Replace('\\', '/').StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包不能包含用户数据目录：" + e.FullName);
                    string parent = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(CtdPaths.Long(parent));
                    e.ExtractToFile(CtdPaths.Long(full), true);
                }
            }
        }

        private static void BackupProgram(string backup)
        {
            try
            {
                if (Directory.Exists(CtdPaths.Long(backup))) CtdPaths.DeleteDirSafe(backup);
                Directory.CreateDirectory(CtdPaths.Long(backup));
                foreach (var name in new[] { "CTD.exe", "CTD.exe.config", "skin3d", "lib" })
                {
                    string src = Path.Combine(CtdPaths.Root, name);
                    string dst = Path.Combine(backup, name);
                    if (File.Exists(CtdPaths.Long(src))) File.Copy(CtdPaths.Long(src), CtdPaths.Long(dst), true);
                    else if (Directory.Exists(CtdPaths.Long(src))) CopyDir(src, dst);
                }
            }
            catch (Exception e) { Log.Warn("更新备份失败：" + e.Message); }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(CtdPaths.Long(dst));
            foreach (var f in Directory.GetFiles(CtdPaths.Long(src), "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(Path.GetFullPath(src).TrimEnd('\\').Length).TrimStart('\\');
                string to = Path.Combine(dst, rel);
                string parent = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(CtdPaths.Long(parent));
                File.Copy(f, CtdPaths.Long(to), true);
            }
        }

        private static string WriteUpdaterScript(string staging, string backup, string marker)
        {
            string script = Path.Combine(CtdPaths.UpdateDir, "apply-update.ps1");
            string ps =
                "$ErrorActionPreference='Continue'\r\n" +
                "$targetPid=[int]$args[0]\r\n" +
                "$root=$args[1]; $stage=$args[2]; $backup=$args[3]; $marker=$args[4]; $pending=$args[5]\r\n" +
                "while(Get-Process -Id $targetPid -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 400}\r\n" +
                "Start-Sleep -Seconds 1\r\n" +
                "if(Test-Path $marker){Remove-Item $marker -Force -ErrorAction SilentlyContinue}\r\n" +
                "robocopy $stage $root /E /XF CTD.json CTD.json.bak CTD.user CTD.dev README-BETA.txt /NFL /NDL /NJH /NJS /NP | Out-Null\r\n" +
                "$proc=$null\r\n" +
                "try{$proc=Start-Process -FilePath (Join-Path $root 'CTD.exe') -PassThru -ErrorAction Stop}catch{}\r\n" +
                "$ok=$false\r\n" +
                "for($i=0;$i -lt 25;$i++){\r\n" +
                "  Start-Sleep -Seconds 1\r\n" +
                "  if($proc -and $proc.HasExited){break}\r\n" +
                "  if(Test-Path $marker){$ok=$true;break}\r\n" +
                "}\r\n" +
                "if(-not $ok){\r\n" +
                "  robocopy $backup $root /E /XF CTD.json CTD.json.bak CTD.user CTD.dev README-BETA.txt /NFL /NDL /NJH /NJS /NP | Out-Null\r\n" +
                "  if(Test-Path $pending){Remove-Item $pending -Force -ErrorAction SilentlyContinue}\r\n" +
                "  Start-Process -FilePath (Join-Path $root 'CTD.exe')\r\n" +
                "}\r\n";
            File.WriteAllText(CtdPaths.Long(script), ps, new UTF8Encoding(false));
            return script;
        }

        private static string PendingPath
        {
            get { return Path.Combine(CtdPaths.UpdateDir, "pending.json"); }
        }

        /// <summary>启动时调用：更新后连续 3 次没进到目标版本，就停止重试并提示。</summary>
        public static void MarkStartupHealthy()
        {
            try
            {
                string path = PendingPath;
                if (!File.Exists(CtdPaths.Long(path))) return;
                var o = JObject.Parse(File.ReadAllText(CtdPaths.Long(path)));
                string to = (string)o["to"] ?? "";
                if (string.Equals(to, CtdVersion.Version, StringComparison.OrdinalIgnoreCase)) return;

                int attempts = (int?)o["attempts"] ?? 0;
                attempts++;
                o["attempts"] = attempts;
                if (attempts >= 3)
                {
                    try { File.Delete(CtdPaths.Long(path)); } catch { }
                    File.WriteAllText(
                        CtdPaths.Long(Path.Combine(CtdPaths.UpdateDir, "rollback.flag")),
                        "更新到 " + to + " 连续失败，已放弃自动更新。\r\n",
                        new UTF8Encoding(false));
                    Log.Warn("更新连续失败 " + attempts + " 次，已停止自动更新重试：" + to);
                    return;
                }
                File.WriteAllText(CtdPaths.Long(path), o.ToString(), new UTF8Encoding(false));
                Log.Warn("检测到未完成的更新（目标 " + to + "，第 " + attempts + " 次启动）");
            }
            catch (Exception e) { Log.Warn("检查更新状态失败：" + e.Message); }
        }

        /// <summary>主界面起来后调用：确认新版本真的能启动，清掉 pending 并写成功标记。</summary>
        public static void ConfirmStartup()
        {
            try
            {
                string path = PendingPath;
                if (!File.Exists(CtdPaths.Long(path))) return;
                var o = JObject.Parse(File.ReadAllText(CtdPaths.Long(path)));
                string to = (string)o["to"] ?? "";
                if (!string.Equals(to, CtdVersion.Version, StringComparison.OrdinalIgnoreCase)) return;

                CtdPaths.EnsureDir(CtdPaths.UpdateDir);
                File.WriteAllText(
                    CtdPaths.Long(Path.Combine(CtdPaths.UpdateDir, "ok-" + SafeName(to) + ".marker")),
                    DateTime.Now.ToString("o"),
                    new UTF8Encoding(false));
                File.Delete(CtdPaths.Long(path));
                Log.Info("更新已确认完成：" + to);
            }
            catch (Exception e) { Log.Warn("确认更新状态失败：" + e.Message); }
        }

        private static void WritePending(string to, string backup)
        {
            try
            {
                CtdPaths.EnsureDir(CtdPaths.UpdateDir);
                var o = new JObject
                {
                    ["from"] = CtdVersion.Version,
                    ["to"] = to,
                    ["backup"] = backup,
                    ["attempts"] = 0
                };
                File.WriteAllText(CtdPaths.Long(PendingPath), o.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Warn("写更新标记失败：" + e.Message); }
        }

        private static bool IsAllowedUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme == Uri.UriSchemeHttps) return true;
                return u.Scheme == Uri.UriSchemeHttp && (u.Host == "127.0.0.1" || u.Host == "localhost");
            }
            catch { return false; }
        }

        private static bool IsNewer(string remote, string current)
        {
            try
            {
                Version r, c;
                if (!Version.TryParse(Normalize(remote), out r)) return false;
                if (!Version.TryParse(Normalize(current), out c)) return false;
                return r > c;
            }
            catch { return false; }
        }

        private static string Normalize(string v)
        {
            v = (v ?? "").Trim();
            if (v.StartsWith("beta-", StringComparison.OrdinalIgnoreCase)) v = v.Substring(5);
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            return v;
        }

        private static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrEmpty(s) ? "update" : s;
        }

        /// <summary>
        /// 下载更新包：长超时 + 断点续传 + 重试。
        /// 以前用 30 秒超时的 HttpClient 一次性拉 10 MB，慢网必被截断，
        /// 结果就是“下载失败/校验失败”，用户在界面上只看到更新没反应。
        /// </summary>
        private static async Task DownloadPackageAsync(string url, string path, long expectedSize)
        {
            const int maxAttempts = 3;
            Exception last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    string local = CtdPaths.Long(path);
                    long have = File.Exists(local) ? new FileInfo(local).Length : 0;
                    if (expectedSize > 0 && have > expectedSize) { File.Delete(local); have = 0; }
                    if (expectedSize > 0 && have == expectedSize) return;

                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

                        using (var resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        {
                            if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                            {
                                have = 0; // 服务器不支持续传，重头下载
                            }
                            else if (!resp.IsSuccessStatusCode)
                            {
                                throw new InvalidOperationException("下载更新包失败：HTTP " + (int)resp.StatusCode);
                            }

                            using (var input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(local,
                                have > 0 ? FileMode.Append : FileMode.Create,
                                FileAccess.Write, FileShare.None, 81920, true))
                            {
                                await input.CopyToAsync(output).ConfigureAwait(false);
                            }
                        }
                    }

                    long got = new FileInfo(local).Length;
                    if (expectedSize > 0 && got != expectedSize)
                        throw new IOException("下载不完整：" + got + " / " + expectedSize);
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    Log.Warn("更新包下载第 " + attempt + " 次失败：" + e.Message);
                    if (attempt < maxAttempts) await Task.Delay(1500).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("更新包下载失败（已重试 " + maxAttempts + " 次）：" +
                                                (last == null ? "" : last.Message));
        }

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(CtdPaths.Long(path)))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 下载/解压的某些 await 会回到线程池；WPF 的 Shutdown 必须在 UI 线程调用。
        /// 这里统一切回 Dispatcher，避免“更新已下载但启动器不退”的假死。
        /// </summary>
        private static void ExitToApply()
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess())
            {
                app.Shutdown();
                return;
            }
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { app.Shutdown(); } catch { }
            }));
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }
    }
}
