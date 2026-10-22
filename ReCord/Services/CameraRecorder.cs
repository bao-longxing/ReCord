using System;
using System.IO;
using System.Threading;
using ReCord.Models;

namespace ReCord.Services
{
    public enum CameraConnectionState
    {
        Offline,
        Connecting,
        Online
    }

    public sealed class CameraRuntimeState
    {
        public int CameraId { get; set; }
        public CameraConnectionState Connection { get; set; }
        public bool RecordingRequested { get; set; }
        public bool IsRecording { get; set; }
        public string FilePath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public DateTime? RecordingStartedUtc { get; set; }
        public DateTime? LastFrameUtc { get; set; }
        public string Message { get; set; }
    }

    public sealed class CameraRecorder : IDisposable
    {
        private readonly object _sync = new object();
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        private CameraProfile _profile;
        private Thread _worker;
        private Mp4VideoWriter _writer;
        private VideoFrame _latestFrame;
        private volatile bool _stopRequested;
        private volatile bool _recordingRequested;
        private bool _disposed;
        private string _requestedPath;
        private CameraRuntimeState _state;
        private DateTime? _recordingStartedUtc;

        public event Action<CameraRuntimeState> StateChanged;

        public CameraProfile Profile
        {
            get { lock (_sync) return _profile.Clone(); }
        }

        /// <summary>初始化指定相机的运行状态和录制设置。</summary>
        public CameraRecorder(CameraProfile profile, RecorderSettings settings)
        {
            _profile = profile.Clone();
            Settings = settings;
            _state = new CameraRuntimeState
            {
                CameraId = profile.Id,
                Connection = CameraConnectionState.Offline,
                Message = "等待连接"
            };
        }

        public RecorderSettings Settings { get; set; }

        /// <summary>创建当前相机运行状态的线程安全副本。</summary>
        public CameraRuntimeState Snapshot()
        {
            lock (_sync)
            {
                CameraRuntimeState copy = new CameraRuntimeState
                {
                    CameraId = _state.CameraId,
                    Connection = _state.Connection,
                    RecordingRequested = _state.RecordingRequested,
                    IsRecording = _state.IsRecording,
                    FilePath = _state.FilePath,
                    Width = _state.Width,
                    Height = _state.Height,
                    Fps = _state.Fps,
                    RecordingStartedUtc = _state.RecordingStartedUtc,
                    LastFrameUtc = _state.LastFrameUtc,
                    Message = _state.Message
                };
                if (copy.IsRecording && _recordingStartedUtc.HasValue)
                    copy.RecordingStartedUtc = _recordingStartedUtc;
                return copy;
            }
        }

        /// <summary>启动后台视频读取与预览线程。</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _worker != null)
                    return;
                _stopRequested = false;
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "CameraRecorder-" + _profile.Id
                };
                _worker.Start();
            }
        }

        /// <summary>请求在指定路径开始录制，等待视频源连接后创建文件。</summary>
        public void StartRecording(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;
            lock (_sync)
            {
                _requestedPath = filePath;
                _recordingRequested = true;
                _state.RecordingRequested = true;
                _state.Message = _state.Connection == CameraConnectionState.Online
                    ? "正在打开录制文件"
                    : "等待视频源";
            }
            Start();
            NotifyState();
        }

        /// <summary>请求停止当前录制并唤醒后台线程。</summary>
        public void StopRecording()
        {
            lock (_sync)
            {
                _recordingRequested = false;
                _state.RecordingRequested = false;
                _state.Message = "正在停止录制";
            }
            _wake.Set();
            NotifyState();
        }

        /// <summary>尝试获取后台线程产生的最新视频帧。</summary>
        public bool TryGetLatestFrame(out VideoFrame frame)
        {
            lock (_sync)
            {
                frame = _latestFrame;
                return frame != null;
            }
        }

        /// <summary>持续连接视频源、读取画面并在需要时写入录制文件。</summary>
        private void WorkerLoop()
        {
            while (!_stopRequested)
            {
                FfmpegVideoDecoder decoder = null;
                bool readEnded = false;
                try
                {
                    UpdateConnection(CameraConnectionState.Connecting, "正在连接");
                    decoder = new FfmpegVideoDecoder();
                    CameraProfile profile;
                    lock (_sync) profile = _profile.Clone();

                    if (!decoder.Open(profile.Url))
                    {
                        UpdateConnection(CameraConnectionState.Offline, "连接失败，稍后重试");
                        WaitForWake(3000);
                        continue;
                    }

                    UpdateOnline(decoder);
                    bool fileSource = decoder.IsFileSource;
                    int frameDelay = Math.Max(10, 1000 / Math.Max(1, decoder.FrameRate));

                    while (!_stopRequested)
                    {
                        VideoFrame frame;
                        if (!decoder.TryReadFrame(out frame))
                        {
                            readEnded = true;
                            break;
                        }

                        lock (_sync)
                        {
                            _latestFrame = frame;
                            _state.LastFrameUtc = frame.TimestampUtc;
                        }

                        ProcessRecording(frame);
                        if (fileSource)
                            WaitForWake(frameDelay);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("相机 " + _profile.Id + " 运行异常：" + ex.Message);
                }
                finally
                {
                    CloseWriter();
                    if (readEnded && !_stopRequested)
                        StopAfterSourceLoss();
                    if (decoder != null)
                        decoder.Dispose();
                }

                if (!_stopRequested)
                {
                    UpdateConnection(CameraConnectionState.Offline, "连接中断，正在重连");
                    WaitForWake(2000);
                }
            }

            CloseWriter();
            UpdateConnection(CameraConnectionState.Offline, "已停止");
        }

        /// <summary>根据录制请求创建写入器并将当前帧写入文件。</summary>
        private void ProcessRecording(VideoFrame frame)
        {
            bool requested;
            string path;
            lock (_sync)
            {
                requested = _recordingRequested;
                path = _requestedPath;
            }

            if (!requested)
            {
                if (_writer != null)
                {
                    CloseWriter();
                    UpdateRecording(false, null, "已停止录制");
                }
                return;
            }

            if (_writer == null)
            {
                try
                {
                    Mp4VideoWriter writer = new Mp4VideoWriter();
                    if (!writer.Open(path, frame.Width, frame.Height, Settings.PreferredFps,
                        Settings.VideoBitrateKbps))
                    {
                        lock (_sync) _recordingRequested = false;
                        UpdateRecording(false, null, "无法创建录制文件");
                        return;
                    }
                    _writer = writer;
                    _recordingStartedUtc = DateTime.UtcNow;
                    UpdateRecording(true, path, "录制中");
                }
                catch (Exception ex)
                {
                    AppLog.Error("创建录制文件失败：" + ex.Message);
                    lock (_sync) _recordingRequested = false;
                    UpdateRecording(false, null, "创建录制文件失败");
                    return;
                }
            }

            string error;
            if (!_writer.Write(frame, out error))
            {
                AppLog.Error("写入视频失败：" + error);
                CloseWriter();
                lock (_sync) _recordingRequested = false;
                UpdateRecording(false, null, "写入失败：" + error);
            }
        }

        /// <summary>关闭当前 MP4 写入器并清除录制开始时间。</summary>
        private void CloseWriter()
        {
            Mp4VideoWriter writer = _writer;
            _writer = null;
            if (writer != null)
            {
                try { writer.Close(); } catch (Exception ex) { AppLog.Warning("关闭录制文件异常：" + ex.Message); }
            }
            _recordingStartedUtc = null;
        }

        /// <summary>视频源提前结束时终止当前录制并记录警告。</summary>
        private void StopAfterSourceLoss()
        {
            bool wasRequested;
            lock (_sync)
            {
                wasRequested = _recordingRequested;
                _recordingRequested = false;
                _state.RecordingRequested = false;
            }
            if (wasRequested)
            {
                UpdateRecording(false, null, "视频源中断，录制已停止");
                AppLog.Warning("相机 " + _profile.Id + " 的视频源中断，当前录制已结束");
            }
        }

        /// <summary>根据已连接解码器更新在线状态和视频参数。</summary>
        private void UpdateOnline(FfmpegVideoDecoder decoder)
        {
            lock (_sync)
            {
                _state.Connection = CameraConnectionState.Online;
                _state.Width = decoder.Width;
                _state.Height = decoder.Height;
                _state.Fps = decoder.FrameRate;
                _state.Message = _recordingRequested ? "等待录制" : "预览中";
            }
            NotifyState();
        }

        /// <summary>更新相机连接状态和用户可见状态消息。</summary>
        private void UpdateConnection(CameraConnectionState connection, string message)
        {
            lock (_sync)
            {
                _state.Connection = connection;
                if (connection != CameraConnectionState.Online)
                {
                    _state.Width = 0;
                    _state.Height = 0;
                    _state.Fps = 0;
                }
                _state.Message = message;
            }
            NotifyState();
        }

        /// <summary>更新录制状态、输出路径和状态消息。</summary>
        private void UpdateRecording(bool recording, string path, string message)
        {
            lock (_sync)
            {
                _state.IsRecording = recording;
                _state.FilePath = path;
                _state.RecordingStartedUtc = recording ? _recordingStartedUtc : null;
                _state.Message = message;
            }
            NotifyState();
        }

        /// <summary>向订阅者发送当前相机状态快照。</summary>
        private void NotifyState()
        {
            Action<CameraRuntimeState> handler = StateChanged;
            if (handler == null)
                return;
            try { handler(Snapshot()); } catch { }
        }

        /// <summary>在有界等待后清除重连或停止唤醒信号。</summary>
        private void WaitForWake(int milliseconds)
        {
            _wake.Wait(milliseconds);
            _wake.Reset();
        }

        /// <summary>停止后台线程并释放录制器持有的全部资源。</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _stopRequested = true;
                _recordingRequested = false;
            }
            _wake.Set();
            Thread worker = _worker;
            if (worker != null && worker.IsAlive && !worker.Join(8000))
                AppLog.Warning("相机 " + _profile.Id + " 未在超时内停止");
            CloseWriter();
            _wake.Dispose();
        }
    }
}
