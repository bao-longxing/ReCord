using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using ReCord.Models;
using ReCord.Services;

namespace ReCord.UI
{
    public sealed class CameraViewItem : INotifyPropertyChanged
    {
        private CameraRuntimeState _state;
        private VideoFrame _frame;
        private bool _isSelected;

        /// <summary>初始化用于设计器预览的示例相机。</summary>
        public CameraViewItem()
            : this(
                new CameraProfile { Id = 1, Name = "Camera 1", Url = "rtsp://192.168.1.10/stream" },
                new CameraRuntimeState
                {
                    CameraId = 1,
                    Connection = CameraConnectionState.Online,
                    Message = "正在监看",
                    Width = 1920,
                    Height = 1080,
                    Fps = 25
                })
        {
        }

        /// <summary>使用相机配置和运行状态初始化视图项。</summary>
        public CameraViewItem(CameraProfile profile, CameraRuntimeState state)
        {
            Profile = profile ?? new CameraProfile();
            _state = state;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public CameraProfile Profile { get; private set; }
        public int Id { get { return Profile.Id; } }
        public string Name { get { return Profile.Name ?? string.Empty; } }
        public string StateText
        {
            get
            {
                return _state == null || string.IsNullOrEmpty(_state.Message)
                    ? "等待连接"
                    : _state.Message;
            }
        }

        public bool IsOnline
        {
            get { return _state != null && _state.Connection == CameraConnectionState.Online; }
        }

        public bool IsRecording
        {
            get { return _state != null && (_state.IsRecording || _state.RecordingRequested); }
        }

        public string FormatText
        {
            get
            {
                return _state != null && _state.Width > 0
                    ? _state.Width + " x " + _state.Height + "  " + _state.Fps + "fps"
                    : "--";
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }

        public VideoFrame Frame
        {
            get { return _frame; }
            set
            {
                if (ReferenceEquals(_frame, value))
                    return;
                _frame = value;
                OnPropertyChanged("Frame");
            }
        }

        /// <summary>应用新的运行状态并通知相关显示属性变化。</summary>
        public void ApplyState(CameraRuntimeState state)
        {
            _state = state;
            OnPropertyChanged("StateText");
            OnPropertyChanged("IsOnline");
            OnPropertyChanged("IsRecording");
            OnPropertyChanged("FormatText");
        }

        /// <summary>触发指定属性的变更通知。</summary>
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class LogViewItem
    {
        /// <summary>根据日志条目生成界面显示文本。</summary>
        public LogViewItem(AppLogEntry entry)
        {
            Level = entry.Level;
            DisplayText = "[" + entry.Timestamp.ToString("HH:mm:ss") + "] " + entry.Message;
        }

        public string DisplayText { get; private set; }
        public AppLogLevel Level { get; private set; }
    }

    public sealed class FolderViewItem
    {
        /// <summary>根据完整路径初始化文件夹显示项。</summary>
        public FolderViewItem(string fullPath)
        {
            FullPath = fullPath;
            Name = System.IO.Path.GetFileName(fullPath);
        }

        public string Name { get; private set; }
        public string FullPath { get; private set; }
    }

    public sealed class RecorderViewDesignData
    {
        /// <summary>初始化录相机界面的设计时示例数据。</summary>
        public RecorderViewDesignData()
        {
            Cameras = new ObservableCollection<CameraViewItem>
            {
                new CameraViewItem(
                    new CameraProfile { Id = 1, Name = "产线入口", Url = "rtsp://192.168.1.10/stream" },
                    new CameraRuntimeState
                    {
                        CameraId = 1,
                        Connection = CameraConnectionState.Online,
                        IsRecording = true,
                        RecordingRequested = true,
                        Message = "正在录制",
                        Width = 1920,
                        Height = 1080,
                        Fps = 25
                    }),
                new CameraViewItem(
                    new CameraProfile { Id = 2, Name = "仓库出口", Url = "rtsp://192.168.1.11/stream" },
                    new CameraRuntimeState
                    {
                        CameraId = 2,
                        Connection = CameraConnectionState.Online,
                        Message = "正在监看",
                        Width = 1280,
                        Height = 720,
                        Fps = 25
                    })
            };
            Cameras[0].IsSelected = true;
            LogEntries = new ObservableCollection<LogViewItem>
            {
                new LogViewItem(new AppLogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = AppLogLevel.Info,
                    Message = "界面设计时预览"
                }),
                new LogViewItem(new AppLogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = AppLogLevel.Record,
                    Message = "Camera 1 已开始录制"
                })
            };
        }

        public ObservableCollection<CameraViewItem> Cameras { get; private set; }
        public ObservableCollection<LogViewItem> LogEntries { get; private set; }
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
    }

    public sealed class FolderPickerDesignData
    {
        /// <summary>初始化文件夹选择界面的设计时示例数据。</summary>
        public FolderPickerDesignData()
        {
            Folders = new ObservableCollection<FolderViewItem>
            {
                new FolderViewItem(@"C:\Recordings\2026-10-22"),
                new FolderViewItem(@"C:\Recordings\产线入口"),
                new FolderViewItem(@"C:\Recordings\仓库出口")
            };
        }

        public ObservableCollection<FolderViewItem> Folders { get; private set; }
    }
}
