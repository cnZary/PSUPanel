# PSUPanel

面向 Segotep / HiMOS 串口电源（如 `KE-1300PD`）的轻量化只读监控工具，基于 `himos/PROTOCOL.md` 协议逆向实现，用于替代原厂 HiMOS 客户端的电源串口监控功能。

## 已实现监控字段

**实时遥测（`CMD02`）**

- 输出电压：`+3.3 V`、`+5 V`、`+12 V`、`5VSB` 待机电压（V）
- 输入侧参数：输入功率（W；按原厂逻辑，原始值 `< 200` 时钳位显示 `10.0`）、输入频率（Hz）、输入电压（V）
- 温度：高压侧温度、低压侧温度、环境温度（℃）
- 风扇与状态：风扇转速（RPM）、原始风扇模式（`0..4`，具体模式语义未实测确认）、故障码（`u16le`，仅十六进制显示）
- 原始报文：通过校验的 `CMD02` 完整帧十六进制原文

**设备信息（`CMD03` / `CMD05`）**

- 型号（`CMD03` 完整 9 字节 ASCII，如 `KE-1300PD`）与序列号（`CMD05` UTF-8 去首尾空白）
- 额定功率（大端 `u16`，W）、额定输入电压范围（V AC）、电流字段低/高（原代码未标注语义名）、效率认证原始码（`P`/`G`/`S`/`T` 或按铜牌显示）、厂商原始字符串、保修信息（含原程序 `< 10` 显示“终身”的标记与年数字段）

**`CMD04` 原始数据**

- 展示 28 字节原始 Payload 十六进制、完整帧十六进制，以及依次切分的 14 个无符号小端 `u16le` 原始数值

**关键语义边界**

- `CMD02` 偏移 18 的 `u16le / 10`（实机常见 `50.30`）是**输入频率（Hz）**，不是输入电流。
- 故障码为原始 `u16le` 数值，**无逐 bit 含义**，界面只显示 `0xXXXX`，不推断或编造故障名称。
- **`CMD04` 物理意义未确认**：原厂软件中对应处理分支为空，当前仅展示原始 Payload 与 14 个 `u16le` 数值，禁止推测其物理含义。

## 软件架构

| 组件 | 技术栈与发布形态 | 职责与通信边界 |
|---|---|---|
| `PSUPanel.Tray.exe` | `.NET 10` `win-x64` NativeAOT 常驻托盘进程 | 唯一打开并独占串口，负责帧编解码、超时清理与状态维护 |
| `PSUPanel.Panel.exe` | `.NET 10` WPF 自包含单文件（`PublishAot=false`、`PublishTrimmed=false`） | 按需启动的桌面监控界面；不直接打开串口，不监听网络端口 |

- 托盘与面板之间仅通过当前用户可访问的本机命名管道（`PSUPanel.Monitor.CurrentUser`，`PipeOptions.CurrentUserOnly`）传输 JSON 快照（`MonitorSnapshot`）。
- 本机一次实测中，`PSUPanel.Tray.exe` 工作集约 `12 MB`（仅作单次实测参考，不作资源占用保证）。

## 使用方法

1. **先完全退出原厂 HiMOS**：串口为独占访问，波特率与帧格式固定为 `115200 8N1`（无流控，不拉高 DTR/RTS）。
2. **保持同目录部署**：确保 `PSUPanel.Tray.exe` 与 `PSUPanel.Panel.exe` 位于同一目录下。
3. **启动托盘监控**：运行 `PSUPanel.Tray.exe` 后，右键托盘图标可查看在线状态、型号、序列号及核心遥测，并切换串口或重新扫描；双击托盘图标或点击菜单“打开完整面板”即可启动或前置 `PSUPanel.Panel.exe`。
4. **端口发现与命令行参数**：
   - 默认自动枚举本机串口，且**只将返回通过长度、包尾和 CRC-8 校验的有效 `CMD02` 遥测帧的端口认定为目标设备**。
   - 支持通过 `PSUPanel.Tray.exe --port COMx` 指定当次启动优先使用的端口；也可以在托盘菜单或面板下拉框中选择固定端口并持久化。
   - 本文档或日志中提及的 `COM7`（及 `himos/PROTOCOL.md` 中的 `COM3`）仅为本机实测示例，并非硬编码端口或通用默认值。

## 安全与写入限制

- **日常完全只读监控**：运行期仅自动周期发送 `CMD04` 遥测查询帧（`55 7E 02 04 06 AE`，默认约 `1000 ms`），**不会自动发送**任何风扇模式、自定义风扇曲线、轮播周期设置或故障清零命令。
- **手动读取型号/序列号的风险提示**：设备部分情况下会主动推送信息，如需手动读取型号与序列号，面板提供“手动读取型号 / 序列号”按钮，发送报文 `55 7E 04 1E 00 00 22 AE`（`1E 00 00`）。该报文与原厂**故障清零命令完全相同**，软件无法感知设备上下文差异，因此界面强制要求**连续两次弹窗确认**，确认后**只发送一次、失败绝不自动重试**。请勿将该操作视为已实测安全的纯读取命令。

## 协议与容错边界

- **校验分离**：
  - 上行发送（TX）使用累加和：`sum(frame[2:-2]) & 0xFF`，仅覆盖 `LEN + CMD + Payload`。
  - 下行接收（RX）使用 `CRC-8/SMBUS`（`poly=0x07, init=0`）：覆盖 `frame[:-2]`，即从包头 `55` 到校验字节前。
- **严格解帧与恢复**：`StreamDecoder` 严格校验包头（`55 7E`）、长度（`LEN + 4`）、包尾（`AE`）与 RX CRC-8；支持保留跨包到达的单独首字节 `55` 以拼接半包，并在坏帧或串口噪声后自动重新同步。
- **超时清值**：串口持续打开但遥测超时（至少 `3000 ms` 或 `3` 倍轮询间隔）时，状态切为超时并立即清除实时遥测值，避免界面继续展示陈旧数据。

## 构建与依赖

- **构建环境**：Windows x64、`.NET 10 SDK`（`global.json` 指定 `10.0.404`）、Visual Studio / C++ Build Tools（NativeAOT 链接所需）。
- **外部依赖**：`System.IO.Ports 10.0.0` 是唯一外部 NuGet 包（仅 `PSUPanel.Tray` 引用），其余均使用 .NET 10 内置库及源生成 JSON。
- **发布命令**：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

发布脚本会先运行 .NET 协议单元测试，通过后再编译发布 `PSUPanel.Tray.exe`（NativeAOT）与 `PSUPanel.Panel.exe`（`win-x64` 自包含单文件）到 `artifacts/PSUPanel`，并清理 `.pdb` 符号文件。

## 验证与实测状态

- **Python 协议自检**（在 `himos` 目录执行）：

```powershell
py -B analysis/validate_protocol.py
```

- **.NET 协议与真实抓包回放测试**（在仓库根目录执行）：

```powershell
dotnet run --project tests/PSUPanel.Protocol.Tests/PSUPanel.Protocol.Tests.csproj -c Release -- himos/analysis/psu-capture.jsonl
```

该测试会校验 `himos/analysis/psu-capture.jsonl` 中的全部 63 帧真实设备记录（`CMD02 × 30`、`CMD04 × 31`、`CMD03 × 1`、`CMD05 × 1`），并完成 539 项协议检查（涵盖 CRC-8 标准向量、TX 帧构造、`CMD02`/`CMD03`/`CMD05` 字段解析、`CMD04` 原始保持、全分割位置与逐字节流式拆包及坏帧恢复）。

- **实机验证范围**：已完成 NativeAOT / 单文件发布、本机 `COM7` 只读遥测、托盘右键菜单以及 WPF 面板的实机检查。**未执行过**面板手动型号读取（`1E 00 00`）实机下发测试，也**未执行过**任何设置写入、风扇模式/曲线或故障清零实机测试。

## 配置与系统影响

- **配置位置**：仅在用户切换或指定串口时写入 `%LocalAppData%\PSUPanel\settings.json`，不包含全局安装配置。
- **零侵入设计**：无需管理员权限，不写入注册表开机启动项，不启动 HWiNFO 或 USBLCD helper，不发起任何联网请求。
- **代码约定**：源代码内部注释使用英文，本说明文档使用简体中文。

## 项目结构

```text
├── PSUPanel.slnx                      # .NET 解决方案文件
├── Directory.Build.props              # 全局编译属性（开启严格警告与可空检查）
├── global.json                        # 锁定 .NET SDK 10.0.404
├── publish.ps1                        # 先跑测试并发布至 artifacts/PSUPanel
├── src/
│   ├── PSUPanel.Protocol/             # 帧编解码、CRC-8/SMBUS 与流式解帧器
│   ├── PSUPanel.Shared/               # 快照模型、源生成 JSON 上下文与命名管道客户端
│   ├── PSUPanel.Tray/                 # NativeAOT 托盘常驻进程、串口服务与管道服务端
│   └── PSUPanel.Panel/                # WPF 单文件监控面板（按需启动）
├── tests/
│   └── PSUPanel.Protocol.Tests/       # 协议与真实抓包回放验证程序
├── himos/                             # 原厂协议逆向文档、Python 原型及抓包日志
└── artifacts/PSUPanel/                # 发布产物目录
```
