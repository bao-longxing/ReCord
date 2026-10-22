using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ReCord.Services
{
    public sealed unsafe class Mp4VideoWriter : IDisposable
    {
        private const int SwsBilinear = 2;
        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private AVStream* _stream;
        private SwsContext* _scaleContext;
        private AVFrame* _yuvFrame;
        private AVPacket* _packet;
        private bool _headerWritten;
        private bool _disposed;
        private long _frameIndex;
        private int _width;
        private int _height;
        private int _fps;

        public string FilePath { get; private set; }

        /// <summary>初始化 H.264/MPEG-4 编码器并创建 MP4 输出文件。</summary>
        public bool Open(string filePath, int width, int height, int fps, int bitrateKbps)
        {
            if (_disposed || width <= 0 || height <= 0)
                return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                FilePath = filePath;
                _width = width;
                _height = height;
                _fps = Math.Max(1, fps);

                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
                if (codec == null)
                    codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
                if (codec == null)
                    codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MPEG4);
                if (codec == null)
                {
                    AppLog.Error("FFmpeg 中没有可用的视频编码器");
                    return false;
                }

                AVFormatContext* formatContext = null;
                int result = ffmpeg.avformat_alloc_output_context2(
                    &formatContext, null, null, filePath);
                if (result < 0 || formatContext == null)
                {
                    AppLog.Error("无法创建 MP4 输出：" + GetAvError(result));
                    return false;
                }
                _formatContext = formatContext;

                _stream = ffmpeg.avformat_new_stream(_formatContext, null);
                if (_stream == null)
                    return false;

                _codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecContext == null)
                    return false;

                _codecContext->codec_id = codec->id;
                _codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                _codecContext->width = width;
                _codecContext->height = height;
                _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                _codecContext->time_base.num = 1;
                _codecContext->time_base.den = _fps;
                _codecContext->framerate.num = _fps;
                _codecContext->framerate.den = 1;
                _codecContext->gop_size = _fps * 2;
                _codecContext->max_b_frames = 0;
                _codecContext->bit_rate = Math.Max(500, bitrateKbps) * 1000L;

                if ((_formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                AVDictionary* options = null;
                if (string.Equals(codec->name != null ? Marshal.PtrToStringAnsi((IntPtr)codec->name) : null,
                    "libx264", StringComparison.OrdinalIgnoreCase))
                {
                    ffmpeg.av_dict_set(&options, "preset", "veryfast", 0);
                    ffmpeg.av_dict_set(&options, "tune", "zerolatency", 0);
                }
                result = ffmpeg.avcodec_open2(_codecContext, codec, &options);
                ffmpeg.av_dict_free(&options);
                if (result < 0)
                {
                    AppLog.Error("无法启动视频编码器：" + GetAvError(result));
                    Close();
                    return false;
                }

                result = ffmpeg.avcodec_parameters_from_context(
                    _stream->codecpar, _codecContext);
                if (result < 0)
                {
                    AppLog.Error("无法复制编码器参数：" + GetAvError(result));
                    Close();
                    return false;
                }

                _stream->time_base = _codecContext->time_base;
                _stream->codecpar->codec_tag = 0;
                if (ffmpeg.avio_open(&_formatContext->pb, filePath, ffmpeg.AVIO_FLAG_WRITE) < 0)
                {
                    AppLog.Error("无法创建录制文件：" + filePath);
                    Close();
                    return false;
                }

                result = ffmpeg.avformat_write_header(_formatContext, null);
                if (result < 0)
                {
                    AppLog.Error("无法写入 MP4 文件头：" + GetAvError(result));
                    Close();
                    return false;
                }
                _headerWritten = true;

                _yuvFrame = ffmpeg.av_frame_alloc();
                if (_yuvFrame == null)
                    return false;
                _yuvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                _yuvFrame->width = width;
                _yuvFrame->height = height;
                if (ffmpeg.av_frame_get_buffer(_yuvFrame, 32) < 0)
                    return false;

                _packet = ffmpeg.av_packet_alloc();
                if (_packet == null)
                    return false;

                _scaleContext = ffmpeg.sws_getContext(
                    width, height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    SwsBilinear, null, null, null);
                if (_scaleContext == null)
                    return false;

                AppLog.Record("开始写盘：" + filePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("初始化录制编码器失败：" + ex.Message);
                Close();
                return false;
            }
        }

        /// <summary>转换 BGR 视频帧、编码并写入当前 MP4 文件。</summary>
        public bool Write(VideoFrame frame, out string error)
        {
            error = null;
            if (_disposed || !_headerWritten || frame == null || frame.Width != _width ||
                frame.Height != _height)
            {
                error = "视频帧格式与编码器不匹配";
                return false;
            }

            try
            {
                if (ffmpeg.av_frame_make_writable(_yuvFrame) < 0)
                {
                    error = "无法锁定编码帧";
                    return false;
                }

                fixed (byte* source = frame.Data)
                {
                    byte*[] sourceData = new byte*[] { source, null, null, null };
                    int[] sourceLines = new int[] { frame.Stride, 0, 0, 0 };
                    ffmpeg.sws_scale(_scaleContext, sourceData, sourceLines, 0, _height,
                        _yuvFrame->data, _yuvFrame->linesize);
                }

                _yuvFrame->pts = _frameIndex++;
                int result = ffmpeg.avcodec_send_frame(_codecContext, _yuvFrame);
                if (result < 0)
                {
                    error = "提交编码帧失败：" + GetAvError(result);
                    return false;
                }

                while (true)
                {
                    result = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                        return true;
                    if (result < 0)
                    {
                        error = "获取编码数据失败：" + GetAvError(result);
                        return false;
                    }
                    if (!WritePacket()) return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>校正时间戳后将编码数据包写入输出容器。</summary>
        private bool WritePacket()
        {
            ffmpeg.av_packet_rescale_ts(_packet, _codecContext->time_base, _stream->time_base);
            _packet->stream_index = _stream->index;
            int result = ffmpeg.av_interleaved_write_frame(_formatContext, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (result < 0)
            {
                AppLog.Error("写入视频数据失败：" + GetAvError(result));
                return false;
            }
            return true;
        }

        /// <summary>提交结束信号并写出编码器缓存的数据包。</summary>
        private void DrainEncoder()
        {
            if (_codecContext == null || _packet == null)
                return;

            ffmpeg.avcodec_send_frame(_codecContext, null);
            while (true)
            {
                int result = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
                if (result < 0)
                    break;
                if (!WritePacket())
                    break;
            }
        }

        /// <summary>将 FFmpeg 错误码转换为可读文本。</summary>
        private static string GetAvError(int error)
        {
            byte* buffer = stackalloc byte[512];
            ffmpeg.av_strerror(error, buffer, 512);
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? ("FFmpeg error " + error);
        }

        /// <summary>结束 MP4 写入并释放编码、缩放和输出资源。</summary>
        public void Close()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (_headerWritten && _codecContext != null)
                {
                    DrainEncoder();
                    ffmpeg.av_write_trailer(_formatContext);
                }
            }
            catch { }
            _headerWritten = false;

            if (_packet != null)
            {
                AVPacket* packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }
            if (_yuvFrame != null)
            {
                AVFrame* frame = _yuvFrame;
                ffmpeg.av_frame_free(&frame);
                _yuvFrame = null;
            }
            if (_scaleContext != null)
            {
                ffmpeg.sws_freeContext(_scaleContext);
                _scaleContext = null;
            }
            if (_formatContext != null && _formatContext->pb != null)
                ffmpeg.avio_closep(&_formatContext->pb);
            if (_codecContext != null)
            {
                AVCodecContext* context = _codecContext;
                ffmpeg.avcodec_free_context(&context);
                _codecContext = null;
            }
            if (_formatContext != null)
            {
                ffmpeg.avformat_free_context(_formatContext);
                _formatContext = null;
            }
            if (!string.IsNullOrEmpty(FilePath))
                AppLog.Record("录制结束：" + FilePath);
        }

        /// <summary>释放视频写入器资源。</summary>
        public void Dispose()
        {
            Close();
        }
    }
}
