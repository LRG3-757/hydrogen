using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
namespace hydrogen
{
    public partial class hydrogen : Form
    {
        // 内存运行副本：解决配置读写不同步BUG
        private string _selectedDecoder = "CPU";
        private string _selectedRenderer = "GPU0";

        private IFrameSource? _player;
        private DComp_render? _renderer;
        #region 托盘组件
        private NotifyIcon? _trayIcon;
        private ContextMenuStrip? _trayMenu;
        #endregion
        #region 配置字段
        private readonly string _iniPath = Path.Combine(Application.StartupPath, "config.ini");
        //4视频槽位 0‑pictureBox1，1‑pictureBox2，2‑pictureBox3，3‑pictureBox4
        private readonly string[] _videoPaths = new string[4];
        private int _nextVideoSlotIndex = 0;
        private int _defaultPlaySlot = 0;
        private readonly List<string> _pauseProcessNames = new List<string>();
        private Thread? _processMonitorThread;
        private volatile bool _monitorRunning;
        private bool _isPausedByApp = false;
        private Watchdog? _watchdog;
        private volatile bool _isRestarting = false;
        #endregion
        public hydrogen()
        {
            InitializeComponent();
            Shown += Hydrogen_Shown;
            InitTrayIcon();
            LoadConfig(); //加载ini到内存变量
            //内存变量同步刷新到TextBox界面
            处理器选择.Text = _selectedDecoder;
            渲染卡选择.Text = _selectedRenderer;

            LoadAllThumbnail();
            if (long.TryParse(RAM阈值.Text, out long ramMb))
                Watchdog.GlobalMemThresholdMB = ramMb;
            if (long.TryParse(VRAM阈值.Text, out long vramMb))
                Watchdog.GlobalVramThresholdMB = vramMb;

            //TextBox控件，删除ComboBox Items相关代码
            StartProcessMonitorThread();
        }
        #region 托盘初始化
        private void InitTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            var menuShow = new ToolStripMenuItem("显示主窗口");
            var menuExit = new ToolStripMenuItem("彻底退出程序");
            _trayMenu.Items.AddRange(new ToolStripItem[] { menuShow, new ToolStripSeparator(), menuExit });
            menuShow.Click += (s, e) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            };
            menuExit.Click += (s, e) =>
            {
                CleanAllResource();
                Application.Exit();
            };
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Hydrogen播放器",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            };
        }
        #endregion
        #region 播放器创建销毁【解码卡、渲染卡独立选择，GPU硬解入口修复】
        private bool CreatePlayer(string videoFile)
        {
            string decodeSel = _selectedDecoder.Trim();
            string renderSel = _selectedRenderer.Trim();

            //输入校验
            if (!(renderSel.Equals("GPU0", StringComparison.OrdinalIgnoreCase)
                || renderSel.Equals("GPU1", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("渲染卡选择仅支持填写 GPU0 / GPU1", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (!(decodeSel.Equals("CPU", StringComparison.OrdinalIgnoreCase)
                || decodeSel.Equals("GPU0", StringComparison.OrdinalIgnoreCase)
                || decodeSel.Equals("GPU1", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("解码处理器仅支持填写 CPU / GPU0 / GPU1", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            HWplayer? hwPlayer = null;
            SWplayer? swPlayer = null;
            int initResult;
            if (decodeSel.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            {
                Log.WriteLog("[主窗体] 解码：CPU软解");
                swPlayer = new SWplayer();
                initResult = swPlayer.SWplayer_start("", videoFile);
                _player = swPlayer;
            }
            else if (decodeSel.Equals("GPU0", StringComparison.OrdinalIgnoreCase))
            {
                Log.WriteLog("[主窗体] 解码：GPU0 D3D11VA硬解");
                hwPlayer = new HWplayer();
                initResult = hwPlayer.HWplayer_start("GPU0", videoFile);
                _player = hwPlayer;
            }
            else if (decodeSel.Equals("GPU1", StringComparison.OrdinalIgnoreCase))
            {
                Log.WriteLog("[主窗体] 解码：GPU1 D3D11VA硬解");
                hwPlayer = new HWplayer();
                initResult = hwPlayer.HWplayer_start("GPU1", videoFile);
                _player = hwPlayer;
            }
            else
            {
                MessageBox.Show($"未知解码选项:{decodeSel}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (initResult != 0)
            {
                var tip = hwPlayer != null ? "硬解初始化失败" : "软解初始化失败";
                MessageBox.Show(tip);
                hwPlayer?.Dispose();
                swPlayer?.Dispose();
                _player = null;
                return false;
            }
            var frameEvt = new AutoResetEvent(false);
            if (hwPlayer != null)
                hwPlayer.FrameEvent = frameEvt;
            else
                swPlayer!.FrameEvent = frameEvt;
            SessionMeta meta;
            AVRational sarRatio;
            if (hwPlayer != null)
            {
                meta = hwPlayer.GetSessionMeta();
                sarRatio = hwPlayer.GetSampleAspectRatio();
            }
            else
            {
                meta = swPlayer!.GetSessionMeta();
                sarRatio = swPlayer.GetSampleAspectRatio();
            }
            double sar = sarRatio.num > 0 && sarRatio.den > 0
                ? (double)sarRatio.num / sarRatio.den
                : 1.0;
            Log.WriteLog($"视频信息：{meta.Width}x{meta.Height} SAR={sar:F4}");
            //跨卡渲染器
            _renderer = new DComp_render(_player, meta.Width, meta.Height, sar, frameEvt, renderSel);
            int renderRet = _renderer.render_start();
            if (renderRet != 0)
            {
                MessageBox.Show("DComp渲染器启动失败");
                _renderer?.Dispose();
                hwPlayer?.Dispose();
                swPlayer?.Dispose();
                _renderer = null;
                _player = null;
                return false;
            }
            if (hwPlayer != null)
                hwPlayer.HWplayer_play();
            else
                swPlayer!.SWplayer_play();
            Log.WriteLog("[主窗体] 播放启动成功");
            return true;
        }
        private void DestroyPlayer()
        {
            if (_player is HWplayer hp)
            {
                hp.HWplayer_stop();
                hp.Dispose();
            }
            else if (_player is SWplayer sp)
            {
                sp.SWplayer_stop();
                sp.Dispose();
            }
            _renderer?.Dispose();
            _player = null;
            _renderer = null;
            Log.WriteLog("[主窗体]播放器资源释放完成");
        }
        #endregion
        private void Hydrogen_Shown(object? sender, EventArgs e)
        {
            string videoFile = _videoPaths[_defaultPlaySlot];
            if (string.IsNullOrWhiteSpace(videoFile) || !File.Exists(videoFile))
            {
                using OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi|所有文件|*.*";
                ofd.Title = "选择视频";
                if (ofd.ShowDialog() != DialogResult.OK)
                {
                    MessageBox.Show("未选择视频，程序关闭");
                    Close();
                    return;
                }
                videoFile = ofd.FileName;
                _videoPaths[_defaultPlaySlot] = videoFile;
                SaveConfig();
                LoadAllThumbnail();
            }
            if (!CreatePlayer(videoFile))
            {
                Close();
                return;
            }
            WindowState = FormWindowState.Minimized;
            if (_player != null)
            {
                _watchdog = new Watchdog(_player, _selectedDecoder + "|" + _selectedRenderer, videoFile);
                _watchdog.OnRestartStarting += () =>
                {
                    if (_isRestarting) return;
                    _isRestarting = true;
                    Log.WriteLog("[看门狗]内存超限，重启播放器");
                    Invoke(() =>
                    {
                        DestroyPlayer();
                        string nextVid = _videoPaths[_defaultPlaySlot];
                        CreatePlayer(nextVid);
                        _isRestarting = false;
                    });
                };
                _watchdog.Start();
            }
        }
        #region INI配置读写 【修复保存失效BUG】
        private void LoadConfig()
        {
            if (!File.Exists(_iniPath))
            {
                //ini不存在，使用默认值
                _selectedDecoder = "CPU";
                _selectedRenderer = "GPU0";
                return;
            }
            //读取并清理空格
            string decRaw = IniHelper.Read("Main", "DecoderGpu", "CPU", _iniPath).Trim();
            string renRaw = IniHelper.Read("Main", "RenderGpu", "GPU0", _iniPath).Trim();

            //校验读取的值，非法自动回退默认
            if (decRaw == "CPU" || decRaw == "GPU0" || decRaw == "GPU1")
                _selectedDecoder = decRaw;
            else
                _selectedDecoder = "CPU";

            if (renRaw == "GPU0" || renRaw == "GPU1")
                _selectedRenderer = renRaw;
            else
                _selectedRenderer = "GPU0";

            RAM阈值.Text = IniHelper.Read("Watchdog", "RAM阈值", "750", _iniPath);
            VRAM阈值.Text = IniHelper.Read("Watchdog", "VRAM阈值", "750", _iniPath);
            _videoPaths[0] = IniHelper.Read("Video", "Picture1Path", "", _iniPath);
            _videoPaths[1] = IniHelper.Read("Video", "Picture2Path", "", _iniPath);
            _videoPaths[2] = IniHelper.Read("Video", "Picture3Path", "", _iniPath);
            _videoPaths[3] = IniHelper.Read("Video", "Picture4Path", "", _iniPath);
            int.TryParse(IniHelper.Read("Video", "DefaultPlaySlot", "0", _iniPath), out _defaultPlaySlot);
            _defaultPlaySlot = Math.Clamp(_defaultPlaySlot, 0, 3);
            string pauseRaw = IniHelper.Read("PauseProgram", "ProcessList", "", _iniPath);
            if (!string.IsNullOrWhiteSpace(pauseRaw))
            {
                string[] arr = pauseRaw.Split(';', StringSplitOptions.RemoveEmptyEntries);
                _pauseProcessNames.Clear();
                暂停程序列表.Items.Clear();
                foreach (var p in arr)
                {
                    _pauseProcessNames.Add(p);
                    暂停程序列表.Items.Add(p);
                }
            }
        }
        private void SaveConfig()
        {
            IniHelper.Write("Main", "DecoderGpu", _selectedDecoder, _iniPath);
            IniHelper.Write("Main", "RenderGpu", _selectedRenderer, _iniPath);
            IniHelper.Write("Watchdog", "RAM阈值", RAM阈值.Text, _iniPath);
            IniHelper.Write("Watchdog", "VRAM阈值", VRAM阈值.Text, _iniPath);
            IniHelper.Write("Video", "Picture1Path", _videoPaths[0], _iniPath);
            IniHelper.Write("Video", "Picture2Path", _videoPaths[1], _iniPath);
            IniHelper.Write("Video", "Picture3Path", _videoPaths[2], _iniPath);
            IniHelper.Write("Video", "Picture4Path", _videoPaths[3], _iniPath);
            IniHelper.Write("Video", "DefaultPlaySlot", _defaultPlaySlot.ToString(), _iniPath);
            IniHelper.Write("PauseProgram", "ProcessList", string.Join(";", _pauseProcessNames), _iniPath);
        }
        private void SaveConfigAndPromptRestart()
        {
            //读取TextBox输入，Trim清理空格，校验合法性
            string decInput = 处理器选择.Text.Trim();
            string renInput = 渲染卡选择.Text.Trim();
            bool decOk = decInput == "CPU" || decInput == "GPU0" || decInput == "GPU1";
            bool renOk = renInput == "GPU0" || renInput == "GPU1";
            if (!decOk || !renOk)
            {
                MessageBox.Show("输入不合法！解码仅支持 CPU/GPU0/GPU1；渲染仅支持 GPU0/GPU1", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            //写入内存变量
            _selectedDecoder = decInput;
            _selectedRenderer = renInput;
            SaveConfig();
            LoadAllThumbnail();
            //==== 修改弹窗文字 ====
            MessageBox.Show("配置保存成功，请重启程序生效！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        #endregion
        #region 进程监控线程
        private void StartProcessMonitorThread()
        {
            _monitorRunning = true;
            _processMonitorThread = new Thread(ProcessMonitorLoop)
            {
                IsBackground = true,
                Name = "ProcessMonitor"
            };
            _processMonitorThread.Start();
        }
        private void ProcessMonitorLoop()
        {
            while (_monitorRunning)
            {
                Thread.Sleep(1000);
                bool found = false;
                foreach (var procName in _pauseProcessNames)
                {
                    if (Process.GetProcessesByName(procName).Length > 0)
                    {
                        found = true;
                        break;
                    }
                }
                if (found && !_isPausedByApp)
                {
                    _isPausedByApp = true;
                    Invoke(() =>
                    {
                        if (_player is HWplayer hp) hp.HWplayer_pause();
                        else if (_player is SWplayer sp) sp.SWplayer_pause();
                        Log.WriteLog("[进程监控]检测到目标程序，暂停播放");
                    });
                }
                else if (!found && _isPausedByApp)
                {
                    _isPausedByApp = false;
                    Invoke(() =>
                    {
                        if (_player is HWplayer hp) hp.HWplayer_play();
                        else if (_player is SWplayer sp) sp.SWplayer_play();
                        Log.WriteLog("[进程监控]目标程序关闭，恢复播放");
                    });
                }
            }
        }
        #endregion
        #region 按钮事件
        private void 点击添加视频_Click(object sender, EventArgs e)
        {
            using OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "视频|*.mp4;*.mkv;*.mov;*.avi";
            ofd.Title = "选择视频文件";
            if (ofd.ShowDialog() != DialogResult.OK) return;
            _videoPaths[_nextVideoSlotIndex] = ofd.FileName;
            _nextVideoSlotIndex++;
            if (_nextVideoSlotIndex >= 4) _nextVideoSlotIndex = 0;
            SaveConfigAndPromptRestart();
        }
        private void 点击添加暂停程序_Click(object sender, EventArgs e)
        {
            using OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "程序|*.exe";
            ofd.Title = "选择需要监控的exe";
            if (ofd.ShowDialog() != DialogResult.OK) return;
            string procName = Path.GetFileNameWithoutExtension(ofd.FileName);
            if (_pauseProcessNames.Contains(procName))
            {
                MessageBox.Show("该进程已存在");
                return;
            }
            _pauseProcessNames.Add(procName);
            暂停程序列表.Items.Add(procName);
            SaveConfigAndPromptRestart();
        }
        private void 保存设置_Click(object sender, EventArgs e)
        {
            SaveConfigAndPromptRestart();
        }
        #endregion
        #region PictureBox点击
        private void pictureBox1_Click(object sender, EventArgs e) => _defaultPlaySlot = 0;
        private void pictureBox2_Click(object sender, EventArgs e) => _defaultPlaySlot = 1;
        private void pictureBox3_Click(object sender, EventArgs e) => _defaultPlaySlot = 2;
        private void pictureBox4_Click(object sender, EventArgs e) => _defaultPlaySlot = 3;
        #endregion
        #region ListBox事件
        private void 暂停程序列表_SelectedIndexChanged(object sender, EventArgs e)
        {
        }
        private void 暂停程序列表_DoubleClick(object sender, EventArgs e)
        {
            if (暂停程序列表.SelectedItem is string procName)
            {
                _pauseProcessNames.Remove(procName);
                暂停程序列表.Items.Remove(procName);
                SaveConfigAndPromptRestart();
            }
        }
        #endregion
        #region 缩略图【修复：取消强制I帧，读到任意帧返回，完善资源释放】
        private unsafe Bitmap? GetVideoThumb(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            AVFormatContext* pFormatCtx = null;
            AVCodecContext* pCodecCtx = null;
            AVFrame* pFrame = null;
            AVFrame* pRgbFrame = null;
            AVPacket* pPacket = null;
            SwsContext* swsCtx = null;
            Bitmap? resultBmp = null;
            try
            {
                AVFormatContext* pLocal = null;
                int retOpen = ffmpeg.avformat_open_input(&pLocal, filePath, null, null);
                if (retOpen != 0)
                    return null;
                pFormatCtx = pLocal;
                if (ffmpeg.avformat_find_stream_info(pFormatCtx, null) < 0)
                    return null;
                int videoStreamId = -1;
                for (int i = 0; i < pFormatCtx->nb_streams; i++)
                {
                    if (pFormatCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStreamId = i;
                        break;
                    }
                }
                if (videoStreamId < 0) return null;
                AVStream* vStream = pFormatCtx->streams[videoStreamId];
                AVCodec* codec = ffmpeg.avcodec_find_decoder(vStream->codecpar->codec_id);
                if (codec == null) return null;
                pCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(pCodecCtx, vStream->codecpar);
                if (ffmpeg.avcodec_open2(pCodecCtx, codec, null) < 0)
                    return null;
                pPacket = ffmpeg.av_packet_alloc();
                pFrame = ffmpeg.av_frame_alloc();
                pRgbFrame = ffmpeg.av_frame_alloc();
                int w = pCodecCtx->width;
                int h = pCodecCtx->height;
                AVPixelFormat outPixFmt = AVPixelFormat.AV_PIX_FMT_BGR24;
                const int SWS_BILINEAR = 2;
                swsCtx = ffmpeg.sws_getContext(w, h, pCodecCtx->pix_fmt, w, h, outPixFmt, SWS_BILINEAR, null, null, null);
                if (swsCtx == null) return null;
                int bufferSize = ffmpeg.av_image_get_buffer_size(outPixFmt, w, h, 1);
                byte* buffer = (byte*)ffmpeg.av_malloc((uint)bufferSize);
                byte_ptrArray4 data4 = default;
                int_array4 line4 = default;
                ffmpeg.av_image_fill_arrays(ref data4, ref line4, buffer, outPixFmt, w, h, 1);
                byte_ptrArray8 data8 = default;
                int_array8 line8 = default;
                pRgbFrame->data = data8;
                pRgbFrame->linesize = line8;
                bool gotFrame = false;
                while (ffmpeg.av_read_frame(pFormatCtx, pPacket) >= 0)
                {
                    if (pPacket->stream_index == videoStreamId)
                    {
                        ffmpeg.avcodec_send_packet(pCodecCtx, pPacket);
                        int recRet = ffmpeg.avcodec_receive_frame(pCodecCtx, pFrame);
                        if (recRet == 0)
                        {
                            //【修复】不再强制I帧，任何有效帧都作为缩略图
                            ffmpeg.sws_scale(swsCtx, pFrame->data, pFrame->linesize, 0, h, pRgbFrame->data, pRgbFrame->linesize);
                            gotFrame = true;
                            break;
                        }
                    }
                    ffmpeg.av_packet_unref(pPacket);
                }
                if (!gotFrame)
                    return null;
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                for (int y = 0; y < h; y++)
                {
                    IntPtr srcPtr = (IntPtr)pRgbFrame->data[0] + pRgbFrame->linesize[0] * y;
                    IntPtr dstPtr = bmpData.Scan0 + bmpData.Stride * y;
                    int lineByte = w * 3;
                    byte[] tempBuf = new byte[lineByte];
                    Marshal.Copy(srcPtr, tempBuf, 0, lineByte);
                    Marshal.Copy(tempBuf, 0, dstPtr, lineByte);
                }
                bmp.UnlockBits(bmpData);
                resultBmp = bmp;
                ffmpeg.av_free(buffer);
            }
            catch
            {
                resultBmp = null;
            }
            finally
            {
                if (pFrame != null) ffmpeg.av_frame_free(&pFrame);
                if (pRgbFrame != null) ffmpeg.av_frame_free(&pRgbFrame);
                if (pPacket != null) ffmpeg.av_packet_free(&pPacket);
                if (pCodecCtx != null) ffmpeg.avcodec_free_context(&pCodecCtx);
                if (pFormatCtx != null) ffmpeg.avformat_close_input(&pFormatCtx);
                if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
            }
            return resultBmp;
        }
        private void LoadAllThumbnail()
        {
            //释放旧图片，防止GDI泄漏
            pictureBox1.Image?.Dispose();
            pictureBox2.Image?.Dispose();
            pictureBox3.Image?.Dispose();
            pictureBox4.Image?.Dispose();
            pictureBox1.Image = GetVideoThumb(_videoPaths[0]);
            pictureBox2.Image = GetVideoThumb(_videoPaths[1]);
            pictureBox3.Image = GetVideoThumb(_videoPaths[2]);
            pictureBox4.Image = GetVideoThumb(_videoPaths[3]);
        }
        #endregion
        #region 资源释放
        private void CleanAllResource()
        {
            _monitorRunning = false;
            _processMonitorThread?.Join(1000);
            _watchdog?.Stop();
            _watchdog?.Dispose();
            _watchdog = null;
            DestroyPlayer();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayMenu?.Dispose();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _trayIcon?.ShowBalloonTip(800, "最小化到托盘", "播放器后台继续运行", ToolTipIcon.Info);
            }
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CleanAllResource();
            Log.WriteLog("[主窗体]程序退出，全部资源释放");
            base.OnFormClosed(e);
        }
        #endregion
        //====设计器绑定空事件全部完整保留====
        #region 设计器绑定空事件
        private void hydrogen_Load(object sender, EventArgs e)
        {
        }
        private void label1_Click(object sender, EventArgs e)
        {
        }
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
        }
        private void RAM阈值_TextChanged(object sender, EventArgs e)
        {
        }
        private void textBox3_TextChanged(object sender, EventArgs e)
        {
        }
        private void button1_Click(object sender, EventArgs e)
        {
            点击添加暂停程序_Click(sender, e);
        }
        private void 渲染处理器选择_TextChanged(object sender, EventArgs e)
        {
        }
        #endregion
    }
    //INI帮助类
    public static class IniHelper
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);
        public static void Write(string section, string key, string value, string filePath)
        {
            WritePrivateProfileString(section, key, value, filePath);
        }
        public static string Read(string section, string key, string def, string filePath)
        {
            StringBuilder sb = new StringBuilder(1024);
            GetPrivateProfileString(section, key, def, sb, sb.Capacity, filePath);
            return sb.ToString();
        }
    }
}
