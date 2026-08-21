// PineKVM-DisplaySwitcher - 独立版监听工具
// 作用: 共享键鼠被切走 -> 熄屏让显示器自动跳到 Mac；键鼠回来 -> 唤醒
// 事件驱动: RegisterDeviceNotification 订阅键盘/鼠标 HID 设备接口的插拔事件
//   (WM_DEVICECHANGE, 内核实时推送, 与 Mac 端 IOHIDManager 回调同级, 无轮询)。
//   状态机按事件维护各接口类的在场计数; 启动基线直接枚举内核设备接口树
//   (SetupAPI, 即时无滞后 —— 实测 WMI Win32_PnPEntity 对移除有 ~1-2s 滞后,
//   故判定一律走事件/接口树, 不重查 WMI)。
// 编译: csc /nologo /target:winexe /optimize+ /out:PineKVM-DisplaySwitcher.exe
//       PineKVM-DisplaySwitcher.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class PineKVMDisplaySwitcher
{
    const int HWND_BROADCAST = 0xffff;
    const uint WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;

    // ---- 唤醒（对应 Mac 端 caffeinate -u 的“声明用户活动”）----
    const uint ES_CONTINUOUS = 0x80000000;
    const uint ES_SYSTEM_REQUIRED = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;
    const uint MOUSEEVENTF_MOVE = 0x0001;

    // ---- 设备接口通知（WM_DEVICECHANGE）----
    const uint WM_DEVICECHANGE = 0x0219;
    const uint WM_DESTROY = 0x0002;
    const int DBT_DEVICEARRIVAL = 0x8000;
    const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    const int HWND_MESSAGE = -3;
    const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    // ---- SetupAPI 设备接口枚举（基线 / 兜底重查）----
    const int DIGCF_PRESENT = 0x00000002;
    const int DIGCF_DEVICEINTERFACE = 0x00000010;

    // 键盘/鼠标 HID 集合的设备接口类 GUID（对应设备管理器里的“键盘”/“鼠标”分类）
    static readonly Guid GUID_DEVINTERFACE_KEYBOARD = new Guid("884b96c3-56ef-11d1-bc8c-00a0c91405dd");
    static readonly Guid GUID_DEVINTERFACE_MOUSE = new Guid("378de44c-56ef-11d1-bc8c-00a0c91405dd");

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        // dbcc_name 是变长字符串（紧跟本结构之后），按偏移手工读取
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);

    [DllImport("user32.dll")]
    static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string name);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfoData,
        ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detailBuf, int detailBufSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    static string LogPath;
    static double ConfirmSeconds = 0.5;
    // 键鼠模式只来自配置文件；配置缺失则为空数组，工具不会匹配任何设备
    static string[] KeyboardPatterns = { };
    static string[] MousePatterns = { };

    // ---- 状态机（计数式，与 Mac 端 IOHIDManager 版 presentCount 同构）----
    static readonly object Gate = new object();
    static readonly object LogLock = new object();
    static int kbCount = 0;                // 键盘类匹配接口在场计数
    static int msCount = 0;                // 鼠标类匹配接口在场计数
    static bool armed = false;             // 本会话至少见过一次键鼠同时在线
    static bool blanked = false;
    static DateTime? missingSince = null;
    static Timer confirmTimer = null;      // 一次性：ConfirmSeconds 后重查并熄屏
    static WndProcDelegate _wndProc = null; // 保持委托引用，防 GC
    static IntPtr hWnd = IntPtr.Zero;
    static IntPtr hDevNotifyKb = IntPtr.Zero;
    static IntPtr hDevNotifyMs = IntPtr.Zero;

    static readonly Regex VidPid = new Regex(@"VID_([0-9A-Fa-f]+)&PID_([0-9A-Fa-f]+)", RegexOptions.Compiled);

    static int Main(string[] args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        LogPath = Path.Combine(dir, "PineKVM-DisplaySwitcher.log");
        LoadConfig();

        if (args.Length > 0)
        {
            if (args[0] == "--install") { InstallStartup(); return 0; }
            if (args[0] == "--remove") { RemoveStartup(); return 0; }
        }

        // 启动前先自动停止旧实例，避免“旧进程还在、新进程被单实例保护拦下”
        StopPreviousInstances();
        Thread.Sleep(500);

        bool createdNew;
        using (Mutex m = new Mutex(true, "PineKVM-DisplaySwitcher-single", out createdNew))
        {
            if (!createdNew)
            {
                Log("Another instance already running; this instance exits.");
                return 0;
            }
            Log("PineKVM-DisplaySwitcher started (standalone, event-driven). Keyboard: "
                + string.Join(", ", KeyboardPatterns)
                + "; Mouse: " + string.Join(", ", MousePatterns));
            Run();
        }
        return 0;
    }

    static void StopPreviousInstances()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("PineKVM-DisplaySwitcher"))
            {
                if (p.Id != Process.GetCurrentProcess().Id)
                {
                    Log("Stopping previous instance PID " + p.Id);
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log("StopPreviousInstances error: " + ex.Message);
        }
    }

    // 配置查找顺序：exe 同目录 -> 项目根目录（config 是唯一一份，放仓库根目录）
    static void LoadConfig()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string root = Path.GetFullPath(Path.Combine(dir, ".."));
        string[] candidates = new string[]
        {
            Path.Combine(dir, "PineKVM-DisplaySwitcher.config"),
            Path.Combine(root, "PineKVM-DisplaySwitcher.config")
        };
        string path = null;
        foreach (string c in candidates)
        {
            if (File.Exists(c)) { path = c; break; }
        }
        if (path == null)
        {
            Log("Config not found; no device patterns loaded, tool will not trigger. Expected: "
                + Path.Combine(dir, "PineKVM-DisplaySwitcher.config"));
            return;
        }
        Log("Config loaded: " + path);
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            if (key == "confirmseconds")
            {
                double d;
                if (double.TryParse(val, out d) && d >= 0) ConfirmSeconds = d;
            }
            else if (key == "keyboardpatterns")
            {
                KeyboardPatterns = val.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else if (key == "mousepatterns")
            {
                MousePatterns = val.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }

    static void Log(string message)
    {
        // 事件/定时器多线程并发写日志，需串行化；时间戳毫秒级与 Mac 端一致
        lock (LogLock)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    // 设备接口路径（如 \\?\HID#VID_373B&PID_1278&MI_02#...）是否命中配置前缀
    // 实例 ID/接口路径都含 vid_xxxx&pid_yyyy，统一按令牌包含匹配
    static bool MatchesPattern(string devicePath, string[] patterns)
    {
        if (devicePath == null) return false;
        string up = devicePath.ToUpperInvariant();
        foreach (string p in patterns)
        {
            Match m = VidPid.Match(p);
            if (!m.Success) continue;
            string token = "VID_" + m.Groups[1].Value.ToUpperInvariant() + "&PID_" + m.Groups[2].Value.ToUpperInvariant();
            if (up.Contains(token)) return true;
        }
        return false;
    }

    static void SetMonitorPower(int state)
    {
        SendMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)state);
    }

    // 实测 SC_MONITORPOWER(-1) 广播在本机无法真正点亮屏幕（需真实输入活动才唤醒），
    // 与 Mac 端 caffeinate -u 对齐：注入 1px 鼠标位移（真实输入, 点亮并重置空闲计时器）,
    // 并在独立线程持 ES_DISPLAY_REQUIRED 约 3 秒——防止空闲计时器已到期导致立刻重新
    // 熄屏（本机熄屏超时 AC 20 分钟, 切去 Mac 超过该时长时正是此情形）; 释放时计时器归零
    static void WakeMonitor()
    {
        Log("wake diagnostic: idle-before-jiggle=" + IdleSeconds() + "s");
        SendMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1));
        mouse_event(MOUSEEVENTF_MOVE, 1, 1, 0, UIntPtr.Zero);
        Thread holder = new Thread(() =>
        {
            try
            {
                Thread.Sleep(300);
                uint idleAfter = IdleSeconds();
                Log("wake diagnostic: idle-after-jiggle=" + idleAfter + "s"
                    + (idleAfter <= 1 ? " (jiggle counted as input, timer reset)"
                                      : " (jiggle did NOT reset idle timer)"));
                // ES_* 按线程生效: 设置与释放必须在同一线程完成
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
                Thread.Sleep(3000);
                SetThreadExecutionState(ES_CONTINUOUS);
                Log("wake diagnostic: display-required hold released");
            }
            catch { }
        });
        holder.IsBackground = true;
        holder.Start();
    }

    static uint IdleSeconds()
    {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref lii)) return 0;
        return ((uint)Environment.TickCount - lii.dwTime) / 1000;
    }

    // ---- SetupAPI 枚举: 内核设备接口树即时状态（无 WMI provider 的滞后）----

    static int CountPresent(Guid classGuid, string[] patterns)
    {
        int count = 0;
        IntPtr devInfo = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == new IntPtr(-1)) return 0;   // INVALID_HANDLE_VALUE
        try
        {
            int structSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            int i = 0;
            while (true)
            {
                SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = structSize;
                if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref classGuid, i, ref data))
                    break;
                string path;
                if (GetInterfacePath(devInfo, ref data, out path) && MatchesPattern(path, patterns))
                    count++;
                i++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
        return count;
    }

    static bool GetInterfacePath(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA data, out string path)
    {
        path = null;
        int required;
        SetupDiGetDeviceInterfaceDetail(devInfo, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
        if (required <= 0) return false;
        IntPtr buf = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);  // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize
            if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref data, buf, required, out required, IntPtr.Zero))
                return false;
            path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
            return path != null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ---- 状态机：事件/兜底触发，判定用接口在场计数（内核即时数据）----

    static void Evaluate()
    {
        lock (Gate)
        {
            EvaluateCore();
        }
    }

    static void EvaluateCore()
    {
        try
        {
            if (kbCount > 0 && msCount > 0)
            {
                CancelConfirmTimer();
                if (blanked)
                {
                    blanked = false;          // 先落状态再阻塞调用（Mac 端同款重入修复）
                    Log("Shared devices are back; waking monitor");
                    WakeMonitor();
                }
                armed = true;
                missingSince = null;
            }
            else if (armed && kbCount == 0 && msCount == 0)
            {
                if (missingSince == null)
                {
                    missingSince = DateTime.Now;
                    Log("Shared keyboard and mouse disappeared; waiting to confirm...");
                    ScheduleConfirmTimer();
                }
            }
            else
            {
                CancelConfirmTimer();
                missingSince = null;
            }
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
        }
    }

    static void CancelConfirmTimer()
    {
        if (confirmTimer != null)
        {
            confirmTimer.Dispose();
            confirmTimer = null;
        }
    }

    static void ScheduleConfirmTimer()
    {
        CancelConfirmTimer();
        if (ConfirmSeconds <= 0)
        {
            OnConfirmTimeout();
            return;
        }
        int delay = (int)Math.Ceiling(ConfirmSeconds * 1000) + 50;
        confirmTimer = new Timer(_ => OnConfirmTimeout(), null, delay, Timeout.Infinite);
    }

    static void OnConfirmTimeout()
    {
        lock (Gate)
        {
            try
            {
                confirmTimer = null;
                if (!armed || blanked) return;
                if (kbCount > 0 || msCount > 0) return;   // 期间有设备回来，不熄屏
                double gone = (DateTime.Now - missingSince.Value).TotalSeconds;
                Log("Both gone for " + gone.ToString("0.0") + "s; turning monitor off so it auto-switches to Mac");
                blanked = true;                   // 先落状态
                armed = false;
                missingSince = null;
                SetMonitorPower(2);               // 再阻塞调用，重入时状态一致
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
            }
        }
    }

    // 设备插拔事件 -> 更新对应接口类的在场计数 -> 跑状态机
    static void OnDeviceEvent(bool arrived, Guid classGuid, string path)
    {
        lock (Gate)
        {
            if (classGuid == GUID_DEVINTERFACE_KEYBOARD)
            {
                if (arrived) kbCount++;
                else if (kbCount > 0) kbCount--;
            }
            else if (classGuid == GUID_DEVINTERFACE_MOUSE)
            {
                if (arrived) msCount++;
                else if (msCount > 0) msCount--;
            }
            Log("Device " + (arrived ? "arrival" : "removal") + ": " + path
                + " (kb=" + kbCount + ", ms=" + msCount + ")");
            EvaluateCore();
        }
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int evt = wParam.ToInt32();
            // 先看事件类型再碰 lParam：DBT_DEVNODES_CHANGED 等广播的 lParam 为 0
            if ((evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero)
            {
                try
                {
                    DEV_BROADCAST_HDR hdr = (DEV_BROADCAST_HDR)Marshal.PtrToStructure(lParam, typeof(DEV_BROADCAST_HDR));
                    if (hdr.dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                    {
                        DEV_BROADCAST_DEVICEINTERFACE bdi = (DEV_BROADCAST_DEVICEINTERFACE)
                            Marshal.PtrToStructure(lParam, typeof(DEV_BROADCAST_DEVICEINTERFACE));
                        string path = Marshal.PtrToStringUni(IntPtr.Add(lParam, 28));
                        bool matched;
                        if (bdi.dbcc_classguid == GUID_DEVINTERFACE_KEYBOARD)
                            matched = MatchesPattern(path, KeyboardPatterns);
                        else if (bdi.dbcc_classguid == GUID_DEVINTERFACE_MOUSE)
                            matched = MatchesPattern(path, MousePatterns);
                        else
                            matched = false;
                        if (matched)
                        {
                            OnDeviceEvent(evt == DBT_DEVICEARRIVAL, bdi.dbcc_classguid, path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("WM_DEVICECHANGE parse error: " + ex.Message);
                }
            }
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static IntPtr CreateHiddenWindow()
    {
        const string clsName = "PineKVM-DisplaySwitcher-Wnd";
        _wndProc = WndProc;
        WNDCLASS wc = new WNDCLASS();
        wc.lpfnWndProc = _wndProc;
        wc.hInstance = GetModuleHandle(null);
        wc.lpszClassName = clsName;
        ushort atom = RegisterClassW(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
        {
            Log("RegisterClass failed: " + Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }
        return CreateWindowExW(0, clsName, "", 0, 0, 0, 0, 0,
            new IntPtr(HWND_MESSAGE), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    static void RegisterDeviceNotifications()
    {
        DEV_BROADCAST_DEVICEINTERFACE filter = new DEV_BROADCAST_DEVICEINTERFACE();
        filter.dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE));
        filter.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
        filter.dbcc_classguid = GUID_DEVINTERFACE_KEYBOARD;
        hDevNotifyKb = RegisterDeviceNotification(hWnd, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
        filter.dbcc_classguid = GUID_DEVINTERFACE_MOUSE;
        hDevNotifyMs = RegisterDeviceNotification(hWnd, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
        Log("Device notifications registered: keyboard=" + (hDevNotifyKb != IntPtr.Zero)
            + ", mouse=" + (hDevNotifyMs != IntPtr.Zero));
    }

    static void Run()
    {
        if (KeyboardPatterns.Length == 0 && MousePatterns.Length == 0)
        {
            Log("No device patterns; tool will not trigger");
        }

        hWnd = CreateHiddenWindow();
        if (hWnd == IntPtr.Zero)
        {
            Log("Cannot create message window; exiting.");
            return;
        }

        // 启动基线: 直接枚举内核设备接口树（即时、无 WMI 滞后）
        kbCount = CountPresent(GUID_DEVINTERFACE_KEYBOARD, KeyboardPatterns);
        msCount = CountPresent(GUID_DEVINTERFACE_MOUSE, MousePatterns);
        Log("Initial state: keyboard interfaces=" + kbCount + ", mouse interfaces=" + msCount);

        RegisterDeviceNotifications();
        Evaluate();

        // 消息循环：之后的一切检测都由设备插拔事件驱动，无任何周期轮询
        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        // 退出前清理（正常运行时消息循环不退出，走到这里只会是 WM_QUIT）
        if (hDevNotifyKb != IntPtr.Zero) UnregisterDeviceNotification(hDevNotifyKb);
        if (hDevNotifyMs != IntPtr.Zero) UnregisterDeviceNotification(hDevNotifyMs);
        if (hWnd != IntPtr.Zero) DestroyWindow(hWnd);
    }

    static void InstallStartup()
    {
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PineKVM-DisplaySwitcher.lnk");
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                new object[] { Assembly.GetExecutingAssembly().Location });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { AppDomain.CurrentDomain.BaseDirectory });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                new object[] { "" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { "PineKVM-DisplaySwitcher" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Log("Startup shortcut created: " + lnk);
        }
        catch (Exception ex)
        {
            Log("InstallStartup error: " + ex.Message);
        }
    }

    static void RemoveStartup()
    {
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PineKVM-DisplaySwitcher.lnk");
        try
        {
            if (File.Exists(lnk)) File.Delete(lnk);
            Log("Startup shortcut removed: " + lnk);
        }
        catch (Exception ex)
        {
            Log("RemoveStartup error: " + ex.Message);
        }
    }
}
