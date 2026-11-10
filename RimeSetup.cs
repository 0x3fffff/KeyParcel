using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KeyParcel
{
    internal static class RimeSetup
    {
        internal const string InstallerName = "weasel-0.17.4.0-installer.exe";
        private const string InstallerHash = "CF509534A8F5F8AF9C98ED7CBB8F135439F145A8CBE7E50EDE42BB5B5AB45C29";
        private const string RegistryPath = @"SOFTWARE\Rime\Weasel";
        private const string Processor = "lua_processor@*keyparcel_input";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string FindDeployer()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = root.OpenSubKey(RegistryPath))
                {
                    var dir = key == null ? null : key.GetValue("InstallDir") as string;
                    if (String.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    var direct = Path.Combine(dir, "WeaselDeployer.exe");
                    if (File.Exists(direct)) return direct;
                    var versioned = Directory.GetDirectories(dir, "weasel-*")
                        .Select(path => new { Path = path, Version = ReadVersion(Path.GetFileName(path).Substring(7)) })
                        .Where(item => item.Version != null).OrderByDescending(item => item.Version)
                        .Select(item => Path.Combine(item.Path, "WeaselDeployer.exe")).FirstOrDefault(File.Exists);
                    if (versioned != null) return versioned;
                }
            return null;
        }

        private static Version ReadVersion(string text)
        {
            Version version;
            return Version.TryParse(text, out version) ? version : null;
        }

        internal static int Install(string installer)
        {
            using (var stream = File.OpenRead(installer))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (hash != InstallerHash) throw new InvalidOperationException("小狼毫安装包校验失败，请使用官方 0.17.4 安装包。");
                return Run(installer, "", true);
            }
        }

        internal static int Import(string deployer)
        {
            string user = UserDirectory();
            if (!Path.IsPathRooted(user)) throw new InvalidOperationException("找不到小狼毫用户目录。");
            var built = Path.Combine(user, "build");
            var defaults = Path.Combine(built, "default.yaml");
            if (!File.Exists(defaults)) Deploy(deployer);
            if (!File.Exists(defaults)) throw new InvalidOperationException("小狼毫尚未生成方案，请在小狼毫菜单里选择「重新部署」后重试。");
            var schemes = EnabledSchemes(ReadUtf8(defaults));
            if (schemes.Length == 0) throw new InvalidOperationException("小狼毫还没有启用的输入方案。请先在小狼毫设置里选一个方案。");

            var updates = new Dictionary<string, string>();
            foreach (string scheme in schemes)
            {
                var path = Path.Combine(user, scheme + ".custom.yaml");
                string old = File.Exists(path) ? ReadUtf8(path) : "";
                string updated;
                if (!TryAddProcessor(old, out updated))
                    throw new InvalidOperationException("配置格式需手动合并：" + Path.GetFileName(path) + "。请查看 Rime 安装说明。");
                if (updated != old) updates.Add(path, updated);
            }

            string lua = Path.Combine(user, "lua", "keyparcel_input.lua");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeyParcel.Rime.lua"))
            using (var reader = new StreamReader(stream, Utf8))
            {
                string source = reader.ReadToEnd();
                if (!File.Exists(lua) || ReadUtf8(lua) != source) updates.Add(lua, source);
            }
            foreach (var change in updates)
            {
                var backup = change.Key + ".keyparcel.bak";
                if (File.Exists(change.Key) && !File.Exists(backup)) File.Copy(change.Key, backup);
                MobileInbox.SaveSetting(change.Key, change.Value);
            }
            Deploy(deployer);
            foreach (string scheme in schemes)
                if (!HasProcessor(built, scheme))
                    throw new InvalidOperationException("未能确认「" + scheme + "」已部署，请打开小狼毫的「重新部署」后重试。");
            return schemes.Length;
        }

        internal static bool IsReady()
        {
            try { return FindDeployer() != null && HasDeployedExtension(UserDirectory()); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        internal static bool HasDeployedExtension(string user)
        {
            var built = Path.Combine(user, "build");
            var defaults = Path.Combine(built, "default.yaml");
            if (!File.Exists(Path.Combine(user, "lua", "keyparcel_input.lua")) || !File.Exists(defaults)) return false;
            var schemes = EnabledSchemes(ReadUtf8(defaults));
            return schemes.Length != 0 && schemes.All(scheme => HasProcessor(built, scheme));
        }

        private static bool HasProcessor(string built, string scheme)
        {
            var deployed = Path.Combine(built, scheme + ".schema.yaml");
            return File.Exists(deployed) && ReadUtf8(deployed).Contains(Processor);
        }

        private static string UserDirectory()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
            {
                var user = key == null ? null : key.GetValue("RimeUserDir") as string;
                return String.IsNullOrWhiteSpace(user)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rime") : user;
            }
        }

        internal static string[] EnabledSchemes(string yaml)
        {
            var list = Regex.Match(yaml, @"(?m)^schema_list:[ \t]*\r?\n(?<items>(?:[ \t]+[^\r\n]*\r?\n?)*)");
            return Regex.Matches(list.Groups["items"].Value, @"(?m)^[ \t]+-[ \t]+schema:[ \t]+([a-zA-Z0-9][a-zA-Z0-9_-]*)[ \t]*\r?$")
                .Cast<Match>().Select(item => item.Groups[1].Value).Distinct().ToArray();
        }

        internal static bool TryAddProcessor(string yaml, out string result)
        {
            result = yaml;
            if (yaml.Contains(Processor)) return true;
            string newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
            string entry = "  \"engine/processors/@before 0\": " + Processor + newline;
            if (String.IsNullOrWhiteSpace(yaml)) { result = "patch:" + newline + entry; return true; }
            if (Regex.IsMatch(yaml, @"(?m)^\.\.\.[ \t\r]*$") || Regex.Matches(yaml, @"(?m)^---[ \t\r]*$").Count > 1)
                return false;
            var patch = Regex.Match(yaml, @"(?m)^patch:[ \t]*(?:#[^\r\n]*)?\r?\n");
            if (!patch.Success || yaml.Contains("engine/processors/@before 0")) return false;
            var firstChild = Regex.Match(yaml.Substring(patch.Index + patch.Length), @"(?m)^(?![ \t]*(?:#|\r?$))(?<indent> *)\S");
            if (firstChild.Success && firstChild.Groups["indent"].Length != 2) return false;
            result = yaml.Insert(patch.Index + patch.Length, entry);
            return true;
        }

        private static string ReadUtf8(string path)
        {
            using (var reader = new StreamReader(path, Utf8, true)) return reader.ReadToEnd();
        }

        private static void Deploy(string deployer)
        {
            if (Run(deployer, "/deploy", false) != 0)
                throw new InvalidOperationException("小狼毫重新部署未完成。请等它结束后重试。");
        }

        private static int Run(string path, string args, bool interactive)
        {
            var info = new ProcessStartInfo(path, args)
            {
                UseShellExecute = interactive,
                WorkingDirectory = Path.GetDirectoryName(path),
                WindowStyle = interactive ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                CreateNoWindow = !interactive
            };
            try
            {
                using (var process = Process.Start(info))
                {
                    if (process == null) throw new InvalidOperationException("无法启动小狼毫。");
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223) return 1223;
                throw new InvalidOperationException("无法启动小狼毫安装或部署程序。", error);
            }
        }
    }
}
