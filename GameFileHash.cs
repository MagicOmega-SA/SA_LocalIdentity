using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MoIdentityTool
{
    /// <summary>
    /// 按客户端自己的规则算出联机房间要比对的文件校验值。
    /// 参考 2021 版 CnCNet 客户端的 FileHashCalculator（见 LICENSE-CNCNET.txt）：
    /// 先逐个文件取 SHA1，再把清单文件与两个 INI 目录的哈希拼起来取一次 SHA1 得到 INI 段，
    /// 最后把九个分段的哈希拼起来再取一次 SHA1。校验值因此是 40 位小写十六进制。
    /// </summary>
    public static class GameFileHash
    {
        /// <summary>客户端读取的文件清单，位于游戏根目录下的 Resources。</summary>
        public const string ConfigRelativePath = "Resources\\FHCConfig.ini";

        /// <summary>
        /// 纯净版 MO 2.6.10.3 客户端的校验值，按上面的规则对纯净版目录算出。
        /// 参照目录算不出值时用它兜底，保证换台机器也能直接进纯净版房间。
        /// </summary>
        public const string CleanInstallHash = "097cd2574a6c6a805a5dbf5473d3bcd8dafd54ee";

        private const string SettingsSection = "Settings";
        private const string FileListSection = "FilenameList";
        private const string CalculateGameExeKey = "CalculateGameExeHash";

        // 客户端在清单之后还会遍历这两个目录，顺序固定为地图代码在前。
        private static readonly string[] TrackedDirectories = { "INI\\Map Code", "INI\\Game Options" };

        // 机器码与校验值的判定都用同一套十六进制规则。
        private static readonly Regex HashPattern = new Regex(@"\A[0-9A-Fa-f]{40}\z", RegexOptions.CultureInvariant);

        /// <summary>判断文本是不是客户端格式的 40 位十六进制校验值。</summary>
        public static bool IsHash(string value)
        {
            return value != null && HashPattern.IsMatch(value.Trim());
        }

        /// <summary>算出指定游戏文件夹的校验值。文件缺失时按客户端的行为跳过。</summary>
        /// <param name="clientRoot">游戏根目录，其下应有 Resources\FHCConfig.ini。</param>
        /// <param name="hash">算出的 40 位小写十六进制校验值。</param>
        /// <param name="error">失败原因，成功时为 null。</param>
        public static bool TryCompute(string clientRoot, out string hash, out string error)
        {
            hash = null;
            error = null;
            if (string.IsNullOrWhiteSpace(clientRoot))
            {
                error = "请选择游戏文件夹。";
                return false;
            }

            string root;
            try
            {
                root = Path.GetFullPath(clientRoot.Trim().Trim('"'));
            }
            catch (ArgumentException)
            {
                error = "文件夹路径无效。";
                return false;
            }
            catch (NotSupportedException)
            {
                error = "文件夹路径无效。";
                return false;
            }

            string configPath = Path.Combine(root, ConfigRelativePath);
            if (!File.Exists(configPath))
            {
                error = "该文件夹里没有 " + ConfigRelativePath + "，不像客户端的游戏目录。";
                return false;
            }

            List<string> listedFiles;
            bool calculateGameExeHash;
            try
            {
                ReadConfig(configPath, out listedFiles, out calculateGameExeHash);
            }
            catch (IOException exception)
            {
                error = "读取 " + ConfigRelativePath + " 失败：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "读取 " + ConfigRelativePath + " 失败：" + exception.Message;
                return false;
            }

            var iniParts = new StringBuilder();
            foreach (string name in listedFiles)
                iniParts.Append(HashFile(Path.Combine(root, name)));

            foreach (string directoryName in TrackedDirectories)
            {
                string directory = Path.Combine(root, directoryName);
                if (!Directory.Exists(directory))
                    continue;

                string[] names = Directory.GetFiles(directory, "*");
                for (int index = 0; index < names.Length; index++)
                    names[index] = Path.GetFileName(names[index]);
                Array.Sort(names, StringComparer.Ordinal);

                foreach (string name in names)
                    iniParts.Append(HashFile(Path.Combine(directory, name)));
            }

            var parts = new StringBuilder();
            parts.Append(HashFile(Path.Combine(root, "Resources\\GameOptions.ini")));
            parts.Append(HashFile(Path.Combine(root, "Resources\\clientdx.exe")));
            parts.Append(HashFile(Path.Combine(root, "Resources\\clientxna.exe")));
            parts.Append(HashFile(Path.Combine(root, "Resources\\clientogl.exe")));
            // 只有配置要求时客户端才算主程序，MO 的配置是 false，这一段留空。
            if (calculateGameExeHash)
                parts.Append(HashFile(Path.Combine(root, "gamemd.exe")));
            parts.Append(HashFile(Path.Combine(root, "Syringe.exe")));
            parts.Append(HashText(iniParts.ToString()));
            parts.Append(HashFile(Path.Combine(root, "INI\\MentalOmegaMaps.ini")));
            parts.Append(HashFile(configPath));

            hash = HashText(parts.ToString());
            return true;
        }

        /// <summary>读出清单文件里的文件名列表与主程序开关，清单顺序按索引号排列。</summary>
        private static void ReadConfig(string configPath, out List<string> listedFiles, out bool calculateGameExeHash)
        {
            listedFiles = new List<string>();
            calculateGameExeHash = false;

            string section = string.Empty;
            var indexed = new List<KeyValuePair<int, string>>();
            foreach (string rawLine in File.ReadAllLines(configPath, Encoding.Default))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (string.Equals(section, SettingsSection, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(key, CalculateGameExeKey, StringComparison.OrdinalIgnoreCase))
                {
                    calculateGameExeHash = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(section, FileListSection, StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    int order;
                    if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out order))
                        order = indexed.Count;
                    indexed.Add(new KeyValuePair<int, string>(order, value));
                }
            }

            indexed.Sort((left, right) => left.Key.CompareTo(right.Key));
            foreach (KeyValuePair<int, string> entry in indexed)
                listedFiles.Add(entry.Value);
        }

        /// <summary>按客户端的方式取文件内容的 SHA1，文件不存在时返回空串。</summary>
        private static string HashFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return string.Empty;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA1 sha = SHA1.Create())
                    return ToHex(sha.ComputeHash(stream));
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        }

        /// <summary>取文本的 SHA1，用于把各段哈希折叠成一段。</summary>
        private static string HashText(string text)
        {
            using (SHA1 sha = SHA1.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
