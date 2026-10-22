using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReCord.Models;
using ReCord.Services;

namespace ReCord.UI
{
    public partial class RecorderView : UserControl, INotifyPropertyChanged
    {
        private readonly ConfigurationStore _store = new ConfigurationStore();
        private readonly ReCordConfiguration _configuration;
        private readonly RecorderManager _manager;
        private readonly Dictionary<int, CameraRuntimeState> _states =
            new Dictionary<int, CameraRuntimeState>();
        private readonly ObservableCollection<CameraViewItem> _cameras =
            new ObservableCollection<CameraViewItem>();
        private readonly ObservableCollection<LogViewItem> _logEntries =
            new ObservableCollection<LogViewItem>();
        private readonly Dictionary<int, long> _lastFrameSequence =
            new Dictionary<int, long>();
        private readonly DispatcherTimer _previewTimer;
        private readonly DispatcherTimer _clockTimer;
        private int _selectedCameraId = -1;
        private bool _runtimeStarted;
        private bool _shuttingDown;
        private bool _logExpanded = true;

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> WindowActionRequested;

        public ObservableCollection<CameraViewItem> Cameras { get { return _cameras; } }
        public ObservableCollection<LogViewItem> LogEntries { get { return _logEntries; } }
        public bool HasCameras { get { return Cameras.Count > 0; } }
        public string CameraCountLabel { get { return Cameras.Count + " 路输入"; } }
        public string PreviewModeLabel { get { return Cameras.Count > 1 ? "多画面" : "单画面"; } }
        public int PreviewColumns
        {
            get
            {
                return Cameras.Count <= 1 ? 1 : Cameras.Count <= 4 ? 2 : Cameras.Count <= 6 ? 3 : 4;
            }
        }
        public string LogCountLabel { get { return "共 " + LogEntries.Count + " 条"; } }

        /// <summary>初始化主视图、配置、录制管理器和界面定时器。</summary>
        public RecorderView()
        {
            InitializeComponent();
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            DataContext = this;
            _configuration = _store.Load();
            _manager = new RecorderManager(_configuration);
            _manager.CameraStateChanged += Manager_CameraStateChanged;
            AppLog.LineWritten += AppLog_LineWritten;
            AddCameraPanel.CamerasSaved += AddCameraPanel_CamerasSaved;
            AddCameraPanel.Cancelled += AddCameraPanel_Cancelled;
            FolderPickerPanel.FolderSelected += FolderPickerPanel_FolderSelected;
            FolderPickerPanel.Cancelled += FolderPickerPanel_Cancelled;

            LoadSettingsToUi();
            RebuildCameraUi();

            _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(66)
            };
            _previewTimer.Tick += PreviewTimer_Tick;
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _clockTimer.Tick += ClockTimer_Tick;
            _previewTimer.Start();
            _clockTimer.Start();

            AppLog.Info("ReCord 单进程录相机已启动");
        }

        /// <summary>启动相机运行时，并按设置开始自动录制。</summary>
        public void StartRuntime()
        {
            if (_runtimeStarted || _shuttingDown)
                return;
            _runtimeStarted = true;
            _manager.StartAll();
            AppLog.Info("相机运行时已启动，共 " + _configuration.Cameras.Count + " 路");
            if (_configuration.Settings.AutoStartRecording && _configuration.Cameras.Count > 0)
            {
                _manager.StartAllRecording();
                AppLog.Record("已按设置自动开始全部录制");
            }
        }

        /// <summary>停止定时器、保存设置并释放录制管理器。</summary>
        public void Shutdown()
        {
            if (_shuttingDown)
                return;
            _shuttingDown = true;
            _previewTimer.Stop();
            _clockTimer.Stop();
            _manager.CameraStateChanged -= Manager_CameraStateChanged;
            AppLog.LineWritten -= AppLog_LineWritten;
            SaveSettings(false);
            _manager.Dispose();
            AppLog.Info("ReCord 已停止");
        }

        /// <summary>根据窗口最大化状态切换标题栏图标。</summary>
        public void SetMaximized(bool maximized)
        {
            if (MaximizeIcon == null)
                return;
            MaximizeIcon.Data = Geometry.Parse(maximized
                ? "M7 7H17V17H7V7Z M5 9V19H15"
                : "M5 5H19V19H5V5Z");
        }

        /// <summary>将当前录制设置载入界面输入控件。</summary>
        private void LoadSettingsToUi()
        {
            RecorderSettings settings = _configuration.Settings;
            settings.Normalize();
            OutputDirectoryBox.Text = settings.OutputDirectory;
            PrefixBox.Text = settings.FileNamePrefix;
            FpsBox.Text = settings.PreferredFps.ToString();
            BitrateBox.Text = settings.VideoBitrateKbps.ToString();
            AutoRecordCheck.IsChecked = settings.AutoStartRecording;
        }

        /// <summary>根据当前配置重建相机预览和选择状态。</summary>
        private void RebuildCameraUi()
        {
            Cameras.Clear();
            _lastFrameSequence.Clear();

            int count = _configuration.Cameras.Count;
            foreach (CameraProfile camera in _configuration.Cameras.OrderBy(c => c.Id))
            {
                CameraRuntimeState state;
                _states.TryGetValue(camera.Id, out state);
                Cameras.Add(new CameraViewItem(camera.Clone(), state));
            }

            OnPropertyChanged("HasCameras");
            OnPropertyChanged("CameraCountLabel");
            OnPropertyChanged("PreviewModeLabel");
            OnPropertyChanged("PreviewColumns");

            CameraProfile selected = _configuration.Cameras.FirstOrDefault(c => c.Id == _selectedCameraId);
            if (selected == null)
                selected = _configuration.Cameras.OrderBy(c => c.Id).FirstOrDefault();
            if (selected != null)
                SelectCamera(selected.Id);
            else
                ClearSelection();
        }

        /// <summary>选中指定相机并刷新详情区域。</summary>
        private void SelectCamera(int cameraId)
        {
            _selectedCameraId = cameraId;
            CameraProfile profile = _configuration.Cameras.FirstOrDefault(c => c.Id == cameraId);
            if (profile == null)
                return;

            foreach (CameraViewItem item in Cameras)
                item.IsSelected = item.Id == cameraId;

            CameraRuntimeState state;
            _states.TryGetValue(cameraId, out state);
            SelectedCameraNameText.Text = profile.Name;
            SelectedSourceText.Text = DisplaySource(profile.Url);
            EditCameraButton.IsEnabled = true;
            SelectedRecordButton.IsEnabled = true;
            UpdateSelectedState(state, profile);
        }

        /// <summary>清除相机选择并恢复详情区域默认内容。</summary>
        private void ClearSelection()
        {
            _selectedCameraId = -1;
            SelectedCameraNameText.Text = "未选择相机";
            SelectedSourceText.Text = "--";
            SelectedCameraStateText.Text = "未选择相机";
            SelectedOnlineDot.Fill = new SolidColorBrush(Color.FromRgb(103, 115, 125));
            RecordingDurationText.Text = "00:00:00";
            RecordingFilePathText.Text = "准备就绪";
            SelectedRecordButtonText.Text = "开始录制";
            SelectedRecordButton.IsEnabled = false;
            EditCameraButton.IsEnabled = false;
        }

        /// <summary>使用相机运行状态刷新当前选中相机的详情。</summary>
        private void UpdateSelectedState(CameraRuntimeState state, CameraProfile profile)
        {
            bool online = state != null && state.Connection == CameraConnectionState.Online;
            SelectedOnlineDot.Fill = online
                ? (Brush)FindResource("OnlineBrush")
                : new SolidColorBrush(Color.FromRgb(103, 115, 125));
            SelectedCameraStateText.Text = state == null || string.IsNullOrEmpty(state.Message)
                ? "等待连接"
                : state.Message;
            SelectedRecordButtonText.Text = state != null && state.RecordingRequested
                ? "停止录制"
                : "开始录制";
            RecordingFilePathText.Text = state != null && !string.IsNullOrEmpty(state.FilePath)
                ? state.FilePath
                : "计划输出：" + _manager.BuildOutputPath(profile);
        }

        /// <summary>将管理器线程中的相机状态变化调度到界面线程。</summary>
        private void Manager_CameraStateChanged(CameraRuntimeState state)
        {
            if (_shuttingDown || state == null)
                return;
            Dispatcher.BeginInvoke(new Action(() => ApplyRuntimeState(state)));
        }

        /// <summary>将相机状态应用到列表、详情和状态栏。</summary>
        private void ApplyRuntimeState(CameraRuntimeState state)
        {
            if (_shuttingDown)
                return;
            _states[state.CameraId] = state;
            CameraViewItem item = Cameras.FirstOrDefault(c => c.Id == state.CameraId);
            if (item != null)
                item.ApplyState(state);
            if (_selectedCameraId == state.CameraId)
            {
                CameraProfile profile = _configuration.Cameras.FirstOrDefault(c => c.Id == state.CameraId);
                if (profile != null)
                    UpdateSelectedState(state, profile);
            }
            if (!string.IsNullOrEmpty(state.Message))
                StatusBarText.Text = state.Message;
        }

        /// <summary>定时拉取各相机最新画面并刷新预览。</summary>
        private void PreviewTimer_Tick(object sender, EventArgs e)
        {
            foreach (CameraViewItem item in Cameras)
            {
                VideoFrame frame;
                if (!_manager.TryGetLatestFrame(item.Id, out frame) || frame == null)
                    continue;
                long sequence;
                _lastFrameSequence.TryGetValue(item.Id, out sequence);
                if (sequence == frame.Sequence)
                    continue;
                _lastFrameSequence[item.Id] = frame.Sequence;
                item.Frame = frame;
            }
        }

        /// <summary>定时更新当前相机的录制时长显示。</summary>
        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            if (_selectedCameraId < 0)
                return;
            CameraRuntimeState state;
            if (!_states.TryGetValue(_selectedCameraId, out state) || state == null)
                return;
            if (state.IsRecording && state.RecordingStartedUtc.HasValue)
            {
                TimeSpan elapsed = DateTime.UtcNow - state.RecordingStartedUtc.Value;
                if (elapsed.TotalHours >= 1)
                    RecordingDurationText.Text = elapsed.ToString(@"hh\:mm\:ss");
                else
                    RecordingDurationText.Text = elapsed.ToString(@"mm\:ss");
            }
            else if (state.RecordingRequested)
            {
                RecordingDurationText.Text = "--:--";
            }
            else
            {
                RecordingDurationText.Text = "00:00:00";
            }
        }

        /// <summary>处理标题栏单击拖动和双击最大化。</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsButtonSource(e.OriginalSource as DependencyObject))
                return;
            if (e.ClickCount == 2)
                WindowActionRequested?.Invoke("maximize");
            else if (e.ClickCount == 1)
                WindowActionRequested?.Invoke("drag");
        }

        /// <summary>判断可视树来源是否位于按钮内部。</summary>
        private static bool IsButtonSource(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        /// <summary>请求最小化主窗口。</summary>
        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowActionRequested?.Invoke("minimize");
        }

        /// <summary>请求切换主窗口最大化状态。</summary>
        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowActionRequested?.Invoke("maximize");
        }

        /// <summary>请求关闭主窗口。</summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            WindowActionRequested?.Invoke("close");
        }

        /// <summary>打开添加相机面板并分配新的相机编号。</summary>
        private void AddCamera_Click(object sender, RoutedEventArgs e)
        {
            int nextId = _configuration.Cameras.Count == 0
                ? 0
                : _configuration.Cameras.Max(c => c.Id) + 1;
            AddCameraPanel.OpenForNew(nextId);
            AddCameraPanel.Visibility = Visibility.Visible;
        }

        /// <summary>响应相机列表项选中事件。</summary>
        private void CameraListItem_Selected(object sender, int cameraId)
        {
            SelectCamera(cameraId);
        }

        /// <summary>响应相机列表项删除请求。</summary>
        private void CameraListItem_DeleteRequested(object sender, int cameraId)
        {
            ListItem_DeleteRequested(sender, cameraId);
        }

        /// <summary>响应预览画面选中事件。</summary>
        private void CameraPreview_Selected(object sender, int cameraId)
        {
            SelectCamera(cameraId);
        }

        /// <summary>打开面板编辑当前选中的相机。</summary>
        private void EditCamera_Click(object sender, RoutedEventArgs e)
        {
            CameraProfile profile = _configuration.Cameras.FirstOrDefault(c => c.Id == _selectedCameraId);
            if (profile == null)
                return;
            AddCameraPanel.OpenForEdit(profile);
            AddCameraPanel.Visibility = Visibility.Visible;
        }

        /// <summary>保存新增或编辑的相机并处理编号和名称冲突。</summary>
        private void AddCameraPanel_CamerasSaved(IList<CameraProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return;

            List<CameraProfile> added = new List<CameraProfile>();
            int nextId = _configuration.Cameras.Count == 0
                ? 0
                : _configuration.Cameras.Max(c => c.Id) + 1;
            foreach (CameraProfile source in profiles)
            {
                CameraProfile profile = source.Clone();
                bool editingExisting = _configuration.Cameras.Any(c => c.Id == profile.Id);
                if (!editingExisting)
                {
                    while (_configuration.Cameras.Any(c => c.Id == profile.Id))
                        profile.Id = nextId++;
                }

                string baseName = string.IsNullOrWhiteSpace(profile.Name) ? "Camera" : profile.Name.Trim();
                string uniqueName = baseName;
                int suffix = 2;
                while (_configuration.Cameras.Any(c => c.Id != profile.Id &&
                    string.Equals(c.Name, uniqueName, StringComparison.CurrentCultureIgnoreCase)))
                {
                    uniqueName = baseName + " (" + suffix + ")";
                    suffix++;
                }
                profile.Name = uniqueName;

                _manager.AddOrUpdate(profile);
                added.Add(profile);
            }
            _store.Save(_configuration);
            AddCameraPanel.Visibility = Visibility.Collapsed;
            _selectedCameraId = added[0].Id;
            RebuildCameraUi();
            AppLog.Info("已保存 " + added.Count + " 个相机：" +
                        string.Join("、", added.Select(c => c.Name)));
        }

        /// <summary>关闭添加或编辑相机面板。</summary>
        private void AddCameraPanel_Cancelled(object sender, EventArgs e)
        {
            AddCameraPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>确认后删除相机并保存配置。</summary>
        private void ListItem_DeleteRequested(object sender, int cameraId)
        {
            CameraProfile profile = _configuration.Cameras.FirstOrDefault(c => c.Id == cameraId);
            if (profile == null)
                return;
            MessageBoxResult result = MessageBox.Show(
                "确定删除相机“" + profile.Name + "”吗？正在录制的文件会先结束并保存。",
                "删除相机", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            _manager.Remove(cameraId);
            _states.Remove(cameraId);
            _store.Save(_configuration);
            if (_selectedCameraId == cameraId)
                _selectedCameraId = -1;
            RebuildCameraUi();
            AppLog.Info("已删除相机：" + profile.Name);
        }

        /// <summary>根据当前状态开始或停止选中相机的录制。</summary>
        private void ToggleSelectedRecording_Click(object sender, RoutedEventArgs e)
        {
            CameraProfile profile = _configuration.Cameras.FirstOrDefault(c => c.Id == _selectedCameraId);
            if (profile == null)
                return;
            CameraRuntimeState state;
            _states.TryGetValue(profile.Id, out state);
            if (state != null && state.RecordingRequested)
            {
                _manager.StopRecording(profile.Id);
                AppLog.Record("请求停止相机 " + profile.Name + " 的录制");
                return;
            }
            if (!SaveSettings(true))
                return;
            _manager.StartRecording(profile);
            AppLog.Record("请求开始录制相机 " + profile.Name);
        }

        /// <summary>保存有效设置后开始所有相机录制。</summary>
        private void StartAllRecording_Click(object sender, RoutedEventArgs e)
        {
            if (_configuration.Cameras.Count == 0 || !SaveSettings(true))
                return;
            _manager.StartAllRecording();
            AppLog.Record("请求开始全部相机录制");
        }

        /// <summary>停止所有相机录制。</summary>
        private void StopAllRecording_Click(object sender, RoutedEventArgs e)
        {
            _manager.StopAllRecording();
            AppLog.Record("请求停止全部相机录制");
        }

        /// <summary>打开输出目录选择面板。</summary>
        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            FolderPickerPanel.Open(OutputDirectoryBox.Text);
            FolderPickerPanel.Visibility = Visibility.Visible;
        }

        /// <summary>将选择的目录写入输出目录输入框。</summary>
        private void FolderPickerPanel_FolderSelected(object sender, string path)
        {
            OutputDirectoryBox.Text = path;
            FolderPickerPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>关闭输出目录选择面板。</summary>
        private void FolderPickerPanel_Cancelled(object sender, EventArgs e)
        {
            FolderPickerPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>处理保存设置按钮并显示操作结果。</summary>
        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings(true);
        }

        /// <summary>校验、应用并保存界面中的录制设置。</summary>
        private bool SaveSettings(bool showResult)
        {
            int fps;
            int bitrate;
            if (!int.TryParse(FpsBox.Text, out fps) || fps < 1 || fps > 60)
            {
                if (showResult) MessageBox.Show("帧率必须是 1 到 60 之间的整数。", "ReCord");
                return false;
            }
            if (!int.TryParse(BitrateBox.Text, out bitrate) || bitrate < 500 || bitrate > 30000)
            {
                if (showResult) MessageBox.Show("码率必须是 500 到 30000 Kbps 之间的整数。", "ReCord");
                return false;
            }
            string output = OutputDirectoryBox.Text == null
                ? string.Empty
                : OutputDirectoryBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                if (showResult) MessageBox.Show("请选择录制输出目录。", "ReCord");
                return false;
            }
            try { Directory.CreateDirectory(output); }
            catch (Exception ex)
            {
                if (showResult) MessageBox.Show("无法创建输出目录：" + ex.Message, "ReCord");
                return false;
            }

            RecorderSettings settings = new RecorderSettings
            {
                OutputDirectory = Path.GetFullPath(output),
                FileNamePrefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? "ReCord" : PrefixBox.Text.Trim(),
                PreferredFps = fps,
                VideoBitrateKbps = bitrate,
                AutoStartRecording = AutoRecordCheck.IsChecked == true
            };
            _manager.ApplySettings(settings);
            _store.Save(_configuration);
            if (showResult)
            {
                StatusBarText.Text = "录制设置已保存";
                AppLog.Info("录制设置已保存");
            }
            return true;
        }

        /// <summary>将日志事件调度到界面线程后追加显示。</summary>
        private void AppLog_LineWritten(AppLogEntry entry)
        {
            if (entry == null || _shuttingDown)
                return;
            Dispatcher.BeginInvoke(new Action(() => AppendLog(entry)));
        }

        /// <summary>追加日志条目并限制界面保留的最大条数。</summary>
        private void AppendLog(AppLogEntry entry)
        {
            LogEntries.Add(new LogViewItem(entry));
            while (LogEntries.Count > 400)
                LogEntries.RemoveAt(0);
            OnPropertyChanged("LogCountLabel");
            LogScrollViewer.ScrollToEnd();
        }

        /// <summary>清空当前界面中的日志列表。</summary>
        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogEntries.Clear();
            OnPropertyChanged("LogCountLabel");
        }

        /// <summary>展开或收起底部日志区域。</summary>
        private void ToggleLog_Click(object sender, RoutedEventArgs e)
        {
            _logExpanded = !_logExpanded;
            LogRow.Height = _logExpanded ? new GridLength(180) : new GridLength(0);
            LogToggleButton.Content = _logExpanded ? "收起" : "展开";
        }

        /// <summary>隐藏网络视频源凭据并生成简短的来源显示文本。</summary>
        private static string DisplaySource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return "--";
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri) && !uri.IsFile)
                return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
            return Path.GetFileName(source);
        }

        /// <summary>触发指定属性的变更通知。</summary>
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
