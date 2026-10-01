using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace hydrogen
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]

    public struct FrameTagHeader
    {
        public uint Magic;
        public uint Checksum;
        public long FrameIndex;
        public long PtsMs;
        public long DurationMs;
        public int Width;
        public int Height;
        public int PixelFormat;
        public int YStride;
        public int UVStride;
        public int YSize;
        public int UVSize;
        public int HeaderSize;
        public int TotalSize;
        public uint Version;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SessionMeta
    {
        public uint Magic;
        public uint Version;
        public int Width;
        public int Height;
        public int PixelFormat;
        public int CodecId;
        public long TotalDurationMs;
        public long TotalFrames;
        public double AvgFps;
        public long Bitrate;
        public long TimeBaseNum;
        public long TimeBaseDen;
        public int ColorSpace;
        public int ColorRange;
        public int Reserved1;
        public int Reserved2;
    }

    public sealed unsafe class FrameRawPacket : IDisposable
    {
        public IntPtr Ptr { get; private set; }
        public int TotalLength { get; private set; }
        public FrameTagHeader Header;
        private bool _disposed = false;
        public FrameRawPacket(IntPtr ptr, int totalLength, FrameTagHeader header)
        {
            Ptr = ptr;
            TotalLength = totalLength;
            Header = header;
        }
        public byte* PixelPtr => (byte*)Ptr + Header.HeaderSize;
        public byte* YPtr => PixelPtr;
        public byte* UVPtr => PixelPtr + Header.YSize;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Ptr);
                Ptr = IntPtr.Zero;
            }
            TotalLength = 0;
            GC.SuppressFinalize(this);
        }
        ~FrameRawPacket()
        {
            if (!_disposed && Ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Ptr);
                Ptr = IntPtr.Zero;
                _disposed = true;
            }
        }
    }

    internal unsafe class HWplayer : IDisposable, IFrameSource
    {
        private AVCodecContext_get_format? _getFormatCallback;
        private AVFormatContext* _pFormatCtx = null;
        private AVCodecContext* _pCodecCtx = null;
        private AVStream* _pVideoStream = null;
        private AVPacket* _pkt = null;
        private AVBufferRef* _hwDeviceCtx = null;
        private int _videoStreamIndex = -1;
        private volatile bool isrunning = false;

        private readonly Queue<FrameRawPacket> _frameQueue = new Queue<FrameRawPacket>();
        private readonly object _queueLock = new object();
        private const int MaxQueueSize = 12;
        private const int WaterLevel = 9;
        private const int OuterWaitLevel = WaterLevel;

        public AutoResetEvent? FrameEvent;
        private AVRational _sar;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint timeEndPeriod(uint uMilliseconds);
        private volatile bool _timerBoosted = false;

        private volatile bool _decodeRunning = false;
        private Thread? _decodeThread = null;

        private double _frameDuration = 1.0 / 30.0;
        private int _frameCount = 0;

        private SessionMeta _sessionMeta;
        private bool _disposed = false;

        public SessionMeta GetSessionMeta() => _sessionMeta;
        public AVRational GetSampleAspectRatio() => _sar;

        public HWplayer()
        {
            _getFormatCallback = GetHardwareFormat;
            _sar.num = 0;
            _sar.den = 1;
        }

        private static unsafe AVPixelFormat GetHardwareFormat(AVCodecContext* ctx, AVPixelFormat* fmtList)
        {
            AVPixelFormat fmt;
            for (fmt = *fmtList; fmt != AVPixelFormat.AV_PIX_FMT_NONE; fmt = *++fmtList)
            {
                if (fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    Log.WriteLog("get_format: 选择 D3D11 硬件格式");
                    return fmt;
                }
            }
            Log.WriteLog("get_format: 未找到 D3D11 格式（硬解不支持）");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private bool QueueWaitForSlot()
        {
            lock (_queueLock)
            {
                while (_decodeRunning && _frameQueue.Count >= OuterWaitLevel)
                {
                    Monitor.Wait(_queueLock, 100);
                }
                return _decodeRunning;
            }
        }

        public bool TryDequeueFrame(out FrameRawPacket? item)
        {
            lock (_queueLock)
            {
                if (_frameQueue.Count == 0)
                {
                    item = null;
                    return false;
                }
                item = _frameQueue.Dequeue();
                Monitor.PulseAll(_queueLock);
                return true;
            }
        }

        public int FrameQueueCount
        {
            get { lock (_queueLock) return _frameQueue.Count; }
        }

        private void ClearQueue()
        {
            lock (_queueLock)
            {
                long before = Process.GetCurrentProcess().PrivateMemorySize64;
                int cleared = 0;
                while (_frameQueue.Count > 0)
                {
                    var item = _frameQueue.Dequeue();
                    item?.Dispose();
                    cleared++;
                }
                long after = Process.GetCurrentProcess().PrivateMemorySize64;
                Log.WriteLog($"ClearQueue: 释放帧={cleared} 内存:{before / 1048576}MB → {after / 1048576}MB");
                Monitor.PulseAll(_queueLock);
            }
        }

        public int HWplayer_play()
        {
            if (!isrunning) { Log.WriteLog("播放器未启动，无法播放"); return -1; }
            if (_decodeRunning) { Log.WriteLog("解码线程已在运行"); return 0; }
            if (!_timerBoosted)
            {
                timeBeginPeriod(1);
                _timerBoosted = true;
            }
            _decodeRunning = true;
            _decodeThread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "HWDecodeThread"
            };
            _decodeThread.Start();
            Log.WriteLog("解码线程已启动（系统时钟精度 1ms）");
            return 0;
        }

        public void HWplayer_pause()
        {
            _decodeRunning = false;
            lock (_queueLock) { Monitor.PulseAll(_queueLock); }
            Log.WriteLog("解码线程已请求停止");
        }

        private unsafe void DecodeLoop()
        {
            Log.WriteLog("=== 解码线程进入 ===");
            _frameCount = 0;
            AVRational timeBase = _pVideoStream->time_base;
            long lastLogTimeMs = 0;
            AVFrame* hwFrame = ffmpeg.av_frame_alloc();
            if (hwFrame == null)
            {
                Log.WriteLog("[致命] hwFrame = av_frame_alloc 失败，解码线程退出");
                _decodeRunning = false;
                return;
            }
            try
            {
                while (_decodeRunning)
                {
                    if (!QueueWaitForSlot()) break;
                    if (!_decodeRunning) break;
                    int ret = ffmpeg.av_read_frame(_pFormatCtx, _pkt);
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        Log.WriteLog("当前视频解码行结束，开始循环播放解码");
                        ffmpeg.av_packet_unref(_pkt);
                        ffmpeg.av_frame_unref(hwFrame);
                        ffmpeg.avcodec_flush_buffers(_pCodecCtx);
                        ffmpeg.avformat_flush(_pFormatCtx);
                        ClearQueue();
                        Log.WriteLog("[循环] 帧队列已清空，准备 seek 到视频流起始位置");
                        _frameCount = 0;
                        long seekTarget = _pVideoStream->start_time;
                        if (seekTarget == ffmpeg.AV_NOPTS_VALUE)
                            seekTarget = 0;
                        ret = ffmpeg.av_seek_frame(
                            _pFormatCtx,
                            _videoStreamIndex,
                            seekTarget,
                            ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (ret < 0)
                        {
                            Log.WriteLog($"av_seek_frame 失败: {FFmpegError(ret)}，退出解码");
                            break;
                        }
                        continue;
                    }
                    if (ret < 0)
                    {
                        ffmpeg.av_packet_unref(_pkt);
                        Log.WriteLog($"av_read_frame 失败: {FFmpegError(ret)}");
                        break;
                    }
                    if (_pkt->stream_index != _videoStreamIndex)
                    {
                        ffmpeg.av_packet_unref(_pkt);
                        continue;
                    }
                    ret = ffmpeg.avcodec_send_packet(_pCodecCtx, _pkt);
                    ffmpeg.av_packet_unref(_pkt);
                    if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        Log.WriteLog($"avcodec_send_packet 失败: {FFmpegError(ret)}");
                        continue;
                    }
                    while (_decodeRunning)
                    {
                        ret = ffmpeg.avcodec_receive_frame(_pCodecCtx, hwFrame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                        if (ret < 0)
                        {
                            Log.WriteLog($"avcodec_receive_frame 失败: {FFmpegError(ret)}");
                            ffmpeg.av_frame_unref(hwFrame);
                            break;
                        }
                        if (hwFrame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                        {
                            Log.WriteLog($"[警告] 帧格式非 D3D11：{hwFrame->format}");
                            ffmpeg.av_frame_unref(hwFrame);
                            continue;
                        }
                        long hwPts = hwFrame->pts;
                        double ptsSec = hwPts == ffmpeg.AV_NOPTS_VALUE
                            ? (_frameCount * _frameDuration)
                            : hwPts * ffmpeg.av_q2d(timeBase);
                        AVFrame* nv12Frame = ffmpeg.av_frame_alloc();
                        try
                        {
                            nv12Frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                            nv12Frame->width = _pCodecCtx->width;
                            nv12Frame->height = _pCodecCtx->height;
                            ret = ffmpeg.av_frame_get_buffer(nv12Frame, 0);
                            if (ret < 0)
                            {
                                Log.WriteLog($"av_frame_get_buffer 失败: {FFmpegError(ret)}");
                                ffmpeg.av_frame_unref(hwFrame);
                                continue;
                            }
                            ret = ffmpeg.av_hwframe_transfer_data(nv12Frame, hwFrame, 0);
                            if (ret < 0)
                            {
                                Log.WriteLog($"av_hwframe_transfer_data 失败: {FFmpegError(ret)}");
                                ffmpeg.av_frame_unref(hwFrame);
                                continue;
                            }
                            ffmpeg.av_frame_unref(hwFrame);
                            if (nv12Frame->data[0] == null || nv12Frame->data[1] == null ||
                                nv12Frame->linesize[0] <= 0 || nv12Frame->linesize[1] <= 0)
                            {
                                Log.WriteLog($"[警告] nv12Frame 数据非法，跳过本帧");
                                continue;
                            }
                            int w = nv12Frame->width;
                            int h = nv12Frame->height;
                            int yStride = nv12Frame->linesize[0];
                            int uvStride = nv12Frame->linesize[1];
                            int uvRows = (h + 1) / 2;
                            int ySize = yStride * h;
                            int uvSize = uvStride * uvRows;
                            int headerSize = Marshal.SizeOf<FrameTagHeader>();
                            int totalSize = headerSize + ySize + uvSize;
                            IntPtr rawPtr;
                            try
                            {
                                rawPtr = Marshal.AllocHGlobal(totalSize);
                            }
                            catch (OutOfMemoryException)
                            {
                                Log.WriteLog($"[警告] AllocHGlobal 抛异常，丢弃本帧");
                                continue;
                            }
                            if (rawPtr == IntPtr.Zero)
                            {
                                Log.WriteLog($"[警告] AllocHGlobal 返回空指针，丢弃本帧");
                                continue;
                            }
                            byte* dst = (byte*)rawPtr;
                            var header = new FrameTagHeader
                            {
                                Magic = 0x52444859,
                                Checksum = 0,
                                FrameIndex = _frameCount,
                                PtsMs = (long)(ptsSec * 1000.0),
                                DurationMs = (long)(_frameDuration * 1000.0),
                                Width = w,
                                Height = h,
                                PixelFormat = 0,
                                YStride = yStride,
                                UVStride = uvStride,
                                YSize = ySize,
                                UVSize = uvSize,
                                HeaderSize = headerSize,
                                TotalSize = totalSize,
                                Version = 1,
                                Reserved1 = 0,
                            };
                            *(FrameTagHeader*)dst = header;
                            byte* srcY = nv12Frame->data[0];
                            byte* dstY = dst + headerSize;
                            if (yStride == w)
                                Buffer.MemoryCopy(srcY, dstY, ySize, ySize);
                            else
                                for (int y = 0; y < h; y++)
                                    Buffer.MemoryCopy(srcY + y * yStride, dstY + y * yStride, yStride, yStride);
                            byte* srcUV = nv12Frame->data[1];
                            byte* dstUV = dst + headerSize + ySize;
                            if (uvStride == w)
                                Buffer.MemoryCopy(srcUV, dstUV, uvSize, uvSize);
                            else
                                for (int y = 0; y < uvRows; y++)
                                    Buffer.MemoryCopy(srcUV + y * uvStride, dstUV + y * uvStride, uvStride, uvStride);
                            var packet = new FrameRawPacket(rawPtr, totalSize, header);
                            bool enqueued = false;
                            lock (_queueLock)
                            {
                                if (_decodeRunning && _frameQueue.Count < MaxQueueSize)
                                {
                                    _frameQueue.Enqueue(packet);
                                    enqueued = true;
                                }
                            }
                            if (!enqueued)
                            {
                                packet.Dispose();
                                Log.WriteLog($"[丢弃] 队列已达硬上限({MaxQueueSize})或线程停止，跳过本帧 pts={ptsSec:F3}s");
                                continue;
                            }
                            _frameCount++;
                            FrameEvent?.Set();
                            long nowMs = (long)(ffmpeg.av_gettime_relative() / 1000);
                            if (nowMs - lastLogTimeMs >= 5000)
                            {
                                lastLogTimeMs = nowMs;
                                Log.WriteLog($"[解码] 第 {_frameCount} 帧 pts={ptsSec:F3}s {w}x{h} 队列={FrameQueueCount}");
                            }
                        }
                        finally
                        {
                            if (nv12Frame != null)
                            {
                                AVFrame* tmpFrame = nv12Frame;
                                ffmpeg.av_frame_free(&tmpFrame);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (hwFrame != null)
                {
                    AVFrame* tmp = hwFrame;
                    ffmpeg.av_frame_free(&tmp);
                    Log.WriteLog("[清理] hwFrame 已在 finally 中释放");
                }
                _decodeRunning = false;
                Log.WriteLog("=== 解码线程退出 ===");
            }
        }

        public unsafe int HWplayer_stop()
        {
            if (!isrunning)
            {
                Log.WriteLog("【提示】播放器已经停止，无需重复销毁");
                return 0;
            }
            Log.WriteLog("【播放器停止】开始销毁FFmpeg全部实例");
            _decodeRunning = false;
            lock (_queueLock) { Monitor.PulseAll(_queueLock); }
            if (_decodeThread != null && _decodeThread.IsAlive)
            {
                if (!_decodeThread.Join(2000))
                {
                    Log.WriteLog("[警告] 解码线程 2 秒内未退出，强制 Pulse + 再等 1 秒");
                    lock (_queueLock) { Monitor.PulseAll(_queueLock); }
                    _decodeThread.Join(1000);
                }
            }
            _decodeThread = null;
            ClearQueue();
            Log.WriteLog("帧队列残留帧已全部释放");
            if (_pkt != null) { AVPacket* tmp = _pkt; ffmpeg.av_packet_free(&tmp); _pkt = null; Log.WriteLog("AVPacket 释放完成"); }
            if (_pCodecCtx != null) { AVCodecContext* tmp = _pCodecCtx; ffmpeg.avcodec_free_context(&tmp); _pCodecCtx = null; Log.WriteLog("AVCodecContext 释放完成"); }
            if (_hwDeviceCtx != null) { AVBufferRef* tmp = _hwDeviceCtx; ffmpeg.av_buffer_unref(&tmp); _hwDeviceCtx = null; Log.WriteLog("硬解设备上下文释放完成"); }
            if (_pFormatCtx != null) { AVFormatContext* tmp = _pFormatCtx; ffmpeg.avformat_close_input(&tmp); _pFormatCtx = null; Log.WriteLog("AVFormatContext 关闭"); }
            _pVideoStream = null;
            _getFormatCallback = null;
            _videoStreamIndex = -1;
            isrunning = false;
            if (_timerBoosted) { timeEndPeriod(1); _timerBoosted = false; Log.WriteLog("系统时钟精度已恢复"); }
            Log.WriteLog("【播放器停止】所有FFmpeg实例销毁完毕");
            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { HWplayer_stop(); } catch (Exception ex) { Log.WriteLog($"Dispose 异常: {ex.Message}"); }
            GC.SuppressFinalize(this);
        }

        ~HWplayer()
        {
            if (_timerBoosted)
            {
                timeEndPeriod(1);
                _timerBoosted = false;
            }
        }

        public unsafe int HWplayer_start(string inGpu, string videoPath)
        {
            if (isrunning) { Log.WriteLog("已启动开始返回"); return -1; }
            isrunning = true;
            Log.WriteLog($"实例创建，文件路径：{videoPath}, gpu:{inGpu}");
            try
            {
                int ret;
                AVFormatContext* pFormatLocal = null;
                Log.WriteLog("[1] 分配 AVFormatContext");
                pFormatLocal = ffmpeg.avformat_alloc_context();
                if (pFormatLocal == null) { Log.WriteLog("avformat_alloc_context 失败"); goto FAIL; }
                Log.WriteLog("[2] avformat_open_input 打开视频文件");
                ret = ffmpeg.avformat_open_input(&pFormatLocal, videoPath, null, null);
                if (ret < 0) { Log.WriteLog($"avformat_open_input 失败: {FFmpegError(ret)}"); goto FAIL; }
                Log.WriteLog("[3] avformat_find_stream_info");
                ret = ffmpeg.avformat_find_stream_info(pFormatLocal, null);
                if (ret < 0) { Log.WriteLog($"avformat_find_stream_info 失败: {FFmpegError(ret)}"); goto FAIL; }
                Log.WriteLog("[4] 遍历查找视频流");
                _videoStreamIndex = -1;
                for (int i = 0; i < pFormatLocal->nb_streams; i++)
                {
                    if (pFormatLocal->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _videoStreamIndex = i;
                        break;
                    }
                }
                if (_videoStreamIndex < 0) { Log.WriteLog("未找到视频流"); goto FAIL; }
                _pVideoStream = pFormatLocal->streams[_videoStreamIndex];
                Log.WriteLog($"视频流索引: {_videoStreamIndex}");
                AVRational fps = _pVideoStream->avg_frame_rate;
                if (fps.num > 0 && fps.den > 0)
                    _frameDuration = (double)fps.den / fps.num;
                else
                    _frameDuration = 1.0 / 30.0;
                Log.WriteLog($"帧时长: {_frameDuration:F4}s (fps={fps.num}/{fps.den})");
                _sar = _pVideoStream->codecpar->sample_aspect_ratio;
                double sarVal = (_sar.num > 0 && _sar.den > 0) ? (double)_sar.num / _sar.den : 1.0;
                Log.WriteLog($"[SAR] sample_aspect_ratio = {_sar.num}:{_sar.den} (值={sarVal:F4})");
                Log.WriteLog("[5] avcodec_find_decoder");
                AVCodecID codecId = _pVideoStream->codecpar->codec_id;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(codecId);
                if (codec == null) { Log.WriteLog($"avcodec_find_decoder 失败: {codecId}"); goto FAIL; }
                Log.WriteLog($"解码器: {ffmpeg.avcodec_get_name(codecId)}");
                Log.WriteLog("[6] avcodec_alloc_context3");
                _pCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_pCodecCtx == null) { Log.WriteLog("avcodec_alloc_context3 失败"); goto FAIL; }
                ret = ffmpeg.avcodec_parameters_to_context(_pCodecCtx, _pVideoStream->codecpar);
                if (ret < 0) { Log.WriteLog($"avcodec_parameters_to_context 失败: {FFmpegError(ret)}"); goto FAIL; }
                Log.WriteLog($"[7] av_hwdevice_ctx_create (D3D11VA, GPU: {inGpu})");
                string? deviceName = string.IsNullOrEmpty(inGpu) ? null : inGpu;
                AVBufferRef* pHwDevLocal = null;
                ret = ffmpeg.av_hwdevice_ctx_create(
                    &pHwDevLocal,
                    AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                    deviceName,
                    null, 0);
                if (ret < 0) { Log.WriteLog($"av_hwdevice_ctx_create 失败: {FFmpegError(ret)}"); goto FAIL; }
                _hwDeviceCtx = pHwDevLocal;
                Log.WriteLog("[8] 绑定 hw_device_ctx 到 _pCodecCtx");
                _pCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                Log.WriteLog("[9] 注册 get_format 回调");
                _pCodecCtx->get_format = _getFormatCallback;
                Log.WriteLog("[10] avcodec_open2 打开解码器");
                ret = ffmpeg.avcodec_open2(_pCodecCtx, codec, null);
                if (ret < 0) { Log.WriteLog($"avcodec_open2 失败: {FFmpegError(ret)}"); goto FAIL; }
                _pkt = ffmpeg.av_packet_alloc();
                if (_pkt == null) { Log.WriteLog("av_packet_alloc 失败"); goto FAIL; }
                _pFormatCtx = pFormatLocal;
                var codecpar = _pVideoStream->codecpar;
                _sessionMeta = new SessionMeta
                {
                    Magic = 0x4D455348,
                    Version = 1,
                    Width = _pCodecCtx->width,
                    Height = _pCodecCtx->height,
                    PixelFormat = 0,
                    CodecId = (int)codecId,
                    TotalDurationMs = _pFormatCtx->duration > 0
                        ? _pFormatCtx->duration / (ffmpeg.AV_TIME_BASE / 1000)
                        : 0,
                    TotalFrames = (_frameDuration > 0 && _pFormatCtx->duration > 0)
                        ? (long)(_pFormatCtx->duration / (double)ffmpeg.AV_TIME_BASE / _frameDuration)
                        : 0,
                    AvgFps = fps.num > 0 && fps.den > 0 ? (double)fps.num / fps.den : 30.0,
                    Bitrate = _pFormatCtx->bit_rate,
                    TimeBaseNum = _pVideoStream->time_base.num,
                    TimeBaseDen = _pVideoStream->time_base.den,
                    ColorSpace = (int)codecpar->color_space,
                    ColorRange = (int)codecpar->color_range,
                    Reserved1 = 0,
                    Reserved2 = 0,
                };
                Log.WriteLog($"[OK] 硬解解码器打开成功，分辨率: {_pCodecCtx->width}x{_pCodecCtx->height}");
                Log.WriteLog($"[会话] 时长={_sessionMeta.TotalDurationMs}ms 帧数≈{_sessionMeta.TotalFrames} fps={_sessionMeta.AvgFps:F2} 码率={_sessionMeta.Bitrate}");
                Log.WriteLog($"[会话] ColorSpace={_sessionMeta.ColorSpace} ColorRange={_sessionMeta.ColorRange}");
                return 0;
            FAIL:
                Log.WriteLog("初始化失败（硬解不支持或参数错误），直接返回，不回退软解");
                HWplayer_stop();
                return -1;
            }
            catch (Exception ex)
            {
                Log.WriteLog($"异常: {ex.Message}\n{ex.StackTrace}");
                HWplayer_stop();
                return -1;
            }
        }

        private string FFmpegError(int err)
        {
            byte[] buffer = new byte[1024];
            fixed (byte* pBuf = buffer)
            {
                ffmpeg.av_strerror(err, pBuf, (ulong)buffer.Length);
            }
            return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }
    }
}
