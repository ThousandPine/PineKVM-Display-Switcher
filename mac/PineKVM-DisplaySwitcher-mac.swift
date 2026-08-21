// PineKVM-DisplaySwitcher-mac - Mac 端监听工具（与 Windows 端互补）
// 作用: 共享键鼠被切走 -> 熄屏让显示器自动跳到 Windows；键鼠回来 -> 唤醒
// 编译: xcrun swiftc -O -swift-version 5 -o PineKVM-DisplaySwitcher-mac src/PineKVM-DisplaySwitcher-mac.swift
//       （或双击 src/编译.command）
// 依赖: 无额外依赖（Foundation + IOKit，系统自带）

import Foundation
import IOKit

// ---------- 全局状态（镜像 C# 版结构） ----------

struct DevicePattern: Hashable, CustomStringConvertible {
    let vid: UInt32
    let pid: UInt32
    var description: String { String(format: "HID\\VID_%04X&PID_%04X", vid, pid) }
}

var logPath = ""
var pollIntervalMs = 1000
var confirmSeconds = 0.5
// 键鼠模式只来自配置文件；配置缺失则为空数组，工具不会匹配任何设备
var keyboardPatterns: [DevicePattern] = []
var mousePatterns: [DevicePattern] = []

// 二进制所在目录（配置/日志都放这里，与 Windows 版行为一致）
var baseDir: String {
    let exe = Bundle.main.executablePath
        ?? URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().path
    return URL(fileURLWithPath: exe).deletingLastPathComponent().path
}

private let logStamp: DateFormatter = {
    let f = DateFormatter()
    f.dateFormat = "yyyy-MM-dd HH:mm:ss"
    return f
}()

// ---------- 日志（同目录 PineKVM-DisplaySwitcher.log，不可写时回退 ~/Library/Logs） ----------

func log(_ message: String) {
    let line = logStamp.string(from: Date()) + "  " + message + "\n"
    var target = logPath
    if target.isEmpty {
        target = URL(fileURLWithPath: baseDir).appendingPathComponent("PineKVM-DisplaySwitcher.log").path
        logPath = target
    }
    if !FileManager.default.fileExists(atPath: target) {
        FileManager.default.createFile(atPath: target, contents: nil)
    }
    if let fh = FileHandle(forWritingAtPath: target) {
        defer { try? fh.close() }
        fh.seekToEndOfFile()
        fh.write(line.data(using: .utf8)!)
    } else {
        let fb = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/PineKVM-DisplaySwitcher.log")
        if !FileManager.default.fileExists(atPath: fb.path) {
            FileManager.default.createFile(atPath: fb.path, contents: nil)
        }
        if let fh = FileHandle(forWritingAtPath: fb.path) {
            defer { try? fh.close() }
            fh.seekToEndOfFile()
            fh.write(line.data(using: .utf8)!)
            logPath = fb.path
            log("Log fallback to: " + fb.path)
        }
    }
}

// ---------- 子进程 ----------

@discardableResult
func runProcess(_ path: String, _ args: [String]) -> Int32 {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    do {
        try p.run()
        p.waitUntilExit()
    } catch {
        log("run \(path) error: \(error.localizedDescription)")
    }
    return p.terminationStatus
}

func capture(_ path: String, _ args: [String]) -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    let pipe = Pipe()
    p.standardOutput = pipe
    do {
        try p.run()
        p.waitUntilExit()
    } catch {
        return ""
    }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    return String(data: data, encoding: .utf8) ?? ""
}

// ---------- 单实例：PID 文件方式（镜像 C# 的 StopPreviousInstances） ----------
// 不用 pkill：pkill 会把自己的父进程（即本工具自身）也匹配杀掉。

func stopPreviousInstances() {
    let pidFile = URL(fileURLWithPath: baseDir).appendingPathComponent("PineKVM-DisplaySwitcher-mac.pid").path
    if let content = try? String(contentsOfFile: pidFile, encoding: .utf8),
       let pid = Int32(content.trimmingCharacters(in: .whitespaces)) {
        // 确认该 PID 仍是本工具（防止 PID 复用后误杀无关进程）
        let ps = capture("/bin/ps", ["-p", "\(pid)", "-o", "comm="])
        if ps.contains("PineKVM-DisplaySwitcher-mac") {
            log("Stopping previous instance PID \(pid)")
            runProcess("/bin/kill", ["-9", "\(pid)"])
        }
    }
    do {
        try "\(getpid())".write(toFile: pidFile, atomically: true, encoding: .utf8)
    } catch {
        log("Write pid file error: \(error.localizedDescription)")
    }
}

// ---------- 配置加载（与 Windows 版同格式：PollIntervalSec / ConfirmSeconds / KeyboardPatterns / MousePatterns） ----------

func parsePatterns(_ raw: String) -> [DevicePattern] {
    var out: [DevicePattern] = []
    let regex = try? NSRegularExpression(pattern: "VID_([0-9A-Fa-f]+)&PID_([0-9A-Fa-f]+)")
    for seg in raw.components(separatedBy: ";") {
        let s = seg.trimmingCharacters(in: .whitespaces)
        if s.isEmpty { continue }
        guard let m = regex?.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)),
              let vidRange = Range(m.range(at: 1), in: s),
              let pidRange = Range(m.range(at: 2), in: s),
              let vid = UInt32(s[vidRange], radix: 16),
              let pid = UInt32(s[pidRange], radix: 16) else {
            log("Config: cannot parse pattern \"" + s + "\", skipped")
            continue
        }
        out.append(DevicePattern(vid: vid, pid: pid))
    }
    return out
}

// 配置查找顺序：二进制同目录 -> 项目根目录（config 是唯一一份，放仓库根目录）
func configCandidates() -> [String] {
    let dir = URL(fileURLWithPath: baseDir)
    return [
        dir.appendingPathComponent("PineKVM-DisplaySwitcher.config").path,
        dir.deletingLastPathComponent().appendingPathComponent("PineKVM-DisplaySwitcher.config").path,
    ]
}

// 返回实际加载的配置文件路径（没找到返回 nil）
func loadConfig() -> String? {
    for path in configCandidates() {
        guard let content = try? String(contentsOfFile: path, encoding: .utf8) else { continue }
        for raw in content.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix(";") { continue }
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            let val = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            switch key {
            case "pollintervalsec":
                if let n = Int(val), n >= 1 { pollIntervalMs = n * 1000 }
            case "confirmseconds":
                if let d = Double(val), d >= 0 { confirmSeconds = d }
            case "keyboardpatterns":
                keyboardPatterns = parsePatterns(val)
            case "mousepatterns":
                mousePatterns = parsePatterns(val)
            default:
                break
            }
        }
        return path
    }
    return nil
}

// ---------- 设备扫描（IOKit 枚举 IOHIDDevice，按配置 VID/PID 匹配） ----------

func numValue(_ dict: [String: Any], _ key: String) -> Int? {
    if let n = dict[key] as? NSNumber { return n.intValue }
    if let s = dict[key] as? String { return Int(s) }
    return nil
}

func strValue(_ dict: [String: Any], _ keys: [String]) -> String {
    for k in keys {
        if let s = dict[k] as? String, !s.isEmpty { return s }
    }
    return ""
}

// 键盘/鼠标分类（仅用于 --check 展示；触发判定只看"匹配设备是否还存在"）
func classify(page: Int, usage: Int) -> (keyboard: Bool, mouse: Bool) {
    if page == 1 { // kHIDPage_GenericDesktop
        switch usage {
        case 6, 7, 0x80...0x83: return (true, false)    // 键盘 / 小键盘 / 系统键
        case 1, 2, 0x30...0x33: return (false, true)    // 指针 / 鼠标 / 指针移动轴
        default: return (true, true)                    // 其余不确定 → 保守同时算
        }
    }
    return (true, true) // 厂商页（0xFF00 等）或未知 → 保守同时算
}

struct HIDDeviceInfo {
    let vid: UInt32
    let pid: UInt32
    let name: String
    let usagePage: Int
    let usage: Int
    var pattern: DevicePattern { DevicePattern(vid: vid, pid: pid) }
}

// 运行循环用：内核侧按 VID/PID 匹配，只问"有没有匹配设备在线"。
// 不拉取任何设备属性（对比 scanMatchedDevices 的 IORegistryEntryCreateCFProperties：
// 每次轮询每台设备都要从内核搬整份属性字典 + mach 消息，实测单核 ~28% CPU）。
// 每个 (VID,PID) 模式一次轻量匹配调用，内核过滤后只返回命中的条目。
func anyMatchedDevicePresent() -> Bool {
    let patterns = Array(Set(keyboardPatterns + mousePatterns))
    guard !patterns.isEmpty else { return false }
    for p in patterns {
        guard let matching = IOServiceMatching("IOHIDDevice") else { continue }
        let dict = matching as NSMutableDictionary
        dict["VendorID"] = NSNumber(value: p.vid)
        dict["ProductID"] = NSNumber(value: p.pid)
        var iter: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iter) == KERN_SUCCESS else { continue }
        defer { IOObjectRelease(iter) }
        if IOIteratorNext(iter) != 0 { return true }
    }
    return false
}

func scanMatchedDevices() -> [HIDDeviceInfo] {
    var out: [HIDDeviceInfo] = []
    let allPatterns = Set(keyboardPatterns + mousePatterns)
    guard !allPatterns.isEmpty else { return out }
    guard let matching = IOServiceMatching("IOHIDDevice") else { return out }
    var iter: io_iterator_t = 0
    guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iter) == KERN_SUCCESS else { return out }
    defer { IOObjectRelease(iter) }
    while true {
        let entry = IOIteratorNext(iter)
        if entry == 0 { break }
        defer { IOObjectRelease(entry) }
        var rawProps: Unmanaged<CFMutableDictionary>?
        guard IORegistryEntryCreateCFProperties(entry, &rawProps, kCFAllocatorDefault, 0) == KERN_SUCCESS,
              let props = rawProps?.takeRetainedValue() as? [String: Any] else { continue }
        guard let vid = numValue(props, "VendorID"),
              let pid = numValue(props, "ProductID") else { continue }
        guard allPatterns.contains(DevicePattern(vid: UInt32(vid), pid: UInt32(pid))) else { continue }
        out.append(HIDDeviceInfo(
            vid: UInt32(vid), pid: UInt32(pid),
            name: strValue(props, ["USB Product Name", "Product"]),
            usagePage: numValue(props, "PrimaryUsagePage") ?? numValue(props, "IOHIDDeviceUsagePage") ?? -1,
            usage: numValue(props, "PrimaryUsage") ?? numValue(props, "IOHIDDeviceUsage") ?? -1))
    }
    return out
}

// ---------- 熄屏 / 唤醒（镜像 C# 的 SetMonitorPower） ----------

func setDisplaySleep() {
    runProcess("/usr/bin/pmset", ["displaysleepnow"])
}

func wakeDisplay() {
    runProcess("/usr/bin/caffeinate", ["-u", "-t", "1"])
}

// ---------- 主循环（逐行对齐 C# 的 Run()） ----------

func run() {
    var armed = false
    var blanked = false
    var missingSince: Date? = nil

    log("PineKVM-DisplaySwitcher started (mac). Keyboard: "
        + keyboardPatterns.map { $0.description }.joined(separator: ", ")
        + "; Mouse: " + mousePatterns.map { $0.description }.joined(separator: ", "))

    while true {
        let present = anyMatchedDevicePresent()

        if present {
            if blanked {
                log("Shared devices are back; waking monitor")
                wakeDisplay()
                blanked = false
            }
            armed = true
            missingSince = nil
        } else if armed && !present {
            if missingSince == nil {
                missingSince = Date()
                log("Shared keyboard and mouse disappeared; waiting to confirm...")
            }
            let gone = Date().timeIntervalSince(missingSince!)
            if gone >= confirmSeconds {
                log("Both gone for " + String(format: "%.1f", gone)
                    + "s; turning monitor off so it auto-switches to Windows")
                setDisplaySleep()
                blanked = true
                armed = false
                missingSince = nil
            }
        } else {
            missingSince = nil
        }

        Thread.sleep(forTimeInterval: TimeInterval(pollIntervalMs) / 1000.0)
    }
}

// ---------- --check：诊断（列出当前匹配设备，无需硬件即可验证配置） ----------

func check() {
    print("PineKVM-DisplaySwitcher-mac --check")
    print("Config file: " + (configCandidates().first { FileManager.default.fileExists(atPath: $0) } ?? "(not found - no device patterns, tool will not trigger)"))
    print("PollIntervalSec: \(pollIntervalMs / 1000)   ConfirmSeconds: \(String(format: "%.1f", confirmSeconds))")
    print("KeyboardPatterns: " + keyboardPatterns.map { $0.description }.joined(separator: ", "))
    print("MousePatterns: " + mousePatterns.map { $0.description }.joined(separator: ", "))
    print("Matched HID devices:")
    let devices = scanMatchedDevices()
    if devices.isEmpty {
        print("  (none - shared keyboard/mouse not connected to this Mac right now)")
    }
    for d in devices {
        let c = classify(page: d.usagePage, usage: d.usage)
        print(String(format: "  VID_%04X&PID_%04X  %@  [usagePage=%d usage=%d -> keyboard:%@ mouse:%@]",
                     d.vid, d.pid, d.name, d.usagePage, d.usage,
                     c.keyboard ? "yes" : "no", c.mouse ? "yes" : "no"))
    }
    print("Devices present: \(devices.isEmpty ? "no" : "yes")")
}

// ---------- 开机自启：LaunchAgent（镜像 C# 的 InstallStartup / RemoveStartup） ----------

let agentLabel = "com.pinekvm.display-switcher"
var agentPlistPath: String {
    FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/LaunchAgents/com.pinekvm.display-switcher.plist").path
}

func installLaunchAgent() {
    let exe = Bundle.main.executablePath
        ?? URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().path
    // 注意：不要加 StandardOutPath/StandardErrorPath —— 实测在本机 macOS 上，
    // 带这两个键的 LaunchAgent 拉起本二进制会 spawn 失败（EX_CONFIG，系统级问题，
    // 与二进制无关的 /bin/sleep 却正常）。工具自己写日志（log()），运行模式不输出 stdout，无需重定向。
    let dict: [String: Any] = [
        "Label": agentLabel,
        "ProgramArguments": [exe],
        "RunAtLoad": true,
        "KeepAlive": false,
        "WorkingDirectory": baseDir,
        "ProcessType": "Background",
    ]
    do {
        let data = try PropertyListSerialization.data(fromPropertyList: dict, format: .xml, options: 0)
        try data.write(to: URL(fileURLWithPath: agentPlistPath))
    } catch {
        print("Install error: \(error.localizedDescription)")
        log("InstallStartup error: \(error.localizedDescription)")
        return
    }
    let domain = "gui/\(getuid())"
    runProcess("/bin/launchctl", ["bootout", domain, agentPlistPath])       // 先卸载旧的（不存在时报错，忽略）
    runProcess("/bin/launchctl", ["enable", domain + "/" + agentLabel])     // 防止之前被 disable 过
    let st = runProcess("/bin/launchctl", ["bootstrap", domain, agentPlistPath])
    if st == 0 {
        print("LaunchAgent installed and started: \(agentLabel)")
        log("Startup agent installed: " + agentPlistPath)
    } else {
        print("launchctl bootstrap failed (status \(st)). Please run from a normal Terminal login session.")
        log("launchctl bootstrap failed: \(st)")
    }
}

func removeLaunchAgent() {
    let domain = "gui/\(getuid())"
    runProcess("/bin/launchctl", ["bootout", domain, agentPlistPath])
    try? FileManager.default.removeItem(atPath: agentPlistPath)
    print("LaunchAgent removed: \(agentLabel)")
    log("Startup agent removed: " + agentPlistPath)
}

// ---------- main ----------

let args = CommandLine.arguments
logPath = URL(fileURLWithPath: baseDir).appendingPathComponent("PineKVM-DisplaySwitcher.log").path

if args.count > 1 {
    switch args[1] {
    case "--install":
        installLaunchAgent()
        exit(0)
    case "--remove":
        removeLaunchAgent()
        exit(0)
    case "--check":
        _ = loadConfig()
        check()
        exit(0)
    default:
        break
    }
}

if let cfg = loadConfig() {
    log("Config loaded: " + cfg)
} else {
    log("Config not found; no device patterns loaded, tool will not trigger. Expected: " + configCandidates().joined(separator: " or "))
}
stopPreviousInstances()
run()
