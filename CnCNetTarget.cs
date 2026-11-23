using System;
using System.Net;
using System.Net.Sockets;

namespace MoIdentityTool
{
    /// <summary>
    /// 决定代理要转发到哪台服务器。顺序是：客户端日志里最后一次建连目标 → 域名解析 → 内置地址。
    /// 解析必须在改写 hosts 之前完成，否则 hosts 会把域名指回本机。
    /// </summary>
    internal static class CnCNetTarget
    {
        /// <summary>最近一次连过的 MO 服务器，域名解析失败时兜底。</summary>
        public const string DefaultHost = "128.140.107.226";

        /// <summary>CnCNet 的 IRC 端口。</summary>
        public const int DefaultPort = 6660;

        // 客户端可能解析到的 GameSurge 入口。MO 的服务器列表默认用德国 TAL，放在最前面。
        private static readonly string[] KnownHostNames =
        {
            "TAL.DE.EU.GameSurge.net",
            "Burstfire.UK.EU.GameSurge.net",
            "Gameservers.NJ.US.GameSurge.net",
            "Prothid.NY.US.GameSurge.Net",
            "Krypt.CA.US.GameSurge.net",
            "Portlane.SE.EU.GameSurge.net",
            "ColoCrossing.IL.US.GameSurge.net",
            "NuclearFallout.WA.US.GameSurge.net",
            "irc.gamesurge.net"
        };

        /// <summary>尽力找出上游服务器地址。返回值表示是否用到了日志或解析结果。</summary>
        /// <param name="logPaths">候选的客户端日志路径。</param>
        /// <param name="host">上游主机地址，失败时为内置地址。</param>
        /// <param name="port">上游端口，失败时为 6660。</param>
        public static bool TryFind(string[] logPaths, out string host, out int port)
        {
            host = DefaultHost;
            port = DefaultPort;

            IPEndPoint logged = CnCNetLogMonitor.FindLastTarget(logPaths);
            if (logged != null)
            {
                host = logged.Address.ToString();
                port = logged.Port;
                return true;
            }

            string resolved;
            if (TryResolveDns(out resolved))
            {
                host = resolved;
                return true;
            }

            return false;
        }

        /// <summary>在本机 DNS 还能正常解析时取出服务器的 IPv4 地址。</summary>
        public static bool TryResolveDns(out string address)
        {
            address = null;
            foreach (string name in KnownHostNames)
            {
                try
                {
                    IPAddress[] found = Dns.GetHostAddresses(name);
                    foreach (IPAddress candidate in found)
                    {
                        if (candidate.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        // hosts 已经把它指到本机时不能用，那会把代理指成回环。
                        if (IPAddress.IsLoopback(candidate))
                            continue;

                        address = candidate.ToString();
                        return true;
                    }
                }
                catch (SocketException) { }
                catch (ArgumentException) { }
            }
            return false;
        }
    }
}
