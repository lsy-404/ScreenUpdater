# ScreenUpdater

给 3.5 寸 USB 串口副屏（Turing Smart Screen revision A，`VID_1A86&PID_5722`，480×320）用的性能面板，取代官方上位机。Windows 11，.NET 10。

## 面板内容

- 标题栏：日期、星期、带秒的时间，可选的室外天气
- 固定磁盘的空间占用条与读写速度
- 前台应用的 FPS 曲线与 P50 / P95 / P99
- 进程、线程、句柄总数
- CPU 与内存叠加曲线（左轴利用率，右轴 GB），CPU 频率、功耗、温度，内存用量与频率
- 网络上下行速度与曲线
- 两张独立显卡：利用率与显存主图、温度，以及按负载类型的分项：3D、Video（编码与解码）、计算（NVIDIA 为 CUDA，其余为 Compute），外加其余引擎中最活跃的一个

主显示器关闭时副屏随之关闭。画面每分钟整体平移 1–2 像素，避免静态内容长期停在同一位置。串口整帧传输约 0.6–1.8 秒，因此每秒只发送发生变化的区域。

## 数据来源

| 数据 | 来源 |
|---|---|
| CPU 利用率与频率、GPU 利用率与显存 | Windows 性能计数器（PDH） |
| NVIDIA 显卡利用率（含 CUDA 负载）、编码 / 解码利用率与功耗上限 | 驱动自带的 NVML |
| GPU 温度 | `D3DKMTQueryAdapterInfo`（`KMTQAITYPE_ADAPTERPERFDATA`） |
| CPU 温度与功耗、GPU 频率 | [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)，CPU 部分需要 [PawnIO](https://pawnio.eu/) 驱动和管理员权限 |
| 帧时间 | [PresentMon](https://github.com/GameTechDev/PresentMon) 控制台程序 |
| 天气 | [Open-Meteo](https://open-meteo.com/) |

## 安装

```powershell
winget install Intel.PresentMon.Console
winget install namazso.PawnIO
```

然后在管理员 PowerShell 中运行 `updater\install.ps1`。它把程序发布到 `updater\dist`，并注册一个登录时以最高权限启动的计划任务 `ScreenUpdater`。

PresentMon 和 PawnIO 都是可选的：缺少前者没有 FPS，缺少后者没有 CPU 温度和功耗。

## 配置

`updater\dist\config.json`，所有字段可省略：

```json
{
  "port": "COM3",
  "flip": true,
  "brightness": 40,
  "intervalMs": 1000,
  "presentMonPath": null,
  "latitude": 0.0,
  "longitude": 0.0
}
```

- `port`：留空时按 USB 标识自动查找
- `flip`：画面旋转 180°
- `latitude` / `longitude`：填写后标题栏显示天气，坐标会发送给 Open-Meteo

## 预览

不连接副屏，把一帧渲染成图片：

```powershell
updater\dist\ScreenUpdater.exe --preview out.png 10
```

## 致谢

串口协议参考了 [turing-smart-screen-python](https://github.com/mathoudebine/turing-smart-screen-python) 对 revision A 的说明。
