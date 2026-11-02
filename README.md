# PCL Steam 联机修改版

> **第三方修改版声明**
> 本仓库与发布的程序是**第三方基于 [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) 独立进行的二次创作产物**，
> 与 PCL 官方及开发者（龙腾猫跃 / LTCatt）没有任何关联，也不是官方后续版本。
> 本项目基于 PCL `2.13.1.1` 源码修改，新增「Steam 联机」功能，其余功能保持原样。

一个能**双人开黑玩 Minecraft** 的 PCL 启动器改版：不需要公网 IP、不需要内网穿透、不需要自己搭中继服务器，
只要两台电脑都装了 Steam 并互为好友，就能把「单人游戏 → 对局域网开放」的房间变成可以远程加入的房间。

**直接下载**：到 [Releases](https://github.com/chyyyyyyyyyyyyyyyyyy/PCL-Steam-Link/releases) 下最新的
`Plain Craft Launcher 2.exe`，替换掉你原来的 PCL 主程序即可（也可以放在单独文件夹里，它会自己读
`%APPDATA%\.minecraft`，游戏版本 / 存档 / 配置全部沿用）。

> Releases 里的构建由 GitHub Actions 自动产出；如果 Actions 队列还没跑完，也可以直接下载仓库里预编译好的
> [`download/Plain Craft Launcher 2.exe`](https://github.com/chyyyyyyyyyyyyyyyyyy/PCL-Steam-Link/raw/main/download/Plain%20Craft%20Launcher%202.exe)（同一份文件）。

---

## 为什么做这个

PCL 官方版本的「联机」页依赖作者的私有服务器配置和 EasyTier 中继节点，公开源码里这部分是被阉割的
（配置拉取被拦截、节点 API 不可用）。这个改版把联机后端换成了 **Steam 网络（SteamNetworkingSockets）**：
利用 Steam 自带的 P2P / 中继网络做端口转发，从而免去服务器成本，也不依赖任何第三方中继服务。

思路参考了 [moeleak/connecttool-qt](https://github.com/moeleak/connecttool-qt)（同样基于 Steam 网络做 TCP 转发）。

## 功能

- **保留 PCL 2.13.1.1 的全部功能**：启动、下载（含原版游戏下载）、Mod / 整合包 / 资源包 / 光影下载、版本管理、
  正版与离线登录、Mod 管理等。可以从官方版直接替换过去，版本文件夹、存档、配置全部沿用
  （依然是读 `%APPDATA%\.minecraft\versions`）。
- **新增「Steam 联机」**：
  - 建房间：填好你「对局域网开放」后的端口 → 点「创建房间」→ 得到一串房间号（就是你的 SteamID）。
  - 加房间：填入房主的房间号 → 点「加入房间」→ 本地会开一个 `localhost:端口`，在 Minecraft 多人游戏里连它就行。
  - 断线检测、Ping 显示、人数显示、房主离开提示、剪贴板里复制了房间号时自动提示加入。

## 使用方法

两台电脑都需要：

1. 安装并**登录 Steam** 客户端（本程序通过 Steam 网络转发，未登录无法联机）。
2. 在 Steam 里**互为好友**（Steam 的 P2P 需要好友关系才能建立连接）。
3. 双击 `Plain Craft Launcher 2.exe`，顶部导航进入「联机」页。

**房主**

1. 正常启动游戏 → 进入存档 → `Esc` → 「对局域网开放」→ 记下聊天栏里显示的**端口号**（例如 `54321`）。
2. 回到 PCL 的「联机」页 → 「创建房间」→ 输入刚才那个端口 → 确定。
3. 页面会显示你的**房间号**（17 位 SteamID），复制发给朋友。

**加入方**

1. 打开 PCL → 「联机」页 → 「加入房间」→ 粘贴房主的房间号 → 确定。
2. 等页面显示「已加入房间」并给出 `localhost:端口`。
3. 启动游戏 → 多人游戏 → 直接连接 `localhost:端口`（或等它出现在局域网列表里）。

## 构建

### 前置

- Windows 10/11 x64
- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)（用于编译联机模块 `PCLink`）
- **Visual Studio 2022 Build Tools**（含「.NET 桌面开发」工作负载，用于编译 VB.NET/WPF 主程序）

### 一键构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

脚本会：

1. 编译 `PCLink`（.NET 8，自包含单文件 exe）；
2. 把它复制到 `Plain Craft Launcher 2\Resources\PCLink.exe`（作为嵌入资源打进启动器，发布时只需一个 exe）；
3. 用 MSBuild 编译主程序，产物在 `Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe`。

也可以手动分两步：

```powershell
# 1) 联机模块
dotnet publish PCLink\PCLink.csproj -c Release -o out-pclink
Copy-Item out-pclink\PCLink.exe "Plain Craft Launcher 2\Resources\PCLink.exe" -Force

# 2) 启动器
msbuild "Plain Craft Launcher 2.sln" /p:Configuration=Release /p:MSBuildEnableWorkloadResolver=false /m
```

`steam_api64.dll` 不随仓库分发（它是 Valve 的 Steamworks 可再分发库）。可以从
[Steamworks SDK](https://partner.steamgames.com/downloads/list) 或任意使用 Steamworks 的程序目录里取
`steam_api64.dll` 放到 `PCLink\` 目录下；直接使用 Releases 里的成品 exe 则不需要这一步（已内置）。

## 与官方版本的技术差异

| 文件 | 改动 |
| --- | --- |
| `Plain Craft Launcher 2\Pages\PageLink\PageLinkMain.xaml.vb` | 联机页后端整段重写：不再拉取私有 ServerConfig、不再下载/启动 EasyTier，改为释放并运行 `PCLink.exe`，通过 stdout 的 `KEY=VALUE` 协议通信 |
| `Plain Craft Launcher 2\Pages\PageLink\PageLinkMain.xaml` | 文案改为「房间号」 |
| `Plain Craft Launcher 2\Pages\PageSetup\PageSetupUI.xaml.vb` | 恢复「联机」导航按钮的显示逻辑 |
| `Plain Craft Launcher 2\Pages\PageSetup\Settings.vb` | 移除强制隐藏联机页的配置项 |
| `Plain Craft Launcher 2\FormMain.xaml.vb` | 恢复「从剪贴板读取房间号并提示加入」的逻辑 |
| `Plain Craft Launcher 2\Modules\ModSecret.vb` | 填入微软正版登录所需的 Client ID（否则正版登录会报 `invalid_client`） |
| `Plain Craft Launcher 2\Pages\PageOther\PageOtherAbout.xaml.vb` | 关于页标注「第三方 Steam 联机修改版」 |
| `Plain Craft Launcher 2\My Project\Resources.resx` | 增加 `PCLink` 嵌入资源条目 |
| `PCLink/` | 新增：Steam 网络端口转发模块（C#，约 1.8k 行） |

联机模块 `PCLink` 的几种用法：

- `PCLink.exe host --port <游戏端口>`：作为房主，监听 Steam P2P 连接，把收到的字节转发到本机游戏端口。
- `PCLink.exe join --host <房主 SteamID> --port <本地监听端口>`：作为加入方，本地开 TCP 监听，把连接转发给房主。
- `PCLink.exe selftest`：本地回环自测（不需要 Steam，用于验证转发链路是否正常）。

## 许可与署名

- **PCL 本体**：版权归 **龙腾猫跃 (LTCatt)**。本仓库继续沿用上游的 [`LICENCE`](LICENCE)（《PCL 分发有限许可》
  +《PCL 存储库合理使用指南》），并按该指南的重度使用条款：明确标注为第三方二次创作、保留关于页首位的作者署名与赞助链接、
  公开源代码、保留原有正版购买劝导逻辑、不修改界面主色相。**请务必阅读该文件后再使用或再分发。**
  如果你需要 PCL 相关的其他授权，请联系原作者。
- **MeloongCore**（`MeloongCore/`）：来自 [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) 的子模块，
  为了构建方便此处直接内置源码，许可证见 `MeloongCore/LICENSE.txt`。
- **Steamworks.NET**：NuGet 包 `Steamworks.NET 2024.8.0`，MIT 许可。`steam_api64.dll` 版权归 Valve，
  按 Steamworks SDK 的可再分发条款使用。
- **Steam 网络思路参考**：[moeleak/connecttool-qt](https://github.com/moeleak/connecttool-qt)（MIT）。
- `PCLink/` 目录内的代码为本项目新增。

## 免责声明

- 本程序通过 Steam 网络转发流量，本质上与「内网穿透 / 局域网联机工具」类似。
  **异常的网络转发行为存在被 Steam 判定为滥用的风险**（connecttool-qt 的作者也在其 README 中提示过可能封号），
  账号安全自负。
- 请勿用于商业倒卖；也请不要把「联机」功能说成是 PCL 官方提供的能力。
- 联机依赖 Steam 网络的可用性。如果双方网络环境过于严格（对称 NAT 且中继不可用），仍可能连不上，
  这属于 Steam 网络的限制。
