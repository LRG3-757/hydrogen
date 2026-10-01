# hydrogen
Lightweight cross-GPU video renderer. Separate GPU for decoding and rendering. Low resource consumption: 5%~8% CPU for soft decode, memory peak ≤300MB. 轻量跨GPU视频渲染器，解码与渲染可分配至不同显卡。低资源开销：软解CPU占用5%~8%，内存峰值≤300MB
## 第三方库 / Third-party Libraries
本项目 Hydrogen 使用 **FFmpeg** 作为视频解码底层。
This project Hydrogen uses **FFmpeg** as the video decoding backend.

FFmpeg 版权 © FFmpeg developers，采用 **GNU Lesser General Public License v2.1 or later (LGPLv2.1+)** 许可分发。
FFmpeg Copyright © FFmpeg developers, licensed under the **GNU Lesser General Public License v2.1 or later (LGPLv2.1+)**.

本程序采用动态链接方式调用FFmpeg库，不修改FFmpeg源码。
This program dynamically links to FFmpeg libraries, no modification to FFmpeg source code.

FFmpeg 项目主页：https://ffmpeg.org/
FFmpeg 法律与许可说明：https://ffmpeg.org/legal.html

### FFmpeg.AutoGen
本项目通过 FFmpeg.AutoGen 封装库调用FFmpeg API。
This project invokes FFmpeg API via FFmpeg.AutoGen wrapper.
### 编译依赖
源代码仓库**不包含FFmpeg二进制DLL**。编译运行前，请自行下载 LGPL 版 FFmpeg x86_64 shared DLL，放到程序输出目录。
Source repository **does not include FFmpeg binary DLLs**. Before build & run, download LGPL build FFmpeg x86_64 shared DLLs and place them into program output folder.

