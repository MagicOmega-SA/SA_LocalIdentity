using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Windows.Forms;

namespace MoIdentityTool
{
    /// <summary>启动本机识别码改写工具的 Windows Forms 程序。</summary>
    internal static class Program
    {
        // Windows Forms 主线程使用单线程单元，Application.Run 负责窗口消息循环。
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 改 hosts 需要管理员权限，启动时提权一次，之后写删全程静默。
            bool relaunched = args.Any(arg => string.Equals(arg, "--elevated", StringComparison.Ordinal));
            if (!HostsRedirector.IsElevated() && !relaunched && TryRelaunchElevated())
                return 0;

            Application.Run(new IdentityForm());
            return 0;
        }

        /// <summary>以管理员身份重启自己。用户取消 UAC 时返回 false，继续以普通权限运行。</summary>
        private static bool TryRelaunchElevated()
        {
            try
            {
                var info = new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--elevated"
                };
                Process.Start(info);
                return true;
            }
            catch (Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (SecurityException) { return false; }
        }
    }

    /// <summary>一键把游戏服务器指向本机，并改写 USER 行里的机器码。</summary>
    internal sealed class IdentityForm : Form
    {
#if SA_LOG
        private const int MaxObservations = 200;
#endif
        private const string DefaultPorts = "6660,6666,6667,6668,6669,7000,8080";

        // 机器码的长度上限包含可选的 ~ 前缀。
        private readonly TextBox machine = new TextBox { Dock = DockStyle.Fill, MaxLength = 10 };
        // 房间比对用的文件校验值，留空则只改机器码。
        private readonly TextBox gameHash = new TextBox
        {
            AccessibleName = "房间文件哈希",
            Dock = DockStyle.Fill,
            MaxLength = 40
        };
        // 参照用的游戏目录，手动填写；默认取工具同级的 Mental Omega。
        private readonly TextBox hashRoot = new TextBox
        {
            AccessibleName = "哈希参照目录",
            Dock = DockStyle.Fill
        };
        private readonly TextBox listenAddress = new TextBox
        {
            AccessibleName = "监听 IP",
            Text = "127.0.0.1",
            Width = 220,
            Anchor = AnchorStyles.Left
        };
        // 客户端会在多个 IRC 端口之间挑一个，这里一次监听整组端口。
        private readonly TextBox portInput = new TextBox
        {
            AccessibleName = "监听端口",
            Text = DefaultPorts,
            Dock = DockStyle.Fill
        };
        private readonly TextBox upstreamHost = new TextBox
        {
            AccessibleName = "上游服务器",
            Dock = DockStyle.Fill,
            MaxLength = 255
        };
        private readonly NumericUpDown upstreamPort = new NumericUpDown
        {
            AccessibleName = "上游端口",
            Minimum = 1,
            Maximum = 65535,
            Value = 6660,
            Width = 132,
            Anchor = AnchorStyles.Left
        };
        private readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label cncnetTarget = MakeLabel("CnCNet 目标：等待客户端连接日志");
        private readonly Timer logPollTimer = new Timer { Interval = 750 };
        private readonly CnCNetLogMonitor[] logMonitors;
        private readonly string[] logPaths;
        private readonly IdentityPreferences preferences;
        private readonly CheckBox autoApply = new CheckBox { Text = "自动跟随建连日志", AutoSize = true };
        private readonly Button start = new Button { Text = "一键启用", AutoSize = true };
        private readonly Button stop = new Button { Text = "停止代理", AutoSize = true, Enabled = false };
#if SA_LOG
        private readonly ListView observations = new ListView
        {
            AccessibleName = "识别码改写与服务器回包记录",
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
#endif
        private readonly bool elevated;
        private IdentityRelay relay;
        private string appliedPorts = string.Empty;
        private string defaultHashSource = string.Empty;
        private bool defaultHashBuiltIn;
        private bool autoStarted;
        private bool waitingForOriginalIdent;
        private bool hostsApplied;
        private int hostCount;

        /// <summary>创建界面、绑定操作，并恢复上次关闭时的识别码。</summary>
        public IdentityForm()
            : this(GetClientLogPaths(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SA_LocalIdentity", "identity.txt"), HostsRedirector.IsElevated())
        {
        }

        internal IdentityForm(string[] clientLogPaths, string settingsPath, bool elevated)
        {
            this.elevated = elevated;
            logPaths = clientLogPaths;
            Text = "心灵终结识别码测试工具";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 10F);
#if SA_LOG
            ClientSize = new Size(1000, 560);
            MinimumSize = new Size(880, 460);
#else
            ClientSize = new Size(1000, 420);
            MinimumSize = new Size(880, 400);
#endif

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 3, RowCount = 11 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(layout);

            AddFullWidth(layout, MakeLabel("点「一键启用」就完成全部准备：把 GameSurge 服务器域名指向本机、自动填好上游、开始改写 USER 里的机器码。关闭窗口或点「停止代理」会自动还原 hosts。"), 0);
            layout.Controls.Add(MakeLabel("监听地址"), 0, 1);
            layout.Controls.Add(listenAddress, 1, 1);
            layout.Controls.Add(MakeLabel("监听端口"), 0, 2);
            layout.Controls.Add(portInput, 1, 2);
            layout.SetColumnSpan(portInput, 2);
            layout.Controls.Add(MakeLabel("上游地址"), 0, 3);
            layout.Controls.Add(upstreamHost, 1, 3);
            layout.Controls.Add(upstreamPort, 2, 3);
            AddField(layout, 4, "机器码", machine, () => machine.Text = IdentityProfile.RandomIdent());
            AddField(layout, 5, "文件哈希", gameHash, "计算", ChooseReferenceRoot);
            AddField(layout, 6, "参照目录", hashRoot, "浏览", BrowseReferenceRoot);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            Button randomBoth = MakeButton("随机生成", Randomize);
            buttons.Controls.AddRange(new Control[] { start, stop, autoApply, randomBoth });
            start.Click += (sender, args) => StartListening();
            // 停止时顺手还原 hosts，避免游戏指向一个已经没人监听的端口。
            stop.Click += (sender, args) =>
            {
                autoApply.Checked = false;
                StopServer();
            };
            // 勾选表示“看到新的建连日志就跟上去”，日志里出现新目标时自动启用。
            autoApply.CheckedChanged += (sender, args) =>
            {
                if (autoApply.Checked)
                    StartListening();
            };
            AddFullWidth(layout, buttons, 7);
            AddFullWidth(layout, status, 8);
            AddFullWidth(layout, cncnetTarget, 9);
#if SA_LOG
            observations.Columns.Add("日志时间", 135);
            observations.Columns.Add("来源", 94);
            observations.Columns.Add("服务器 IP", 150);
            observations.Columns.Add("昵称", 140);
            observations.Columns.Add("机器码", 110);
            observations.Columns.Add("IP 码（回包）", 245);
            AddFullWidth(layout, observations, 10);
#endif

            preferences = new IdentityPreferences(settingsPath);
            RestoreIdentity(clientLogPaths);
            CleanupStaleHosts();
            FormClosing += (sender, args) =>
            {
                SaveIdentity();
                RevertHosts();
            };
            logMonitors = clientLogPaths.Select(path => new CnCNetLogMonitor(path)).ToArray();
            logPollTimer.Tick += (sender, args) => RefreshCnCNetTarget();
            logPollTimer.Start();
            Disposed += (sender, args) =>
            {
                logPollTimer.Dispose();
                relay?.Dispose();
                relay = null;
            };
        }

        private void RestoreIdentity(string[] clientLogPaths)
        {
            string[] saved = null;
            try
            {
                saved = preferences.Load();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                status.Text = "读取识别码设置失败。" + error.Message;
            }

            machine.Text = saved != null && saved.Length > 0 ? saved[0] : string.Empty;
            gameHash.Text = saved != null && saved.Length > 1 ? saved[1] : string.Empty;
            hashRoot.Text = saved != null && saved.Length > 2 && saved[2].Length > 0
                ? saved[2]
                : DefaultReferenceRoot();
            // 没填过文件哈希时，按参照目录算一次；目录不可用就留空，由用户手动指定。
            bool hashPrepared = EnsureDefaultHash();
            if (string.IsNullOrWhiteSpace(machine.Text))
                machine.Text = CnCNetLogMonitor.FindLastSubmittedIdent(clientLogPaths) ?? string.Empty;

            // 上游只在没填过的时候猜一次，之后以日志里的最新目标为准。
            IPEndPoint last = CnCNetLogMonitor.FindLastTarget(clientLogPaths);
            if (last != null)
            {
                upstreamHost.Text = last.Address.ToString();
                upstreamPort.Value = last.Port;
                AddPort(last.Port);
                cncnetTarget.Text = "CnCNet 最近目标：" + last;
            }

            waitingForOriginalIdent = string.IsNullOrWhiteSpace(machine.Text);
            if (status.Text.Length == 0)
            {
                if (hashPrepared)
                {
                    status.Text = defaultHashBuiltIn
                        ? "文件哈希用内置的纯净版值 " + gameHash.Text + "，点「一键启用」。"
                        : "已按 " + defaultHashSource + " 算出文件哈希 " + gameHash.Text + "，点「一键启用」。";
                    if (waitingForOriginalIdent)
                        status.Text += " 机器码等原版客户端日志提供。";
                }
                else if (waitingForOriginalIdent)
                    status.Text = "等待原版客户端日志提供机器码。";
                else
                    status.Text = "准备好后点「一键启用」。";
            }

            // 窗口一出现就自动接管，省掉一次点击。用户随时可以点「停止代理」。
            Shown += (sender, args) =>
            {
                if (autoStarted)
                    return;
                autoStarted = true;
                StartListening();
            };
        }

        /// <summary>文件哈希为空时直接用内置的纯净版哈希；「参照目录」只在手动点「计算」时使用。</summary>
        private bool EnsureDefaultHash()
        {
            if (!string.IsNullOrWhiteSpace(gameHash.Text))
                return false;

            gameHash.Text = GameFileHash.CleanInstallHash;
            defaultHashSource = "内置的纯净版哈希";
            defaultHashBuiltIn = true;
            return true;
        }

        /// <summary>按「参照目录」算一次文件哈希，目录为空时先提示补上。</summary>
        private void ChooseReferenceRoot()
        {
            string reference = (hashRoot.Text ?? string.Empty).Trim();
            if (reference.Length == 0)
            {
                status.Text = "先在「参照目录」里填游戏目录，例如 <游戏目录>\\Mental Omega。";
                return;
            }

            string hash;
            string error;
            if (!GameFileHash.TryCompute(reference, out hash, out error))
            {
                status.Text = "计算文件哈希失败：" + error;
                return;
            }

            gameHash.Text = hash;
            defaultHashSource = reference;
            status.Text = "已按 " + reference + " 算出文件哈希 " + hash + "。";
        }

        /// <summary>浏览选择参照用的游戏文件夹。</summary>
        private void BrowseReferenceRoot()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择参照用的游戏文件夹（里面应有 Resources\\FHCConfig.ini）";
                dialog.ShowNewFolderButton = false;
                string current = (hashRoot.Text ?? string.Empty).Trim();
                if (current.Length > 0 && Directory.Exists(current))
                    dialog.SelectedPath = current;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                hashRoot.Text = dialog.SelectedPath;
                ChooseReferenceRoot();
            }
        }

        /// <summary>默认参照目录：工具同级的 Mental Omega；找不到就用工具所在目录。</summary>
        private static string DefaultReferenceRoot()
        {
            string folder = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                folder,
                Path.Combine(folder, "..", "Mental Omega"),
                Path.Combine(folder, "Mental Omega"),
                Path.Combine(folder, "..", "..", "Mental Omega")
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(Path.Combine(full, GameFileHash.ConfigRelativePath)))
                        return full;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }

            try
            {
                return Path.GetFullPath(Path.Combine(folder, "..", "Mental Omega"));
            }
            catch (ArgumentException)
            {
                return folder;
            }
            catch (NotSupportedException)
            {
                return folder;
            }
        }

        /// <summary>上次异常退出可能残留 hosts 段落，启动时先清掉，否则游戏连不上服务器。</summary>
        private void CleanupStaleHosts()
        {
            if (!elevated || !HostsRedirector.IsManaged(HostsRedirector.HostsPath))
                return;

            string error;
            if (HostsRedirector.Revert(HostsRedirector.HostsPath, out error))
                status.Text = "已清理上次退出时残留的 hosts 条目。";
            else
                status.Text = "清理残留 hosts 失败：" + error;
        }

        private void SaveIdentity()
        {
            try { preferences.Save(machine.Text, gameHash.Text, hashRoot.Text); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "保存识别码失败。" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string[] GetClientLogPaths()
        {
            string folder = AppDomain.CurrentDomain.BaseDirectory;
            return new[] { folder, Path.Combine(folder, "..", "Mental Omega"), Path.Combine(Path.GetPathRoot(folder), "Mental Omega") }
                .Select(root => Path.GetFullPath(Path.Combine(root, "Client", "client.log")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void RefreshCnCNetTarget()
        {
            foreach (CnCNetLogMonitor log in logMonitors)
            {
                string target = log.ReadUpdates(AddLogObservation, FillListeningEndpoint);
                if (target != null)
                    cncnetTarget.Text = "CnCNet 最近目标：" + target;
            }
        }

        private void FillListeningEndpoint(IPEndPoint remote)
        {
            // 已在改写时保留用户设定，避免新日志打断正在使用的连接。
            if (relay == null)
            {
                upstreamHost.Text = remote.Address.ToString();
                upstreamPort.Value = Math.Max(1, Math.Min(65535, remote.Port));
                AddPort(remote.Port);
            }

            if (autoApply.Checked)
                ApplyProfile();
        }

        /// <summary>把日志里出现的端口补进监听列表，客户端拨哪个都能落到代理上。</summary>
        private void AddPort(int port)
        {
            if (port < 1 || port > 65535)
                return;

            string error;
            int[] ports = ParsePorts(portInput.Text, out error);
            if (error != null)
                return;
            if (ports.Contains(port))
                return;

            portInput.Text = string.Join(",", ports.Concat(new[] { port }).Select(value => value.ToString()).ToArray());
        }

        /// <summary>解析逗号分隔的端口列表。</summary>
        private static int[] ParsePorts(string text, out string error)
        {
            error = null;
            var ports = new List<int>();
            string[] parts = (text ?? string.Empty).Split(new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                int value;
                if (!int.TryParse(part, out value) || value < 1 || value > 65535)
                {
                    error = "监听端口请填 1 到 65535 之间的数字，多个用逗号分隔：" + part;
                    return new int[0];
                }
                if (!ports.Contains(value))
                    ports.Add(value);
            }

            if (ports.Count == 0)
            {
                error = "监听端口不能为空，例如 6660,6667。";
                return new int[0];
            }
            return ports.ToArray();
        }

        /// <summary>在指定行放置一个带独立随机按钮的输入字段。</summary>
        private static void AddField(TableLayoutPanel layout, int row, string name, TextBox field, Action randomize)
        {
            AddField(layout, row, name, field, "随机生成", randomize);
        }

        /// <summary>在指定行放置一个输入字段，右侧按钮的文字与操作由调用方决定。</summary>
        private static void AddField(TableLayoutPanel layout, int row, string name, TextBox field, string buttonText, Action handler)
        {
            layout.Controls.Add(MakeLabel(name), 0, row);
            layout.Controls.Add(field, 1, row);
            layout.Controls.Add(MakeButton(buttonText, handler), 2, row);
        }

        /// <summary>让说明、状态和按钮区横跨表格的所有列。</summary>
        private static void AddFullWidth(TableLayoutPanel layout, Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, layout.ColumnCount);
        }

        /// <summary>创建铺满单元格并左侧垂直居中的标签。</summary>
        private static Label MakeLabel(string label)
        {
            return new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }

        /// <summary>创建随文字自动调整宽度并绑定点击操作的按钮。</summary>
        private static Button MakeButton(string label, Action handler)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (sender, args) => handler();
            return button;
        }

        /// <summary>重新生成机器码样本。</summary>
        private void Randomize()
        {
            machine.Text = IdentityProfile.RandomIdent();
        }

        /// <summary>一键启用：补齐识别码与上游，接管 hosts，然后开始改写。</summary>
        private void StartListening()
        {
            if (string.IsNullOrWhiteSpace(machine.Text))
                machine.Text = CnCNetLogMonitor.FindLastSubmittedIdent(logPaths) ?? IdentityProfile.RandomIdent();

            if (string.IsNullOrWhiteSpace(upstreamHost.Text))
                PrepareUpstream();

            // 用户清空过文件哈希时，默认补内置的纯净版哈希，保证一键启用就会改写校验值。
            EnsureDefaultHash();

            ApplyHosts();
            ApplyProfile();
        }

        /// <summary>补齐上游地址：先看日志，再解析域名，最后退回内置地址。</summary>
        private void PrepareUpstream()
        {
            string host;
            int port;
            bool found = CnCNetTarget.TryFind(logPaths, out host, out port);

            upstreamHost.Text = host;
            upstreamPort.Value = Math.Max(1, Math.Min(65535, port));
            AddPort(port);
            if (!found)
                status.Text = "没读到客户端日志，先按内置地址 " + host + ":" + port + " 转发。";
        }

        /// <summary>把服务器域名指向本机。没有管理员权限时只提示，不阻断改写。</summary>
        private void ApplyHosts()
        {
            if (!elevated)
            {
                hostsApplied = false;
                hostCount = 0;
                return;
            }

            string[] hostnames = HostsRedirector.MergeHostnames(HostsRedirector.HostnamesFromLogs(logPaths));
            string error;
            if (HostsRedirector.Apply(HostsRedirector.HostsPath, hostnames, out error))
            {
                hostsApplied = true;
                hostCount = hostnames.Length;
            }
            else
            {
                hostsApplied = false;
                hostCount = 0;
                status.Text = "写 hosts 失败：" + error;
            }
        }

        /// <summary>删除本工具写入的 hosts 段落。</summary>
        private void RevertHosts()
        {
            if (!hostsApplied)
                return;

            string error;
            if (!HostsRedirector.Revert(HostsRedirector.HostsPath, out error))
            {
                status.Text = "还原 hosts 失败：" + error + "。请手动删除 " + HostsRedirector.BeginMarker +
                    " 到 " + HostsRedirector.EndMarker + " 之间的行。";
            }
            hostsApplied = false;
            hostCount = 0;
        }

        /// <summary>按服务运行状态切换开始与停止按钮的可用性。</summary>
        private void UpdateServerButtons()
        {
            bool running = relay != null;
            start.Enabled = !running;
            stop.Enabled = running;
        }

        /// <summary>校验并应用界面中的设置，按需创建或复用改写代理。</summary>
        private void ApplyProfile()
        {
            IPAddress address;
            if (!IPAddress.TryParse(listenAddress.Text.Trim(), out address))
            {
                status.Text = "请输入有效的 IPv4 或 IPv6 监听地址。";
                UpdateServerButtons();
                return;
            }

            string portsError;
            int[] ports = ParsePorts(portInput.Text, out portsError);
            if (portsError != null)
            {
                status.Text = portsError;
                UpdateServerButtons();
                return;
            }

            string upstream = upstreamHost.Text.Trim();
            int upstreamPortValue = (int)upstreamPort.Value;
            if (upstream.Length == 0)
            {
                status.Text = "请填写上游 CnCNet 服务器地址，或先让客户端连一次，工具会从日志自动填入。";
                UpdateServerButtons();
                return;
            }
            if (IsUnderHostsRedirect(upstream))
            {
                status.Text = "上游地址不能填已被 hosts 指向本机的域名，请填服务器的真实 IP。";
                UpdateServerButtons();
                return;
            }
            if (IsSelfLoop(address, ports, upstream, upstreamPortValue))
            {
                status.Text = "上游服务器指向了本机同一端口，会形成回环。请填写真实服务器地址与端口。";
                UpdateServerButtons();
                return;
            }

            // 在更新服务前构造经过校验的配置。输入有误时只显示验证信息。
            IdentityProfile requested;
            try
            {
                requested = new IdentityProfile(machine.Text);
            }
            catch (ArgumentException error)
            {
                status.Text = error.Message;
                UpdateServerButtons();
                return;
            }

            // 回填规范化后的字段，例如把六位十六进制简写扩展为 MO.XXXXXX。
            machine.Text = requested.Ident;
            string hashValue = gameHash.Text.Trim();
            if (hashValue.Length > 0 && !GameFileHash.IsHash(hashValue))
            {
                status.Text = "文件哈希请填 40 位十六进制，或点「计算」从游戏文件夹生成；留空则只改机器码。";
                UpdateServerButtons();
                return;
            }
            hashValue = hashValue.ToLowerInvariant();
            gameHash.Text = hashValue;
            string portsKey = string.Join(",", ports.Select(value => value.ToString()).ToArray());
            try
            {
                bool changed = relay == null ||
                    !address.Equals(relay.Address) ||
                    !string.Equals(portsKey, appliedPorts, StringComparison.Ordinal) ||
                    !relay.UpstreamHost.Equals(upstream, StringComparison.OrdinalIgnoreCase) ||
                    relay.UpstreamPort != upstreamPortValue;
                if (changed)
                {
                    // 新代理构造成功后才替换旧代理。绑定失败时旧连接仍可使用。
                    var created = new IdentityRelay(address, ports, requested, upstream, upstreamPortValue, hashValue);
#if SA_LOG
                    created.Observation += AddRelayObservation;
#endif
                    IdentityRelay previous = relay;
                    relay = created;
                    if (previous != null)
                        previous.Dispose();
                }
                else
                {
                    relay.Apply(requested);
                    relay.ApplyGameHash(hashValue);
                }

                appliedPorts = portsKey;
                listenAddress.Text = relay.Address.ToString();
                status.Text = "正在改写 " + relay.Address + ":" + appliedPorts + " → 上游 " + relay.UpstreamHost + ":" +
                    relay.UpstreamPort + "，识别码 " + requested.Ident + "。";
                if (hashValue.Length > 0)
                    status.Text += " 房间文件哈希按 " + hashValue.Substring(0, 8) + "… 上报，进房间不会再报文件不一致。";
                if (hostsApplied)
                    status.Text += " 已把 " + hostCount + " 个服务器域名指向本机，退出时自动还原。";
                else if (!elevated)
                    status.Text += " 未以管理员运行，未改 hosts；需要手动把服务器域名指向 127.0.0.1。";
                UpdateServerButtons();
            }
            catch (SocketException error)
            {
                status.Text = "无法监听 " + address + ":" + appliedPorts + "。" + error.Message;
                if (relay != null)
                    status.Text += " 当前仍在监听 " + relay.Address + ":" + string.Join(",", relay.Ports.Select(value => value.ToString()).ToArray()) + "。";
                UpdateServerButtons();
            }
            catch (InvalidOperationException error)
            {
                status.Text = error.Message;
                UpdateServerButtons();
            }
            catch (ArgumentException error)
            {
                status.Text = error.Message;
                UpdateServerButtons();
            }
        }

        /// <summary>判断上游是不是被 hosts 接管了的域名，那样会转回自己。</summary>
        private bool IsUnderHostsRedirect(string upstream)
        {
            return HostsRedirector.MergeHostnames(HostsRedirector.HostnamesFromLogs(logPaths))
                .Any(host => string.Equals(host, upstream, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>判断上游是否指向本机同一端口，避免代理把连接转发给自己。</summary>
        private static bool IsSelfLoop(IPAddress listen, int[] ports, string upstream, int upstreamPortValue)
        {
            if (!ports.Contains(upstreamPortValue))
                return false;

            IPAddress target;
            if (IPAddress.TryParse(upstream, out target))
                return IPAddress.IsLoopback(target) || target.Equals(IPAddress.Any) || target.Equals(listen);
            return string.Equals(upstream, "localhost", StringComparison.OrdinalIgnoreCase);
        }

#if SA_LOG
        /// <summary>把改写代理的后台记录转发到界面线程。</summary>
        private void AddRelayObservation(string time, string action, string remote, string nick, string value, string detail)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string, string, string, string, string, string>(AddRelayObservation),
                        new object[] { time, action, remote, nick, value, detail });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }

            AddLogObservation(time, action, remote, nick, value, detail);
        }
#endif

        private void AddLogObservation(string time, string direction, string serverIp, string nick, string ident, string hostname)
        {
            if (waitingForOriginalIdent && direction == "发出 USER" && ident.Length == 9 &&
                ident.StartsWith("MO.", StringComparison.Ordinal) && ident.Skip(3).All(Uri.IsHexDigit))
            {
                waitingForOriginalIdent = false;
                if (string.IsNullOrWhiteSpace(machine.Text))
                {
                    machine.Text = ident;
                    if (autoApply.Checked)
                        ApplyProfile();
                    else
                        status.Text = "已从客户端日志读取机器码。";
                }
            }

#if SA_LOG
            observations.Items.Insert(0, new ListViewItem(new[] { time, direction, serverIp ?? "—", nick, ident, hostname }));
            if (observations.Items.Count > MaxObservations)
                observations.Items.RemoveAt(MaxObservations);
#endif
        }

        /// <summary>释放当前改写代理，并还原 hosts。</summary>
        private void StopServer()
        {
            if (relay != null)
            {
                relay.Dispose();
                relay = null;
            }

            bool reverted = hostsApplied;
            RevertHosts();
            UpdateServerButtons();
            status.Text = reverted ? "代理已停止，hosts 已还原。" : "代理已停止。";
        }
    }
}
