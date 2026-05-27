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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // --------------------------------------------------------------------------
        // PROPERTIES & CONSTRUCTOR
        // --------------------------------------------------------------------------
        
        public long PointId { get; private set; }
        private int _currentStyle = 2; // Default to Option 2 (Hollow Ring)
        private Storyboard? _pulseStoryboard;

        public IndicatorWindow(long pointId, double screenX, double screenY, int styleType)
        {
            InitializeComponent();
            
            this.PointId = pointId;
            
            // Set initial position centered over target coordinates (Width=60, Height=60)
            this.Left = screenX - 30;
            this.Top = screenY - 30;
            
            SetStyle(styleType);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            
            // Make the window fully click-through, non-activatable, and hide from Alt+Tab
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        // --------------------------------------------------------------------------
        // PUBLIC METHODS
        // --------------------------------------------------------------------------
        
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
    }
}
