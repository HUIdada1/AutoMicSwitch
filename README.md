# AutoMicSwitch

切换 Windows 默认播放设备（耳机 / 音箱）时，自动把默认麦克风切到对应设备。附带一个本地网页控制台。

作者：沐辉 ｜ MIT License ｜ .NET Framework 4.x，无第三方依赖

## 解决的问题

同时插着耳机、音箱、摄像头时，Windows 只认一套默认输入和输出。戴耳机开麦想用耳机麦，切到外放又得手动把麦克风换成摄像头麦，每次都要去声音设置里点两下。

这个程序常驻后台盯着默认播放设备，一旦变化，就按规则把默认麦克风一起切过去（通信麦克风也会设）。设备用硬件路径里的 `VID_xxxx` 识别，换 USB 口、改显示名、重装驱动都不会失效。

## 界面

![控制台](docs/screenshot.png)

## 功能

- 默认播放设备变化 → 自动切换默认麦克风
- 本地网页控制台：信号流向、当前设备、规则表、设备清单、实时日志
- 托盘常驻，可暂停 / 恢复、立即同步、打开日志
- 开机自启，界面开关或脚本都行
- 规则写在 `rules.txt`，改完重启程序即可，不用重新编译

## 快速开始

需要 .NET Framework 4.x（Windows 10 / 11 自带）。

1. 双击 `build.cmd` 编译，产出 `dist\AutoMicSwitch.exe`
2. 编辑 `dist\rules.txt`，填上你自己的设备（见下一节）
3. 双击 `dist\AutoMicSwitch.exe`，浏览器会打开控制台 http://127.0.0.1:17817/

想开机自启，双击 `install-autostart.cmd`；取消用 `uninstall-autostart.cmd`。

## 配置 rules.txt

`rules.txt` 放在 exe 同一个目录，一行一条：

```
# 播放设备关键字|目标麦克风关键字|说明
VID_1234|VID_1234|耳机播放时用耳机自己的麦克风
VID_5678|VID_5678|另一副耳机

# 兜底：播放设备没匹配到规则时用哪个麦克风，写 none 表示保持不动
fallback=VID_9ABC
```

关键字匹配的是设备硬件路径的子串，写 `VID_xxxx` 就够。

查 VID 的方法：设备管理器 → 选中设备 → 右键「属性」→「详细信息」→「硬件 Id」，会看到类似 `USB\VID_1234&PID_5678` 的字符串，取 `VID_1234`。完整示例见 `rules.example.txt`。

## 命令行

| 参数 | 说明 |
| --- | --- |
| 无 | 启动并打开控制台 |
| `--startup` | 静默启动，只驻留托盘 |

## 目录结构

```
src/
  Program.cs        入口、托盘、控制台 API
  AudioEngine.cs    联动引擎
  HttpServer.cs     本地 HTTP 服务
  Dashboard.html    控制台页面（编译时嵌入 exe）
build.cmd           一键编译
rules.example.txt   规则示例
install-autostart.cmd / uninstall-autostart.cmd
docs/               界面预览与截图
```

## 原理

- 每 0.7 秒用 MMDevice API（`IMMDeviceEnumerator::GetDefaultAudioEndpoint`）读一次默认播放设备
- 发现变化后按规则找到目标麦克风，用 `AudioSes.dll` 里未公开的 `IPolicyConfig::SetDefaultEndpoint` 设为默认。`eConsole` / `eMultimedia` / `eCommunications` 三个角色一起设，效果等同于手动在设置面板里选
- 设置完在 2 / 4 / 6 秒各读回校验一次，被别的程序改回去会自动重试
- 控制台是本地 `TcpListener` 绑 `127.0.0.1`，页面嵌在 exe 里，不联网、不上传任何数据
- 单实例运行，靠命名互斥量避免重复启动

## 已知问题

- 麦克风切完几秒又被改回去，通常是有别的音频软件在抢默认设备。日志里会写「被改回，重试」，找到那个软件关掉它的自启即可
- 有些主板或音箱会枚举出一个没有拾音孔的假麦克风端点，写规则时别选到它

## License

MIT © 2026 沐辉
