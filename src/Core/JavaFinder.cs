using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CTD.Core
{
    public class JavaInfo
    {
        public string Path { get; set; }          // java.exe 或 javaw.exe
        public string Version { get; set; }       // 17.0.1
        public int Major { get; set; }            // 17
        public bool Is64Bit { get; set; }
        public string Vendor { get; set; } = "";
        public bool Detected { get; set; }
        public string Error { get; set; }

        public string Javaw
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return Path;
                var dir = System.IO.Path.GetDirectoryName(Path);
                var exe = System.IO.Path.GetFileName(Path);
                if (string.Equals(exe, "java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var javaw = System.IO.Path.Combine(dir, "javaw.exe");
                    if (File.Exists(javaw)) return javaw;
                }
                return Path;
            }
        }

        public string Display
        {
            get
            {
                if (!Detected) return "Java " + (Major > 0 ? Major.ToString() : "?") + " · " + Path;
                return "Java " + Version + (Is64Bit ? " (64 位)" : " (32 位)") + " · " + Vendor;
            }
        }

        public override string ToString() { return Display; }
    }

    /// <summary>查找与检测本机 Java。</summary>
    public static class JavaFinder
    {
        private static List<JavaInfo> _cache;
        private static readonly object _lock = new object();

        public static List<JavaInfo> FindAll(bool force = false)
        {
            lock (_lock)
            {
                if (!force && _cache != null) return new List<JavaInfo>(_cache);
            }
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<JavaInfo>();

            // 1) 启动器自动下载的 Java
            try
            {
                if (Directory.Exists(CtdPaths.JavaDir))
                {
                    foreach (var f in Directory.GetFiles(CtdPaths.JavaDir, "javaw.exe", SearchOption.AllDirectories))
                        paths.Add(f);
                }
            }
            catch { }

            // 2) 用户手动添加 / 当前选择
            var s = CtdSettings.Current;
            if (s.ExtraJavaPaths != null)
                foreach (var p in s.ExtraJavaPaths)
                    if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
            if (!string.IsNullOrWhiteSpace(s.JavaPath)) paths.Add(s.JavaPath);

            // 3) JAVA_HOME
            try
            {
                var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrEmpty(jh))
                {
                    AddFromHome(paths, jh);
                }
            }
            catch { }

            // 4) 注册表
            foreach (var root in new[] { @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\JRE", @"SOFTWARE\JavaSoft\JDK" })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            using (var sk = key.OpenSubKey(sub))
                            {
                                var home = sk == null ? null : sk.GetValue("JavaHome") as string;
                                if (!string.IsNullOrEmpty(home)) AddFromHome(paths, home);
                                var ms = sk == null ? null : sk.GetValue("MSIInstallDir") as string;
                                if (!string.IsNullOrEmpty(ms)) AddFromHome(paths, ms);
                            }
                        }
                    }
                }
                catch { }
            }

            // 5) 常见安装目录
            var roots = new List<string>
            {
                @"C:\Program Files\Java", @"C:\Program Files (x86)\Java",
                @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\AdoptOpenJDK",
                @"C:\Program Files\Zulu", @"C:\Program Files\Microsoft",
                @"C:\Program Files\BellSoft", @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\Semeru", @"C:\Program Files\Temurin",
                @"C:\Program Files\Common Files\Oracle\Java",
                @"C:\Program Files\JetBrains",
                System.IO.Path.Combine(CtdPaths.Root, "jre")
            };
            foreach (var r in roots)
            {
                try
                {
                    if (!Directory.Exists(r)) continue;
                    foreach (var sub in Directory.GetDirectories(r))
                    {
                        var cand = System.IO.Path.Combine(sub, "bin", "javaw.exe");
                        if (File.Exists(cand)) paths.Add(cand);
                        var cand2 = System.IO.Path.Combine(sub, "jre", "bin", "javaw.exe");
                        if (File.Exists(cand2)) paths.Add(cand2);
                    }
                }
                catch { }
            }

            // 6) .minecraft 自带的 runtime 目录（官方启动器下载的 Java）
            try
            {
                foreach (var md in new[] { Environment.GetEnvironmentVariable("APPDATA") })
                {
                    if (string.IsNullOrEmpty(md)) continue;
                    var rt = System.IO.Path.Combine(md, ".minecraft", "runtime");
                    if (Directory.Exists(rt))
                        foreach (var f in Directory.GetFiles(rt, "javaw.exe", SearchOption.AllDirectories))
                            paths.Add(f);
                }
                var rt2 = System.IO.Path.Combine(CtdPaths.GameDir, "runtime");
                if (Directory.Exists(rt2))
                    foreach (var f in Directory.GetFiles(rt2, "javaw.exe", SearchOption.AllDirectories))
                        paths.Add(f);
            }
            catch { }

            // 7) PATH
            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var part in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    try
                    {
                        var j = System.IO.Path.Combine(part.Trim(), "javaw.exe");
                        if (File.Exists(j)) paths.Add(j);
                    }
                    catch { }
                }
            }
            catch { }

            foreach (var p in paths)
            {
                var info = Detect(p);
                if (info != null) result.Add(info);
            }

            // 去重（同一 major + 同一目录只留一个）
            var dedup = new List<JavaInfo>();
            foreach (var j in result.OrderByDescending(j => j.Major).ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (dedup.Any(d => string.Equals(d.Path, j.Path, StringComparison.OrdinalIgnoreCase))) continue;
                dedup.Add(j);
            }
            lock (_lock) _cache = dedup;
            return new List<JavaInfo>(dedup);
        }

        private static void AddFromHome(HashSet<string> paths, string home)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            var a = System.IO.Path.Combine(home.Trim().Trim('"'), "bin", "javaw.exe");
            if (File.Exists(a)) paths.Add(a);
        }

        public static JavaInfo Detect(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;
            // 统一成 java.exe 用于检测（javaw 无控制台输出）
            string javaExe = exePath;
            if (string.Equals(System.IO.Path.GetFileName(javaExe), "javaw.exe", StringComparison.OrdinalIgnoreCase))
            {
                var cand = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(javaExe) ?? "", "java.exe");
                if (File.Exists(cand)) javaExe = cand;
            }
            if (!File.Exists(javaExe)) return null;

            var info = new JavaInfo { Path = exePath, Detected = false };
            try
            {
                var psi = new ProcessStartInfo(javaExe, "-version")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var err = p.StandardError.ReadToEnd();
                    var outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(12000))
                    {
                        try { p.Kill(); } catch { }
                        info.Error = "检测超时";
                        return info;
                    }
                    var text = err + "\n" + outp;
                    ParseVersion(text, info);
                }
            }
            catch (Exception e)
            {
                info.Error = e.Message;
                return info;
            }
            return info;
        }

        private static void ParseVersion(string text, JavaInfo info)
        {
            var m = Regex.Match(text, "version\\s+\"([^\"]+)\"");
            if (!m.Success) m = Regex.Match(text, "openjdk\\s+([0-9][^\\s]*)");
            if (!m.Success) m = Regex.Match(text, "java version\\s+\"?([0-9][^\"\\s]*)");
            var ver = m.Success ? m.Groups[1].Value : null;
            if (string.IsNullOrEmpty(ver)) { info.Error = "无法识别版本"; return; }
            info.Version = ver;
            info.Major = ParseMajor(ver);
            info.Is64Bit = text.IndexOf("64-Bit", StringComparison.OrdinalIgnoreCase) >= 0
                           || text.IndexOf("x86_64", StringComparison.OrdinalIgnoreCase) >= 0
                           || text.IndexOf("amd64", StringComparison.OrdinalIgnoreCase) >= 0;
            if (text.IndexOf("OpenJDK", StringComparison.OrdinalIgnoreCase) >= 0) info.Vendor = "OpenJDK";
            if (text.IndexOf("Temurin", StringComparison.OrdinalIgnoreCase) >= 0) info.Vendor = "Temurin";
            if (text.IndexOf("Zulu", StringComparison.OrdinalIgnoreCase) >= 0) info.Vendor = "Zulu";
            if (text.IndexOf("Oracle", StringComparison.OrdinalIgnoreCase) >= 0 && string.IsNullOrEmpty(info.Vendor)) info.Vendor = "Oracle";
            if (text.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) info.Vendor = "Microsoft";
            if (string.IsNullOrEmpty(info.Vendor)) info.Vendor = "Java";
            info.Detected = true;
        }

        public static int ParseMajor(string ver)
        {
            if (string.IsNullOrEmpty(ver)) return 0;
            var parts = ver.Split('.', '_', '-');
            int a;
            if (!int.TryParse(parts[0], out a)) return 0;
            if (a == 1 && parts.Length > 1)
            {
                int b;
                if (int.TryParse(parts[1], out b)) return b; // 1.8.0_51 -> 8
            }
            return a;
        }

        /// <summary>按版本需求挑选最合适的 Java。</summary>
        public static JavaInfo Pick(int requiredMajor, List<JavaInfo> candidates = null)
        {
            var all = candidates ?? FindAll();
            var usable = all.Where(j => j.Detected && j.Major > 0).ToList();
            if (usable.Count == 0) return null;

            var s = CtdSettings.Current;
            // 手动模式：固定使用用户选中的 Java；版本不匹配时让启动前检查明确报错，
            // 而不是悄悄换成一个“看起来能用”的 Java。
            if (!s.AutoJava)
            {
                if (!string.IsNullOrWhiteSpace(s.JavaPath))
                {
                    var sel = usable.FirstOrDefault(j =>
                        string.Equals(j.Path, s.JavaPath, StringComparison.OrdinalIgnoreCase));
                    if (sel != null) return sel;
                }
                return PickFrom(usable, requiredMajor, true);
            }

            // 自动模式：完全按版本需要挑，不再被旧的手动选择带偏。
            // 优先用启动器文件夹里自带的 Java，复制到别的电脑后不依赖系统 Java。
            var bundled = usable.Where(j => IsBundled(j.Path)).ToList();
            if (bundled.Count > 0)
            {
                var picked = PickFrom(bundled, requiredMajor, false);
                if (picked != null) return picked;
            }
            return PickFrom(usable, requiredMajor, true);
        }

        private static bool IsBundled(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                var full = System.IO.Path.GetFullPath(path);
                var javaDir = System.IO.Path.GetFullPath(CtdPaths.JavaDir).TrimEnd('\\') + "\\";
                var jreDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(CtdPaths.Root, "jre")).TrimEnd('\\') + "\\";
                return full.StartsWith(javaDir, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(jreDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static JavaInfo PickFrom(List<JavaInfo> usable, int requiredMajor, bool allowLower)
        {
            if (requiredMajor > 0)
            {
                var exact = usable.Where(j => j.Major == requiredMajor).OrderByDescending(j => j.Is64Bit).FirstOrDefault();
                if (exact != null) return exact;
                var higher = usable.Where(j => j.Major > requiredMajor).OrderBy(j => j.Major).FirstOrDefault();
                if (higher != null) return higher;
                return allowLower
                    ? usable.Where(j => j.Major < requiredMajor).OrderByDescending(j => j.Major).FirstOrDefault()
                    : null;
            }
            return usable.OrderByDescending(j => j.Major).FirstOrDefault();
        }

        /// <summary>根据 MC 版本推断所需 Java 大版本。</summary>
        public static int RequiredMajorFor(VersionJson merged)
        {
            if (merged != null && merged.JavaVersion != null && merged.JavaVersion.MajorVersion > 0)
                return merged.JavaVersion.MajorVersion;
            var mc = merged == null ? null : (merged.InheritsFrom ?? merged.Id);
            return RequiredMajorForMc(mc);
        }

        public static int RequiredMajorForMc(string mcVersion)
        {
            if (string.IsNullOrEmpty(mcVersion)) return 8;
            var v = ParseMcVersion(mcVersion);
            if (v == null) return 8;
            int major = v[0], minor = v[1], patch = v.Length > 2 ? v[2] : 0;
            if (v.Length > 3) patch = patch * 100 + v[3]; // 1.20.5.1 之类
            // 26.x 起 Minecraft 改用年份式版本号（26.2），需要 Java 25；
            // 旧逻辑把 major != 1 一律当 Java 8，JSON 缺 javaVersion 时会选错 Java。
            if (major != 1) return major >= 26 ? 25 : 8;
            if (minor >= 21) return 21;
            if (minor == 20 && patch >= 5) return 21;
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }

        private static int[] ParseMcVersion(string s)
        {
            var m = Regex.Match(s, "(\\d+)\\.(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?");
            if (!m.Success) return null;
            var list = new List<int>();
            for (int i = 1; i < m.Groups.Count; i++)
                if (m.Groups[i].Success) list.Add(int.Parse(m.Groups[i].Value));
            return list.ToArray();
        }

        // ==================== 自动下载 Java ====================
        // 说明：Adoptium 的二进制实际托管在 GitHub，部分网络会连不上，
        // 因此优先用 Microsoft OpenJDK（aka.ms 直链）与 Azul Zulu 作为来源。
        public static string AdoptiumUrl(int major)
        {
            return "https://api.adoptium.net/v3/binary/latest/" + major + "/ga/windows/x64/jre/hotspot/normal/eclipse";
        }

        public static string MicrosoftUrl(int major)
        {
            return "https://aka.ms/download-jdk/microsoft-jdk-" + major + "-windows-x64.zip";
        }

        private static async Task<List<string>> ZuluUrlsAsync(int major, CancellationToken ct)
        {
            var list = new List<string>();
            try
            {
                var url = "https://api.azul.com/metadata/v1/zulu/packages/?java_version=" + major +
                          "&os=windows&arch=x64&java_package_type=jre&archive_type=zip&latest=true&release_status=ga&page_size=1";
                var text = await Net.GetStringAsync(url, ct, 1).ConfigureAwait(false);
                var arr = Newtonsoft.Json.Linq.JArray.Parse(text);
                if (arr.Count > 0)
                {
                    var u = (string)arr[0]["download_url"];
                    if (!string.IsNullOrEmpty(u)) list.Add(u);
                }
            }
            catch { }
            return list;
        }

        /// <summary>按优先级给出可用的 Java 下载地址（会自动探测 Zulu）。</summary>
        public static async Task<List<string>> GetDownloadUrlsAsync(int major, CancellationToken ct)
        {
            var list = new List<string>();
            if (major >= 11) list.Add(MicrosoftUrl(major));
            foreach (var u in await ZuluUrlsAsync(major, ct).ConfigureAwait(false))
                if (!list.Contains(u)) list.Add(u);
            var adoptium = AdoptiumUrl(major);
            if (!list.Contains(adoptium)) list.Add(adoptium);
            return list;
        }

        public static async Task<JavaInfo> DownloadJavaAsync(int major, IProgress<string> status, IProgress<long> bytes, CancellationToken ct)
        {
            var target = System.IO.Path.Combine(CtdPaths.JavaDir, "jdk-" + major);
            CtdPaths.EnsureDir(CtdPaths.JavaDir);
            var zip = System.IO.Path.Combine(CtdPaths.CacheDir, "java-" + major + ".zip");
            CtdPaths.EnsureDir(CtdPaths.CacheDir);

            if (status != null) status.Report("正在选择下载源…");
            var urls = await GetDownloadUrlsAsync(major, ct).ConfigureAwait(false);
            if (urls.Count == 0) throw new IOException("没有可用的 Java 下载源");

            long length = -1;
            foreach (var u in urls)
            {
                try
                {
                    length = await Net.GetContentLengthAsync(u, ct).ConfigureAwait(false);
                    if (length > 0) break;
                }
                catch { }
            }

            var item = new DownloadItem("Java " + major, zip, urls[0], length, "");
            foreach (var u in urls) item.WithUrl(u);
            if (status != null) status.Report("正在下载 Java " + major + (length > 0 ? "（" + CtdPaths.FormatSize(length) + "）" : ""));
            await Net.DownloadAsync(item, bytes, ct).ConfigureAwait(false);

            if (status != null) status.Report("正在解压 Java…");
            var tmpDir = target + ".tmp";
            CtdPaths.DeleteDirSafe(tmpDir);
            CtdPaths.EnsureDir(tmpDir);
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(CtdPaths.Long(zip), CtdPaths.Long(tmpDir));
            }
            catch (Exception e)
            {
                throw new IOException("解压 Java 失败: " + e.Message, e);
            }

            // 找到 bin\javaw.exe
            var javaw = Directory.GetFiles(CtdPaths.Long(tmpDir), "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (javaw == null) throw new IOException("下载的 Java 压缩包中没有 javaw.exe");

            var rootDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(javaw));
            CtdPaths.DeleteDirSafe(target);
            CtdPaths.EnsureDir(target);
            MoveDirectoryContents(rootDir, target);
            try { CtdPaths.DeleteDirSafe(tmpDir); } catch { }
            try { CtdPaths.DeleteFileSafe(zip); } catch { }

            var finalJavaw = Directory.GetFiles(CtdPaths.Long(target), "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
            var info = Detect(finalJavaw);
            if (info == null || !info.Detected) throw new IOException("Java 安装后检测失败");
            if (status != null) status.Report("Java " + info.Version + " 就绪");
            return info;
        }

        private static void MoveDirectoryContents(string src, string dst)
        {
            CtdPaths.EnsureDir(dst);
            foreach (var dir in Directory.GetDirectories(CtdPaths.Long(src), "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(src.Length).TrimStart('\\', '/');
                CtdPaths.EnsureDir(System.IO.Path.Combine(dst, rel));
            }
            foreach (var file in Directory.GetFiles(CtdPaths.Long(src), "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(src.Length).TrimStart('\\', '/');
                var target = System.IO.Path.Combine(dst, rel);
                CtdPaths.EnsureParent(target);
                if (File.Exists(CtdPaths.Long(target))) File.Delete(CtdPaths.Long(target));
                File.Move(CtdPaths.Long(file), CtdPaths.Long(target));
            }
        }
    }
}
