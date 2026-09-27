# LocalSend-WinForms

一款基于 **LocalSend v2 协议** 实现的 Windows 局域网文件传输工具，可与其他 LocalSend v2客户端自动发现、互相收发文件。

本软件使用**WinForms** + **Win32** 构建，旨在给用户提供Windows**原生**、**快速**、**轻量**的LocalSend体验。

* * *

## 功能特性

* 自动设备发现（UDP 组播 + HTTP 子网扫描兜底）
* 双向互传：既能向其他 LocalSend 设备发送，也能接收来自它们的文件
* 系统托盘常驻，支持快速发送、设置、打开传输目录、关于、退出等快捷操作
* 文件管理器右键「发送文件」菜单，支持多选文件一次性发起传输
* 接收确认弹窗、独立传输进度弹窗、Windows 标准通知
* 中文 / 英文界面，运行时可切换
* 全程**零第三方依赖**（仅使用 .NET 8 自带类库）

| 类别  | 说明  |
| --- | --- |
| 设备发现 | UDP 组播 `224.0.0.167:53317` 监听他人广播；每 5 秒主动广播自身存在；每 30 秒对本地 `/24` 子网做 HTTP 兜底扫描（`GET /api/localsend/v2/info`），60 秒未活跃的设别自动剔除 |
| 发送  | 从主窗口选择文件/文件夹或直接拖拽，选中局域网设备即可发送；支持多文件并发、断点无关的重试（最多 3 次，指数退避） |
| 接收  | 内置依赖注入式 HTTP 服务器（基于 `TcpListener` + `SslStream`），无需管理员权限即可启用 HTTPS |
| 确认弹窗 | 收到传输请求时弹出独立确认窗口，可逐项勾选接收文件，带倒计时自动拒绝 |
| 进度弹窗 | 每个传输会话一个独立进度窗口，显示总进度、单文件进度、实时速率，完成后自动关闭 |
| 通知  | 传输完成 / 收到请求时通过 `NotifyIcon` 气泡通知提醒（可选升级为 Action Center Toast，见下文） |
| 托盘菜单 | 打开主界面、快速发送、设置、打开传输目录、关于、退出 |
| 右键菜单 | 在资源管理器任意文件上右键「发送文件」，多选后一次性转发给已运行的实例 |
| 设置  | 设备名称、默认保存路径、端口、HTTP/HTTPS、语言、自动接收、通知开关、文件重名策略、PIN 码、计算 SHA-256 校验、最小化到托盘、开机自启 |
| 重名冲突 | 提供「重命名」或「覆盖」两种策略，自动避免覆盖已有文件 |
| 安全  | 自签名 TLS 证书（每用户生成），发送端对证书 SHA-256 指纹做 TOFU（首次信任）固定 |

* * *

## 系统要求

* Windows 10 / 11（x64 或 AnyCPU）
* [.NET 8 运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（构建需要 .NET 8 SDK）
* 无需管理员权限；右键菜单与开机启动均写入当前用户（`HKCU`）注册表

* * *

## 构建与运行

### 1. 安装 .NET 8 SDK

若本机尚无 SDK，可用官方脚本安装到用户目录（无需管理员）：

    # 在项目根目录执行
    Invoke-WebRequest -Uri https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
    powershell -ExecutionPolicy Bypass -File .\dotnet-install.ps1 -Channel 8.0 -InstallDir "$env:USERPROFILE\.dotnet"

然后让构建使用本地 SDK（任选其一）：

    $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"   # 临时
    # 或
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"

### 2. 构建

推荐方式（版本化输出目录，见下节说明）：

    # <项目目录>
    powershell -ExecutionPolicy Bypass -File tools\build.ps1

直接调用 SDK 则将输出落在默认位置 `LocalSendWin\bin\Release\net8.0-windows\`。

### 2c. 构建安装包（MSI）

打包由 `installer\` 目录（WiX Toolset v4）与 `tools\build-installer.ps1` 完成，沿用与构建一致的「版本化输出、绝不覆盖」约定：

    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1          # 打包
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -List    # 只读列出已打包的 MSI
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -AppDir <指定源目录>

产物：

    builds\LocalSend-WinForms-1.0.0+20260927-164749\LocalSend-WinForms-1.0.0+20260927-164749.msi

脚本会自动挑选 `builds\` 下最新的应用产物目录作为打包源（按目录名里的`+yyyyMMdd-HHmmss` 排序，不按文件时间），并校验其中确实含有`LocalSend-WinForms.exe/.dll/.deps.json/.runtimeconfig.json/Resources\logo.ico`。`LocalSendWin.csproj` 的 `Version` 与 `installer\Product.wxs` 的 `<Package Version>`必须一致，不一致会直接报错而不是打出一个版本对不上的 MSI。

前置条件：`wix` dotnet 全局工具（4.0.4）与 `WixToolset.Sdk/4.0.4` 已还原，脚本会优先使用 `%USERPROFILE%\.dotnet` 下的 .NET SDK。

安装向导（全英文界面，`Language="1033"`）的流程：

    Welcome -> License Agreement -> Feature Selection -> Choose Install Location -> Ready -> Progress

* **Feature Selection** 页有两个复选框：`Start Menu shortcut`（默认勾选）与 `Desktop shortcut`（默认不勾选）。
* **Choose Install Location** 页可改安装目录；在功能选择页点 `Browse...` 同样能改。
* 依赖检测：找不到 .NET 8 Windows Desktop 运行时会直接拒绝安装，并给出下载链接。

无人值守安装（供脚本/自动化使用）：

    msiexec /i "builds\...\LocalSend-WinForms-1.0.0+....msi" /qn /norestart
    msiexec /i "....msi" /qn /norestart ADDLOCAL="MainFeature,DesktopShortcutFeature"
    msiexec /x "....msi" /qn /norestart

#### 卸载会清掉什么

| 清理项 | 位置  |
| --- | --- |
| 开始菜单项与 `LocalSend-WinForms` 文件夹 | `%ProgramData%\Microsoft\Windows\Start Menu\Programs\` |
| 桌面快捷方式（若安装时勾选） | 桌面  |
| 安装目录及其内容 | `INSTALLFOLDER`（默认是 `%ProgramFiles%\LocalSend-WinForms\`） |
| 资源管理器右键菜单动词 | `HKCU\Software\Classes\*\shell\LocalSend-WinForms` |
| 开机自启项（若用户开启过） | `HKCU\...\Windows\CurrentVersion\Run` |
| 安装标记与安装路径 | `HKLM\Software\LocalSend-WinForms` |

**有意不删**：`%LocalAppData%\LocalSendWin\` 下的设置、自签名证书与日志。删除证书会让所有已配对设备的指纹全部变化。

#### 动手改 `installer\` 时必读

* **组件 GUID 全部是手写的固定值**，不要改成 `Guid="*"`。自动生成的组件码只存在于当次构建的 `.wixpdb` 里，下一次构建会换一批，Windows 会当成新组件安装，旧文件不会被替换，安装目录里留下历史残留。
  
* **升级码 `UpgradeCode` 不要动**。改了它就等于换产品，「应用和功能」里会多出一条而不是就地升级。
  
* **不要把每个组件里的「清理项」合并**。MSI 不允许一个组件同时含 per-user 与per-machine 数据（`ICE57`），所以清理逻辑被拆成 `UninstallCleanupComponent`（HKLM）与 `UserContextCleanupComponent`（HKCU），两个快捷键组件的 key path 也都在HKCU，否则 `ICE38` / `ICE43` 会拦下构建。
  
* **这是 WiX v4，不是 v3**，网上大量 v3 写法会直接编译失败。已验证的 v3 → v4 差异：
  
  | v3 写法 | v4 正确写法 |
  | --- | --- |
  | `<Wix><Product>` 双层根 | 只有 `<Wix><Package>`，所有内容都在 `<Package>` 里 |
  | `<Condition>` 作 子元素 | `<Launch Condition="..." Message="..."/>` |
  | `<RemoveRegistry>` | `<RemoveRegistryKey Action="removeOnUninstall"/>` + `<RemoveRegistryValue>`（后者没有 `Action`） |
  | `<DirectorySearch Name="...">` | 没有 `Name`，`Path` 要写完整目录路径 |
  | `<Package Platform="x64" Description="..." SummaryCodepage="...">` | 这些属性都不存在，架构是 `.wixproj` 的 `InstallerPlatform` |
  | 手写 `TARGETDIR` / `ProgramFiles64Folder` | `<StandardDirectory>` |
  | `<Property Value="[OTHER]">` | `<SetProperty Id="..." Value="[OTHER]" Before="CostFinalize"/>`（`WIX1077`） |
  | `<MajorUpgrade>` 可不写 `DowngradeErrorMessage` | v4 必须写 |
  
* **v4 只提供 `WixUI_Minimal` / `WixUI_FeatureTree` / `WixUI_InstallDir` 三个界面变体**，没有同时含「功能选择页」和「安装位置页」的组合。这里用的是 `WixUI_FeatureTree`：它的 `CustomizeDlg`（v3 叫 `FeatureSelectionDlg`）带两个勾选框，靠 `Browse...` 按钮改安装目录，因此需要显式设 `_BrowseProperty` 才能让浏览框从当前目录起步。
  
* 想确认某个 MSI 的实际内容，可以反编译而不是猜：`wix msi decompile <msi> -out <.wxs>`，再用 `grep` 查表内容。校验 ICE 用 `wix msi validate <msi>`。
  

### 3. 运行

直接启动 `LocalSend-WinForms.exe` 即可。首次运行会：

* 在 `%LocalAppData%\LocalSendWin\` 下生成自签名证书与 `settings.json`
* 在系统托盘显示图标

### 4. 安装资源管理器右键菜单

打开「设置 → 资源管理器集成」，点击「安装菜单」即可在 `HKEY_CURRENT_USER\Software\Classes\*\shell` 下写入 `LocalSend-WinForms` 动词（`MultiSelectModel=Player`，支持多选）。卸载点击「移除菜单」。

* * *

##

## 配置说明（设置项）

设置保存在 `%LocalAppData%\LocalSendWin\settings.json`，主要字段：

| 字段  | 含义  | 默认值 |
| --- | --- | --- |
| `deviceName` | 本机在局域网中显示的名称 | 计算机名 |
| `downloadDirectory` | 接收文件保存目录 | 用户「下载」文件夹 |
| `port` | 监听端口 | `53317` |
| `useHttps` | 是否启用 HTTPS（自签名证书） | `true` |
| `language` | `zh-CN` / `en-US` | `zh-CN` |
| `autoAccept` | 免确认直接接收 | `false` |
| `notifyOnReceive` / `notifyOnComplete` | 收到请求 / 传输完成通知 | `true` |
| `conflictMode` | `Rename`（重命名） / `Overwrite`（覆盖） | `Rename` |
| `pin` | 接收端 PIN 码（空表示不验证） | 空   |
| `computeSha256` | 接收后校验 SHA-256 | `true` |
| `minimizeToTray` | 关闭窗口最小化到托盘而非退出 | `true` |
| `launchAtStartup` | 开机自启（写入 `HKCU\...\Run`） | `false` |

> 修改端口或 HTTP/HTTPS 会重启底层网络监听；其余项即时生效。

* * *

## 协议实现细节（LocalSend v2）

参考规范：<https://github.com/localsend/protocol>

### 发现阶段

* 监听 UDP 组播 `224.0.0.167:53317`，解析他人广播的 `DeviceInfo`。
* 定期广播自身 `DeviceInfo`（字段：`alias`、`version`、`deviceModel`、`deviceType`、`fingerprint`、`port`、`protocol`、`download`、`announcement`）。
* HTTP 兜底：对每个 `/24` 子网的主机依次请求 `GET /api/localsend/v2/info`，补全未通过组播发现的设备。

### 发送阶段（发送端 → 接收端）

1. `POST /api/localsend/v2/prepare-upload` —— 携带 `DeviceInfo` 与文件清单，接收端返回 `sessionId` 与每个文件的授权 `token`（可针对单文件返回 `null` 表示拒绝）。
2. `POST /api/localsend/v2/upload?sessionId=...&fileId=...&token=...` —— 逐文件上传字节流，带进度回调。
3. 任一文件失败按策略重试（最多 3 次，指数退避）；遇到 `403`/`404` 会重新握手。
4. `POST /api/localsend/v2/cancel` —— 取消整个会话。

### 接收阶段（内置 HTTP 服务器）

* 基于 `TcpListener` 直接解析 HTTP/1.1 请求；启用 HTTPS 时在外层包 `SslStream`（自签名证书 + 指纹固定，无需 `netsh` 绑定）。
* 会话与源 IP 绑定，防止跨设备串号；接收端可设置 PIN 码校验。
* 接收完成后可选计算 SHA-256 与发送端声明值比对，确保完整性。

### 并发与边界

* 每个传输由独立的 `sessionId`（接收）或 `operationId`（发送）区分，进度精准路由到对应窗口。
* 资源管理器多选文件时，多个进程实例通过 `Mutex` 互斥 + `NamedPipe`（`LocalSend-WinForms.Pipe`）把文件清单汇聚到已运行的单一实例。

* * *

## 可选：Action Center Toast 通知

默认使用 `NotifyIcon` 气泡通知（零依赖、兼容性最好）。如需更现代的 Windows 10/11 操作中心通知：

1. 在 `LocalSendWin.csproj` 中取消注释 `CommunityToolkit.WinUI.Notifications` 包引用。
2. 在 `Services/NotificationService.cs` 中启用 `USE_WINRT_TOAST` 条件编译分支。
3. 重新构建。

* * *

## 已知问题

打开 HTTPS加密 可能导致传输失败，需要在最好发送端和接收端都关闭加密

受限于 api 文字传输暂时采用 txt 的方式

## 许可证

本项目以 MIT 许可证发布。LocalSend 协议与官方客户端版权归 [LocalSend 项目](https://localsend.org) https://github.com/localsend/localsend 所有。
