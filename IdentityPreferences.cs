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

        /// <summary>读取上次保存的机器码。缺失时返回 null。</summary>
        internal string Load()
        {
            try
            {
                string[] fields = File.ReadAllLines(path, Encoding.UTF8);
                return fields.Length > 0 ? fields[0] : null;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        internal void Save(string ident)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staged, (ident ?? string.Empty) + Environment.NewLine, new UTF8Encoding(false));
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
