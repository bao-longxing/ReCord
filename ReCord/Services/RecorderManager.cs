using System;
using System.Collections.Generic;
using System.IO;
using ReCord.Models;

namespace ReCord.Services
{
    public sealed class RecorderManager : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, CameraRecorder> _recorders =
            new Dictionary<int, CameraRecorder>();
        private bool _disposed;

        public event Action<CameraRuntimeState> CameraStateChanged;

        public ReCordConfiguration Configuration { get; private set; }

        /// <summary>使用指定配置初始化相机录制管理器。</summary>
        public RecorderManager(ReCordConfiguration configuration)
        {
            Configuration = configuration ?? ReCordConfiguration.CreateDefault();
            Configuration.Settings.Normalize();
        }

        /// <summary>为配置中的所有相机创建录制器并启动预览线程。</summary>
        public void StartAll()
        {
            lock (_sync)
            {
                foreach (CameraProfile profile in Configuration.Cameras)
                {
                    CameraRecorder recorder;
                    if (!_recorders.TryGetValue(profile.Id, out recorder))
                    {
                        recorder = CreateRecorder(profile);
                        _recorders[profile.Id] = recorder;
                    }
                    recorder.Start();
                }
            }
        }

        /// <summary>新增相机或替换现有相机配置并重启对应录制器。</summary>
        public void AddOrUpdate(CameraProfile profile)
        {
            CameraRecorder oldRecorder = null;
            CameraRecorder newRecorder;
            lock (_sync)
            {
                if (_recorders.TryGetValue(profile.Id, out oldRecorder))
                    _recorders.Remove(profile.Id);

                int index = Configuration.Cameras.FindIndex(c => c.Id == profile.Id);
                if (index >= 0)
                    Configuration.Cameras[index] = profile.Clone();
                else
                    Configuration.Cameras.Add(profile.Clone());

                newRecorder = CreateRecorder(profile);
                _recorders[profile.Id] = newRecorder;
            }
            if (oldRecorder != null)
                oldRecorder.Dispose();
            newRecorder.Start();
        }

        /// <summary>移除指定相机及其录制器资源。</summary>
        public void Remove(int cameraId)
        {
            CameraRecorder recorder = null;
            lock (_sync)
            {
                if (_recorders.TryGetValue(cameraId, out recorder))
                    _recorders.Remove(cameraId);
                Configuration.Cameras.RemoveAll(c => c.Id == cameraId);
            }
            if (recorder != null)
                recorder.Dispose();
        }

        /// <summary>规范化并将新的录制设置应用到所有相机。</summary>
        public void ApplySettings(RecorderSettings settings)
        {
            settings.Normalize();
            Configuration.Settings = settings;
            lock (_sync)
            {
                foreach (CameraRecorder recorder in _recorders.Values)
                    recorder.Settings = settings;
            }
        }

        /// <summary>确保相机录制器已启动并开始写入新的 MP4 文件。</summary>
        public void StartRecording(CameraProfile camera)
        {
            CameraRecorder recorder = GetRecorder(camera.Id);
            if (recorder == null)
            {
                recorder = CreateRecorder(camera);
                lock (_sync) _recorders[camera.Id] = recorder;
                recorder.Start();
            }
            recorder.StartRecording(BuildOutputPath(camera));
        }

        /// <summary>开始配置中所有相机的录制。</summary>
        public void StartAllRecording()
        {
            foreach (CameraProfile camera in Configuration.Cameras)
                StartRecording(camera);
        }

        /// <summary>停止指定相机的录制。</summary>
        public void StopRecording(int cameraId)
        {
            CameraRecorder recorder = GetRecorder(cameraId);
            if (recorder != null)
                recorder.StopRecording();
        }

        /// <summary>停止所有已创建录制器的录制。</summary>
        public void StopAllRecording()
        {
            lock (_sync)
            {
                foreach (CameraRecorder recorder in _recorders.Values)
                    recorder.StopRecording();
            }
        }

        /// <summary>尝试获取指定相机最新的一帧画面。</summary>
        public bool TryGetLatestFrame(int cameraId, out VideoFrame frame)
        {
            CameraRecorder recorder = GetRecorder(cameraId);
            if (recorder != null)
                return recorder.TryGetLatestFrame(out frame);
            frame = null;
            return false;
        }

        /// <summary>获取指定相机的当前运行状态快照。</summary>
        public CameraRuntimeState GetState(int cameraId)
        {
            CameraRecorder recorder = GetRecorder(cameraId);
            return recorder != null ? recorder.Snapshot() : null;
        }

        /// <summary>根据日期、前缀和相机名称生成新的录制文件路径。</summary>
        public string BuildOutputPath(CameraProfile camera)
        {
            RecorderSettings settings = Configuration.Settings;
            string folder = Path.Combine(settings.OutputDirectory,
                DateTime.Now.ToString("yyyy-MM-dd"));
            string name = PathHelper.SanitizeFilePart(settings.FileNamePrefix) + "_" +
                          PathHelper.SanitizeFilePart(camera.Name) + "_" +
                          DateTime.Now.ToString("HHmmss_fff") + ".mp4";
            return Path.Combine(folder, name);
        }

        /// <summary>线程安全地查找指定相机的录制器。</summary>
        private CameraRecorder GetRecorder(int cameraId)
        {
            lock (_sync)
            {
                CameraRecorder recorder;
                _recorders.TryGetValue(cameraId, out recorder);
                return recorder;
            }
        }

        /// <summary>创建录制器并转发其状态变化事件。</summary>
        private CameraRecorder CreateRecorder(CameraProfile profile)
        {
            CameraRecorder recorder = new CameraRecorder(profile, Configuration.Settings);
            recorder.StateChanged += state =>
            {
                Action<CameraRuntimeState> handler = CameraStateChanged;
                if (handler != null)
                {
                    try { handler(state); } catch { }
                }
            };
            return recorder;
        }

        /// <summary>停止并释放所有相机录制器。</summary>
        public void Dispose()
        {
            List<CameraRecorder> recorders;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                recorders = new List<CameraRecorder>(_recorders.Values);
                _recorders.Clear();
            }
            foreach (CameraRecorder recorder in recorders)
                recorder.Dispose();
        }
    }
}
