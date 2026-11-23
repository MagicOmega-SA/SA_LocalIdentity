using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MoIdentityTool
{
    internal sealed class CnCNetLogMonitor
    {
        private const int ReadLimit = 64 * 1024;
        private const int CheckpointLength = 32;
        private const int OriginalIdentLookback = 4 * 1024 * 1024;
        private static readonly Regex ConnectionLine = new Regex(
            @"^\s*\d{2}\.\d{2}\.\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+(?:(?<attempt>Attempting connection to)\s+(?<endpoint>\S+)|Success?fully connected to\s+(?<ip>\S+)\s+on port\s+(?<port>\d{1,5}))\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex IrcLogLine = new Regex(
            @"^\s*(?<time>\d{2}\.\d{2}\.\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+(?<direction>SRM|RMP):\s+(?<message>.+)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex SubmittedIdentLine = new Regex(
            @"^\s*\d{2}\.\d{2}\.\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+SRM: USER (?<ident>MO\.[0-9A-Fa-f]{6}) ",
            RegexOptions.CultureInvariant);

        private readonly string path;
        private long position;
        private byte[] checkpoint = new byte[0];
        private string serverIp;
        private string ownNick;
        private string submittedIdent;
        private string submittedAt;

        internal CnCNetLogMonitor(string path)
        {
            this.path = path;
            ReadUpdates(null, null, skipExisting: true);
        }

        internal static string FindLastSubmittedIdent(string[] paths)
        {
            foreach (string path in paths.OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        if (stream.Length > OriginalIdentLookback)
                        {
                            stream.Position = stream.Length - OriginalIdentLookback;
                            reader.ReadLine();
                        }

                        string lastIdent = null;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            Match match = SubmittedIdentLine.Match(line);
                            if (match.Success)
                                lastIdent = match.Groups["ident"].Value;
                        }
                        if (lastIdent != null)
                            return lastIdent;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>从最近改动的日志里找出最后一次建连目标，可跳过本机代理地址。</summary>
        internal static IPEndPoint FindLastTarget(string[] paths, bool upstreamOnly = false)
        {
            foreach (string path in paths.OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        if (stream.Length > OriginalIdentLookback)
                        {
                            stream.Position = stream.Length - OriginalIdentLookback;
                            reader.ReadLine();
                        }

                        IPEndPoint last = null;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            Match match = ConnectionLine.Match(line);
                            IPEndPoint endpoint;
                            if (match.Success && TryReadEndpoint(match, out endpoint) &&
                                (!upstreamOnly || CnCNetTarget.IsUpstreamAddress(endpoint.Address)))
                                last = endpoint;
                        }
                        if (last != null)
                            return last;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        internal string ReadUpdates(Action<string, string, string, string, string, string> record,
            Action<IPEndPoint> connected, bool skipExisting = false)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (skipExisting)
                    {
                        position = stream.Length;
                        SaveCheckpoint(reader, stream);
                        return null;
                    }

                    // The game may delete or truncate the log when it starts again.
                    if (stream.Length < position || !MatchesCheckpoint(reader, stream))
                        Reset();

                    long length = stream.Length;
                    if (length < position)
                        return null;

                    bool discardFirstLine = length - position > ReadLimit;
                    if (discardFirstLine)
                        ResetSession();
                    long start = discardFirstLine ? length - ReadLimit : position;
                    stream.Position = start;
                    byte[] bytes = reader.ReadBytes((int)(length - start));
                    int lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
                    if (lastNewline < 0)
                        return null;

                    position = start + lastNewline + 1;
                    SaveCheckpoint(reader, stream);

                    string latest = null;
                    using (var lines = new StringReader(Encoding.UTF8.GetString(bytes, 0, lastNewline + 1)))
                    {
                        if (discardFirstLine)
                            lines.ReadLine();

                        string line;
                        while ((line = lines.ReadLine()) != null)
                        {
                            string target = ParseTarget(line, connected);
                            if (target != null)
                                latest = target;
                            ReadOwnIdentity(line, record);
                        }
                    }
                    return latest;
                }
            }
            catch (FileNotFoundException) { Reset(); }
            catch (DirectoryNotFoundException) { Reset(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        private bool MatchesCheckpoint(BinaryReader reader, FileStream stream)
        {
            if (position == 0)
                return true;

            stream.Position = position - checkpoint.Length;
            return checkpoint.SequenceEqual(reader.ReadBytes(checkpoint.Length));
        }

        private void SaveCheckpoint(BinaryReader reader, FileStream stream)
        {
            int count = (int)Math.Min(position, CheckpointLength);
            stream.Position = position - count;
            checkpoint = reader.ReadBytes(count);
        }

        private void Reset()
        {
            position = 0;
            checkpoint = new byte[0];
            ResetSession();
        }

        private void ResetSession()
        {
            serverIp = null;
            ownNick = null;
            submittedIdent = null;
            submittedAt = null;
        }

        private string ParseTarget(string line, Action<IPEndPoint> connected)
        {
            Match match = ConnectionLine.Match(line);
            if (!match.Success)
                return null;

            IPEndPoint endpoint;
            if (!TryReadEndpoint(match, out endpoint))
                return null;

            bool attempting = match.Groups["attempt"].Success;
            ResetSession();
            if (!attempting)
            {
                serverIp = endpoint.Address.ToString();
                connected?.Invoke(endpoint);
            }

            return (attempting ? "尝试连接 " : "TCP 已连接 ") + endpoint;
        }

        /// <summary>从建连日志行里取出端点，尝试连接与已连接两种写法都支持。</summary>
        private static bool TryReadEndpoint(Match match, out IPEndPoint endpoint)
        {
            endpoint = null;
            bool attempting = match.Groups["attempt"].Success;
            string addressText = match.Groups["ip"].Value;
            string portText = match.Groups["port"].Value;
            if (attempting)
            {
                string loggedEndpoint = match.Groups["endpoint"].Value;
                int separator = loggedEndpoint.LastIndexOf(':');
                if (separator < 1)
                    return false;

                addressText = loggedEndpoint.Substring(0, separator);
                portText = loggedEndpoint.Substring(separator + 1);
            }

            IPAddress address;
            int port;
            if (!IPAddress.TryParse(addressText.Trim('[', ']'), out address) ||
                !int.TryParse(portText, out port) || port < 1 || port > 65535)
                return false;

            endpoint = new IPEndPoint(address, port);
            return true;
        }

        private void ReadOwnIdentity(string line, Action<string, string, string, string, string, string> record)
        {
            Match entry = IrcLogLine.Match(line);
            if (!entry.Success)
                return;

            string time = entry.Groups["time"].Value;
            string message = entry.Groups["message"].Value;
            if (entry.Groups["direction"].Value == "SRM")
            {
                string[] fields = message.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2)
                    return;

                if (fields[0] == "USER")
                {
                    submittedIdent = fields[1];
                    submittedAt = time;
                }
                else if (fields[0] == "NICK" && (ownNick == null || submittedIdent != null))
                {
                    ownNick = fields[1];
                    RecordSubmission(record);
                }
                return;
            }

            string[] reply = message.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (reply.Length != 3)
                return;

            string[] parameters = reply[2].Split(',');
            if (reply[1] == "001")
            {
                ownNick = parameters[0];
                RecordSubmission(record);
                return;
            }

            if (reply[1] == "352" && parameters.Length >= 7 && IsOwnNick(parameters[5]))
            {
                record?.Invoke(time, "收到 WHO", serverIp, ownNick, parameters[2], parameters[3]);
                return;
            }

            string[] prefix = reply[0].TrimStart(':').Split(new[] { '!', '@' }, 3);
            if (prefix.Length != 3 || !IsOwnNick(prefix[0]))
                return;

            if (reply[1] == "JOIN" || reply[1] == "PART")
                record?.Invoke(time, "收到 " + reply[1], serverIp, ownNick, prefix[1], prefix[2]);
            else if (reply[1] == "NICK")
                ownNick = parameters[0];
        }

        private bool IsOwnNick(string nick)
        {
            return !string.IsNullOrEmpty(ownNick) && string.Equals(ownNick, nick, StringComparison.OrdinalIgnoreCase);
        }

        private void RecordSubmission(Action<string, string, string, string, string, string> record)
        {
            if (submittedIdent == null || string.IsNullOrEmpty(ownNick))
                return;

            record?.Invoke(submittedAt, "发出 USER", serverIp, ownNick, submittedIdent, "—");
            submittedIdent = null;
            submittedAt = null;
        }
    }
}
