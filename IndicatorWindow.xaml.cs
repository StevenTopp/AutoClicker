using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace AutoClicker
{
    public partial class IndicatorWindow : Window
    {
        // --------------------------------------------------------------------------
        // WIN32 P/INVOKE DECLARATIONS
        // --------------------------------------------------------------------------
        
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64();
            else
                return GetWindowLong32(hWnd, nIndex);
        }

        private static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            if (IntPtr.Size == 8)
                return (int)SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong)).ToInt64();
            else
                return SetWindowLong32(hWnd, nIndex, dwNewLong);
        }

        // --------------------------------------------------------------------------
        // PROPERTIES & CONSTRUCTOR
        // --------------------------------------------------------------------------
        
        public long PointId { get; private set; }
        private int _currentStyle = 2; // Default to Option 2 (Hollow Ring)
        private Storyboard? _pulseStoryboard;
        private Storyboard? _highlightStoryboard;

        public IndicatorWindow(long pointId, double screenX, double screenY, int styleType, int number)
        {
            InitializeComponent();
            
            this.PointId = pointId;
            
            // Set initial position centered over target coordinates (Width=60, Height=60)
            this.Left = screenX - 30;
            this.Top = screenY - 30;
            
            SetNumber(number);
            SetStyle(styleType);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            
            try
            {
                // Make the window fully click-through, non-activatable, and hide from Alt+Tab
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                int targetStyle = extendedStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                int resultStyle = SetWindowLong(hwnd, GWL_EXSTYLE, targetStyle);
                
                int finalStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                bool success = (finalStyle & WS_EX_TRANSPARENT) != 0;

                // Log the result to help troubleshoot style injection
                string logMessage = $"IndicatorWindow [PointId={PointId}] OnSourceInitialized: hwnd={hwnd}, " +
                                    $"original=0x{extendedStyle:X8}, target=0x{targetStyle:X8}, " +
                                    $"result=0x{resultStyle:X8}, final=0x{finalStyle:X8}, " +
                                    $"WS_EX_TRANSPARENT_Success={success}";
                
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {logMessage}\r\n");
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IndicatorWindow OnSourceInitialized Exception: {ex.ToString()}\r\n");
                }
                catch { }
            }
        }

        // --------------------------------------------------------------------------
        // PUBLIC METHODS
        // --------------------------------------------------------------------------
        
        public void SetNumber(int number)
        {
            TxtNumber.Text = number.ToString();
        }

        public void SetStyle(int styleType)
        {
            _currentStyle = styleType;
            
            // Stop active animation if exists
            if (_pulseStoryboard != null)
            {
                _pulseStoryboard.Stop(this);
                _pulseStoryboard = null;
            }

            // Reset visibilities
            ContainerStyle1.Visibility = Visibility.Collapsed;
            ContainerStyle2.Visibility = Visibility.Collapsed;
            ContainerStyle3.Visibility = Visibility.Collapsed;
            ContainerStyle4.Visibility = Visibility.Collapsed;

            // Show selected container
            switch (_currentStyle)
            {
                case 1:
                    ContainerStyle1.Visibility = Visibility.Visible;
                    break;
                case 2:
                    ContainerStyle2.Visibility = Visibility.Visible;
                    break;
                case 3:
                    ContainerStyle3.Visibility = Visibility.Visible;
                    // Trigger pulse animation
                    _pulseStoryboard = (Storyboard)this.Resources["PulseAnimation"];
                    _pulseStoryboard.Begin(this, true);
                    break;
                case 4:
                    ContainerStyle4.Visibility = Visibility.Visible;
                    break;
                default:
                    ContainerStyle2.Visibility = Visibility.Visible; // Fallback to Option 2
                    break;
            }
        }

        public void UpdatePosition(double screenX, double screenY)
        {
            this.Left = screenX - 30;
            this.Top = screenY - 30;
        }

        public void TriggerHighlight()
        {
            Dispatcher.Invoke(() =>
            {
                _highlightStoryboard = (Storyboard)this.Resources["HighlightAnimation"];
                _highlightStoryboard.Begin(this, true);
            });
        }
    }
}
