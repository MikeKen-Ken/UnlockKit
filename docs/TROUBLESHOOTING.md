**简体中文** · [English](TROUBLESHOOTING.en.md)

# 排查手册

按你看到的报错组织。如果这里没有你的情况，先跑：

```console
unlockkit-cli "path\to\thing" --json -v
```

把输出贴进 issue。再加一个环境变量 `UNLOCKKIT_DEBUG=1`，可以看到扫描器的内部计数。

---

## 「操作无法完成，因为文件夹或其中的文件已在另一个程序中打开」

这就是 `ERROR_SHARING_VIOLATION`（`0x20`，在 `robocopy`、.NET、PowerShell 里通常显示为 `0x80070020`）。

有东西开着这个对象，而且没有传 `FILE_SHARE_DELETE`。UnlockKit 会把它判为 `LOCKED`，指出是哪个进程，通常关掉句柄就够了。

**如果目标是文件夹**，持有者几乎从来不是文件夹本身，而是里面的某个文件或子目录 —— 所以 UnlockKit 报 `LOCKED (subtree)` 并列出具体路径。删文件夹意味着删掉里面的东西，所以它们的锁就是你的问题，哪怕文件夹对象看起来是空闲的。

点 `Unlock` 之后常见两种结果：

- **句柄又回来了。** 监视器在毫秒内重新打开了它们。对 `TSVNCache.exe`、`SearchIndexer.exe`、`OneDrive.exe`、`Dropbox.exe` 和杀毒软件来说这是预期行为。这种情况不要用 `Unlock`，用 `Delete` —— 树删除会一次性关掉整棵子树，然后比监视器重新锁定更快地删完。如果还是失败，去治监视器（见下文）。
- **一个都关不掉。** 所有句柄都被拒绝或读不到。「拒绝」意味着是拒绝名单或 PPL 进程；「读不到」意味着你需要提权。报告会把这两种情况分开。

---

## 「拒绝访问」/「你需要权限来执行此操作」

`ERROR_ACCESS_DENIED`（`0x5`，`0x80070005`）。

当没有任何句柄持有该文件时，UnlockKit 会报 `ACL BLOCKS DELETE`。这个结论的意思就是字面意思：**这不是锁**，任何解锁工具都帮不上。当前账户缺的是对象上（或某一级父目录上）的 `DELETE` 权限。

先确认它不是只读或系统属性那么简单，然后看 ACL：

```console
icacls "D:\path\to\thing"
```

要修就得夺取所有权并给自己授 `DELETE`，这会改变文件的安全设置。UnlockKit 故意不偷偷做这件事 —— 夺取了一个你本来没打算动的文件的所有权，比删除失败更糟糕。

---

## 报 `KERNEL-HELD LOCK` —— 过滤驱动持有

`ERROR_SHARING_VIOLATION`，但指向目标的用户态句柄是**零**。

意思是这把锁在内核过滤驱动（minifilter）里：杀毒、备份代理、终端安全、云同步，或者某个文件系统过滤驱动。用户态代码关不掉那些句柄 —— 不是这个工具，也不是别的工具。任何声称能做到的，要么自己装了驱动，要么在骗你。

真正有效的手段：

1. **暂停元凶。** 杀毒实时防护、OneDrive/Dropbox 同步、备份任务。
2. `unlockkit-cli delete "path" --reboot` —— 把路径写进 `PendingFileRenameOperations`，下次开机的 `SMSS` 阶段、任何用户态代码运行之前把它删掉。
3. 重启，手动删，然后去配排除列表。

定位驱动：如果对话框里根本不提任何进程，用 `fltmc filters` 看装了哪些 minifilter，再看安全产品的驱动清单。

---

## `STATUS_CANNOT_DELETE` —— 文件是正在运行的程序

你在删一个当前被映射为映像节的 `.exe` 或 `.dll`。Windows 会拒绝，而且拒绝得对：删掉运行中映像的载体文件是未定义行为。报告里写作 `Cannot delete: file is mapped as an image section (running exe/dll)`。

先停掉进程。如果是服务，先停服务。如果是被加载进 `explorer.exe` 的（比如一个 shell 扩展），那这个文件要等资源管理器重启才会解除映射。

---

## `Unlock` 报 `PPL-protected (level 0x..)`

受保护进程（Protected Process Light）。`MsMpEng.exe`（Defender）、`services.exe`、`lsass.exe`、`csrss.exe` 这些，即使有 `SeDebugPrivilege` 也打不开。UnlockKit 会把保护级别打出来，让这次拒绝是有解释的，而不是莫名其妙的。

这不是待修的 bug。在 `csrss` 或 `winlogon` 里关句柄，可能让桌面直接失去响应；拒绝名单存在的理由就是这个。

---

## 删掉又回来、每次都这样 —— 监视器进程

会盯着目录的进程（版本控制的图标覆盖、搜索索引、同步客户端）会持续重新打开句柄。关掉它们只是治症状，根治办法是让它别再盯这个路径。

| 进程 | 根治办法 |
| --- | --- |
| `TSVNCache.exe`（TortoiseSVN） | 设置 → 图标覆盖 → **排除路径** → 加上这个文件夹。另一半是状态缓存，设为 *none* 或 *shell* |
| `TGitCache.exe`（TortoiseGit） | 同一个对话框、同一个排除列表 |
| `SearchIndexer.exe` | 索引选项 → 修改 → 取消勾选该文件夹 |
| `OneDrive.exe` | 设置 → 账户 → 选择文件夹，或暂停同步 |
| `Dropbox.exe` / `GoogleDriveFS.exe` | 选择性同步 —— 把这个文件夹移出同步 |
| 杀毒软件 | 把该文件夹加进实时扫描排除项 |
| `explorer.exe` | 通常是缩略图或预览处理器：把视图切成「详细信息」，或者清一次缩略图缓存 |

对这样的文件夹跑 `unlockkit-cli close "path"`，会报告关掉了若干句柄，然后一秒后同一个进程又占住了。这就是它的特征。

---

## 报 `NOT LOCKED`，但就是删不掉

扫描器没找到任何句柄，`DELETE` 权限也有，删除却失败了。常见原因：

- **路径过长。** 超过 `MAX_PATH`（260）又没有 `\\?\` 前缀。UnlockKit 内部用的是扩展长度形式，但调用方可能不是。
- **问题在父目录**，不在这个文件：只读、ACL、或者一个重解析点。删除在父目录上失败，报错却指向子项。
- **目标在扫描和删除之间被移动了。** 重跑一次。
- **过滤驱动**让删除失败而不是让打开失败 —— 这会表现为一个意料之外的 `NTSTATUS`，值得带 `--json -v` 报上来。

---

## 右键项出现在「显示更多选项」里

Windows 11 上这是预期行为。UnlockKit 写的是传统注册表菜单项；Windows 11 的一级菜单只接受带应用标识的 MSIX 打包 `IExplorerCommand`。键盘绕法：选中文件按 `Shift+F10`。

---

## 菜单项在，但点了没反应

这个菜单项启动的是 `%LOCALAPPDATA%\Programs\UnlockKit\UnlockKit.exe`。确认这个文件还在 —— 如果程序被移动或删除时没先移除菜单，这个项就成了死链接。重新运行程序，把 **Install context menu** 点两次（先移除，再安装）即可重写路径。

---

## 更新检查失败

状态栏显示 `Check updates` 但日志里是 `GitHub is rate-limiting update checks from this machine` —— GitHub 的匿名 API 每个 IP 每小时只给 60 次。等一会儿再点。这不是 UnlockKit 的限制，也没法绕（要绕就得让每个用户配 token，不划算）。

其它失败先看 `%LOCALAPPDATA%\UnlockKit\update.log`，它会记下每一次检查和下载的结果。完全离线、或者不想让它联网，设 `UNLOCKKIT_NO_UPDATE=1`。

---

## 点了「Restart and update」，窗口关了但版本没变

替换是在进程退出之后做的，日志里应该有三行：`apply: staged ... -> ...`、`apply: replaced ... with x.y.z`，以及（失败时）`apply: could not replace`。

- **`could not replace` / `access denied`**：程序被放在了普通用户写不进去的目录（`Program Files`、只读共享、被组策略锁住的路径）。把它挪到 `%LOCALAPPDATA%\Programs\UnlockKit\` 再更新，或者手动下载新版本覆盖。
- **日志到最后一行就断了**：替换进程被杀软拦住了。单文件 exe 在替换瞬间是「自己写自己」，启发式规则容易误判；把 `%LOCALAPPDATA%\UnlockKit\` 加进排除列表。
- **更新成功但菜单项还指向旧路径**：菜单存的是绝对路径。若程序位置变了，把 **Install context menu** 点两次（先移除再安装）。

---

## 报 bug

```console
unlockkit-cli "path" --json -v > report.json
```

请附上：

- Windows 版本（`winver`）；
- 窗口是否以管理员身份运行；
- 结论文本；
- 你期望的结果。

不要把包含隐私路径的报告贴出来 —— 子树匹配的 JSON 里会带真实路径。
