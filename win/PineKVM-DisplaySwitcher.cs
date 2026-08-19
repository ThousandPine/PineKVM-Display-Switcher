// PineKVM-DisplaySwitcher - 独立版监听工具
// 作用: 共享键鼠被切走 -> 熄屏让显示器自动跳到 Mac；键鼠回来 -> 唤醒
// 编译: csc /nologo /target:winexe /optimize+ /out:PineKVM-DisplaySwitcher.exe
//       /r:System.Management.dll PineKVM-DisplaySwitcher.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class PineKVMDisplaySwitcher
{
    const int HWND_BROADCAST = 0xffff;
    const uint WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;

    static string LogPath;
    static int PollIntervalMs = 1000;
    static double ConfirmSeconds = 0.5;
    // 键鼠模式只来自配置文件；配置缺失则为空数组，工具不会匹配任何设备
    static string[] KeyboardPatterns = { };
    static string[] MousePatterns = { };

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
            Log("PineKVM-DisplaySwitcher started (standalone). Keyboard: " + string.Join(", ", KeyboardPatterns)
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
        string[] candidates = new string[]
        {
            Path.Combine(dir, "PineKVM-DisplaySwitcher.config"),
            Path.Combine(Directory.GetParent(dir) != null ? Directory.GetParent(dir).FullName : dir,
                "PineKVM-DisplaySwitcher.config")
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
            if (key == "pollintervalsec")
            {
                int n;
                if (int.TryParse(val, out n) && n >= 1) PollIntervalMs = n * 1000;
            }
            else if (key == "confirmseconds")
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
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    static bool AnyPresent(string pnpClass, string[] patterns)
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = '" + pnpClass + "'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string id = Convert.ToString(obj["DeviceID"]);
                    foreach (string pattern in patterns)
                    {
                        if (id.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    static void SetMonitorPower(int state)
    {
        SendMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)state);
    }

    static void Run()
    {
        bool armed = false;
        bool blanked = false;
        DateTime? missingSince = null;

        while (true)
        {
            try
            {
                bool kb = AnyPresent("Keyboard", KeyboardPatterns);
                bool ms = AnyPresent("Mouse", MousePatterns);

                if (kb && ms)
                {
                    if (blanked)
                    {
                        Log("Shared devices are back; waking monitor");
                        SetMonitorPower(-1);
                        blanked = false;
                    }
                    armed = true;
                    missingSince = null;
                }
                else if (armed && !kb && !ms)
                {
                    if (missingSince == null)
                    {
                        missingSince = DateTime.Now;
                        Log("Shared keyboard and mouse disappeared; waiting to confirm...");
                    }
                    double gone = (DateTime.Now - missingSince.Value).TotalSeconds;
                    if (gone >= ConfirmSeconds)
                    {
                        Log("Both gone for " + gone.ToString("0.0") + "s; turning monitor off so it auto-switches to Mac");
                        SetMonitorPower(2);
                        blanked = true;
                        armed = false;
                        missingSince = null;
                    }
                }
                else
                {
                    missingSince = null;
                }
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
            }

            Thread.Sleep(PollIntervalMs);
        }
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
