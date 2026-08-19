# PineKVM Display Switcher

KVM 双端联动工具：当共享键鼠被 USB KVM 切走后，另一台电脑自动熄屏（DPMS），配合显示器自身的“自动信号源”功能，让显示器跳到当前正在使用的电脑画面；键鼠切回时自动唤醒。

- **Windows 端**（C#）：键鼠被切走 → Windows 熄屏，显示器自动跳到 Mac；切回 → 唤醒
- **Mac 端**（Swift）：键鼠被切走 → Mac 熄屏，显示器自动跳到 Windows；切回 → 唤醒

两台都运行时可双向闭环；只运行单端则只有单方向。

## 功能

- 监听共享键盘/鼠标（按 VID/PID 匹配）
- 共享键鼠消失满确认时间（默认值见「配置」）后判定为“已切换到另一台电脑”，触发本机显示器熄屏
- 键鼠回来时自动唤醒显示器
- 自动替换：重复启动会先停止旧实例再启动新实例，无需手动结束进程
- 隐藏运行：无窗口、无托盘
- 工具只做两件事：键鼠同时消失满确认时间 → 熄屏；键鼠回来 → 唤醒

### Windows 端

- 进程名 `PineKVM-DisplaySwitcher.exe`，WMI 轮询 + DPMS 熄屏

### Mac 端

- 进程名 `PineKVM-DisplaySwitcher-mac`，IOKit 轮询 + `pmset displaysleepnow` 熄屏、`caffeinate -u` 唤醒
- 源码 `mac/PineKVM-DisplaySwitcher-mac.swift`，swiftc 编译，无额外依赖
- 开机自启：LaunchAgent（`com.pinekvm.display-switcher`）
- 诊断：`./PineKVM-DisplaySwitcher-mac --check` 列出当前匹配到的共享键鼠设备

## 适用条件

- Windows 与另一台电脑共用一台显示器（显示器双输入直连）
- 键鼠/音箱通过 USB KVM 切换
- 显示器 OSD 开启“自动输入源 / 自动信号源”

## 工作原理

1. 后台轮询共享键鼠是否还存在
2. 两者同时消失满确认时间 → 判定已切走 → Windows 显示器进入 DPMS 熄屏
3. 显示器检测到当前输入无信号 → 自动跳到另一台电脑的输入
4. 键鼠切回 → 自动唤醒显示器

## 当前限制

- **双向需双端都运行**：Windows 端只覆盖“从 Windows 切走”方向；Mac 端只覆盖“从 Mac 切走”方向。两端同时运行时双向闭环，只运行单端则只有单方向。
- 显示器只有在当前输入失去信号时才会自动切换；两端同时输出信号时（如某端空闲熄屏晚于切换瞬间），显示器可能短暂停在原输入，随后由另一端熄屏完成跳转，属显示器自动源固有行为。

## 目录结构

Windows 端与 Mac 端各自独立成文件夹，各自包含完整的工具（二进制、脚本），可单独拷到对应机器使用（配置文件放根目录，两端共用）：

```
PineKVM-Display-Switcher/
├── README.md                       — 使用说明（本文件）
├── PineKVM-DisplaySwitcher.config  — 配置（唯一一份，两端共用）
├── win/                            — Windows 端（C#）
│   ├── PineKVM-DisplaySwitcher.exe     后台工具（编译产物）
│   ├── PineKVM-DisplaySwitcher.cs      源码
│   └── *.bat（启动 / 设置开机自启 / 移除开机自启 / 查看状态 / 查看日志 / 编译）
└── mac/                            — Mac 端（Swift）
    ├── PineKVM-DisplaySwitcher-mac     后台工具（编译产物）
    ├── PineKVM-DisplaySwitcher-mac.swift 源码
    └── *.command（启动 / 设置开机自启 / 移除开机自启 / 查看状态 / 查看日志 / 编译）
```

## 使用

### Windows 端（在 Windows 机器上，进入 `win\` 目录）

1. 双击 `设置开机自启.bat`（推荐，开机自动运行；会先移除旧注册再重新注册，可重复点击）
2. 立即运行：双击 `启动.bat`（会自动先停止旧实例再启动新实例）
3. 按 KVM 切到 Mac：约 1~2 秒后 Windows 熄屏，显示器自动跳转到 Mac
4. 切回 Windows：键鼠回来后屏幕自动唤醒

命令行（在 `win\` 目录运行）：

```powershell
.\PineKVM-DisplaySwitcher.exe            # 直接运行
.\PineKVM-DisplaySwitcher.exe --install  # 安装开机自启
.\PineKVM-DisplaySwitcher.exe --remove   # 移除开机自启
```

**停止**：任务管理器里直接结束 `PineKVM-DisplaySwitcher.exe`，或先运行 `查看状态.bat` 拿到 PID 后执行 `Stop-Process -Id <PID>`。

### Mac 端（在这台 Mac 上，进入 `mac/` 目录）

1. 双击 `编译.command` 编译（需要已安装 Xcode 命令行工具，仅首次/改源码后需要）
2. 双击 `设置开机自启.command`（LaunchAgent：`com.pinekvm.display-switcher`，开机自动运行；可重复点击）
3. 立即运行：双击 `启动.command`
4. 按 KVM 切到 Windows：约 1~2 秒后 Mac 熄屏，显示器自动跳转到 Windows
5. 切回 Mac：键鼠回来后屏幕自动唤醒

命令行（在 `mac/` 目录运行）：

```bash
./PineKVM-DisplaySwitcher-mac            # 直接运行
./PineKVM-DisplaySwitcher-mac --install  # 安装开机自启（LaunchAgent）
./PineKVM-DisplaySwitcher-mac --remove   # 移除开机自启
./PineKVM-DisplaySwitcher-mac --check    # 诊断：列出当前匹配到的键鼠设备
```

**停止**：`查看状态.command` 拿到 PID 后 `kill <PID>`；若已设自启，先运行 `移除开机自启.command` 再结束进程。

首次运行若系统弹出 USB 访问权限提示，点“允许”。

## 配置

配置文件**只有一份**，放在仓库根目录 `PineKVM-DisplaySwitcher.config`，两端共用。工具启动时按「自身所在目录 → 项目根目录」的顺序查找：

- `PollIntervalSec`：轮询间隔（秒，整数，最小 1）
- `ConfirmSeconds`：键鼠同时消失多久后触发（秒，默认 0.5，支持小数）
- `KeyboardPatterns` / `MousePatterns`：共享键鼠的 VID/PID 前缀，分号分隔

修改后需重启两端工具。

**换键鼠后**：编辑 `KeyboardPatterns` / `MousePatterns` 为新设备的 VID/PID，保存后重启两端工具。查看当前键鼠 ID 的方法（Windows PowerShell）：

```powershell
Get-PnpDevice -Class Keyboard -PresentOnly | Select-Object InstanceId
Get-PnpDevice -Class Mouse -PresentOnly | Select-Object InstanceId
```

Mac 端可用 `./PineKVM-DisplaySwitcher-mac --check` 确认实际加载的配置路径和匹配到的设备。

> 注意：若只把 `win\` 或 `mac\` 目录单独拷走（脱离项目根目录），工具找不到配置会在日志记录 `Config not found` 且**不会匹配任何设备**（不触发任何动作）——部署时请保持完整项目目录结构。

## 查看状态与日志

- Windows：`win\查看状态.bat` 显示运行中的进程 PID；`win\查看日志.bat` 打开日志文件
- Mac：`mac\查看状态.command` 显示进程与 LaunchAgent 状态；`mac\查看日志.command` 打开日志文件
- 两端日志关键词相同：`turning monitor off` 表示触发成功，`waking monitor` 表示已切回
- Mac 端额外可用 `--check` 查看当前匹配到的共享设备及其 VID/PID、键盘/鼠标分类
- 日志文件 `PineKVM-DisplaySwitcher.log` 在各自工具目录下自动生成（Mac 端目录不可写时回退到 `~/Library/Logs/`）

## 重新编译

- Windows：编辑 `win\PineKVM-DisplaySwitcher.cs` → 双击 `win\编译.bat` → 双击 `win\启动.bat` 运行新版
- Mac：编辑 `mac\PineKVM-DisplaySwitcher-mac.swift` → 双击 `mac\编译.command` → 双击 `mac\启动.command` 运行新版

## 故障排查

- **不切换**：确认显示器 OSD 的自动输入源已开启且输入源模式为“自动”；查看日志是否有 `disappeared` / `turning monitor off`；确认配置里的键鼠 ID 匹配当前设备（Mac 上用 `--check` 核对）。
- **黑屏但不跳输入**：检查 OSD 输入源设置；移动鼠标可唤醒本机显示器。
- **误切换**：共享键鼠消失满配置的确认时间才会触发；故意拔掉键鼠清理时会触发一次，属正常。
- **重复启动**：会先停止旧实例再启动新实例，日志记录 `Stopping previous instance`。
- **Mac 开机自启拉起失败**：若手动编辑过 LaunchAgent plist（`~/Library/LaunchAgents/com.pinekvm.display-switcher.plist`），**不要加 `StandardOutPath` / `StandardErrorPath` 键**——实测这两个键会导致本工具被 launchd 拉起失败（exit 78 / EX_CONFIG，日志无任何输出）。工具自带文件日志，无需重定向；用 `mac\设置开机自启.command` 重新生成即可。
