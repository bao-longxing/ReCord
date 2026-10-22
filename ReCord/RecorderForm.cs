using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using ReCord.UI;

namespace ReCord
{
    public sealed class RecorderForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int HT_CAPTION = 2;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int ResizeBorder = 8;

        private readonly RecorderView _view;
        private bool _shutdown;

        /// <summary>初始化无边框主窗口并承载录相机视图。</summary>
        public RecorderForm()
        {
            Text = "ReCord";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = new Size(1100, 720);
            Size = new Size(1480, 920);
            BackColor = Color.FromArgb(31, 38, 51);
            DoubleBuffered = true;
            MaximizedBounds = Screen.FromControl(this).WorkingArea;

            _view = new RecorderView();
            _view.WindowActionRequested += View_WindowActionRequested;
            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                BackColor = BackColor,
                Child = _view
            };
            Controls.Add(host);
        }

        /// <summary>窗口显示后启动相机运行环境。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _view.StartRuntime();
        }

        /// <summary>关闭窗口前释放录相机运行时资源。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_shutdown)
            {
                _shutdown = true;
                _view.Shutdown();
            }
            base.OnFormClosing(e);
        }

        /// <summary>跟随窗口状态更新最大化边界和图标。</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            if (_view != null)
                _view.SetMaximized(WindowState == FormWindowState.Maximized);
        }

        /// <summary>处理命中测试以实现无边框窗口缩放。</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                Point point = PointToClient(new Point(m.LParam.ToInt32()));
                bool left = point.X <= ResizeBorder;
                bool right = point.X >= ClientSize.Width - ResizeBorder;
                bool top = point.Y <= ResizeBorder;
                bool bottom = point.Y >= ClientSize.Height - ResizeBorder;
                if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)HTLEFT;
                else if (right) m.Result = (IntPtr)HTRIGHT;
                else if (top) m.Result = (IntPtr)HTTOP;
                else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                else m.Result = (IntPtr)HTCLIENT;
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>执行视图请求的最小化、最大化、关闭或拖动操作。</summary>
        private void View_WindowActionRequested(string action)
        {
            switch (action)
            {
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "maximize":
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    break;
                case "close":
                    Close();
                    break;
                case "drag":
                    if (WindowState == FormWindowState.Maximized)
                        return;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
                    break;
            }
        }

        /// <summary>释放鼠标捕获以开始窗口拖动。</summary>
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        /// <summary>向指定窗口发送 Windows 消息。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
