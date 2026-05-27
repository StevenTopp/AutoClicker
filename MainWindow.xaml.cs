using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace AutoClicker
{
    public partial class MainWindow : Window
    {
        // --------------------------------------------------------------------------
        // WIN32 P/INVOKE DECLARATIONS
        // --------------------------------------------------------------------------
        
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        // Win32 常量定义
        private const uint GA_ROOT = 2;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint MK_LBUTTON = 0x0001;
        
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NONE = 0x0000;
        
        // 键码常量
        private const uint VK_F7 = 0x76;
        private const uint VK_F8 = 0x77;
        private const uint VK_F9 = 0x78;

        // 热键 ID
        private const int HOTKEY_ID_CAPTURE = 777;
        private const int HOTKEY_ID_START = 888;
        private const int HOTKEY_ID_STOP = 999;

        // --------------------------------------------------------------------------
        // CLASS VARIABLES
        // --------------------------------------------------------------------------
        
        private IntPtr _windowHandle;
        private HwndSource _hwndSource;
        private bool _isCapturing = false;
        private bool _isClicking = false;
        private List<Thread> _clickThreads = new List<Thread>();
        private List<ClickPoint> _clickPoints = new List<ClickPoint>();
        private string _clickMode = "sequential"; // "sequential" or "independent"

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 初始化 WebView2 控件
                await webView.EnsureCoreWebView2Async(null);
                
                // 绑定消息接收事件
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                
                // 加载内嵌前端自包含网页
                string htmlContent = GetEmbeddedHtml();
                webView.CoreWebView2.NavigateToString(htmlContent);
                Log("WebView2 成功加载并展示内嵌网页。");
            }
            catch (Exception ex)
            {
                Log($"WebView2 初始化异常: {ex.ToString()}");
                MessageBox.Show($"WebView2 初始化失败: {ex.Message}\n请确认已安装 Edge 浏览器 WebView2 运行时。", "初始化异常");
            }
        }

        private string GetEmbeddedHtml()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string resourceName = "AutoClicker.web.index.html";
                using (System.IO.Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log($"未找到内嵌资源: {resourceName}");
                        return "<h1>未找到内嵌网页资源</h1>";
                    }
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"读取内嵌网页失败: {ex.ToString()}");
                return $"<h1>读取内嵌网页发生异常: {ex.Message}</h1>";
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            
            // 获取窗体句柄并挂接消息钩子监听全局热键消息
            _windowHandle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(_windowHandle);
            _hwndSource.AddHook(HwndHook);

            // 注册默认启动与停止热键
            RegisterHotKey(_windowHandle, HOTKEY_ID_START, MOD_NONE, VK_F8);
            RegisterHotKey(_windowHandle, HOTKEY_ID_STOP, MOD_NONE, VK_F9);
        }

        protected override void OnClosed(EventArgs e)
        {
            // 窗体关闭时卸载钩子和注销热键
            StopClicking();
            
            UnregisterHotKey(_windowHandle, HOTKEY_ID_START);
            UnregisterHotKey(_windowHandle, HOTKEY_ID_STOP);
            if (_isCapturing)
            {
                UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE);
            }

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource.Dispose();
            }
            
            base.OnClosed(e);
        }

        // --------------------------------------------------------------------------
        // WPF WINDOW MESSAGE LOOP HOOK (WM_HOTKEY INTERACTION)
        // --------------------------------------------------------------------------
        
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == HOTKEY_ID_CAPTURE && _isCapturing)
                {
                    // F7 被按下，触发光标捕获逻辑
                    POINT screenPos;
                    if (GetCursorPos(out screenPos))
                    {
                        IntPtr targetHwnd = WindowFromPoint(screenPos);
                        if (targetHwnd != IntPtr.Zero)
                        {
                            IntPtr rootHwnd = GetAncestor(targetHwnd, GA_ROOT);
                            StringBuilder titleBuilder = new StringBuilder(256);
                            GetWindowText(rootHwnd, titleBuilder, 256);
                            string title = titleBuilder.ToString();
                            if (string.IsNullOrEmpty(title))
                            {
                                title = $"HWND: {rootHwnd}";
                            }

                            POINT clientPos = screenPos;
                            ScreenToClient(rootHwnd, ref clientPos);

                            // 取消注册 F7
                            UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE);
                            _isCapturing = false;

                            // 恢复软件窗口显示
                            this.WindowState = WindowState.Normal;
                            this.Show();
                            this.Activate();

                            // 发送捕获数据给前端网页
                            SendToJs(new
                            {
                                type = "captured",
                                data = new
                                {
                                    hwnd = rootHwnd.ToInt64(),
                                    title = title,
                                    x = screenPos.X,
                                    y = screenPos.Y,
                                    x_rel = clientPos.X,
                                    y_rel = clientPos.Y
                                }
                            });
                        }
                    }
                    handled = true;
                }
                else if (hotkeyId == HOTKEY_ID_START)
                {
                    // F8 快捷键触发启动点击
                    SendToJs(new { type = "hotkey_start" });
                    handled = true;
                }
                else if (hotkeyId == HOTKEY_ID_STOP)
                {
                    // F9 快捷键触发停止点击
                    StopClicking();
                    SendToJs(new { type = "hotkey_stop" });
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // --------------------------------------------------------------------------
        // WEBVIEW2 MESSAGING BRIDGE
        // --------------------------------------------------------------------------
        
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string rawJson = "";
            try
            {
                rawJson = e.TryGetWebMessageAsString();
                Log($"收到 JS 消息: {rawJson}");
                using (JsonDocument doc = JsonDocument.Parse(rawJson))
                {
                    JsonElement root = doc.RootElement;
                    string action = root.GetProperty("action").GetString();

                    if (action == "start_capture")
                    {
                        Log("收到开始捕获动作，最小化窗口...");
                        this.WindowState = WindowState.Minimized;
                        _isCapturing = true;
                        
                        Task.Delay(300).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                bool res = RegisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE, MOD_NONE, VK_F7);
                                Log($"注册 F7 全局热键结果: {res}");
                            });
                        });
                    }
                    else if (action == "start_clicking")
                    {
                        string configsJson = root.GetProperty("configs").GetString();
                        string clickMode = root.GetProperty("clickMode").GetString();
                        Log($"收到开始点击动作，模式: {clickMode}");
                        
                        Dispatcher.Invoke(() =>
                        {
                            StartClicking(configsJson, clickMode);
                        });
                    }
                    else if (action == "stop_clicking")
                    {
                        Log("收到停止点击动作");
                        Dispatcher.Invoke(() =>
                        {
                            StopClicking();
                        });
                    }
                    else if (action == "get_windows")
                    {
                        var windows = GetVisibleWindows();
                        SendToJs(new { type = "windows", data = windows });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"解析 JS 消息发生异常: {ex.ToString()} | 消息内容为: {rawJson}");
            }
        }

        private void SendToJs(object data)
        {
            if (webView?.CoreWebView2 != null)
            {
                string jsonString = JsonSerializer.Serialize(data);
                webView.CoreWebView2.PostWebMessageAsString(jsonString);
            }
        }

        // --------------------------------------------------------------------------
        // CLICK ENGINE & MULTITHREADING
        // --------------------------------------------------------------------------
        
        private void StartClicking(string configsJson, string clickMode)
        {
            Log($"StartClicking 被调用: mode={clickMode}, configs={configsJson}");
            if (_isClicking)
            {
                Log("点击任务已在运行中，忽略此次调用。");
                return;
            }

            try
            {
                _clickPoints = JsonSerializer.Deserialize<List<ClickPoint>>(configsJson);
                Log($"反序列化配置成功，点个数: {_clickPoints?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                Log($"反序列化配置发生异常: {ex.ToString()}");
                return;
            }

            if (_clickPoints == null || _clickPoints.Count == 0)
            {
                Log("点击配置点列表为空，取消启动。");
                return;
            }

            _isClicking = true;
            _clickMode = clickMode;
            _clickThreads.Clear();

            if (_clickMode == "sequential")
            {
                Log("启动顺序循环点击工作线程...");
                Thread t = new Thread(SequentialClickWorker) { IsBackground = true };
                t.Start();
                _clickThreads.Add(t);
            }
            else
            {
                Log("启动独立并发点击工作线程...");
                foreach (var pt in _clickPoints)
                {
                    Log($"启动单点线程: Id={pt.Id}, Title={pt.Title}, Interval={pt.Interval}ms");
                    Thread t = new Thread(IndependentClickWorker) { IsBackground = true };
                    t.Start(pt);
                    _clickThreads.Add(t);
                }
            }
        }

        private void StopClicking()
        {
            Log("StopClicking 被调用，停止点击线程...");
            _isClicking = false;
            _clickThreads.Clear();
        }

        private void SequentialClickWorker()
        {
            while (_isClicking)
            {
                foreach (var pt in _clickPoints)
                {
                    if (!_isClicking) break;
                    ExecuteSingleClick(pt);
                    Thread.Sleep(Math.Max(pt.Interval, 10));
                }
            }
        }

        private void IndependentClickWorker(object obj)
        {
            ClickPoint pt = (ClickPoint)obj;
            while (_isClicking)
            {
                ExecuteSingleClick(pt);
                Thread.Sleep(Math.Max(pt.Interval, 10));
            }
        }

        private void ExecuteSingleClick(ClickPoint pt)
        {
            Log($"ExecuteSingleClick 启动: Id={pt.Id}, Title={pt.Title}, ClickMode={pt.ClickMode}, Hwnd={pt.Hwnd}, X={pt.X}, Y={pt.Y}, XRel={pt.XRel}, YRel={pt.YRel}");
            if (pt.ClickMode == "background")
            {
                IntPtr hwnd = new IntPtr(pt.Hwnd);
                if (IsWindow(hwnd))
                {
                    IntPtr lParam = (IntPtr)((pt.YRel << 16) | (pt.XRel & 0xFFFF));
                    Log($"投递后台点击 WM_LBUTTONDOWN 到 HWND: {hwnd}, lParam: {lParam.ToInt64()}");
                    PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
                    Thread.Sleep(10);
                    PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
                    Log("后台点击消息投递完成。");
                }
                else
                {
                    Log($"警告: HWND {hwnd} 已经失效，降级执行前台物理点击。");
                    ActivePhysicalClick(pt.X, pt.Y);
                }
            }
            else
            {
                Log("执行前台物理点击。");
                ActivePhysicalClick(pt.X, pt.Y);
            }
        }

        private void ActivePhysicalClick(int x, int y)
        {
            try
            {
                POINT origPos;
                GetCursorPos(out origPos);
                
                Log($"前台物理点击: 移至 ({x}, {y})，点击后恢复至 ({origPos.X}, {origPos.Y})");
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                Thread.Sleep(10);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                
                Thread.Sleep(10);
                SetCursorPos(origPos.X, origPos.Y);
            }
            catch (Exception ex)
            {
                Log($"前台物理点击发生异常: {ex.ToString()}");
            }
        }

        // --------------------------------------------------------------------------
        // HELPER METHODS
        // --------------------------------------------------------------------------
        
        private List<WindowInfo> GetVisibleWindows()
        {
            var windows = new List<WindowInfo>();
            EnumWindows((hwnd, lParam) =>
            {
                if (IsWindowVisible(hwnd))
                {
                    StringBuilder titleBuilder = new StringBuilder(256);
                    GetWindowText(hwnd, titleBuilder, 256);
                    string title = titleBuilder.ToString();
                    
                    if (!string.IsNullOrEmpty(title) && title != "智能后台鼠标连点器")
                    {
                        windows.Add(new WindowInfo
                        {
                            Hwnd = hwnd.ToInt64(),
                            Title = title
                        });
                    }
                }
                return true;
            }, IntPtr.Zero);
            
            windows.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return windows;
        }

        private void Log(string message)
        {
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
            }
            catch { }
        }
    }

    // 数据模型映射类
    public class ClickPoint
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        
        [JsonPropertyName("title")]
        public string Title { get; set; }
        
        [JsonPropertyName("hwnd")]
        public long Hwnd { get; set; }
        
        [JsonPropertyName("x")]
        public int X { get; set; }
        
        [JsonPropertyName("y")]
        public int Y { get; set; }
        
        [JsonPropertyName("x_rel")]
        public int XRel { get; set; }
        
        [JsonPropertyName("y_rel")]
        public int YRel { get; set; }
        
        [JsonPropertyName("interval")]
        public int Interval { get; set; }
        
        [JsonPropertyName("clickMode")]
        public string ClickMode { get; set; } // "background" or "active"
    }

    public class WindowInfo
    {
        [JsonPropertyName("hwnd")]
        public long Hwnd { get; set; }
        
        [JsonPropertyName("title")]
        public string Title { get; set; }
    }
}