using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReCord.Models;
using ReCord.Services;

namespace ReCord.UI
{
    public partial class AddCameraOverlay : UserControl
    {
        private CameraProfile _editing;
        private CancellationTokenSource _scanCts;
        private readonly List<IpScanCandidate> _scanResults = new List<IpScanCandidate>();

        public event Action<IList<CameraProfile>> CamerasSaved;
        public event EventHandler Cancelled;

        /// <summary>初始化添加和编辑相机的悬浮面板。</summary>
        public AddCameraOverlay()
        {
            InitializeComponent();
        }

        /// <summary>重置面板以使用指定编号添加新相机。</summary>
        public void OpenForNew(int nextId)
        {
            CancelScan();
            _editing = new CameraProfile { Id = nextId, Name = "Camera " + nextId, Url = "rtsp://" };
            TitleText.Text = "添加相机";
            NameBox.Text = _editing.Name;
            UrlBox.Text = _editing.Url;
            ValidationText.Visibility = Visibility.Collapsed;
            ScanSection.Visibility = Visibility.Collapsed;
            ResetScanResults();
            OnvifUsernameBox.Text = string.Empty;
            OnvifPasswordBox.Password = string.Empty;
            ScanRangeBox.Text = IpCameraScanner.GetLocalSubnets().FirstOrDefault() ?? "192.168.1.0/24";
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>载入指定相机的配置以进行编辑。</summary>
        public void OpenForEdit(CameraProfile profile)
        {
            CancelScan();
            _editing = profile.Clone();
            TitleText.Text = "编辑相机";
            NameBox.Text = profile.Name;
            UrlBox.Text = profile.Url;
            ValidationText.Visibility = Visibility.Collapsed;
            ScanSection.Visibility = Visibility.Collapsed;
            OnvifUsernameBox.Text = string.Empty;
            OnvifPasswordBox.Password = string.Empty;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>选择本地视频文件并填入视频源地址。</summary>
        private void PickFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "选择视频文件",
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.ts|所有文件|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() == true)
                UrlBox.Text = dialog.FileName;
        }

        /// <summary>校验相机输入并提交当前配置。</summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text == null ? string.Empty : NameBox.Text.Trim();
            string url = UrlBox.Text == null ? string.Empty : UrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowValidation("请输入相机名称。");
                return;
            }
            if (string.IsNullOrWhiteSpace(url) || url == "rtsp://")
            {
                ShowValidation("请输入有效的视频源地址。");
                return;
            }
            _editing.Name = name;
            _editing.Url = url;
            CamerasSaved?.Invoke(new List<CameraProfile> { _editing.Clone() });
        }

        /// <summary>展开或收起相机扫描区域。</summary>
        private void ToggleScan_Click(object sender, RoutedEventArgs e)
        {
            if (ScanSection.Visibility == Visibility.Visible)
            {
                CancelScan();
                ScanSection.Visibility = Visibility.Collapsed;
                return;
            }
            ScanSection.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(ScanRangeBox.Text))
                ScanRangeBox.Text = IpCameraScanner.GetLocalSubnets().FirstOrDefault() ?? "192.168.1.0/24";
            Dispatcher.BeginInvoke(new Action(() => ContentScrollViewer.ScrollToEnd()));
        }

        /// <summary>启动或停止 ONVIF 与 RTSP 设备扫描。</summary>
        private async void StartScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanCts != null)
            {
                _scanCts.Cancel();
                ScanStatusText.Text = "正在停止设备发现...";
                return;
            }

            string range = ScanRangeBox.Text == null ? string.Empty : ScanRangeBox.Text.Trim();
            if (IpCameraScanner.ParseRange(range).Count == 0)
            {
                ShowValidation("请输入有效 IPv4 网段，例如 192.168.1.0/24。");
                return;
            }

            ValidationText.Visibility = Visibility.Collapsed;
            ResetScanResults();
            CancellationTokenSource cts = new CancellationTokenSource();
            _scanCts = cts;
            StartScanButton.Content = "停止发现";
            ScanStatusText.Text = "正在发现 ONVIF 设备并扫描 " + range + " ...";

            Progress<IpScanProgress> progress = new Progress<IpScanProgress>(item =>
            {
                if (!ReferenceEquals(_scanCts, cts))
                    return;
                if (item.Phase == IpScanPhase.Onvif)
                {
                    ScanStatusText.Text = item.Total > 0
                        ? item.CurrentAddress + "  ·  " + item.Completed + "/" + item.Total +
                          "  ·  已发现 " + item.Found + " 台"
                        : item.CurrentAddress + (item.Found > 0 ? "  ·  已发现 " + item.Found + " 台" : string.Empty);
                }
                else
                {
                    ScanStatusText.Text = "RTSP 端口扫描 " + item.CurrentAddress + "  ·  " +
                                          item.Completed + "/" + item.Total +
                                          "  ·  已发现 " + item.Found + " 台";
                }
            });

            try
            {
                OnvifCredentials credentials = new OnvifCredentials(
                    OnvifUsernameBox.Text, OnvifPasswordBox.Password);
                IList<IpScanCandidate> results = await IpCameraScanner.ScanAsync(
                    range, credentials, progress, cts.Token);
                if (!ReferenceEquals(_scanCts, cts))
                    return;

                _scanResults.AddRange(results);
                ScanResultsList.ItemsSource = _scanResults;
                foreach (object item in ScanResultsList.Items)
                    ScanResultsList.SelectedItems.Add(item);
                int onvifCount = results.Count(c => c.IsOnvif);
                ScanStatusText.Text = results.Count == 0
                    ? "发现完成，未发现 ONVIF 或开放 RTSP 端口的设备"
                    : "发现完成：ONVIF " + onvifCount + " 台，RTSP " +
                      (results.Count - onvifCount) + " 台，已默认全部选中";
                UpdateScanSelection();
                ContentScrollViewer.UpdateLayout();
                ContentScrollViewer.ScrollToEnd();
            }
            catch (OperationCanceledException)
            {
                ScanStatusText.Text = "设备发现已停止";
            }
            catch (Exception ex)
            {
                ScanStatusText.Text = "设备发现失败：" + ex.Message;
                AppLog.Warning("相机发现失败：" + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_scanCts, cts))
                {
                    _scanCts = null;
                    StartScanButton.Content = "开始发现";
                }
                cts.Dispose();
            }
        }

        /// <summary>扫描结果选择变化时更新选择状态。</summary>
        private void ScanResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateScanSelection();
        }

        /// <summary>根据已选扫描结果更新按钮状态和选择计数。</summary>
        private void UpdateScanSelection()
        {
            int selected = ScanResultsList.SelectedItems.Count;
            AddScannedButton.IsEnabled = selected > 0;
            ScanSelectionText.Text = _scanResults.Count == 0
                ? "未发现设备"
                : "已选择 " + selected + " / " + _scanResults.Count + " 台设备";
        }

        /// <summary>将选中的扫描结果转换为相机配置并提交。</summary>
        private void AddScanned_Click(object sender, RoutedEventArgs e)
        {
            List<CameraProfile> profiles = new List<CameraProfile>();
            int offset = 0;
            foreach (IpScanCandidate candidate in ScanResultsList.SelectedItems.Cast<IpScanCandidate>())
            {
                profiles.Add(new CameraProfile
                {
                    Id = _editing.Id + offset,
                    Name = candidate.DisplayName,
                    Url = candidate.SuggestedUrl
                });
                offset++;
            }
            if (profiles.Count == 0)
            {
                ShowValidation("请至少选择一台扫描到的相机。");
                return;
            }
            CamerasSaved?.Invoke(profiles);
        }

        /// <summary>清空扫描结果并重置扫描状态文本。</summary>
        private void ResetScanResults()
        {
            _scanResults.Clear();
            ScanResultsList.ItemsSource = null;
            ScanStatusText.Text = "选择网段后开始设备发现";
            UpdateScanSelection();
        }

        /// <summary>取消正在执行的设备扫描并恢复扫描按钮。</summary>
        private void CancelScan()
        {
            CancellationTokenSource cts = _scanCts;
            _scanCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
            if (StartScanButton != null)
            {
                StartScanButton.Content = "开始发现";
                ScanStatusText.Text = "设备发现已停止";
            }
        }

        /// <summary>显示输入校验错误消息。</summary>
        private void ShowValidation(string message)
        {
            ValidationText.Text = message;
            ValidationText.Visibility = Visibility.Visible;
        }

        /// <summary>取消扫描并关闭相机编辑面板。</summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelScan();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }
}
