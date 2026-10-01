using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace hydrogen
{
    internal unsafe class SWplayer : IDisposable, IFrameSource
    {
        // ============ FFmpeg 核心对象 ============
        private AVFormatContext* _pFormatCtx = null;
        private AVCodecContext* _pCodecCtx = null;
        private AVStream* _pVideoStream = null;
        private AVPacket* _pkt = null;
        private SwsContext* _swsCtx = null;
        private int _videoStreamIndex = -1;
        private volatile bool isrunning = false;

        // ============ 帧队列（和硬解完全一致：最多12，水位9） ============
        private readonly Queue<FrameRawPacket> _frameQueue = new Queue<FrameRawPacket>();
        private readonly object _queueLock = new object();
        private const int MaxQueueSize = 12;
        private const int WaterLevel = 9;
        private const int OuterWaitLevel = WaterLevel;

        // ★ 渲染线程唤醒信号
        public AutoResetEvent? FrameEvent;
        // ★ SAR（像素宽高比）
        private AVRational _sar;

        // ============ winmm 时钟 ============
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint timeEndPeriod(uint uMilliseconds);
        private volatile bool _timerBoosted = false;

        // ============ 解码线程 / 状态 ============
        private volatile bool _decodeRunning = false;
        private Thread? _decodeThread = null;

        // ============ PTS 时序 ============
        private double _frameDuration = 1.0 / 30.0;
        private int _frameCount = 0;

        // ============ 会话元数据 ============
        private SessionMeta _sessionMeta;
        // ============ IDisposable ============
        private bool _disposed = false;

        // ============ 对外接口 ============
        public SessionMeta GetSessionMeta() => _sessionMeta;
        public AVRational GetSampleAspectRatio() => _sar;

        // ============ IFrameSource 接口实现 ============
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

        public SWplayer()
        {
            _sar.num = 0;
            _sar.den = 1;
        }

        // ================================================================
        //  队列操作（原样复刻HWplayer）
        // ================================================================
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

        // ================================================================
        //  播放控制（和硬解同名）
        // ================================================================
        public int SWplayer_play()
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
                Name = "SWDecodeThread"
            };
            _decodeThread.Start();
            Log.WriteLog("软解解码线程已启动（系统时钟精度 1ms）");
            return 0;
        }

        public void SWplayer_pause()
        {
            _decodeRunning = false;
            lock (_queueLock) { Monitor.PulseAll(_queueLock); }
            Log.WriteLog("软解解码线程已请求停止");
        }

        // ================================================================
        //  FFmpeg错误辅助
        // ================================================================
        // ================================================================
        //  FFmpeg错误辅助【修复AV_ERROR_MAX_STRING_SIZE找不到】
        // ================================================================
        private string GetFfmpegError(int err)
        {
            const int AV_ERROR_MAX_STRING_SIZE = 64;
            byte* buf = stackalloc byte[AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(err, buf, AV_ERROR_MAX_STRING_SIZE);
            return Encoding.UTF8.GetString((byte*)buf, AV_ERROR_MAX_STRING_SIZE).TrimEnd('\0');
        }


        // ================================================================
        //  软解解码主循环：SwsContext 统一转 NV12，输出格式与HW硬解完全一致
        // ================================================================
        private unsafe void DecodeLoop()
        {
            Log.WriteLog("=== 软解解码线程进入 ===");
            _frameCount = 0;
            AVRational timeBase = _pVideoStream->time_base;
            long lastLogTimeMs = 0;
            AVFrame* srcFrame = ffmpeg.av_frame_alloc();
            AVFrame* dstNv12Frame = ffmpeg.av_frame_alloc();
            if (srcFrame == null || dstNv12Frame == null)
            {
                Log.WriteLog("[致命] av_frame_alloc 失败，解码线程退出");
                ffmpeg.av_frame_free(&srcFrame);
                ffmpeg.av_frame_free(&dstNv12Frame);
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
                    // ================== EOF 循环播放分支 ==================
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        Log.WriteLog("当前视频软解到达末尾，开始循环播放");
                        ffmpeg.av_packet_unref(_pkt);
                        ffmpeg.av_frame_unref(srcFrame);
                        ffmpeg.av_frame_unref(dstNv12Frame);
                        if (_swsCtx != null)
                        {
                            ffmpeg.sws_freeContext(_swsCtx);
                            _swsCtx = null;
                        }
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
                            Log.WriteLog($"av_seek_frame 失败: {GetFfmpegError(ret)}，退出解码");
                            break;
                        }
                        continue;
                    }
                    // ================== EOF 分支结束 ==================
                    if (ret < 0)
                    {
                        ffmpeg.av_packet_unref(_pkt);
                        Log.WriteLog($"av_read_frame 失败: {GetFfmpegError(ret)}");
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
                        Log.WriteLog($"avcodec_send_packet 失败: {GetFfmpegError(ret)}");
                        continue;
                    }
                    while (_decodeRunning)
                    {
                        ret = ffmpeg.avcodec_receive_frame(_pCodecCtx, srcFrame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                        if (ret < 0)
                        {
                            Log.WriteLog($"avcodec_receive_frame 失败: {GetFfmpegError(ret)}");
                            ffmpeg.av_frame_unref(srcFrame);
                            break;
                        }
                        long pts = srcFrame->pts;
                        double ptsSec = pts == ffmpeg.AV_NOPTS_VALUE
                            ? (_frameCount * _frameDuration)
                            : pts * ffmpeg.av_q2d(timeBase);
                        int w = srcFrame->width;
                        int h = srcFrame->height;
                        // 初始化/重建sws上下文，分辨率或者格式变化时自动重建
                        if (_swsCtx == null || srcFrame->width != dstNv12Frame->width || srcFrame->height != dstNv12Frame->height)
                        {
                            if (_swsCtx != null)
                            {
                                ffmpeg.sws_freeContext(_swsCtx);
                                _swsCtx = null;
                            }
                            _swsCtx = ffmpeg.sws_getContext(w, h, (AVPixelFormat)srcFrame->format,
                            w, h, AVPixelFormat.AV_PIX_FMT_NV12,
                            2, null, null, null);
                            if (_swsCtx == null)
                            {
                                Log.WriteLog("sws_getContext 创建失败，丢弃当前帧");
                                ffmpeg.av_frame_unref(srcFrame);
                                continue;
                            }
                            dstNv12Frame->width = w;
                            dstNv12Frame->height = h;
                            dstNv12Frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                            ret = ffmpeg.av_frame_get_buffer(dstNv12Frame, 0);
                            if (ret < 0)
                            {
                                Log.WriteLog($"av_frame_get_buffer NV12帧分配失败 {GetFfmpegError(ret)}");
                                ffmpeg.av_frame_unref(srcFrame);
                                continue;
                            }
                        }
                        // 色彩空间转换 → NV12
                        ret = ffmpeg.sws_scale(_swsCtx, srcFrame->data, srcFrame->linesize, 0, h,
                                               dstNv12Frame->data, dstNv12Frame->linesize);
                        if (ret < 0)
                        {
                            Log.WriteLog($"sws_scale 转换失败 {GetFfmpegError(ret)}");
                            ffmpeg.av_frame_unref(srcFrame);
                            continue;
                        }
                        int yStride = dstNv12Frame->linesize[0];
                        int uvStride = dstNv12Frame->linesize[1];
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
                            ffmpeg.av_frame_unref(srcFrame);
                            continue;
                        }
                        if (rawPtr == IntPtr.Zero)
                        {
                            Log.WriteLog($"[警告] AllocHGlobal 返回空指针，丢弃本帧");
                            ffmpeg.av_frame_unref(srcFrame);
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
                        // 拷贝Y平面
                        byte* srcY = dstNv12Frame->data[0];
                        byte* dstY = dst + headerSize;
                        if (yStride == w)
                            Buffer.MemoryCopy(srcY, dstY, ySize, ySize);
                        else
                            for (int y = 0; y < h; y++)
                                Buffer.MemoryCopy(srcY + y * yStride, dstY + y * yStride, yStride, yStride);
                        // 拷贝UV平面
                        byte* srcUV = dstNv12Frame->data[1];
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
                            ffmpeg.av_frame_unref(srcFrame);
                            continue;
                        }
                        _frameCount++;
                        FrameEvent?.Set();
                        long nowMs = (long)(ffmpeg.av_gettime_relative() / 1000);
                        if (nowMs - lastLogTimeMs >= 30000)
                        {
                            lastLogTimeMs = nowMs;
                            Log.WriteLog($"[软解] 第 {_frameCount} 帧 pts={ptsSec:F3}s {w}x{h} 队列={FrameQueueCount}");
                        }
                        ffmpeg.av_frame_unref(srcFrame);
                    }
                }
            }
            finally
            {
                if (_swsCtx != null)
                {
                    ffmpeg.sws_freeContext(_swsCtx);
                    _swsCtx = null;
                    Log.WriteLog("[清理] SwsContext 释放");
                }
                if (srcFrame != null)
                {
                    AVFrame* tmp = srcFrame;
                    ffmpeg.av_frame_free(&tmp);
                    Log.WriteLog("[清理] srcFrame 已在finally释放");
                }
                if (dstNv12Frame != null)
                {
                    AVFrame* tmp = dstNv12Frame;
                    ffmpeg.av_frame_free(&tmp);
                    Log.WriteLog("[清理] dstNv12Frame 已在finally释放");
                }
                _decodeRunning = false;
                Log.WriteLog("=== 软解解码线程退出 ===");
            }
        }

        // ================================================================
        //  资源释放
        // ================================================================
        public unsafe int SWplayer_stop()
        {
            if (!isrunning)
            {
                Log.WriteLog("【提示】软解播放器已经停止，无需重复销毁");
                return 0;
            }
            Log.WriteLog("【软解播放器停止】开始销毁FFmpeg全部实例");
            _decodeRunning = false;
            lock (_queueLock) { Monitor.PulseAll(_queueLock); }
            if (_decodeThread != null && _decodeThread.IsAlive)
            {
                if (!_decodeThread.Join(2000))
                {
                    Log.WriteLog("[警告] 软解解码线程 2 秒内未退出，强制 Pulse + 再等 1 秒");
                    lock (_queueLock) { Monitor.PulseAll(_queueLock); }
                    _decodeThread.Join(1000);
                }
            }
            _decodeThread = null;
            ClearQueue();
            Log.WriteLog("帧队列残留帧已全部释放");
            if (_swsCtx != null)
            {
                ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = null;
            }
            if (_pkt != null) { AVPacket* tmp = _pkt; ffmpeg.av_packet_free(&tmp); _pkt = null; Log.WriteLog("AVPacket 释放完成"); }
            if (_pCodecCtx != null) { AVCodecContext* tmp = _pCodecCtx; ffmpeg.avcodec_free_context(&tmp); _pCodecCtx = null; Log.WriteLog("AVCodecContext 释放完成"); }
            if (_pFormatCtx != null) { AVFormatContext* tmp = _pFormatCtx; ffmpeg.avformat_close_input(&tmp); _pFormatCtx = null; Log.WriteLog("AVFormatContext 关闭"); }
            _pVideoStream = null;
            _videoStreamIndex = -1;
            isrunning = false;
            if (_timerBoosted) { timeEndPeriod(1); _timerBoosted = false; Log.WriteLog("系统时钟精度已恢复"); }
            Log.WriteLog("【软解播放器停止】所有FFmpeg实例销毁完毕");
            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SWplayer_stop(); } catch (Exception ex) { Log.WriteLog($"Dispose 异常: {ex.Message}"); }
            GC.SuppressFinalize(this);
        }

        ~SWplayer()
        {
            if (_timerBoosted)
            {
                timeEndPeriod(1);
                _timerBoosted = false;
            }
        }

        public unsafe int SWplayer_start(string unusedGpu, string videoPath)
        {
            if (isrunning) { Log.WriteLog("软解实例已启动，直接返回"); return -1; }
            isrunning = true;
            Log.WriteLog($"软解实例创建，文件路径：{videoPath}");
            try
            {
                int ret;
                AVFormatContext* pFormatLocal = null;
                Log.WriteLog("[1] 分配 AVFormatContext");
                pFormatLocal = ffmpeg.avformat_alloc_context();
                if (pFormatLocal == null) { Log.WriteLog("avformat_alloc_context 失败"); goto FAIL; }
                Log.WriteLog("[2] avformat_open_input 打开视频文件");
                ret = ffmpeg.avformat_open_input(&pFormatLocal, videoPath, null, null);
                if (ret < 0) { Log.WriteLog($"avformat_open_input 失败: {GetFfmpegError(ret)}"); goto FAIL; }
                Log.WriteLog("[3] avformat_find_stream_info");
                ret = ffmpeg.avformat_find_stream_info(pFormatLocal, null);
                if (ret < 0) { Log.WriteLog($"avformat_find_stream_info 失败: {GetFfmpegError(ret)}"); goto FAIL; }
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
                // ★ 读取 SAR（像素宽高比）
                _sar = _pVideoStream->codecpar->sample_aspect_ratio;
                double sarVal = (_sar.num > 0 && _sar.den > 0) ? (double)_sar.num / _sar.den : 1.0;
                Log.WriteLog($"[SAR] sample_aspect_ratio = {_sar.num}:{_sar.den} (值={sarVal:F4})");
                Log.WriteLog("[5] avcodec_find_decoder");
                AVCodecID codecId = _pVideoStream->codecpar->codec_id;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(codecId);
                if (codec == null) { Log.WriteLog($"avcodec_find_decoder 失败: {codecId}"); goto FAIL; }
                Log.WriteLog($"软解解码器: {ffmpeg.avcodec_get_name(codecId)}");
                Log.WriteLog("[6] avcodec_alloc_context3");
                _pCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_pCodecCtx == null) { Log.WriteLog("avcodec_alloc_context3 失败"); goto FAIL; }
                ret = ffmpeg.avcodec_parameters_to_context(_pCodecCtx, _pVideoStream->codecpar);
                if (ret < 0) { Log.WriteLog($"avcodec_parameters_to_context 失败: {GetFfmpegError(ret)}"); goto FAIL; }
                Log.WriteLog("[10] avcodec_open2 打开软解码器");
                ret = ffmpeg.avcodec_open2(_pCodecCtx, codec, null);
                if (ret < 0) { Log.WriteLog($"avcodec_open2 失败: {GetFfmpegError(ret)}"); goto FAIL; }
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
                Log.WriteLog($"[OK] 软解解码器打开成功，分辨率: {_pCodecCtx->width}x{_pCodecCtx->height}");
                Log.WriteLog($"[会话] 时长={_sessionMeta.TotalDurationMs}ms 帧数≈{_sessionMeta.TotalFrames} fps={_sessionMeta.AvgFps:F2} 码率={_sessionMeta.Bitrate}");
                Log.WriteLog($"[会话] ColorSpace={_sessionMeta.ColorSpace} ColorRange={_sessionMeta.ColorRange}");
                return 0;
            FAIL:
                Log.WriteLog("软解初始化失败，直接返回");
                SWplayer_stop();
                return -1;
            }
            catch (Exception ex)
            {
                Log.WriteLog($"异常: {ex.Message}\n{ex.StackTrace}");
                SWplayer_stop();
                return -1;
            }
        }
    }
}
