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
using System.Linq;
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
        
        // 默认热键 ID
        private const int HOTKEY_ID_CAPTURE_START = 1001;
        private const int HOTKEY_ID_CAPTURE_POINT = 1002;
        private const int HOTKEY_ID_CLICK_TOGGLE = 1003;
        private const int HOTKEY_ID_CLEAR_ALL = 1004;
        private const int HOTKEY_ID_TOGGLE_ENABLE = 1005; // 快捷禁用热键

        // --------------------------------------------------------------------------
        // CLASS VARIABLES
        // --------------------------------------------------------------------------
        
        private IntPtr _windowHandle;
        private HwndSource? _hwndSource;
        private bool _isCapturing = false;
        private bool _isClicking = false;
        private List<Thread> _clickThreads = new List<Thread>();
        private List<ClickPoint> _clickPoints = new List<ClickPoint>();
        private List<IndicatorWindow> _indicatorWindows = new List<IndicatorWindow>();
        private string _clickMode = "sequential"; // "sequential" or "independent"
        private int _indicatorStyle = 2; // 默认 Option 2 空心圆环

        // 默认全局热键配置
        private HotkeyConfig _configCaptureStart = new HotkeyConfig { Key = "F7", Modifiers = "Ctrl+Alt" };
        private HotkeyConfig _configCapturePoint = new HotkeyConfig { Key = "F7", Modifiers = "None" };
        private HotkeyConfig _configClickToggle = new HotkeyConfig { Key = "F8", Modifiers = "None" };
        private HotkeyConfig _configClearAll = new HotkeyConfig { Key = "F9", Modifiers = "Ctrl+Alt" };
        private HotkeyConfig _configToggleEnable = new HotkeyConfig { Key = "D", Modifiers = "Ctrl+Alt" }; // 默认 Ctrl+Alt+D

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
                
                // 加载内嵌前端网页
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
                using (System.IO.Stream? stream = assembly.GetManifestResourceStream(resourceName))
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

            // 注册默认全局快捷键
            RegisterAllCustomHotkeys();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 窗体关闭时卸载所有指示器窗口
            StopClicking();
            
            Dispatcher.Invoke(() => {
                foreach (var win in _indicatorWindows)
                {
                    try { win.Close(); } catch { }
                }
                _indicatorWindows.Clear();
            });

            // 注销所有全局热键
            UnregisterAllHotkeys();

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource.Dispose();
            }
            
            base.OnClosed(e);
        }

        // --------------------------------------------------------------------------
        // HOTKEY CONTROLLER (REGISTER & UNREGISTER)
        // --------------------------------------------------------------------------

        private void RegisterAllCustomHotkeys()
        {
            UnregisterAllHotkeys();

            // 注册常驻全局快捷键
            bool resCaptureStart = RegisterSingleHotkey(HOTKEY_ID_CAPTURE_START, _configCaptureStart);
            bool resClickToggle = RegisterSingleHotkey(HOTKEY_ID_CLICK_TOGGLE, _configClickToggle);
            bool resClearAll = RegisterSingleHotkey(HOTKEY_ID_CLEAR_ALL, _configClearAll);
            bool resToggleEnable = RegisterSingleHotkey(HOTKEY_ID_TOGGLE_ENABLE, _configToggleEnable);

            Log($"初始化注册热键: 开始采点({_configCaptureStart.Modifiers}+{_configCaptureStart.Key})={resCaptureStart}, " +
                $"连点Toggle({_configClickToggle.Modifiers}+{_configClickToggle.Key})={resClickToggle}, " +
                $"清空点位({_configClearAll.Modifiers}+{_configClearAll.Key})={resClearAll}, " +
                $"禁用点位({_configToggleEnable.Modifiers}+{_configToggleEnable.Key})={resToggleEnable}");
        }

        private bool RegisterSingleHotkey(int id, HotkeyConfig config)
        {
            uint fsModifiers = ParseModifiers(config.Modifiers);
            uint vk = ParseKey(config.Key);
            if (vk == 0) return false;

            return RegisterHotKey(_windowHandle, id, fsModifiers, vk);
        }

        private void UnregisterAllHotkeys()
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE_START);
            UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE_POINT);
            UnregisterHotKey(_windowHandle, HOTKEY_ID_CLICK_TOGGLE);
            UnregisterHotKey(_windowHandle, HOTKEY_ID_CLEAR_ALL);
            UnregisterHotKey(_windowHandle, HOTKEY_ID_TOGGLE_ENABLE);
        }

        private uint ParseModifiers(string modStr)
        {
            uint modifiers = 0;
            if (string.IsNullOrEmpty(modStr)) return modifiers;
            
            string[] mods = modStr.Split(new char[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var mod in mods)
            {
                string m = mod.Trim().ToUpper();
                if (m == "CTRL" || m == "CONTROL") modifiers |= 0x0002;
                else if (m == "ALT") modifiers |= 0x0001;
                else if (m == "SHIFT") modifiers |= 0x0004;
                else if (m == "WIN" || m == "WINDOWS") modifiers |= 0x0008;
            }
            return modifiers;
        }

        private uint ParseKey(string keyStr)
        {
            if (string.IsNullOrEmpty(keyStr)) return 0;
            keyStr = keyStr.Trim().ToUpper();

            if (keyStr.StartsWith("F") && keyStr.Length > 1)
            {
                if (int.TryParse(keyStr.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
                {
                    return (uint)(0x70 + (fNum - 1));
                }
            }

            if (keyStr.Length == 1)
            {
                char c = keyStr[0];
                if (c >= 'A' && c <= 'Z') return (uint)c;
                if (c >= '0' && c <= '9') return (uint)c;
            }

            switch (keyStr)
            {
                case "SPACE": case "空格": return 0x20;
                case "ENTER": case "回车": case "RETURN": return 0x0D;
                case "TAB": return 0x09;
                case "ESCAPE": case "ESC": return 0x1B;
                case "BACKSPACE": case "退格": case "BACK": return 0x08;
                case "DELETE": case "DEL": case "删除": return 0x2E;
                case "INSERT": case "INS": return 0x2D;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": case "PGUP": return 0x21;
                case "PAGEDOWN": case "PGDN": return 0x22;
                case "UP": case "向上": return 0x26;
                case "DOWN": case "向下": return 0x28;
                case "LEFT": case "向左": return 0x25;
                case "RIGHT": case "向右": return 0x27;
            }

            return 0;
        }

        // --------------------------------------------------------------------------
        // WPF WINDOW MESSAGE LOOP HOOK (WM_HOTKEY INTERACTION)
        // --------------------------------------------------------------------------
        
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                
                if (hotkeyId == HOTKEY_ID_CAPTURE_START)
                {
                    if (_isClicking) return IntPtr.Zero;

                    if (_isCapturing)
                    {
                        // 退出采点模式
                        _isCapturing = false;
                        UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE_POINT);
                        
                        this.WindowState = WindowState.Normal;
                        this.Show();
                        this.Activate();
                        
                        SendToJs(new { type = "capture_state_changed", isCapturing = false });
                        Log("通过热键退出采点捕获状态。");
                    }
                    else
                    {
                        // 开启采点模式
                        _isCapturing = true;
                        this.WindowState = WindowState.Minimized;
                        
                        // 延迟 300ms 注册悬停单点采点热键，等待最小化动画完成
                        Task.Delay(300).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                bool res = RegisterSingleHotkey(HOTKEY_ID_CAPTURE_POINT, _configCapturePoint);
                                Log($"采点中：注册悬停捕获 F7 热键结果: {res}");
                            });
                        });

                        SendToJs(new { type = "capture_state_changed", isCapturing = true });
                        Log("进入采点捕获状态，主窗口已最小化。");
                    }
                    handled = true;
                }
                else if (hotkeyId == HOTKEY_ID_CAPTURE_POINT && _isCapturing)
                {
                    // F7 悬停捕获单点
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

                            long newPointId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + new Random().Next(0, 1000);
                            
                            var pt = new ClickPoint
                            {
                                Id = newPointId,
                                Title = title,
                                Hwnd = rootHwnd.ToInt64(),
                                X = screenPos.X,
                                Y = screenPos.Y,
                                XRel = clientPos.X,
                                YRel = clientPos.Y,
                                Interval = 500, // 默认速度
                                ClickMode = "background", // 默认后台
                                Enabled = true // 默认启用
                            };

                            _clickPoints.Add(pt);

                            // 即时在主线程创建小红色指示器圆圈并显示数字序号
                            Dispatcher.Invoke(() =>
                            {
                                var win = new IndicatorWindow(pt.Id, pt.X, pt.Y, _indicatorStyle, _clickPoints.Count);
                                win.Show();
                                win.Opacity = 1.0;
                                _indicatorWindows.Add(win);
                            });

                            // 回传新增点位信息给前端 JS
                            SendToJs(new
                            {
                                type = "point_added",
                                point = new
                                {
                                    id = pt.Id,
                                    title = pt.Title,
                                    hwnd = pt.Hwnd,
                                    x = pt.X,
                                    y = pt.Y,
                                    x_rel = pt.XRel,
                                    y_rel = pt.YRel,
                                    interval = pt.Interval,
                                    clickMode = pt.ClickMode,
                                    enabled = pt.Enabled
                                }
                            });
                            
                            Log($"悬停捕获点位成功: {title} ({screenPos.X}, {screenPos.Y})");
                        }
                    }
                    handled = true;
                }
                else if (hotkeyId == HOTKEY_ID_CLICK_TOGGLE)
                {
                    // F8 键 Toggle 启动与暂停
                    if (_isClicking)
                    {
                        // 暂停点击
                        StopClicking();
                        SendToJs(new { type = "click_state_changed", isClicking = false });
                    }
                    else
                    {
                        // 如果在采点状态，先强制退出采点状态
                        if (_isCapturing)
                        {
                            _isCapturing = false;
                            UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE_POINT);
                            this.WindowState = WindowState.Normal;
                            this.Show();
                            this.Activate();
                            SendToJs(new { type = "capture_state_changed", isCapturing = false });
                        }

                        // 启动点击
                        if (_clickPoints.Count > 0)
                        {
                            StartClicking(JsonSerializer.Serialize(_clickPoints), _clickMode);
                            SendToJs(new { type = "click_state_changed", isClicking = true });
                        }
                    }
                    handled = true;
                }
                else if (hotkeyId == HOTKEY_ID_CLEAR_ALL)
                {
                    // Ctrl+Alt+F9 一键清空所有已标记坐标，并销毁所有圆圈指示器
                    StopClicking();
                    
                    _clickPoints.Clear();

                    Dispatcher.Invoke(() =>
                    {
                        foreach (var win in _indicatorWindows)
                        {
                            try { win.Close(); } catch { }
                        }
                        _indicatorWindows.Clear();
                    });

                    SendToJs(new { type = "points_cleared" });
                    Log("通过全局快捷键清空了所有点位并销毁了全部指示器小红圈。");
                    handled = true;
                }
                else if (hotkeyId == HOTKEY_ID_TOGGLE_ENABLE)
                {
                    // Ctrl+Alt+D 全局快捷键禁用/启用鼠标当前悬停位置的红圈
                    POINT screenPos;
                    if (GetCursorPos(out screenPos))
                    {
                        double minDistance = double.MaxValue;
                        ClickPoint? closestPoint = null;

                        // 搜索离鼠标绝对坐标 30 像素内最近的标记点
                        foreach (var pt in _clickPoints)
                        {
                            double dx = screenPos.X - pt.X;
                            double dy = screenPos.Y - pt.Y;
                            double dist = Math.Sqrt(dx * dx + dy * dy);
                            if (dist < 30 && dist < minDistance)
                            {
                                minDistance = dist;
                                closestPoint = pt;
                            }
                        }

                        if (closestPoint != null)
                        {
                            closestPoint.Enabled = !closestPoint.Enabled;

                            // 实时同步圆圈的透明度 (禁用变 0.25, 启用变 1.0)
                            Dispatcher.Invoke(() =>
                            {
                                var win = _indicatorWindows.FirstOrDefault(w => w.PointId == closestPoint.Id);
                                if (win != null)
                                {
                                    win.Opacity = closestPoint.Enabled ? 1.0 : 0.25;
                                    win.TriggerHighlight(); // 播放一个醒目的动画反馈
                                }
                            });

                            // 通知前端 JS 同步卡片的禁用置灰视效
                            SendToJs(new
                            {
                                type = "point_enabled_toggled",
                                id = closestPoint.Id,
                                enabled = closestPoint.Enabled
                            });

                            Log($"通过全局热键 Toggle 了悬停点启用状态: ID={closestPoint.Id}, Title={closestPoint.Title}, Enabled={closestPoint.Enabled}");
                        }
                    }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // --------------------------------------------------------------------------
        // WEBVIEW2 MESSAGING BRIDGE
        // --------------------------------------------------------------------------
        
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string rawJson = "";
            try
            {
                rawJson = e.TryGetWebMessageAsString();
                Log($"收到 JS 消息: {rawJson}");
                using (JsonDocument doc = JsonDocument.Parse(rawJson))
                {
                    JsonElement root = doc.RootElement;
                    string action = root.GetProperty("action").GetString() ?? "";

                    if (action == "start_capture")
                    {
                        // 前端点击捕获按钮
                        Log("收到开始捕获动作，最小化窗口...");
                        this.WindowState = WindowState.Minimized;
                        _isCapturing = true;
                        
                        Task.Delay(300).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                bool res = RegisterSingleHotkey(HOTKEY_ID_CAPTURE_POINT, _configCapturePoint);
                                Log($"注册捕获 F7 热键结果: {res}");
                            });
                        });
                        SendToJs(new { type = "capture_state_changed", isCapturing = true });
                    }
                    else if (action == "stop_capture")
                    {
                        // 前端取消捕获
                        _isCapturing = false;
                        UnregisterHotKey(_windowHandle, HOTKEY_ID_CAPTURE_POINT);
                        this.WindowState = WindowState.Normal;
                        this.Show();
                        this.Activate();
                        SendToJs(new { type = "capture_state_changed", isCapturing = false });
                    }
                    else if (action == "start_clicking")
                    {
                        string configsJson = root.GetProperty("configs").GetString() ?? "[]";
                        string clickMode = root.GetProperty("clickMode").GetString() ?? "sequential";
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
                    else if (action == "sync_points")
                    {
                        // 接收前端点位列表的数据变化同步 (删除、修改速度、禁用/启用状态同步)
                        string pointsJson = root.GetProperty("points").GetString() ?? "[]";
                        var newPoints = JsonSerializer.Deserialize<List<ClickPoint>>(pointsJson);
                        SyncPointsAndIndicators(newPoints);
                    }
                    else if (action == "update_indicator_style")
                    {
                        // 前端切换小红圈样式
                        int newStyle = root.GetProperty("style").GetInt32();
                        _indicatorStyle = newStyle;
                        Dispatcher.Invoke(() =>
                        {
                            foreach (var win in _indicatorWindows)
                            {
                                win.SetStyle(_indicatorStyle);
                            }
                        });
                        Log($"指示器圆圈样式成功切换为 Option {newStyle}");
                    }
                    else if (action == "update_hotkeys")
                    {
                        // 前端配置自定义快捷键
                        string settingsJson = root.GetProperty("hotkeys").GetRawText();
                        var newSettings = JsonSerializer.Deserialize<HotkeySettings>(settingsJson);
                        if (newSettings != null)
                        {
                            _configCaptureStart = newSettings.CaptureStart;
                            _configCapturePoint = newSettings.CapturePoint;
                            _configClickToggle = newSettings.ClickToggle;
                            _configClearAll = newSettings.ClearAll;
                            
                            if (newSettings.ToggleEnable != null)
                            {
                                _configToggleEnable = newSettings.ToggleEnable;
                            }

                            // 重新注册系统快捷键
                            Dispatcher.Invoke(() =>
                            {
                                RegisterAllCustomHotkeys();
                            });
                        }
                    }
                    else if (action == "highlight_point")
                    {
                        // 前端 Hover 卡片，通知对应圆圈高亮放大
                        long ptId = root.GetProperty("id").GetInt64();
                        Dispatcher.Invoke(() =>
                        {
                            var win = _indicatorWindows.FirstOrDefault(w => w.PointId == ptId);
                            if (win != null)
                            {
                                win.TriggerHighlight();
                            }
                        });
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
        // COORDINATE INDICATORS LIFECYCLE MANAGEMENT
        // --------------------------------------------------------------------------

        private void SyncPointsAndIndicators(List<ClickPoint>? newPoints)
        {
            if (newPoints == null) return;
            _clickPoints = newPoints;

            Dispatcher.Invoke(() =>
            {
                // 1. 关闭已被前端彻底移除的点位对应的指示器小窗口
                var activeIds = new HashSet<long>(_clickPoints.Select(p => p.Id));
                var toRemove = _indicatorWindows.Where(w => !activeIds.Contains(w.PointId)).ToList();
                foreach (var w in toRemove)
                {
                    try { w.Close(); } catch { }
                    _indicatorWindows.Remove(w);
                }

                // 2. 补齐或更新现有指示器窗口位置、样式、数字标号、透明度
                for (int i = 0; i < _clickPoints.Count; i++)
                {
                    var pt = _clickPoints[i];
                    var existing = _indicatorWindows.FirstOrDefault(w => w.PointId == pt.Id);
                    if (existing != null)
                    {
                        existing.UpdatePosition(pt.X, pt.Y);
                        existing.SetStyle(_indicatorStyle);
                        existing.SetNumber(i + 1); // 重新计算序号绘制 (1, 2, 3...)
                        existing.Opacity = pt.Enabled ? 1.0 : 0.25; // 禁用变半透明，启用恢复正常
                    }
                    else
                    {
                        var w = new IndicatorWindow(pt.Id, pt.X, pt.Y, _indicatorStyle, i + 1);
                        w.Show();
                        w.Opacity = pt.Enabled ? 1.0 : 0.25;
                        _indicatorWindows.Add(w);
                    }
                }
            });
        }

        // --------------------------------------------------------------------------
        // CLICK ENGINE & MULTITHREADING
        // --------------------------------------------------------------------------
        
        private void StartClicking(string configsJson, string clickMode)
        {
            Log($"StartClicking 被调用: mode={clickMode}");
            if (_isClicking)
            {
                Log("点击任务已在运行中，忽略此次调用。");
                return;
            }

            try
            {
                _clickPoints = JsonSerializer.Deserialize<List<ClickPoint>>(configsJson) ?? new List<ClickPoint>();
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
                    Thread t = new Thread(IndependentClickWorker) { IsBackground = true };
                    t.Start(pt);
                    _clickThreads.Add(t);
                }
            }
        }

        private void StopClicking()
        {
            Log("StopClicking 被调用，停止点击工作线程...");
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
                    
                    // 独立禁用控制：跳过已禁用的点
                    if (!pt.Enabled) continue;

                    ExecuteSingleClick(pt);
                    Thread.Sleep(Math.Max(pt.Interval, 10));
                }
            }
        }

        private void IndependentClickWorker(object? obj)
        {
            if (obj == null) return;
            ClickPoint pt = (ClickPoint)obj;
            while (_isClicking)
            {
                // 独立并发多线程下：从全局点池中检索最新的状态，保证能够实时动态开启/关闭
                var actualPt = _clickPoints.FirstOrDefault(p => p.Id == pt.Id);
                if (actualPt == null || !actualPt.Enabled)
                {
                    Thread.Sleep(50); // 挂起并等待重新启用
                    continue;
                }

                ExecuteSingleClick(actualPt);
                Thread.Sleep(Math.Max(actualPt.Interval, 10));
            }
        }

        private void ExecuteSingleClick(ClickPoint pt)
        {
            if (pt.ClickMode == "background")
            {
                IntPtr hwnd = new IntPtr(pt.Hwnd);
                if (IsWindow(hwnd))
                {
                    IntPtr lParam = (IntPtr)((pt.YRel << 16) | (pt.XRel & 0xFFFF));
                    PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
                    Thread.Sleep(10);
                    PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
                }
                else
                {
                    ActivePhysicalClick(pt.X, pt.Y);
                }
            }
            else
            {
                ActivePhysicalClick(pt.X, pt.Y);
            }
        }

        private void ActivePhysicalClick(int x, int y)
        {
            try
            {
                POINT origPos;
                GetCursorPos(out origPos);
                
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
        public string Title { get; set; } = "";
        
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
        public string ClickMode { get; set; } = "background"; // "background" or "active"

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true; // 新增属性：独立控制启用/禁用
    }

    public class WindowInfo
    {
        [JsonPropertyName("hwnd")]
        public long Hwnd { get; set; }
        
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
    }

    // 快捷键配置结构
    public class HotkeyConfig
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("modifiers")]
        public string Modifiers { get; set; } = "None"; // e.g. "Ctrl+Alt", "Shift"
    }

    public class HotkeySettings
    {
        [JsonPropertyName("captureStart")]
        public HotkeyConfig CaptureStart { get; set; } = new HotkeyConfig();

        [JsonPropertyName("capturePoint")]
        public HotkeyConfig CapturePoint { get; set; } = new HotkeyConfig();

        [JsonPropertyName("clickToggle")]
        public HotkeyConfig ClickToggle { get; set; } = new HotkeyConfig();

        [JsonPropertyName("clearAll")]
        public HotkeyConfig ClearAll { get; set; } = new HotkeyConfig();

        [JsonPropertyName("toggleEnable")]
        public HotkeyConfig ToggleEnable { get; set; } = new HotkeyConfig(); // 快捷禁用快捷键
    }
}