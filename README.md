# SA_LocalIdentity

SA_LocalIdentity 是面向《心灵终结》的 Windows IRC 工具。它读取客户端日志，在窗口中显示最近的 CnCNet 连接目标，并按会话记录昵称、机器码和服务端返回的 IP 码。

「CnCNet 最近目标」显示日志里最近一次连接的服务器 IP 和端口。下方表格记录每条会话事件对应的服务器与识别字段，便于在切换服务器后回看。

## 文件说明

从 GitHub Release 下载同一版本的 EXE 和 DLL，将两者放在同一目录，再运行 EXE。若要让工具自动读取客户端连接日志，可以把这两个文件放到游戏根目录。

| 文件 | 用途 |
| --- | --- |
| `SA_LocalIdentity.exe` | Windows Forms 界面，加载同目录下的 `SA_LocalIdentity.dll`。 |
| `SA_LocalIdentity.dll` | IRC 代理和识别码组件。 |
| `SA_LocalIdentity.nolog.exe` 与 `SA_LocalIdentity.nolog.dll` | 无日志版（关闭会话记录表，保留请求改写与守护）。当前构建脚本只产出上面两件，无日志版尚未随构建生成。 |
| `Program.cs` | 窗口、界面交互和状态显示。 |
| `CnCNetLogMonitor.cs` | 读取客户端日志，提取连接地址和 IRC 会话字段。 |
| `IdentityPreferences.cs` | 保存和恢复工具中的识别码输入。 |
| `IdentityRelay.cs` | IRC 代理实现，包括连接管理、识别码改写和回包过滤。 |
| `GameFileHash.cs` | 按客户端的算法计算游戏目录的文件校验值，用于房间一致性。 |
| `HostsRedirector.cs` | 管理工具对 Windows `hosts` 文件的写入和还原。 |
| `Build-Plugin.ps1` | 构建脚本，常规版和无日志版均输出到源码目录。 |

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Plugin.ps1
```

需要 Windows、.NET SDK 和 Windows 自带的 .NET Framework 4.x。构建过程无需安装游戏。

## 快速使用：双击即可

1. 双击 `SA_LocalIdentity.exe`。首次会弹 UAC —— 因为要写 hosts；拒绝授权也能跑，只是需要自己改 hosts（见下）。
2. 窗口一出现，工具就自动开始接管，不需要再点按钮：
   - 机器码留空时，先取 `client.log` 里最近一次 `USER` 报过的值，没有再随机生成 `MO.XXXXXX`；
   - 文件哈希留空时直接用内置的纯净版哈希 `097cd257…`，不按本机目录算；
   - 上游地址留空时按顺序确定：`client.log` 最后一次建连目标 → 解析 GameSurge 域名（`TAL.DE.EU`
     优先，正是 MO 默认入口）→ 默认 `128.140.107.226:6660`；
   - 把 9 个 GameSurge 服务器域名写进 hosts，指向 `127.0.0.1`；
   - 在本机 `6660,6666,6667,6668,6669,7000,8080` 上同时监听（被占用的端口自动跳过，状态栏会写「端口跳过」）。
3. 启动游戏、进 CnCNet 大厅。表格里会出现「改写 USER」「服务器识别码」「改写文件哈希」等记录。
4. 退出工具（或点「停止代理」）时 hosts 里的段落会被自动删掉。若工具被强杀导致残留，
   下次启动会先清理自己留下的段落，不会把游戏永久指向本机。

想改参数就改完再点 **「一键启用」**；界面上的「自动跟随建连日志」勾选后，客户端每次换服务器都会把新的上游地址和端口补进配置。

## 避免「文件不一致」红字

联机大厅会用 CTCP 报文互相核对游戏文件：

- 非房主进房时，客户端向频道发 `NOTICE #频道 :\x01FHSH <40位hex>\x01`（`FHSH` = 文件哈希）。
- 房主收到后与自己的哈希比对，不一致就回 `\x01MM <昵称>\x01`，并在房主端弹红字；同时房主会把该玩家标记为文件不一致。
- 读档大厅走同一套逻辑，只是换成了 `IHSH`。

工具的做法：**只改上报值，不动本地任何文件**。默认上报内置的纯净版哈希，也可以按「参照目录」自己算一个。

1. 「文件哈希」留空时，启动和「一键启用」都用内置的纯净版哈希
   `097cd2574a6c6a805a5dbf5473d3bcd8dafd54ee`，状态栏会写「文件哈希用内置的纯净版值 …」。
2. 想改用别的安装做参照：在「参照目录」里填路径（默认是工具同级/所在的那个 MO，可手填、可点「浏览」），
   再点「计算」——按客户端算法（FHCConfig 列表 + `INI\Map Code` + `INI\Game Options` + 各 exe 的 SHA1 折叠）
   算出 40 位哈希填进「文件哈希」，状态栏会写「已按 … 算出文件哈希 …」。
   目录里没有 `Resources\FHCConfig.ini` 或算不出来时只报错、不改动已有的哈希。也可以直接粘贴 40 位十六进制。
3. 点 **「一键启用」**。代理把本机发出的 `\x01FHSH <自己的哈希>\x01` 换成这个值 —— 房主比对通过，
   **它根本不会发 `MM`，别人也就看不到关于你的提示**。

服务器发来的通知一律原样转给客户端，工具不屏蔽任何回包：如果房主那边仍判定不一致，红字照常显示。

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
- 文件哈希只改**上报值**：本地文件、`FHCConfig.ini` 和游戏目录都不动。因此它让房主「看到一致」，
  但不会让两台机器的文件真的变成一样 —— 房间里的实际行为仍取决于各自安装的内容。
- 代理解析明文 IRC。TLS 加密会话的内容无法通过该解析流程读取。
- 最多同时处理 8 个连接。

## 连接记录

| 列 | 含义 |
| --- | --- |
| 日志时间 | 客户端记录使用 `client.log` 的时间戳，工具记录使用本机时间。 |
| 来源 | 事件类型，例如客户端发出的 `USER`、`改写文件哈希`，或上游连接失败。 |
| 服务器 IP | 该条记录对应的连接目标或上游服务器地址。缺少地址时显示 `—`。 |
| 昵称 | 该条会话记录对应的 IRC 昵称。 |
| 机器码 | 事件所记录的 IRC `ident`，结合「来源」判断取值时点。 |
| IP 码（回包） | IRC 服务端返回的 `hostname`。 |

## 许可证

本项目的原创代码采用 [MIT 许可证](LICENSE)。`GameFileHash.cs` 参考了 2021 版 CnCNet 客户端的 `FileHashCalculator`，其上游版权和 MIT 许可见 [LICENSE-CNCNET.txt](LICENSE-CNCNET.txt)。
