using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MoIdentityTool
{
    /// <summary>保存一组校验过的机器码。构造完成后可在线程之间共享。</summary>
    public sealed class IdentityProfile
    {
        /// <summary>用于替换 USER 行的机器码，保留输入中可选的 ~ 前缀。</summary>
        public string Ident { get; private set; }

        /// <summary>去掉首尾空白，并按工具支持的格式校验机器码。</summary>
        /// <param name="ident">六位十六进制简写，或带 MO. 前缀的机器码。完整写法支持可选的 ~。</param>
        /// <exception cref="ArgumentException">格式不符合要求。</exception>
        public IdentityProfile(string ident)
        {
            ident = (ident ?? string.Empty).Trim();
            // 简写仅补 MO. 前缀。格式校验忽略大小写，保存时保留原有字符大小写。
            if (Regex.IsMatch(ident, @"\A[0-9A-Fa-f]{6}\z"))
                ident = "MO." + ident;

            if (!Regex.IsMatch(ident, @"\A(?:~)?MO\.[0-9A-Fa-f]{6}\z", RegexOptions.IgnoreCase))
                throw new ArgumentException("机器码请输入 6 位十六进制，或完整的 MO.XXXXXX 格式。");

            Ident = ident;
        }

        /// <summary>生成带 MO. 前缀和六位大写十六进制字符的机器码。</summary>
        public static string RandomIdent()
        {
            return "MO." + RandomHex(3);
        }

        /// <summary>生成指定字节数的随机值，每个字节编码为两位大写十六进制。</summary>
        private static string RandomHex(int bytes)
        {
            byte[] buffer = new byte[bytes];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(buffer);
            return BitConverter.ToString(buffer).Replace("-", "");
        }
    }

    /// <summary>
    /// 把客户端的 IRC 连接转发到上游 CnCNet 服务器，并把 USER 行里的机器码换成配置值。
    /// 多个端口同时监听，客户端无论拨 6660 还是 6667 都能落到代理上。
    /// </summary>
    public sealed class IdentityRelay : IDisposable
    {
        // 同时转发的连接上限，超出后直接关闭新连接。
        private const int MaxClients = 8;

        // gate 同时保护连接集合和 disposed，界面线程与连接线程都会访问。
        private readonly List<TcpListener> listeners = new List<TcpListener>();
        private readonly List<int> ports = new List<int>();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly object gate = new object();
        // UI 线程可更新配置，连接线程读取其引用。
        private volatile IdentityProfile profile;
        private readonly string upstreamHost;
        private readonly int upstreamPort;
        private bool disposed;

        /// <summary>监听启动后绑定的实际地址。</summary>
        public IPAddress Address { get; private set; }
        /// <summary>实际绑定成功的全部端口，按构造顺序排列。</summary>
        public IReadOnlyList<int> Ports { get { return ports; } }
        /// <summary>实际绑定成功的第一个端口。</summary>
        public int Port { get { return ports.Count == 0 ? 0 : ports[0]; } }
        /// <summary>当前转发的上游主机名或地址。</summary>
        public string UpstreamHost { get { return upstreamHost; } }
        /// <summary>当前转发的上游端口。</summary>
        public int UpstreamPort { get { return upstreamPort; } }
        /// <summary>转发过程中的记录，参数依次为时间、动作、对方、昵称、值、说明。</summary>
        public event Action<string, string, string, string, string, string> Observation;

        /// <summary>绑定单个本机监听端口，并把之后的连接转发到指定上游服务器。</summary>
        /// <param name="listenAddress">本机监听地址，0.0.0.0 表示全部网卡。</param>
        /// <param name="listenPort">本机监听端口，传入 0 由系统选择空闲端口。</param>
        /// <param name="initialProfile">改写 USER 时使用的机器码。</param>
        /// <param name="upstreamHost">上游 CnCNet 服务器地址。</param>
        /// <param name="upstreamPort">上游 CnCNet 服务器端口。</param>
        /// <exception cref="ArgumentException">上游地址、端口或识别码无效。</exception>
        public IdentityRelay(IPAddress listenAddress, int listenPort, IdentityProfile initialProfile, string upstreamHost, int upstreamPort)
            : this(listenAddress, new[] { listenPort }, initialProfile, upstreamHost, upstreamPort)
        {
        }

        /// <summary>同时监听多个端口。个别端口被占用时跳过，全部失败才报错。</summary>
        /// <exception cref="InvalidOperationException">没有任何端口能绑定。</exception>
        public IdentityRelay(IPAddress listenAddress, IEnumerable<int> listenPorts, IdentityProfile initialProfile, string upstreamHost, int upstreamPort)
        {
            if (listenAddress == null)
                throw new ArgumentNullException("listenAddress");
            if (listenPorts == null)
                throw new ArgumentNullException("listenPorts");
            if (initialProfile == null)
                throw new ArgumentNullException("initialProfile");
            if (string.IsNullOrWhiteSpace(upstreamHost))
                throw new ArgumentException("上游服务器地址不能为空。", "upstreamHost");
            if (upstreamPort < 1 || upstreamPort > 65535)
                throw new ArgumentException("上游服务器端口必须在 1 到 65535 之间。", "upstreamPort");

            int[] wanted = listenPorts.Where(port => port >= 0 && port <= 65535).Distinct().ToArray();
            if (wanted.Length == 0)
                throw new ArgumentException("监听端口必须在 1 到 65535 之间。", "listenPorts");

            profile = initialProfile;
            this.upstreamHost = upstreamHost.Trim();
            this.upstreamPort = upstreamPort;
            Address = listenAddress;

            foreach (int candidate in wanted)
            {
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(listenAddress, candidate);
                    listener.ExclusiveAddressUse = true;
                    listener.Start(MaxClients);
                }
                catch (SocketException error)
                {
                    if (listener != null)
                    {
                        try { listener.Stop(); }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }
                    }

                    // 端口被别的程序占着不影响其它端口，记一条记录继续。
                    Report("端口跳过", listenAddress + ":" + candidate, string.Empty, candidate.ToString(), error.Message);
                    continue;
                }

                listeners.Add(listener);
                ports.Add(((IPEndPoint)listener.LocalEndpoint).Port);
            }

            if (listeners.Count == 0)
                throw new InvalidOperationException("这些端口都无法监听：" +
                    string.Join("、", wanted.Select(port => port.ToString()).ToArray()) + "，可能已被其它程序占用。");

            foreach (TcpListener listener in listeners)
                AcceptClients(listener);
        }

        /// <summary>替换改写用的机器码。后续每一个 USER 行都会使用新值。</summary>
        public void Apply(IdentityProfile updatedProfile)
        {
            if (updatedProfile != null)
                profile = updatedProfile;
        }

        /// <summary>在单个监听端口上持续接受连接，直至监听停止或套接字报错。</summary>
        private async void AcceptClients(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (SocketException) { return; }
                // 监听停止后待决的接受操作会以已释放或无效状态结束，都属于正常退出。
                catch (InvalidOperationException) { return; }

                lock (gate)
                {
                    if (disposed || clients.Count >= MaxClients)
                    {
                        client.Close();
                        continue;
                    }
                    clients.Add(client);
                }

                Relay(client);
            }
        }

        /// <summary>为一个客户端建立上游连接并双向转发，结束后关闭两侧套接字。</summary>
        private async void Relay(TcpClient client)
        {
            string remote = Describe(client);
            TcpClient upstream = null;
            try
            {
                upstream = new TcpClient();
                await upstream.ConnectAsync(upstreamHost, upstreamPort).ConfigureAwait(false);
                var state = new RelayState();
                Report("连接转入", remote, string.Empty, upstreamHost + ":" + upstreamPort, "开始转发");
                Task fromClient = Pump(client, upstream, true, remote, state);
                Task toClient = Pump(upstream, client, false, remote, state);
                await Task.WhenAny(fromClient, toClient).ConfigureAwait(false);
            }
            catch (SocketException error)
            {
                Report("上游连接失败", remote, string.Empty, upstreamHost + ":" + upstreamPort, error.Message);
            }
            finally
            {
                Close(upstream);
                lock (gate) { clients.Remove(client); }
                Close(client);
            }
        }

        /// <summary>按行转发一个方向的数据。rewrite 为 true 时改写客户端发出的 USER 行。</summary>
        private async Task Pump(TcpClient from, TcpClient to, bool rewrite, string remote, RelayState state)
        {
            StreamReader reader;
            StreamWriter writer;
            try
            {
                reader = new StreamReader(from.GetStream(), new UTF8Encoding(false));
                writer = new StreamWriter(to.GetStream(), new UTF8Encoding(false));
                writer.AutoFlush = true;
                writer.NewLine = "\r\n";
            }
            catch (IOException) { return; }
            catch (InvalidOperationException) { return; }

            while (true)
            {
                string line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (IOException) { return; }
                catch (InvalidOperationException) { return; }

                if (line == null)
                    return;

                string outgoing;
                if (rewrite)
                    outgoing = Rewrite(line, state);
                else
                {
                    ObserveServerReply(line, state);
                    outgoing = line;
                }

                try
                {
                    await writer.WriteLineAsync(outgoing).ConfigureAwait(false);
                }
                catch (IOException) { return; }
                catch (InvalidOperationException) { return; }
            }
        }

        /// <summary>记录客户端昵称，并把 USER 行里的机器码换成当前配置值。</summary>
        private string Rewrite(string line, RelayState state)
        {
            if (line.StartsWith("NICK ", StringComparison.OrdinalIgnoreCase))
            {
                state.Nick = line.Substring(5).Trim();
                return line;
            }

            if (!line.StartsWith("USER ", StringComparison.OrdinalIgnoreCase))
                return line;

            int identStart = line.IndexOf(' ') + 1;
            int identEnd = line.IndexOf(' ', identStart);
            if (identStart <= 0 || identEnd < 0)
                return line;

            string original = line.Substring(identStart, identEnd - identStart);
            string replaced = RewriteIdent(original);
            if (string.Equals(replaced, original, StringComparison.Ordinal))
                return line;

            Report("改写 USER", upstreamHost + ":" + upstreamPort, state.Nick, replaced, original);
            return line.Substring(0, identStart) + replaced + line.Substring(identEnd);
        }

        /// <summary>保留行内的游戏标识前缀，只替换最后的六位机器码。</summary>
        private string RewriteIdent(string token)
        {
            IdentityProfile current = profile;
            if (current == null)
                return token;

            string target = current.Ident;
            if (target.StartsWith("~", StringComparison.Ordinal))
                target = target.Substring(1);
            int targetDot = target.LastIndexOf('.');
            string code = targetDot >= 0 ? target.Substring(targetDot + 1) : target;

            int dot = token.LastIndexOf('.');
            if (dot < 0)
                return target;
            return token.Substring(0, dot + 1) + code;
        }

        /// <summary>从服务器回包中取出本连接被分配的完整识别码，昵称后的第一处 @ 即主机名。</summary>
        private void ObserveServerReply(string line, RelayState state)
        {
            if (string.IsNullOrEmpty(state.Nick))
                return;

            int index = line.IndexOf(state.Nick + "!", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return;

            int marker = index + state.Nick.Length + 1;
            int at = line.IndexOf('@', marker);
            if (at < 0)
                return;

            int end = at + 1;
            while (end < line.Length && line[end] != ' ' && line[end] != ',')
                end++;

            string hostname = line.Substring(at + 1, end - at - 1);
            if (!hostname.EndsWith(".IP4", StringComparison.OrdinalIgnoreCase))
                return;

            string ident = line.Substring(marker, at - marker);
            if (string.Equals(state.Hostname, hostname, StringComparison.Ordinal) &&
                string.Equals(state.Ident, ident, StringComparison.Ordinal))
                return;

            state.Hostname = hostname;
            state.Ident = ident;
            Report("服务器识别码", upstreamHost + ":" + upstreamPort, state.Nick, ident, hostname);
        }

        /// <summary>向观察者推送一条转发记录。</summary>
        private void Report(string action, string remote, string nick, string value, string detail)
        {
            Action<string, string, string, string, string, string> handler = Observation;
            if (handler == null)
                return;
            handler(DateTime.Now.ToString("HH:mm:ss.fff"), action, remote, nick, value, detail);
        }

        /// <summary>取得远端地址文本，用于记录。</summary>
        private static string Describe(TcpClient client)
        {
            try
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                return endpoint == null ? "unknown" : endpoint.ToString();
            }
            catch (SocketException) { return "unknown"; }
            catch (ObjectDisposedException) { return "unknown"; }
        }

        /// <summary>关闭套接字，忽略已经关闭的情况。</summary>
        private static void Close(TcpClient client)
        {
            if (client == null)
                return;
            try { client.Close(); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>停止全部监听并关闭转发连接。重复调用会直接返回。</summary>
        public void Dispose()
        {
            TcpClient[] snapshot;
            TcpListener[] stopping;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                snapshot = clients.ToArray();
                clients.Clear();
                stopping = listeners.ToArray();
                listeners.Clear();
            }

            foreach (TcpListener listener in stopping)
            {
                try { listener.Stop(); }
                catch (SocketException) { }
                catch (InvalidOperationException) { }
            }

            foreach (TcpClient client in snapshot)
                Close(client);
        }

        /// <summary>单个转发连接内共享的状态，用于记录昵称和最近一次服务器分配的识别码。</summary>
        private sealed class RelayState
        {
            public string Nick = string.Empty;
            public string Ident;
            public string Hostname;
        }
    }
}
