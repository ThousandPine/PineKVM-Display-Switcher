# PineKVM Display Switcher

Windows 后台小工具：当共享键鼠被 USB KVM 切走后，自动让 Windows 显示器熄屏（DPMS），配合显示器自身的“自动信号源”功能，让显示器跳到另一台电脑的画面；键鼠切回时自动唤醒 Windows 显示器。

## 功能

- 监听共享键盘/鼠标（按 VID/PID 匹配）
- 键盘和鼠标同时消失 0.5 秒后判定为“已切换到另一台电脑”，触发 Windows 显示器熄屏
- 键鼠回来时自动唤醒显示器
- 自动替换：重复启动会先停止旧实例再启动新实例，无需手动结束进程
- 隐藏运行：无窗口、无托盘，进程名 `PineKVM-DisplaySwitcher.exe`

## 适用条件

- Windows 与另一台电脑共用一台显示器（显示器双输入直连）
- 键鼠/音箱通过 USB KVM 切换
- 显示器 OSD 开启“自动输入源 / 自动信号源”

## 工作原理

1. 后台轮询共享键鼠是否还存在
2. 两者同时消失 0.5 秒 → 判定已切走 → Windows 显示器进入 DPMS 熄屏
3. 显示器检测到当前输入无信号 → 自动跳到另一台电脑的输入
4. 键鼠切回 → 自动唤醒显示器

## 当前限制

本工具只实现“从 Windows 切走”的方向：切走后自动熄屏，显示器跳到另一台电脑。

切回 Windows 时，显示器**不会自动跳回来**：显示器只有在当前输入失去信号时才会自动切换；切回时 Windows 重新亮屏，但另一台电脑仍保持输出，显示器会停在那边。要让显示器自动跳回 Windows，需要另一台电脑在键鼠被切走时也执行熄屏联动（该联动目前尚未实现）。

## 目录结构

- `PineKVM-DisplaySwitcher.exe` — 独立编译的后台工具
- `PineKVM-DisplaySwitcher.config` — 配置（轮询间隔、确认时间、键鼠 ID）
- `启动.bat` / `设置开机自启.bat` / `移除开机自启.bat`
- `查看日志.bat` / `查看状态.bat`
- `说明.txt` / `README.md`
- `src\PineKVM-DisplaySwitcher.cs` — 源码
- `src\编译.bat` — 一键重新编译

## 使用

1. 双击 `设置开机自启.bat`（推荐，开机自动运行；会先移除旧注册再重新注册，可重复点击）
2. 立即运行：双击 `启动.bat`（会自动先停止旧实例再启动新实例）
3. 按 KVM 切到另一台电脑：约 1~2 秒后 Windows 熄屏，显示器自动跳转
4. 切回 Windows：键鼠回来后屏幕自动唤醒

命令行（在项目根目录运行）：

```powershell
.\PineKVM-DisplaySwitcher.exe            # 直接运行
.\PineKVM-DisplaySwitcher.exe --install  # 安装开机自启
.\PineKVM-DisplaySwitcher.exe --remove   # 移除开机自启
```

## 配置

编辑 `PineKVM-DisplaySwitcher.config`：

- `PollIntervalSec`：轮询间隔（秒）
- `ConfirmSeconds`：键鼠同时消失多久后触发（秒）
- `KeyboardPatterns` / `MousePatterns`：共享键鼠的 VID/PID 前缀，分号分隔

修改后需重启工具。

## 查看状态与日志

- `查看状态.bat`：显示运行中的进程 PID
- `查看日志.bat`：打开日志文件
- 日志中出现 `turning monitor off so it auto-switches` 表示触发成功，`waking monitor` 表示已切回

## 重新编译

编辑 `src\PineKVM-DisplaySwitcher.cs` → 双击 `src\编译.bat`（自动停止旧进程并编译）→ 双击 `启动.bat` 运行新版。

## 故障排查

- **不切换**：确认显示器 OSD 的自动输入源已开启且输入源模式为“自动”；查看日志是否有 `disappeared` / `turning monitor off`；确认配置里的键鼠 ID 匹配当前设备。
- **黑屏但不跳输入**：检查 OSD 输入源设置；移动鼠标可唤醒 Windows 显示器。
- **误切换**：只有键盘和鼠标同时消失 0.5 秒才会触发；故意拔掉键鼠清理时会触发一次，属正常。
- **重复启动**：会先停止旧实例再启动新实例，日志记录 `Stopping previous instance`。
