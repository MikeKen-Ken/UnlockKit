<p align="center">
  <a href="README.md"><b>简体中文</b></a> · <a href="README.en.md">English</a>
</p>

<p align="center">
  <img src="assets/icon.png" width="112" alt="UnlockKit icon">
</p>

<h1 align="center">UnlockKit</h1>

<p align="center">
  <b>解锁、并强制删除 Windows 不肯放手的文件。</b><br>
  先告诉你是哪个进程占着，再动手清掉 —— 窗口、命令行、右键菜单，三种方式都行。
</p>

<p align="center">
  <a href="https://github.com/MikeKen-Ken/UnlockKit/actions/workflows/build.yml"><img src="https://github.com/MikeKen-Ken/UnlockKit/actions/workflows/build.yml/badge.svg" alt="build"></a>
  <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-4B5563.svg" alt="Windows 10/11 x64">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4.svg" alt=".NET 9">
</p>

---

<p align="center">
  <img src="assets/screenshots/light.png" width="410" alt="UnlockKit 浅色界面">
  <img src="assets/screenshots/dark.png" width="410" alt="UnlockKit 深色界面">
  <br>
  <sub>浅色 / 深色 —— 这是一次真实扫描：<code>explorer.exe</code> 和 <code>TSVNCache.exe</code> 占着 <code>D:\archive\cc1</code> 里的 7 个文件</sub>
</p>

---

## 问题出在哪

你想删一个文件夹，Windows 只回你一句：

> 操作无法完成，因为文件夹或其中的文件已在另一个程序中打开
> 拒绝访问
> 另一个程序正在使用此文件，进程无法访问。

大多数工具的做法是甩给你一个「解锁」按钮。点下去，失败了；重启之后能删了 —— 但你始终不知道刚才到底发生了什么。

UnlockKit 先给诊断，再动手。它枚举全系统打开的文件句柄，按**卷序列号 + 文件 ID** 匹配你的目标（不是比字符串，所以硬链接、`\\?\` 别名、盘符别名都骗不过它），明确告诉你五种可能原因里，你中的是哪一个。只有这一步之后它才行动 —— 用能生效的最轻手段，而不是最猛的那个。

## 它能做什么

- **指名道姓。** 进程名、PID，以及它到底开着了哪些句柄、拿到了什么访问权限。不会再有「某个程序」。
- **扫描文件夹内部。** 目标是目录时，它遍历整棵子树。因为文件夹本身几乎从来不是被占用的那个对象 —— 被占的是里面的某个子文件，而监视器（SVN/Git 图标缓存、杀毒、同步盘、资源管理器缩略图）会在毫秒级重新打开它。
- **用 POSIX 语义删除**，即使还有别的句柄开着，文件名也能立刻解除链接、字节在最后一个句柄关闭时释放。只读属性一并忽略，因为这在同一次调用里就能指定。
- **关闭阻塞的句柄** —— 光靠 POSIX 不够时才做。每关一个之前都会重新校验它指向的还是不是那个对象，所以被回收复用的句柄号绝不可能误伤别的东西。
- **结束进程**作为最后手段，并且做不到时如实说明（`PPL`、僵尸进程、权限不足）。
- **安排重启时删除** —— 当这个锁从用户态确实破不了，就通过 `PendingFileRenameOperations` 交给下次开机，在 `SMSS` 阶段执行。
- **右键集成** —— 一个 `UnlockKit` 父项，下挂 `Unlock` 和 `Delete`，各带图标；只写当前用户，全程不需要管理员权限。
- **深色模式** —— 首次启动跟随系统（任务栏里的浅色/深色），右下角一键切换，右键那个开关可以选「跟随 Windows」；选择存在 `HKCU\Software\UnlockKit`，标题栏也一起变深，不会出现白色边框配黑窗口。

## 安装

从 [Releases](https://github.com/MikeKen-Ken/UnlockKit/releases) 下载 `UnlockKit.exe` —— 单个自包含文件，不需要装 .NET 运行时，约 68 MB。

然后二选一：

- **直接运行。** 它是绿色版，可以把文件或文件夹拖进窗口，也可以直接当参数传进去。
- **点窗口里的 `Install context menu` 按钮。** 它会把可执行文件复制到 `%LOCALAPPDATA%\Programs\UnlockKit\UnlockKit.exe`，并写入当前用户的 `HKCU` 菜单项。再点一次（此时按钮显示 `Remove context menu`）就能撤掉。

> **Windows 11 注意：** 这是传统注册表菜单项，所以会出现在**「显示更多选项」**里（或者按 `Shift+F10`）。要进一级菜单，必须用 MSIX 打包并带应用标识的 `IExplorerCommand` —— 这是 shell 的策略，不是注册表能绕过去的，为一个工具去背打包的副作用也不值。

> **未签名：** 没有代码签名证书，所以第一次运行 SmartScreen 会提示「Windows 已保护你的电脑」。点「更多信息 → 仍要运行」，或者干脆用页面最下方那两条命令自己从源码构建。

> **界面目前只有英文。** 功能、菜单、日志都是英文；这份说明是中文的。

## 快速上手

```
1. 把删不掉的文件夹拖进窗口
2. Scan            -> "LOCKED (subtree): 5 file(s) inside are held by 1 process"
                      TSVNCache.exe  pid 8124
                        0x1234  READ|WRITE|DELETE
                        -> D:\archive\ccccc\assets\config.json
3. Unlock          -> 关掉这些句柄，文件夹本身不动
4. Delete          -> 没了
```

## 命令行

引擎同时是一个独立控制台程序 —— `unlockkit-cli.exe`，同一个发布页。适合写进脚本、构建步骤和 CI。

```console
unlockkit-cli "D:\archive\ccccc"                  # 只诊断（默认）
unlockkit-cli diagnose "D:\archive\ccccc"         # 同上，写明动作
unlockkit-cli delete   "D:\archive\ccccc"         # 立即删除（POSIX 语义）
unlockkit-cli delete   "D:\archive\ccccc" --reboot  # 允许排到下次开机删除
unlockkit-cli close    "D:\archive\ccccc"         # 只关句柄，保留文件
unlockkit-cli kill     "D:\archive\ccccc"         # 结束占用的进程
```

| 参数 | 含义 |
| --- | --- |
| `--json` | 机器可读输出，每次运行一个对象 |
| `-v`, `--verbose` | 打印每个句柄的完整对象路径 |
| `--reboot` | 允许使用 `PendingFileRenameOperations` 兜底 |
| `-h`, `--help` | 显示用法 |

退出码是稳定的，可以直接 `if errorlevel` 判断：

| 退出码 | 含义 |
| --- | --- |
| `0` | 成功，或者本来就没被占用 |
| `1` | 被占用 —— 动作被拒绝或失败 |
| `2` | 参数错误，或路径无法读取 |

## 它怎么判断

诊断是一个**结论**，不是一个布尔值。结论决定接下来做什么。

| 结论（实际输出） | 含义 | UnlockKit 的处理 |
| --- | --- | --- |
| `NOT LOCKED` | 没有用户态句柄占着，且 `DELETE` 权限可用 | 正常删除 |
| `LOCKED but deletable` | 有句柄，但对方开了 `FILE_SHARE_DELETE` | 一次 POSIX 删除即可，不解任何句柄 |
| `LOCKED` | 句柄**没开** share-delete | 关掉这些句柄，再 POSIX 删除 |
| `LOCKED (subtree)` | 文件夹本身空闲，但里面的文件被占 | 一次性关掉整棵子树，自底向上删 |
| `ACL BLOCKS DELETE` | 没人占用；这个账户缺 `DELETE` 权限 | 拒绝，并告诉你这是权限问题，不是锁 |
| `KERNEL-HELD LOCK` | `ERROR_SHARING_VIOLATION`，但没有任何可关的句柄 | 拒绝（除非你主动要求排到开机时删） |

最后一行是关键。当文件被过滤驱动占住，**没有任何用户态工具能破** —— 不是这个，也不是别的。UnlockKit 会直接说清楚，而不是假装失败：一个锁只有一个原因，含糊其辞正是那些「解锁器」口碑崩掉的原因。

## 它做不到的事

现实里删不掉的情况，大约一半能从用户态解决。剩下一半是结构性的，点多少次都没用：

- **内核驱动持有** —— 杀毒软件的 minifilter、备份代理、云同步客户端在过滤驱动里持有文件。用户态关不掉那些句柄，重启删除是唯一出口。
- **映像节被映射** —— 正在运行的 `.exe`、已加载的 `.dll` 删不掉。返回 `STATUS_CANNOT_DELETE`，而且这个行为是对的。
- **PPL 保护进程** —— `MsMpEng`、`services`、`lsass` 这类进程受保护，即使有 `SeDebugPrivilege` 也打不开。UnlockKit 会按名单拒绝，并告诉你它的保护级别。
- **正在终止的进程** —— 卡在终止流程里的进程，句柄要等内核回收才释放。等，或者重启。
- **ACL 问题** —— 缺 `DELETE` 权限是权限问题。UnlockKit 能准确诊断，但不会背着你偷偷夺取文件所有权。

拒绝碰 `System`、`csrss`、`wininit`、`winlogon`、`services`、`lsass` 和杀毒引擎，是**故意的**，不是待修复的限制。在那些进程里关句柄，轻则桌面无响应，重则直接蓝屏。

## 从源码构建

要求：Windows 10/11 x64、[.NET 9 SDK](https://dotnet.microsoft.com/download)。

```console
git clone https://github.com/MikeKen-Ken/UnlockKit.git
cd UnlockKit
dotnet build -c Release

# 单文件 GUI
dotnet publish src/UnlockKit.UI/UnlockKit.UI.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/gui

# 单文件 CLI
dotnet publish src/UnlockKit/UnlockKit.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/cli
```

CI 每次 push 都跑一遍同样的流程，推 `v*` tag 时会把两个二进制挂到 GitHub Release 上 —— 见 [`.github/workflows/build.yml`](.github/workflows/build.yml)。

## 仓库结构

```
src/
  UnlockKit/            引擎 + CLI —— 句柄扫描、安全策略、删除阶梯
    Native/             P/Invoke 层（ntdll、kernel32、advapi32）
    Core/               扫描器、进程探测、路径映射、安全策略
    Actions/            POSIX 删除、句柄关闭、递归树删除
  UnlockKit.UI/         WPF 前端、用户级 shell 集成、自安装
    Themes/             浅色 / 深色两套颜色字典 + 全部控件样式
docs/
  DESIGN.md             每个机制为什么存在，代价是什么
  DESIGN.en.md          同上（英文）
  TROUBLESHOOTING.md    故障分类手册 —— 提 issue 前先看这个
  TROUBLESHOOTING.en.md 同上（英文）
assets/                 图标与截图源文件
```

## 文档

文档和 README 一样是双语的：

- **[docs/DESIGN.md](docs/DESIGN.md)** —— 句柄扫描、身份匹配、删除阶梯，以及 TOCTOU 上的取舍。
- **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** —— 「我还是删不掉」。按你看到的报错组织。

英文版见 [DESIGN.en.md](docs/DESIGN.en.md) / [TROUBLESHOOTING.en.md](docs/TROUBLESHOOTING.en.md)。

## 许可证

[MIT](LICENSE)。随便用，没有担保 —— 你要是指错了路径，它会很乐意把错的东西删掉。
