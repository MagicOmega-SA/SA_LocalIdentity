# SA_LocalIdentity

SA_LocalIdentity 是面向《心灵终结》的 Windows IRC 工具。它读取客户端日志，在窗口中显示最近的 CnCNet 连接目标，并按会话记录昵称、机器码和服务端返回的 IP 码。

「CnCNet 最近目标」显示日志里最近一次连接的服务器 IP 和端口。下方表格记录每条会话事件对应的服务器与识别字段，便于在切换服务器后回看。

## 文件说明

| 文件 | 用途 |
| --- | --- |
| `SA_LocalIdentity.exe` | Windows Forms 界面，加载同目录下的 `SA_LocalIdentity.dll`。 |
| `SA_LocalIdentity.dll` | IRC 代理和识别码组件。 |
| `Program.cs` | 窗口、界面交互和状态显示。 |
| `CnCNetLogMonitor.cs` | 读取客户端日志，提取连接地址和 IRC 会话字段。 |
| `IdentityPreferences.cs` | 保存和恢复工具中的识别码输入。 |
| `IdentityRelay.cs` | IRC 代理实现，包括连接管理和回包解析。 |
| `HostsRedirector.cs` | 管理工具对 Windows `hosts` 文件的写入和还原。 |
| `Build-Plugin.ps1` | 构建脚本，产物同时复制到 `..\Mental Omega`。 |

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Plugin.ps1
```

需要 `dotnet`（用它的 `csc.dll`），以 `/warnaserror+` 编译。

## 快速使用：一键启用

1. 双击 `SA_LocalIdentity.exe`。首次会弹 UAC —— 因为要写 hosts；拒绝授权也能跑，只是需要自己改 hosts（见下）。
2. 点 **「一键启用」**。工具会自动：
   - 机器码留空时随机生成 `MO.XXXXXX`；
   - 上游地址留空时从 `Mental Omega\Client\client.log` 最后一次建连目标里取（如 `128.140.107.226:6660`）；
   - 把 9 个 GameSurge 服务器域名写进 hosts，指向 `127.0.0.1`；
   - 在本机 `6660,6666,6667,6668,6669,7000,8080` 上同时监听（被占用的端口自动跳过，状态栏会写「端口跳过」）。
3. 启动游戏、进 CnCNet 大厅。表格里会出现「改写 USER」「服务器识别码」两类记录。
4. 退出工具（或点「停止代理」）时 hosts 里的段落会被自动删掉。若工具被强杀导致残留，
   下次启动会先清理自己留下的段落，不会把游戏永久指向本机。

界面上的「自动跟随建连日志」勾选后，客户端每次换服务器都会把新的上游地址和端口补进配置。

## 手动配置（不想用 hosts 自动改写时）

- 「监听 IP」默认 `127.0.0.1`，「监听端口」可填多个（逗号分隔），默认 `6660,6666,6667,6668,6669,7000,8080`。
- 「上游地址/端口」填真实服务器，可从「CnCNet 最近目标」标签或 `Client\client.log` 里读。
- 让游戏连本机的方式：把客户端会解析的域名写进 `C:\Windows\System32\drivers\etc\hosts`（需管理员）：

  ```
  127.0.0.1  irc.gamesurge.net
  127.0.0.1  Burstfire.UK.EU.GameSurge.net
  127.0.0.1  TAL.DE.EU.GameSurge.net
  127.0.0.1  NuclearFallout.WA.US.GameSurge.net
  127.0.0.1  Prothid.NY.US.GameSurge.Net
  127.0.0.1  Krypt.CA.US.GameSurge.net
  127.0.0.1  Gameservers.NJ.US.GameSurge.net
  127.0.0.1  Portlane.SE.EU.GameSurge.net
  127.0.0.1  ColoCrossing.IL.US.GameSurge.net
  ```

  具体域名看 `Client\client.log` 里的 `Attempting to DNS resolve <名字> (<域名>)`。

## 能力边界

- 可以改写的是 `USER` 行里的**机器码**（`MO.XXXXXX` / `CnCNet.xxxxxx` 这类前缀会保留，只换最后一段十六进制）。
  服务器记录的 ident 就是改后的值 —— 别人 `WHO` 你看到的是新机器码。
- **IP 码（`hostname`）**由 IRC 服务端分配，可能根据连接的来源 IP 生成。界面显示的是服务端回包中的值。
- 代理解析明文 IRC。TLS 加密会话的内容无法通过该解析流程读取。
- 最多同时处理 8 个连接。

## 连接记录

| 列 | 含义 |
| --- | --- |
| 日志时间 | 客户端记录使用 `client.log` 的时间戳，工具记录使用本机时间。 |
| 来源 | 事件类型，例如客户端发出的 `USER`、服务端回包或上游连接失败。 |
| 服务器 IP | 该条记录对应的连接目标或上游服务器地址。缺少地址时显示 `—`。 |
| 昵称 | 该条会话记录对应的 IRC 昵称。 |
| 机器码 | 事件所记录的 IRC `ident`，结合「来源」判断取值时点。 |
| IP 码（回包） | IRC 服务端返回的 `hostname`。 |