using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
namespace hydrogen
{
    public interface IFrameSource
    {
        bool TryDequeueFrame(out FrameRawPacket? packet);
    }
    internal sealed unsafe class DComp_render : IDisposable
    {
        private readonly IFrameSource _player;
        private readonly int _videoWidth;
        private readonly int _videoHeight;
        private readonly int _screenWidth;
        private readonly int _screenHeight;
        private double _sar;
        private string? _gpuAdapterName;
        private uint _gpuAdapterIndex;
        public enum SarMode
        {
            Ignore,
            FillScreen,
            Letterbox,
        }
        private SarMode _sarMode = SarMode.FillScreen;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _workerW = IntPtr.Zero;
        private ID3D11Device _device = null!;
        private ID3D11DeviceContext _context = null!;
        private IDCompositionDevice _compDevice = null!;
        private IDCompositionTarget _compTarget = null!;
        private IDCompositionVisual _compVisual = null!;
        private IDXGISwapChain1 _swapChain = null!;
        private ID3D11RenderTargetView _backBufferRTV = null!;
        private ID3D11Texture2D _backBuffer = null!;
        private ID3D11Texture2D _nv12Texture = null!;
        private ID3D11ShaderResourceView1 _nv12SRV_Y = null!;
        private ID3D11ShaderResourceView1 _nv12SRV_UV = null!;
        private ID3D11VertexShader _vs = null!;
        private ID3D11PixelShader _ps = null!;
        private ID3D11Buffer _vertexBuffer = null!;
        private ID3D11InputLayout _inputLayout = null!;
        private ID3D11SamplerState _sampler = null!;
        private ID3D11RasterizerState _rasterizerState = null!;
        private Thread? _renderThread;
        private volatile bool _running = false;
        private bool _disposed = false;
        private readonly AutoResetEvent _frameEvent = new AutoResetEvent(false);
        private long _firstPtsMs = -1;
        private long _renderStartMs = 0;
        private long _lastPtsMs = -1;
        private long _renderedCount = 0;
        private long _lastLogTimeMs = 0;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        // ========= 修改构造函数，增加 renderCardToken 参数 =========
        public DComp_render(IFrameSource player, int videoWidth, int videoHeight, double sar, AutoResetEvent frameEvent, string renderCardToken)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
            _frameEvent = frameEvent ?? throw new ArgumentNullException(nameof(frameEvent));
            _sar = (sar > 0 && Math.Abs(sar - 1.0) > 0.0001) ? sar : 1.0;
            _gpuAdapterName = null;
            _gpuAdapterIndex = 0;

            // 解析文本标记 GPU0 -> index 0；GPU1 -> index1
            if (renderCardToken.Equals("GPU0", StringComparison.OrdinalIgnoreCase))
            {
                SetGpu(0, "GPU0");
            }
            else if (renderCardToken.Equals("GPU1", StringComparison.OrdinalIgnoreCase))
            {
                SetGpu(1, "GPU1");
            }
            else
            {
                Log.WriteLog($"[DComp] 未知渲染卡标记 {renderCardToken}，回退默认GPU0");
                SetGpu(0, "GPU0");
            }

            var scr = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
            _screenWidth = scr.Bounds.Width;
            _screenHeight = scr.Bounds.Height;
            Log.WriteLog($"[DComp] 创建：视频={_videoWidth}x{_videoHeight} 屏幕={_screenWidth}x{_screenHeight} SAR={_sar:F4} 模式={_sarMode}");
        }

        public void SetSar(double sar)
        {
            if (sar > 0 && Math.Abs(sar - 1.0) > 0.0001)
            {
                _sar = sar;
                Log.WriteLog($"[DComp] SAR 已设置为 {sar:F4}");
            }
            else
            {
                _sar = 1.0;
                Log.WriteLog("[DComp] SAR 已重置为 1.0（忽略 SAR）");
            }
            if (_vertexBuffer != null)
            {
                _vertexBuffer.Dispose();
                CreateFullscreenQuad();
            }
        }
        public void SetSarMode(SarMode mode)
        {
            _sarMode = mode;
            Log.WriteLog($"[DComp] SAR 模式已设置为 {mode}");
            if (_vertexBuffer != null)
            {
                _vertexBuffer.Dispose();
                CreateFullscreenQuad();
            }
        }
        public void SetGpu(uint adapterIndex, string? adapterName = null)
        {
            _gpuAdapterIndex = adapterIndex;
            _gpuAdapterName = adapterName;
            Log.WriteLog($"[DComp] GPU 已设置为 index={adapterIndex} name={adapterName ?? "(默认)"}");
        }
        public int render_start()
        {
            if (_running) return -1;
            try
            {
                if (!FindWorkerW()) { Log.WriteLog("[DComp] WorkerW 查找失败"); return -1; }
                CreateChildWindow();
                Initialize();
                _running = true;
                _renderStartMs = Stopwatch.GetTimestamp() / 10000;
                _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "DCompRenderThread" };
                _renderThread.Start();
                Log.WriteLog("[DComp] 渲染线程已启动");
                return 0;
            }
            catch (Exception ex)
            {
                Log.WriteLog($"[DComp] 启动失败: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }
        public int render_stop()
        {
            if (!_running) return 0;
            _running = false;
            _frameEvent.Set();
            if (_renderThread != null && _renderThread.IsAlive) _renderThread.Join(2000);
            _renderThread = null;
            Log.WriteLog("[DComp] 渲染线程已停止");
            return 0;
        }
        private bool FindWorkerW()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return false;
            IntPtr workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (workerW == IntPtr.Zero)
            {
                SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
                Thread.Sleep(500);
                workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            }
            if (workerW == IntPtr.Zero) return false;
            _workerW = workerW;
            Log.WriteLog($"[DComp] WorkerW = 0x{_workerW:X}");
            return true;
        }
        private void CreateChildWindow()
        {
            _hwnd = CreateWindowEx(
                0x00000020, "STATIC", "HydrogenWallpaper",
                0x80000000 | 0x10000000,
                0, 0, _screenWidth, _screenHeight,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new Exception("CreateWindowEx 失败");
            SetParent(_hwnd, _workerW);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _screenWidth, _screenHeight, 0x0040 | 0x0010);
            ShowWindow(_hwnd, 5);
            Log.WriteLog($"[DComp] 子窗口 = 0x{_hwnd:X} 尺寸={_screenWidth}x{_screenHeight}");
        }
        private void Initialize()
        {
            IDXGIAdapter? selectedAdapter = null;
            IDXGIFactory2? factory = null;
            try
            {
                factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
                IDXGIAdapter1? adapter = null;
                factory.EnumAdapters1(_gpuAdapterIndex, out adapter);
                if (adapter == null)
                {
                    Log.WriteLog($"[DComp] ★ 找不到 GPU index={_gpuAdapterIndex}，使用默认");
                }
                else
                {
                    var desc = adapter.Description1;
                    Log.WriteLog($"[DComp] ★ 使用 GPU：{desc.Description} (index={_gpuAdapterIndex})");
                    selectedAdapter = adapter;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLog($"[DComp] GPU 枚举异常: {ex.Message}");
            }
            var deviceFlags = DeviceCreationFlags.BgraSupport;
            if (selectedAdapter != null)
            {
                D3D11.D3D11CreateDevice(
                    selectedAdapter,
                    DriverType.Unknown,
                    deviceFlags,
                    null,
                    out _device,
                    out _context).CheckError();
            }
            else
            {
                D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    deviceFlags,
                    null,
                    out _device,
                    out _context).CheckError();
            }
            Log.WriteLog($"[DComp] D3D11 设备创建成功 FeatureLevel={_device.FeatureLevel}");
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter2 = dxgiDevice.GetAdapter();
            using var factory2 = adapter2.GetParent<IDXGIFactory2>();
            var swapDesc = new SwapChainDescription1
            {
                Width = (uint)_screenWidth,
                Height = (uint)_screenHeight,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Scaling = Scaling.Stretch,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                Stereo = false,
                Flags = SwapChainFlags.None
            };
            _swapChain = factory2.CreateSwapChainForComposition(_device, swapDesc);
            _compDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _compDevice.CreateTargetForHwnd(_hwnd, true, out _compTarget);
            _compVisual = _compDevice.CreateVisual();
            _compVisual.SetContent(_swapChain);
            _compTarget.SetRoot(_compVisual);
            _compDevice.Commit();
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _backBufferRTV = _device.CreateRenderTargetView(_backBuffer);
            CreateNv12Texture();
            CompileShaders();
            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            });
            _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                FrontCounterClockwise = false,
                DepthBias = 0,
                DepthBiasClamp = 0,
                SlopeScaledDepthBias = 0,
                DepthClipEnable = true,
                ScissorEnable = false,
                MultisampleEnable = false,
                AntialiasedLineEnable = false
            });
            CreateFullscreenQuad();
            Log.WriteLog("[DComp] 初始化完成");
        }
        private void CreateNv12Texture()
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)_videoWidth,
                Height = (uint)_videoHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None,
            };
            _nv12Texture = _device.CreateTexture2D(desc);
            var device3 = _device.QueryInterface<ID3D11Device3>();
            var srvYDesc = new ShaderResourceViewDescription1
            {
                Format = Format.R8_UNorm,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView1 { MipLevels = 1, MostDetailedMip = 0, PlaneSlice = 0 },
            };
            _nv12SRV_Y = device3.CreateShaderResourceView1(_nv12Texture, srvYDesc);
            var srvUVDesc = new ShaderResourceViewDescription1
            {
                Format = Format.R8G8_UNorm,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView1 { MipLevels = 1, MostDetailedMip = 0, PlaneSlice = 1 },
            };
            _nv12SRV_UV = device3.CreateShaderResourceView1(_nv12Texture, srvUVDesc);
            Log.WriteLog("[DComp] NV12 纹理 + Y/UV SRV 创建成功");
        }
        private void CompileShaders()
        {
            string vsSource = @"
struct VSInput  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOutput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOutput main(VSInput input) {
    VSOutput o;
    o.pos = float4(input.pos, 0.0f, 1.0f);
    o.uv = input.uv;
    return o;
}";
            string psSource = @"
Texture2D<float>  texY  : register(t0);
Texture2D<float2> texUV : register(t1);
SamplerState      samp  : register(s0);
struct PSInput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(PSInput input) : SV_TARGET {
    float y = texY.Sample(samp, input.uv).r;
    float2 uv = texUV.Sample(samp, input.uv).rg;
    y  = (y - 16.0f / 255.0f) * (255.0f / 219.0f);
    uv = (uv - 128.0f / 255.0f) * (255.0f / 224.0f);
    float r = y + 1.402f * uv.y;
    float g = y - 0.344f * uv.x - 0.714f * uv.y;
    float b = y + 1.772f * uv.x;
    return float4(saturate(r), saturate(g), saturate(b), 1.0f);
}";
            var vsBlob = Compiler.Compile(vsSource, "main", "vs_shader", "vs_5_0", ShaderFlags.None, EffectFlags.None);
            _vs = _device.CreateVertexShader(vsBlob.Span);
            var psBlob = Compiler.Compile(psSource, "main", "ps_shader", "ps_5_0", ShaderFlags.None, EffectFlags.None);
            _ps = _device.CreatePixelShader(psBlob.Span);
            Log.WriteLog("[DComp] Shader 编译成功");
        }
        private unsafe void CreateFullscreenQuad()
        {
            float uMin = 0.0f, uMax = 1.0f;
            float vMin = 0.0f, vMax = 1.0f;
            double videoAspect = (double)_videoWidth / _videoHeight;
            double displayAspect = videoAspect * _sar;
            double screenAspect = (double)_screenWidth / _screenHeight;
            if (_sarMode == SarMode.FillScreen)
            {
                if (screenAspect < displayAspect)
                {
                    double uScale = screenAspect / displayAspect;
                    double uOffset = (1.0 - uScale) / 2.0;
                    uMin = (float)uOffset;
                    uMax = (float)(1.0 - uOffset);
                }
                else if (screenAspect > displayAspect)
                {
                    double vScale = displayAspect / screenAspect;
                    double vOffset = (1.0 - vScale) / 2.0;
                    vMin = (float)vOffset;
                    vMax = (float)(1.0 - vOffset);
                }
            }
            else if (_sarMode == SarMode.Letterbox)
            {
                Log.WriteLog("[DComp] 注意：Letterbox 模式暂未实现，回退到 FillScreen");
            }
            Log.WriteLog($"[DComp] SAR 校正：视频显示比例={displayAspect:F4} 屏幕比例={screenAspect:F4} UV X=[{uMin:F4},{uMax:F4}] UV Y=[{vMin:F4},{vMax:F4}]");
            float[] vertices = new float[]
            {
                -1.0f, -1.0f, uMin, vMax,
                 1.0f, -1.0f, uMax, vMax,
                -1.0f,  1.0f, uMin, vMin,
                -1.0f,  1.0f, uMin, vMin,
                 1.0f, -1.0f, uMax, vMax,
                 1.0f,  1.0f, uMax, vMin,
            };
            var bufferDesc = new BufferDescription
            {
                ByteWidth = (uint)(vertices.Length * sizeof(float)),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
                StructureByteStride = 0,
            };
            fixed (float* pVertices = vertices)
            {
                _vertexBuffer = _device.CreateBuffer(bufferDesc, (IntPtr)pVertices);
            }
            if (_inputLayout == null)
            {
                var inputElements = new InputElementDescription[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                };
                string vsSource = @"
struct VSInput  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOutput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOutput main(VSInput input) {
    VSOutput o;
    o.pos = float4(input.pos, 0.0f, 1.0f);
    o.uv = input.uv;
    return o;
}";
                var vsBlob = Compiler.Compile(vsSource, "main", "vs_shader", "vs_5_0", ShaderFlags.None, EffectFlags.None);
                _inputLayout = _device.CreateInputLayout(inputElements, vsBlob.Span);
            }
            Log.WriteLog("[DComp] 全屏四边形创建成功（SAR 已应用）");
        }
        private unsafe void RenderLoop()
        {
            Log.WriteLog("[DComp] === 渲染线程进入 ===");
            while (_running)
            {
                _frameEvent.WaitOne(100);
                if (!_running) break;
                try
                {
                    while (_player.TryDequeueFrame(out var packet) && packet != null)
                    {
                        try
                        {
                            FrameTagHeader h = packet.Header;
                            WaitForPts(h.PtsMs);
                            UploadNv12(packet);
                            RenderFrame();
                            _swapChain.Present(1, PresentFlags.None);
                            _renderedCount++;
                            long nowMs = Stopwatch.GetTimestamp() / 10000;
                            if (nowMs - _lastLogTimeMs >= 5000)
                            {
                                _lastLogTimeMs = nowMs;
                                Log.WriteLog($"[DComp] 已渲染 {_renderedCount} 帧 PTS={h.PtsMs}ms");
                            }
                        }
                        finally
                        {
                            packet.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLog($"[DComp] 渲染异常: {ex.Message}\n{ex.StackTrace}");
                    Thread.Sleep(100);
                }
            }
            Log.WriteLog("[DComp] === 渲染线程退出 ===");
        }
        private void WaitForPts(long ptsMs)
        {
            if (_firstPtsMs < 0)
            {
                _firstPtsMs = ptsMs;
                _renderStartMs = Stopwatch.GetTimestamp() / 10000;
                _lastPtsMs = ptsMs;
                return;
            }
            if (ptsMs < _lastPtsMs - 100)
            {
                _firstPtsMs = ptsMs;
                _renderStartMs = Stopwatch.GetTimestamp() / 10000;
                _lastPtsMs = ptsMs;
                return;
            }
            _lastPtsMs = ptsMs;
            long nowMs = Stopwatch.GetTimestamp() / 10000;
            long targetMs = _renderStartMs + (ptsMs - _firstPtsMs);
            long waitMs = targetMs - nowMs;
            if (waitMs > 1 && waitMs < 500) Thread.Sleep((int)waitMs);
            else if (waitMs >= 500)
            {
                _firstPtsMs = ptsMs;
                _renderStartMs = Stopwatch.GetTimestamp() / 10000;
            }
        }
        private unsafe void UploadNv12(FrameRawPacket packet)
        {
            if (_context == null || _nv12Texture == null) return;
            var map = _context.Map(_nv12Texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte* dst = (byte*)map.DataPointer;
                int dstYStride = (int)map.RowPitch;
                int dstUVOffset = dstYStride * _videoHeight;
                byte* srcY = packet.YPtr;
                for (int y = 0; y < _videoHeight; y++)
                {
                    Buffer.MemoryCopy(
                        srcY + y * packet.Header.YStride,
                        dst + y * dstYStride,
                        dstYStride,
                        _videoWidth);
                }
                byte* srcUV = packet.UVPtr;
                int uvRows = (_videoHeight + 1) / 2;
                for (int y = 0; y < uvRows; y++)
                {
                    Buffer.MemoryCopy(
                        srcUV + y * packet.Header.UVStride,
                        dst + dstUVOffset + y * dstYStride,
                        dstYStride,
                        _videoWidth);
                }
            }
            finally
            {
                _context.Unmap(_nv12Texture, 0);
            }
        }
        private void RenderFrame()
        {
            if (_context == null || _backBufferRTV == null) return;
            _context.OMSetRenderTargets(_backBufferRTV);
            _context.RSSetViewport(new Viewport(0, 0, _screenWidth, _screenHeight));
            _context.RSSetState(_rasterizerState);
            _context.ClearRenderTargetView(_backBufferRTV, new Color4(0, 0, 0, 1));
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.IASetInputLayout(_inputLayout);
            _context.IASetVertexBuffer(0, _vertexBuffer, 16, 0);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.PSSetSampler(0, _sampler);
            if (_nv12SRV_Y != null) _context.PSSetShaderResource(0, _nv12SRV_Y);
            if (_nv12SRV_UV != null) _context.PSSetShaderResource(1, _nv12SRV_UV);
            _context.Draw(6, 0);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            render_stop();
            _frameEvent?.Dispose();
            _rasterizerState?.Dispose();
            _sampler?.Dispose();
            _inputLayout?.Dispose();
            _vertexBuffer?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();
            _nv12SRV_Y?.Dispose();
            _nv12SRV_UV?.Dispose();
            _nv12Texture?.Dispose();
            _backBufferRTV?.Dispose();
            _backBuffer?.Dispose();
            _swapChain?.Dispose();
            _compVisual?.Dispose();
            _compTarget?.Dispose();
            _compDevice?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            Log.WriteLog("[DComp] 已释放所有资源");
            GC.SuppressFinalize(this);
        }
    }
}
