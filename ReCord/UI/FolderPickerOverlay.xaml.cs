using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReCord.UI
{
    public partial class FolderPickerOverlay : UserControl
    {
        private string _currentPath;
        private readonly ObservableCollection<FolderViewItem> _folders =
            new ObservableCollection<FolderViewItem>();
        public event EventHandler<string> FolderSelected;
        public event EventHandler Cancelled;

        public ObservableCollection<FolderViewItem> Folders { get { return _folders; } }

        /// <summary>初始化输出目录选择面板。</summary>
        public FolderPickerOverlay()
        {
            InitializeComponent();
            if (!DesignerProperties.GetIsInDesignMode(this))
                DataContext = this;
        }

        /// <summary>打开指定起始目录，路径无效时回退到程序目录。</summary>
        public void Open(string startPath)
        {
            Navigate(string.IsNullOrWhiteSpace(startPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : startPath);
        }

        /// <summary>切换当前目录并刷新其子文件夹列表。</summary>
        private void Navigate(string path)
        {
            try
            {
                DirectoryInfo directory = new DirectoryInfo(path);
                if (!directory.Exists)
                    directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                _currentPath = directory.FullName;
                PathBox.Text = _currentPath;
                Folders.Clear();
                foreach (string child in Directory.GetDirectories(_currentPath)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    Folders.Add(new FolderViewItem(child));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法读取目录：" + ex.Message, "ReCord",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>导航到当前目录的上一级目录。</summary>
        private void Up_Click(object sender, RoutedEventArgs e)
        {
            DirectoryInfo parent = Directory.GetParent(_currentPath);
            if (parent != null)
                Navigate(parent.FullName);
        }

        /// <summary>双击文件夹列表项时进入对应目录。</summary>
        private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FolderViewItem item = FolderList.SelectedItem as FolderViewItem;
            if (item != null)
                Navigate(item.FullPath);
        }

        /// <summary>在当前目录下创建文件夹并进入该目录。</summary>
        private void CreateFolder_Click(object sender, RoutedEventArgs e)
        {
            string name = NewFolderBox.Text == null ? string.Empty : NewFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;
            try
            {
                string path = Path.Combine(_currentPath, name);
                Directory.CreateDirectory(path);
                NewFolderBox.Text = string.Empty;
                Navigate(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("新建文件夹失败：" + ex.Message, "ReCord",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>确认并提交当前有效的目录路径。</summary>
        private void Choose_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(PathBox.Text) && Directory.Exists(PathBox.Text))
                _currentPath = Path.GetFullPath(PathBox.Text);
            FolderSelected?.Invoke(this, _currentPath);
        }

        /// <summary>取消目录选择并通知宿主。</summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }
}
