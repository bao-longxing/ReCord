using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReCord.Services;

namespace ReCord.UI
{
    public partial class CameraPreviewControl : UserControl
    {
        public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
            "Frame", typeof(VideoFrame), typeof(CameraPreviewControl),
            new PropertyMetadata(null, OnFrameChanged));

        private WriteableBitmap _bitmap;
        private int _width;
        private int _height;

        public event EventHandler<int> Selected;

        /// <summary>初始化相机画面预览控件。</summary>
        public CameraPreviewControl()
        {
            InitializeComponent();
        }

        public VideoFrame Frame
        {
            get { return (VideoFrame)GetValue(FrameProperty); }
            set { SetValue(FrameProperty, value); }
        }

        /// <summary>画面依赖属性变化时触发位图重绘或清空预览。</summary>
        private static void OnFrameChanged(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            CameraPreviewControl control = dependencyObject as CameraPreviewControl;
            if (control == null)
                return;
            VideoFrame frame = args.NewValue as VideoFrame;
            if (frame == null)
                control.ClearFrame();
            else
                control.RenderFrame(frame);
        }

        /// <summary>将 BGR 视频帧写入预览位图。</summary>
        private void RenderFrame(VideoFrame frame)
        {
            if (frame == null || frame.Data == null || frame.Width <= 0 || frame.Height <= 0)
                return;
            if (_bitmap == null || _width != frame.Width || _height != frame.Height)
            {
                _width = frame.Width;
                _height = frame.Height;
                _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgr24, null);
                PreviewImage.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Data, frame.Stride, 0);
            Placeholder.Visibility = Visibility.Collapsed;
        }

        /// <summary>清空当前画面并显示占位内容。</summary>
        public void ClearFrame()
        {
            PreviewImage.Source = null;
            _bitmap = null;
            Placeholder.Visibility = Visibility.Visible;
        }

        /// <summary>点击预览画面时通知宿主选中对应相机。</summary>
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CameraViewItem item = DataContext as CameraViewItem;
            if (item != null)
                Selected?.Invoke(this, item.Id);
        }
    }
}
