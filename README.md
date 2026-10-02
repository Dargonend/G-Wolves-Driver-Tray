# G-Wolves Driver Tray

> **非官方工具，与厂商无关。** 与 G-Wolves、mouse.pink / mouse.xyz 及其所有者无关联，未获其授权或认可；
> 产品名与商标归各自所有者所有，本项目不包含任何官方代码或资源。详见 [DISCLAIMER.md](DISCLAIMER.md)。

在 Windows 托盘区**实时显示鼠标电量**，不用每次去开网页驱动。
右键菜单里还能直接切换 **SPDT 左右键开关**、**灵敏度 DPI**、**回报率**、**响应高度（LOD）**、**竞技模式**。

```
┌────┐   G-Wolves Driver Tray 78%（未充电）  3.986 V
│ 78 │   绿=正常  橙=偏低  红=严重不足  蓝=充电中
└────┘
```

## 下载

到 **[Releases](../../releases/latest)** 下载 zip，解压后双击 `G-Wolves-Driver-Tray.exe`。

- **系统要求**：Windows 7 SP1 及以上（.NET Framework 4.0 随 Win8 以上内置，无需安装任何东西）
- **适用设备**：使用 [mouse.pink](https://mouse.pink/) 网页驱动的 G-Wolves 系列鼠标。
  程序自动扫描识别，**不需要选择型号**
- 免安装、不写注册表、不需要额外运行时

## ⚠️ 两个注意事项

**① 第一次运行会被 Windows 拦一下。**
本程序没有代码签名证书，SmartScreen 会提示「已保护你的电脑」，请点「更多信息」→「仍要运行」。
不放心可以直接看源码，或用系统自带的 `csc.exe` 自行编译（`build.ps1`，不需要安装任何 SDK）。

**② Windows 11 会把新图标收进折叠区。**
点开任务栏的 `^`，把电量图标拖出来即可常驻。

## 使用

| 操作 | 效果 |
|---|---|
| 指针悬停 | 查看电量、充电状态、电压 |
| 左键单击 | 打开详情窗口 |
| 右键单击 | 菜单：SPDT / 灵敏度 / 回报率 / 响应高度 / 竞技模式 / 开机自启 / 诊断 / 退出 |

默认每 5 秒刷新，详情窗口打开时加快到 1 秒。
读不到时多半是鼠标休眠了，动一下鼠标几秒内会自动恢复。
重复启动只会唤出已有窗口，不会出现两个图标。

## 从源码构建

```bat
powershell -ExecutionPolicy Bypass -File build.ps1
```

- 协议格式、命令表、EEPROM 地址表：**[docs/协议说明.md](docs/协议说明.md)**
- 完整命令行参数：`G-Wolves-Driver-Tray.exe --help`

## 许可

以 **MIT 许可证**发布，详见 [LICENSE](LICENSE)。

**非官方工具**，与 G-Wolves 及其所有者无关联，未获其授权或认可；
本项目不授予任何商标权，详见 [DISCLAIMER.md](DISCLAIMER.md)。

按原样（AS IS）提供，不提供任何担保，使用风险自负 —— 本工具会向鼠标 EEPROM
写入你主动点击的设置，每次写完都会立即回读校验。

不联网、不收集、不上传任何数据。
