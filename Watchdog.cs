using System;
using System.Diagnostics;
using System.Threading;
using Vortice.DXGI;

namespace hydrogen
{
    /// <summary>
    /// 解码器看门狗：
    ///   - 监控进程内存，超过阈值重启解码器
    ///   - 监控本进程显存（IDXGIAdapter3.QueryVideoMemoryInfo），超过阈值重启解码器
    /// </summary>
    internal class Watchdog : IDisposable
    {
        //==================== 【用户可修改 全局静态配置】UI直接读写这两个 ====================
        public static long GlobalMemThresholdMB { get; set; } = 750;
        public static long GlobalVramThresholdMB { get; set; } = 750;

        // ============ 依赖对象 ============
        private readonly IFrameSource? _player;
        private readonly string _gpu;
        private readonly string _videoPath;

        // ============ 配置参数（实例，不再使用传入的mb阈值，读取上面静态全局） ============
        private readonly int _checkIntervalMs;
        private readonly int _cooldownMs;
        private readonly bool _enableVramMonitor;

        // ============ 线程 / 状态 ============
        private Thread? _watchThread;
        private volatile bool _running;
        private bool _disposed;

        // ============ Vortice DXGI ============
        private IDXGIAdapter3? _adapter3;
        private bool _vramQueryAvailable;

        // ============ 统计 ============
        private int _restartCount;
        private DateTime _lastRestartTime = DateTime.MinValue;
        private long _lastProcessMemMB;
        private long _lastProcessVramMB;

        public event Action? OnRestartStarting;
        public event Action<int>? OnRestartCompleted;

        public int RestartCount => _restartCount;
        public long LastProcessMemMB => _lastProcessMemMB;
        public long LastProcessVramMB => _lastProcessVramMB;
        public bool VramMonitorAvailable => _vramQueryAvailable;

        public Watchdog(
            IFrameSource player,
            string gpu,
            string videoPath,
            long memThresholdMB = 750,     // 保留签名兼容，现在不再使用，优先读取静态全局
            long vramThresholdMB = 750,    // 保留签名兼容，现在不再使用，优先读取静态全局
            int checkIntervalMs = 2000,
            int cooldownMs = 10000,
            bool enableVramMonitor = true)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrEmpty(videoPath)) throw new ArgumentException("videoPath 不能为空");

            _player = player;
            _gpu = gpu ?? "";
            _videoPath = videoPath;

            _checkIntervalMs = checkIntervalMs < 500 ? 500 : checkIntervalMs;
            _cooldownMs = cooldownMs < 1000 ? 1000 : cooldownMs;
            _enableVramMonitor = enableVramMonitor;

            if (_enableVramMonitor)
            {
                InitVramQuery();
            }

            Log.WriteLog($"[看门狗] 创建：全局内存阈值={GlobalMemThresholdMB}MB 全局显存阈值={GlobalVramThresholdMB}MB 显存监控可用={_vramQueryAvailable}");
        }

        // ================================================================
        //  DXGI 显存查询初始化（Vortice 版）
        // ================================================================
        private void InitVramQuery()
        {
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint i = 0; ; i++)
                {
                    IDXGIAdapter1? adapter1 = null;
                    try
                    {
                        if (!factory.EnumAdapters1(i, out adapter1).Success)
                            break;
                    }
                    catch
                    {
                        break;
                    }
                    if (adapter1 == null) break;

                    var desc = adapter1.Description1;
                    bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
                    Log.WriteLog($"[看门狗] DXGI 适配器[{i}]: {desc.Description}, 专用显存={desc.DedicatedVideoMemory / 1048576}MB, 软件={isSoftware}");

                    if (!isSoftware && _adapter3 == null)
                    {
                        _adapter3 = adapter1.QueryInterface<IDXGIAdapter3>();
                        Log.WriteLog($"[看门狗] 选用适配器: {desc.Description}");
                    }
                    adapter1.Dispose();
                }

                if (_adapter3 != null)
                {
                    _vramQueryAvailable = true;
                    Log.WriteLog("[看门狗] DXGI 显存查询初始化成功（Vortice）");
                }
                else
                {
                    Log.WriteLog("[看门狗] 未找到硬件适配器，显存监控禁用");
                    _vramQueryAvailable = false;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLog($"[看门狗] DXGI 初始化失败: {ex.Message}\n{ex.StackTrace}");
                _vramQueryAvailable = false;
            }
        }

        // ================================================================
        //  查询本进程显存（Vortice 版）
        // ================================================================
        private long QueryThisProcessVramBytes()
        {
            if (!_vramQueryAvailable || _adapter3 == null) return -1;
            try
            {
                var info = _adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                return (long)info.CurrentUsage;
            }
            catch (Exception ex)
            {
                Log.WriteLog($"[看门狗] QueryVideoMemoryInfo 异常: {ex.Message}");
                return -1;
            }
        }

        // ================================================================
        //  启动 / 停止
        // ================================================================
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Watchdog));
            if (_running) return;
            _running = true;
            _watchThread = new Thread(WatchLoop) { IsBackground = true, Name = "DecoderWatchdogThread" };
            _watchThread.Start();
            Log.WriteLog("[看门狗] 监控线程已启动");
        }

        public void Stop()
        {
            _running = false;
            _watchThread?.Join(2000);
            _watchThread = null;
        }

        // ================================================================
        //  监控循环
        // ================================================================
        private void WatchLoop()
        {
            Log.WriteLog("[看门狗] 监控线程进入");
            while (_running)
            {
                try
                {
                    int slept = 0;
                    while (slept < _checkIntervalMs && _running)
                    {
                        int chunk = Math.Min(500, _checkIntervalMs - slept);
                        Thread.Sleep(chunk);
                        slept += chunk;
                    }
                    if (!_running) break;

                    if (_player == null)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    // ✅ 每次检测读取【最新全局阈值】，UI改完立刻生效，不用重启看门狗
                    long memThresholdBytes = GlobalMemThresholdMB * 1024 * 1024;
                    long vramThresholdBytes = GlobalVramThresholdMB * 1024 * 1024;

                    using var proc = Process.GetCurrentProcess();
                    long memBytes = proc.PrivateMemorySize64;
                    long vramBytes = QueryThisProcessVramBytes();

                    _lastProcessMemMB = memBytes / 1048576;
                    _lastProcessVramMB = vramBytes > 0 ? vramBytes / 1048576 : -1;

                    bool memOver = memBytes > memThresholdBytes;
                    bool vramOver = vramBytes > 0 && vramBytes > vramThresholdBytes;

                    bool coolDownOk = (DateTime.Now - _lastRestartTime).TotalMilliseconds > _cooldownMs;

                    Log.WriteLog($"[看门狗] 内存={_lastProcessMemMB}MB 显存={_lastProcessVramMB}MB");
                    if ((memOver || vramOver) && coolDownOk)
                    {
                        _restartCount++;
                        _lastRestartTime = DateTime.Now;
                        string reason = memOver ? $"内存({_lastProcessMemMB}MB)" : $"显存({_lastProcessVramMB}MB)";
                        Log.WriteLog($"[看门狗] ★ {reason}超限，准备重启");
                        RestartDecoder(reason);
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLog($"[看门狗] 监控循环异常: {ex.Message}");
                }
            }
            Log.WriteLog("[看门狗] 监控线程退出");
        }

        // ================================================================
        //  重启解码器
        // ================================================================
        private void RestartDecoder(string reason)
        {
            try { OnRestartStarting?.Invoke(); } catch { }
            var sw = Stopwatch.StartNew();
            try
            {
                if (_player is HWplayer hwP)
                {
                    hwP.HWplayer_stop();
                    hwP.Dispose();
                }
                else if (_player is SWplayer swP)
                {
                    swP.SWplayer_stop();
                    swP.Dispose();
                }

                Log.WriteLog($"[看门狗] [1/3] 旧播放器已销毁 ({sw.ElapsedMilliseconds}ms)");

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(300);

                Log.WriteLog($"[看门狗] [2/3] GC 完成 ({sw.ElapsedMilliseconds}ms)");
                Log.WriteLog($"[看门狗] [3/3] 已释放旧播放器，等待外部重建 (原因: {reason})");

                try { OnRestartCompleted?.Invoke(_restartCount); } catch { }
                sw.Stop();
                Log.WriteLog($"[看门狗] 重启完成 耗时={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log.WriteLog($"[看门狗] 重启异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ================================================================
        //  Dispose
        // ================================================================
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _adapter3?.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }

        ~Watchdog()
        {
            _running = false;
        }
    }
}
