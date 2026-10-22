using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReCord.UI
{
    public partial class CameraListItemControl : UserControl
    {
        public event EventHandler<int> Selected;
        public event EventHandler<int> DeleteRequested;

        /// <summary>初始化相机列表项控件。</summary>
        public CameraListItemControl()
        {
            InitializeComponent();
        }

        /// <summary>点击列表项时通知宿主选中对应相机。</summary>
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CameraViewItem item = DataContext as CameraViewItem;
            if (item != null)
                Selected?.Invoke(this, item.Id);
        }

        /// <summary>点击删除按钮时通知宿主删除对应相机。</summary>
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            CameraViewItem item = DataContext as CameraViewItem;
            if (item != null)
                DeleteRequested?.Invoke(this, item.Id);
        }
    }
}
