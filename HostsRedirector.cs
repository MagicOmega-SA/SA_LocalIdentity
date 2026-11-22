using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MoIdentityTool
{
    /// <summary>把 CnCNet 服务器域名指向本机，并在退出时清理。只改动自己标记的那一段 hosts。</summary>
    internal static class HostsRedirector
    {
        internal const string BeginMarker = "# >>> SA_LocalIdentity >>>";
        internal const string EndMarker = "# <<< SA_LocalIdentity <<<";

        // 客户端服务器列表里常见的 GameSurge 入口；日志里发现的新域名会自动追加。
        internal static readonly string[] KnownHosts =
        {
            "irc.gamesurge.net",
            "Burstfire.UK.EU.GameSurge.net",
            "TAL.DE.EU.GameSurge.net",
            "NuclearFallout.WA.US.GameSurge.net",
            "Prothid.NY.US.GameSurge.Net",
            "Krypt.CA.US.GameSurge.net",
            "Gameservers.NJ.US.GameSurge.net",
            "Portlane.SE.EU.GameSurge.net",
            "ColoCrossing.IL.US.GameSurge.net"
        };

        private const int LogLookback = 4 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Regex ServerNameLine = new Regex(
            @"Attempting to DNS resolve [^()]*\((?<host>[A-Za-z0-9][A-Za-z0-9.\-]*)\)",
            RegexOptions.CultureInvariant);
        private static readonly Regex IPv4Literal = new Regex(@"\A\d{1,3}(?:\.\d{1,3}){3}\z", RegexOptions.CultureInvariant);

        /// <summary>系统 hosts 文件路径。</summary>
        internal static string HostsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts"); }
        }

        /// <summary>修改前的 hosts 备份，只写一次，便于人工核对。</summary>
        internal static string BackupPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SA_LocalIdentity", "hosts.backup.txt");
            }
        }

        /// <summary>当前进程是否以管理员身份运行。改 hosts 需要这个权限。</summary>
        internal static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>判断 hosts 内容里是否已经带着本工具写入的段落。</summary>
        internal static bool ContainsBlock(string content)
        {
            return !string.IsNullOrEmpty(content) && content.IndexOf(BeginMarker, StringComparison.Ordinal) >= 0;
        }

        /// <summary>去掉本工具写入的段落，保留其它内容。</summary>
        internal static string RemoveBlock(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content ?? string.Empty;

            var kept = new List<string>();
            bool inside = false;
            foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed == BeginMarker)
                {
                    inside = true;
                    continue;
                }
                if (trimmed == EndMarker)
                {
                    inside = false;
                    continue;
                }
                if (!inside)
                    kept.Add(line);
            }
            return string.Join("\r\n", kept.ToArray());
        }

        /// <summary>把指定域名全部指向 127.0.0.1，替换掉旧的同名段落。</summary>
        internal static string AddBlock(string content, IEnumerable<string> hostnames)
        {
            string stripped = RemoveBlock(content);
            var builder = new StringBuilder(stripped);
            if (builder.Length > 0 && builder[builder.Length - 1] != '\n')
                builder.Append("\r\n");

            builder.Append(BeginMarker).Append("\r\n");
            foreach (string host in Normalize(hostnames))
                builder.Append("127.0.0.1 ").Append(host).Append("\r\n");
            builder.Append(EndMarker).Append("\r\n");
            return builder.ToString();
        }

        /// <summary>把域名列表整理成可写进 hosts 的形式。</summary>
        internal static string[] Normalize(IEnumerable<string> hostnames)
        {
            if (hostnames == null)
                return new string[0];

            var result = new List<string>();
            foreach (string raw in hostnames)
            {
                string host = (raw ?? string.Empty).Trim().TrimEnd('.');
                if (host.Length == 0 || host.Length > 253)
                    continue;
                if (host.IndexOf('.') < 0 || IPv4Literal.IsMatch(host))
                    continue;
                if (!result.Contains(host, StringComparer.OrdinalIgnoreCase))
                    result.Add(host);
            }
            return result.ToArray();
        }

        /// <summary>写入 hosts。失败时返回 false 并给出原因。</summary>
        internal static bool Apply(string path, IEnumerable<string> hostnames, out string error)
        {
            try
            {
                string original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                string updated = AddBlock(original, hostnames);
                SaveBackup(original);
                WriteAllText(path, updated);
                error = null;
                return true;
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException ||
                                            failure is SecurityException || failure is NotSupportedException)
            {
                error = failure.Message;
                return false;
            }
        }

        /// <summary>从 hosts 里删掉本工具写入的段落。</summary>
        internal static bool Revert(string path, out string error)
        {
            try
            {
                error = null;
                if (!File.Exists(path))
                    return true;
                string original = File.ReadAllText(path);
                if (!ContainsBlock(original))
                    return true;
                WriteAllText(path, RemoveBlock(original));
                return true;
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException ||
                                            failure is SecurityException || failure is NotSupportedException)
            {
                error = failure.Message;
                return false;
            }
        }

        /// <summary>判断 hosts 里是否留着本工具上一次写入的段落。</summary>
        internal static bool IsManaged(string path)
        {
            try
            {
                return File.Exists(path) && ContainsBlock(File.ReadAllText(path));
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException ||
                                            failure is SecurityException || failure is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>内置域名与客户端日志里出现过的域名合并去重。</summary>
        internal static string[] MergeHostnames(IEnumerable<string> extra)
        {
            return Normalize(KnownHosts.Concat(extra ?? Enumerable.Empty<string>()));
        }

        /// <summary>从客户端日志里找出客户端会去解析的服务器域名。</summary>
        internal static string[] HostnamesFromLogs(IEnumerable<string> logPaths)
        {
            var found = new List<string>();
            if (logPaths == null)
                return found.ToArray();

            foreach (string path in logPaths)
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        string tail;
                        if (stream.Length > LogLookback)
                        {
                            stream.Seek(-LogLookback, SeekOrigin.End);
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                reader.ReadLine();
                                tail = reader.ReadToEnd();
                            }
                        }
                        else
                        {
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                                tail = reader.ReadToEnd();
                        }

                        foreach (Match match in ServerNameLine.Matches(tail))
                            found.Add(match.Groups["host"].Value);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return Normalize(found);
        }

        private static void SaveBackup(string original)
        {
            try
            {
                string backup = BackupPath;
                if (File.Exists(backup))
                    return;
                string folder = Path.GetDirectoryName(backup);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                File.WriteAllText(backup, original, Utf8NoBom);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void WriteAllText(string path, string content)
        {
            FileAttributes attributes = FileAttributes.Normal;
            bool restoreAttributes = false;
            if (File.Exists(path))
            {
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    restoreAttributes = true;
                }
            }

            try
            {
                File.WriteAllText(path, content, Utf8NoBom);
            }
            finally
            {
                if (restoreAttributes)
                {
                    try { File.SetAttributes(path, attributes); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
