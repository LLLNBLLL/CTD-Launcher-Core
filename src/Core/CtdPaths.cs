using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CTD.Core
{
    /// <summary>全局路径与文件系统工具。所有长路径安全，均使用绝对路径。</summary>
    public static class CtdPaths
    {
        public static readonly string Root = NormalizeRoot(AppDomain.CurrentDomain.BaseDirectory);
        public static readonly string DataDir = Path.Combine(Root, "data");
        public static readonly string LogDir = Path.Combine(DataDir, "logs");
        public static readonly string JavaDir = Path.Combine(DataDir, "java");
        public static readonly string CacheDir = Path.Combine(DataDir, "cache");
        public static readonly string TempDir = Path.Combine(DataDir, "temp");
        public static readonly string UpdateDir = Path.Combine(DataDir, "update");
        /// <summary>用户配置/账户放在系统用户目录，复制启动器文件夹时不会带走。</summary>
        public static readonly string UserConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CTD-Launcher");
        /// <summary>
        /// 用户端 Beta 包放一个空的 CTD.user 标记，使用独立的用户配置，
        /// 不会读写开发机/主启动器的 CTD.json，也不会带上已有账户和设置。
        /// </summary>
        private static readonly string ConfigSuffix = BuildConfigSuffix();

        private static string BuildConfigSuffix()
        {
            // 开发目录显式标记 CTD.dev：即使更新包误带 CTD.user，也绝不切到用户配置。
            if (File.Exists(Path.Combine(Root, "CTD.dev"))) return "";
            return File.Exists(Path.Combine(Root, "CTD.user")) ? ".user" : "";
        }

        public static readonly string ConfigFile = Path.Combine(UserConfigDir, "CTD" + ConfigSuffix + ".json");

        /// <summary>界面音效目录：发布包里的 lib\Assets\Sounds（开发输出目录同布局）。</summary>
        public static readonly string SoundsDir = Path.Combine(Root, "lib", "Assets", "Sounds");

        private static string NormalizeRoot(string dir)
        {
            dir = dir.TrimEnd('\\', '/');
            return dir;
        }

        /// <summary>游戏目录（可在设置中修改）。</summary>
        public static string GameDir
        {
            get { return CtdSettings.Current.GameDir; }
        }

        public static string VersionDir(string gameDir, string id) { return Path.Combine(gameDir, "versions", id); }
        public static string VersionJson(string gameDir, string id) { return Path.Combine(gameDir, "versions", id, id + ".json"); }
        public static string VersionJar(string gameDir, string id) { return Path.Combine(gameDir, "versions", id, id + ".jar"); }
        public static string LibrariesDir(string gameDir) { return Path.Combine(gameDir, "libraries"); }
        public static string AssetsDir(string gameDir) { return Path.Combine(gameDir, "assets"); }
        public static string NativesDir(string gameDir, string id) { return Path.Combine(gameDir, "versions", id, "natives"); }
        public static string ModsDir(string gameDir) { return Path.Combine(gameDir, "mods"); }

        /// <summary>
        /// 把旧版便携 data\CTD.json 及其备份迁到用户目录。
        /// 迁移后便携目录里不再保留账户/设置，避免直接复制启动器文件夹带走配置。
        /// </summary>
        public static void MigratePortableConfig()
        {
            try
            {
                if (!Directory.Exists(Long(DataDir))) return;
                EnsureDir(UserConfigDir);
                bool hasExternal = File.Exists(Long(ConfigFile));
                foreach (var src in Directory.GetFiles(Long(DataDir), "CTD.json*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(src);
                    string dest = Path.Combine(UserConfigDir, name);
                    if (string.Equals(name, "CTD.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (hasExternal)
                            dest = Path.Combine(UserConfigDir, "CTD.json.portable-" +
                                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    }
                    else if (File.Exists(Long(dest)))
                    {
                        dest = Path.Combine(UserConfigDir, name + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    }

                    try
                    {
                        if (File.Exists(Long(dest))) File.Delete(Long(dest));
                        File.Move(Long(src), Long(dest));
                    }
                    catch
                    {
                        try
                        {
                            File.Copy(Long(src), Long(dest), true);
                            File.Delete(Long(src));
                        }
                        catch (Exception e)
                        {
                            Log.Warn("迁移用户配置失败：" + src + " -> " + dest + " / " + e.Message);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("检查用户配置迁移失败：" + e.Message);
            }
        }

        public static void EnsureDir(string dir)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public static void EnsureParent(string file)
        {
            var d = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(d)) EnsureDir(d);
        }

        /// <summary>把路径转换为 Win32 长路径形式（仅在必要时前置 \\?\）。</summary>
        public static string Long(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\")) return path;
            if (path.Length < 240) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            if (path.Length >= 2 && path[1] == ':') return @"\\?\" + path;
            return path;
        }

        public static bool FileExists(string path)
        {
            try { return File.Exists(Long(path)); } catch { return false; }
        }

        public static long FileSize(string path)
        {
            try
            {
                var fi = new FileInfo(Long(path));
                return fi.Exists ? fi.Length : -1;
            }
            catch { return -1; }
        }

        public static void DeleteFileSafe(string path)
        {
            try { if (FileExists(path)) File.Delete(Long(path)); } catch (Exception e) { Log.Warn("删除文件失败: " + path + " / " + e.Message); }
        }

        public static bool DeleteDirSafe(string path)
        {
            try
            {
                if (!Directory.Exists(Long(path))) return true;
                // 清除只读属性，避免删除失败
                foreach (var f in Directory.GetFiles(Long(path), "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(Long(path), true);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("删除目录失败: " + path + " / " + e.Message);
                return false;
            }
        }

        public static void WriteAllText(string path, string text, Encoding encoding = null)
        {
            EnsureParent(path);
            var tmp = path + ".tmp";
            File.WriteAllText(Long(tmp), text, encoding ?? new UTF8Encoding(false));
            DeleteFileSafe(path);
            File.Move(Long(tmp), Long(path));
        }

        public static string ReadAllText(string path)
        {
            if (!FileExists(path)) return null;
            return File.ReadAllText(Long(path), Encoding.UTF8);
        }

        /// <summary>在资源管理器中打开目录（并尽量选中文件）。</summary>
        public static void OpenInExplorer(string path)
        {
            try
            {
                if (Directory.Exists(Long(path)))
                    Process.Start("explorer.exe", "\"" + path + "\"");
                else if (FileExists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(Long(dir)))
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch (Exception e) { Log.Warn("打开目录失败: " + e.Message); }
        }

        /// <summary>递归计算目录占用大小（MB）。</summary>
        public static double DirSizeMb(string dir)
        {
            try
            {
                if (!Directory.Exists(Long(dir))) return 0;
                long sum = 0;
                foreach (var f in Directory.EnumerateFiles(Long(dir), "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(f).Length; } catch { }
                }
                return sum / 1024.0 / 1024.0;
            }
            catch { return 0; }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "-";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        /// <summary>让文件名合法（替换非法字符）。</summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public static IEnumerable<string> EnumerateFilesSafe(string dir, string pattern, SearchOption opt)
        {
            if (!Directory.Exists(Long(dir))) return Enumerable.Empty<string>();
            try { return Directory.EnumerateFiles(Long(dir), pattern, opt).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }
    }
}
