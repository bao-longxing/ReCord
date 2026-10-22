using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ReCord.Services
{
    public sealed unsafe class FfmpegVideoDecoder : IDisposable
    {
        private const int SwsBilinear = 2;
        private static readonly object NetworkLock = new object();
        private static bool _networkInitialized;

        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private SwsContext* _scaleContext;
        private AVFrame* _decodedFrame;
        private AVPacket* _packet;
        private byte* _bgrBuffer;
        private int _videoStreamIndex = -1;
        private AVPixelFormat _sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private readonly byte*[] _sourceData = new byte*[8];
        private readonly int[] _sourceLines = new int[8];
        private readonly byte*[] _destinationData = new byte*[8];
        private readonly int[] _destinationLines = new int[8];
        private bool _disposed;
        private long _sequence;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameRate { get; private set; }
        public bool IsOpened { get; private set; }
        public bool IsFileSource { get; private set; }

        /// <summary>打开本地文件或网络视频源并初始化视频解码器。</summary>
        public bool Open(string source)
        {
            if (_disposed || string.IsNullOrWhiteSpace(source))
                return false;

            IsFileSource = !IsNetworkSource(source);
            bool isFile = !IsNetworkSource(source) && File.Exists(source);
            if (!IsNetworkSource(source) && !isFile)
            {
                AppLog.Error("输入源不存在：" + source);
                return false;
            }

            EnsureNetworkInitialized();
            try
            {
                ReleaseResources();
                _formatContext = ffmpeg.avformat_alloc_context();
                if (_formatContext == null)
                    return false;

                AVDictionary* options = null;
                if (IsNetworkSource(source))
                {
                    ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                    ffmpeg.av_dict_set(&options, "stimeout", "5000000", 0);
                    ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
                    ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
                }

                AVFormatContext* opened = _formatContext;
                int result = ffmpeg.avformat_open_input(&opened, source, null, &options);
                _formatContext = opened;
                ffmpeg.av_dict_free(&options);
                if (result < 0)
                {
                    AppLog.Error("无法打开视频源 " + Redact(source) + "：" + GetAvError(result));
                    ReleaseResources();
                    return false;
                }

                result = ffmpeg.avformat_find_stream_info(_formatContext, null);
                if (result < 0)
                {
                    AppLog.Error("无法读取视频流信息：" + GetAvError(result));
                    ReleaseResources();
                    return false;
                }

                _videoStreamIndex = -1;
                for (uint i = 0; i < _formatContext->nb_streams; i++)
                {
                    if (_formatContext->streams[i]->codecpar->codec_type ==
                        AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _videoStreamIndex = (int)i;
                        break;
                    }
                }
                if (_videoStreamIndex < 0)
                {
                    AppLog.Error("输入源中没有视频流：" + Redact(source));
                    ReleaseResources();
                    return false;
                }

                AVStream* stream = _formatContext->streams[(uint)_videoStreamIndex];
                AVCodecParameters* parameters = stream->codecpar;
                Width = parameters->width;
                Height = parameters->height;
                FrameRate = ResolveFrameRate(stream->avg_frame_rate, stream->r_frame_rate);
                if (Width <= 0 || Height <= 0)
                {
                    AppLog.Error("视频分辨率无效：" + Width + "x" + Height);
                    ReleaseResources();
                    return false;
                }

                AVCodec* codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
                if (codec == null)
                {
                    AppLog.Error("找不到视频解码器：" + parameters->codec_id);
                    ReleaseResources();
                    return false;
                }

                _codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecContext == null)
                    return false;
                ffmpeg.avcodec_parameters_to_context(_codecContext, parameters);
                _codecContext->thread_count = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
                _codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                result = ffmpeg.avcodec_open2(_codecContext, codec, null);
                if (result < 0)
                {
                    AppLog.Error("无法启动视频解码器：" + GetAvError(result));
                    ReleaseResources();
                    return false;
                }

                _decodedFrame = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();
                int bufferSize = Width * Height * 3;
                _bgrBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_decodedFrame == null || _packet == null || _bgrBuffer == null)
                    throw new OutOfMemoryException("无法分配视频解码缓冲区");

                _destinationData[0] = _bgrBuffer;
                _destinationLines[0] = Width * 3;
                _sequence = 0;
                IsOpened = true;
                AppLog.Info("视频源已连接：" + Redact(source) + "，" + Width + "x" + Height +
                            " @ " + FrameRate + "fps");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("打开视频源异常：" + ex.Message);
                ReleaseResources();
                return false;
            }
        }

        /// <summary>解码下一帧并转换为可用于预览和录制的 BGR 数据。</summary>
        public bool TryReadFrame(out VideoFrame frame)
        {
            frame = null;
            if (!IsOpened || _formatContext == null || _codecContext == null)
                return false;

            try
            {
                while (!_disposed)
                {
                    int result = ffmpeg.av_read_frame(_formatContext, _packet);
                    if (result == ffmpeg.AVERROR_EOF)
                        return false;
                    if (result < 0)
                    {
                        AppLog.Warning("读取视频帧失败：" + GetAvError(result));
                        IsOpened = false;
                        return false;
                    }

                    if (_packet->stream_index != _videoStreamIndex)
                    {
                        ffmpeg.av_packet_unref(_packet);
                        continue;
                    }

                    result = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    ffmpeg.av_packet_unref(_packet);
                    if (result < 0 && result != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        AppLog.Warning("解码器拒绝视频包：" + GetAvError(result));
                        continue;
                    }

                    result = ffmpeg.avcodec_receive_frame(_codecContext, _decodedFrame);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        continue;
                    if (result == ffmpeg.AVERROR_EOF)
                        return false;
                    if (result < 0)
                    {
                        AppLog.Warning("解码视频帧失败：" + GetAvError(result));
                        continue;
                    }

                    AVPixelFormat pixelFormat = (AVPixelFormat)_decodedFrame->format;
                    if (_scaleContext == null || pixelFormat != _sourcePixelFormat)
                    {
                        _scaleContext = ffmpeg.sws_getCachedContext(
                            _scaleContext,
                            Width, Height, pixelFormat,
                            Width, Height, AVPixelFormat.AV_PIX_FMT_BGR24,
                            SwsBilinear, null, null, null);
                        _sourcePixelFormat = pixelFormat;
                    }
                    if (_scaleContext == null)
                    {
                        AppLog.Error("无法创建视频颜色转换上下文");
                        return false;
                    }

                    for (int i = 0; i < 8; i++)
                    {
                        _sourceData[i] = _decodedFrame->data[(uint)i];
                        _sourceLines[i] = _decodedFrame->linesize[(uint)i];
                    }

                    int scaledRows = ffmpeg.sws_scale(
                        _scaleContext,
                        _sourceData, _sourceLines, 0, Height,
                        _destinationData, _destinationLines);
                    if (scaledRows <= 0)
                        continue;

                    int stride = Width * 3;
                    byte[] pixels = new byte[stride * Height];
                    Marshal.Copy((IntPtr)_bgrBuffer, pixels, 0, pixels.Length);
                    frame = new VideoFrame
                    {
                        Data = pixels,
                        Width = Width,
                        Height = Height,
                        Stride = stride,
                        Sequence = ++_sequence,
                        TimestampUtc = DateTime.UtcNow
                    };
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("读取视频源异常：" + ex.Message);
                IsOpened = false;
            }
            return false;
        }

        /// <summary>从平均帧率或实时帧率中解析有效帧率。</summary>
        private static int ResolveFrameRate(AVRational average, AVRational real)
        {
            AVRational rate = average.num > 0 && average.den > 0 ? average : real;
            if (rate.num <= 0 || rate.den <= 0)
                return 25;
            double fps = (double)rate.num / rate.den;
            if (fps < 1 || fps > 120)
                return 25;
            return Math.Max(1, (int)Math.Round(fps));
        }

        /// <summary>判断输入地址是否使用支持的网络流协议。</summary>
        private static bool IsNetworkSource(string source)
        {
            return source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>确保 FFmpeg 网络模块仅初始化一次。</summary>
        private static void EnsureNetworkInitialized()
        {
            if (_networkInitialized)
                return;
            lock (NetworkLock)
            {
                if (_networkInitialized)
                    return;
                try { ffmpeg.avformat_network_init(); }
                catch (Exception ex) { AppLog.Warning("FFmpeg 网络初始化失败：" + ex.Message); }
                _networkInitialized = true;
            }
        }

        /// <summary>隐藏视频源地址中的用户名和密码后再用于日志。</summary>
        private static string Redact(string source)
        {
            try
            {
                Uri uri = new Uri(source);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                    return source.Replace(uri.UserInfo + "@", "***@");
            }
            catch { }
            return source;
        }

        /// <summary>将 FFmpeg 错误码转换为可读文本。</summary>
        private static string GetAvError(int error)
        {
            byte* buffer = stackalloc byte[1024];
            ffmpeg.av_strerror(error, buffer, 1024);
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? ("FFmpeg error " + error);
        }

        /// <summary>释放解码器、格式上下文、帧缓冲和缩放资源。</summary>
        private void ReleaseResources()
        {
            IsOpened = false;
            _sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

            if (_scaleContext != null)
            {
                ffmpeg.sws_freeContext(_scaleContext);
                _scaleContext = null;
            }
            if (_bgrBuffer != null)
            {
                ffmpeg.av_free(_bgrBuffer);
                _bgrBuffer = null;
            }
            if (_decodedFrame != null)
            {
                AVFrame* frame = _decodedFrame;
                ffmpeg.av_frame_free(&frame);
                _decodedFrame = null;
            }
            if (_packet != null)
            {
                AVPacket* packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }
            if (_codecContext != null)
            {
                AVCodecContext* context = _codecContext;
                ffmpeg.avcodec_free_context(&context);
                _codecContext = null;
            }
            if (_formatContext != null)
            {
                AVFormatContext* context = _formatContext;
                ffmpeg.avformat_close_input(&context);
                _formatContext = null;
            }
            _videoStreamIndex = -1;
            Width = 0;
            Height = 0;
            FrameRate = 25;
        }

        /// <summary>停止解码并释放视频解码器资源。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseResources();
        }
    }
}
