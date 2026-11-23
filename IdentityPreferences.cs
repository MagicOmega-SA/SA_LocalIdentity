using System;
using System.IO;
using System.Text;

namespace MoIdentityTool
{
    internal sealed class IdentityPreferences
    {
        private readonly string path;

        internal IdentityPreferences(string path)
        {
            this.path = path;
        }

        /// <summary>读取上次保存的设置：机器码、文件哈希、参照目录各占一行。缺失时返回 null。</summary>
        internal string[] Load()
        {
            try
            {
                string[] fields = File.ReadAllLines(path, Encoding.UTF8);
                if (fields.Length == 0)
                    return null;

                string hash = fields.Length > 1 ? fields[1].Trim() : string.Empty;
                string root = fields.Length > 2 ? fields[2].Trim() : string.Empty;
                return new[] { fields[0], hash, root };
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        internal void Save(string ident, string gameHash, string hashRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staged, (ident ?? string.Empty) + Environment.NewLine +
                    (gameHash ?? string.Empty) + Environment.NewLine +
                    (hashRoot ?? string.Empty) + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(staged, path, null);
                else
                {
                    try { File.Move(staged, path); }
                    catch (IOException) when (File.Exists(path)) { File.Replace(staged, path, null); }
                }
            }
            finally
            {
                File.Delete(staged);
            }
        }
    }
}
