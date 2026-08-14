# AGENTS.md

本文档面向 AI 编码助手与新加入的开发者，描述 Encodex 项目的整体情况与版本发布流程。

## 1. 项目概况

**Encodex** 是一个 Windows 桌面工具，用于批量扫描项目文件夹中的文本文件、自动检测其编码（BOM / 空字节启发式 / Ude 统计检测），并统一转换为目标编码（UTF-8 带/无 BOM、UTF-16/32 LE/BE、GBK/GB2312/GB18030、Big5、EUC-KR、Shift-JIS、ASCII、ISO-8859-1，共 14 种）。

### 技术栈

- **.NET Framework 4.8.1**（SDK 风格 csproj，目标框架 `net481`），仅面向 Windows
- **WPF + WPF-UI（Fluent 风格）** 界面，MVVM 模式（CommunityToolkit.Mvvm）
- 核心依赖均为 NuGet `PackageReference`，版本号使用通配符（`8.*` / `4.*` 等）：
  `CommunityToolkit.Mvvm`、`System.Text.Json`、`System.Text.Encoding.CodePages`、`Ude.NetStandard`、`WPF-UI`
- 测试框架：xUnit（`Encodex.Tests`）
- 无 CI/CD，本地 Visual Studio / `dotnet` CLI 构建

### 解决方案结构

```
Encodex.slnx
├── Encodex/                  主程序（WinExe）
│   ├── Models/               ConversionStatus、EncodingOption、ExtensionOption、ExtensionGroup、
│   │                         FileConversionItem、ReportSection
│   ├── Services/             FileScanner（扫描）、EncodingDetector（检测）、EncodingConverter（并发转换）、
│   │                         ExtensionProfile（扩展名配置）、AppSettingsStore（设置持久化）、
│   │                         PathHelper、AppUpdateService（联网更新）、CliRunner（--cli 无头模式）
│   ├── ViewModels/           MainViewModel（驱动全部界面行为）
│   ├── Resources/            Strings.resx + Res.cs（全部用户可见文案，见「关键约定」）
│   ├── MainWindow.xaml(.cs)  主界面（四个 Tab：配置 → 文件列表 → 转换 → 报告）
│   ├── PreviewWindow.xaml(.cs)  转换前编码预览对话框
│   ├── AddExtensionDialog.xaml(.cs)  自定义扩展名输入框
│   ├── App.xaml(.cs)         启动入口：注册编码提供程序、恢复主题、识别 --cli 分支
│   └── AssemblyInfo.cs       程序集元数据；版本号唯一来源（见第 3 节）
├── Encodex.Updater/          独立更新器（net481，被主程序 ProjectReference 编译进输出目录）
│   └── AssemblyInfo.cs       InternalsVisibleTo("Encodex.Tests")
└── Encodex.Tests/            xUnit 测试，经 ProjectReference 引用主项目（传递引用含 Updater）
```

### 关键约定

- 版本号**只维护在** `AssemblyInfo.cs` 的 `AssemblyVersion` / `AssemblyFileVersion`，未使用自动版本注入。
- 用户设置存放在 `%APPDATA%\Encodex\settings.json`（不在程序目录），覆盖式更新不会丢失用户配置；
  内容含主题、上次源文件夹、目标编码 DisplayName、扩展名勾选；`Save` 为原子写（.tmp + `File.Replace`）。
- **所有用户可见文案**必须放在 `Resources/Strings.resx`，经 `Res` 静态类访问（XAML 用 `{x:Static r:Res.XXX}`，
  xmlns:r="clr-namespace:Encodex.Resources"）；默认中文，新增语言只需添加语言变体 .resx。
  例外：`Encodex.Updater` 是独立程序集，无法引用 `Res`，其弹窗文案保持硬编码中文。
- 主项目通过 `Compile Remove` 排除 `Encodex.Tests/**` 与 `Encodex.Updater/**`；Updater 通过 `ProjectReference` 编译进主程序输出目录。
- `EncodingConverter.ConvertAsync` 并发转换（SemaphoreSlim 上限 8），各文件相互独立；原地覆盖模式（`overwriteInPlace`）
  先全量备份到 `%TEMP%\Encodex-backup-<时间戳>` 再写回，备份目录随报告展示。
- 转换/复制后保留源文件时间戳（`PreserveTimestamps`）；检测编码可在文件列表手动纠正
  （DataGrid 可编辑列，候选见 `MainViewModel.AvailableDetectedEncodings`）。
- 目标机器需预装 .NET Framework 4.8.1 运行时（框架依赖部署，不支持单文件发布）。

## 2. 联网更新机制

采用 **update.xml 清单 + 独立更新器 + zip 整目录覆盖** 方案：

```
用户点击 🔄 检查更新
  → AppUpdateService 拉取 update.xml，比较版本
  → 发现新版：提示 → 下载 zip 到 %TEMP%（状态栏显示百分比）→ SHA-256 校验（大小写不敏感）
  → 启动 Encodex.Updater.exe（参数：主进程PID、zip路径、安装目录）后退出
  → Updater 等待主进程退出（文件锁释放）→ 解压覆盖安装目录 → 重启主程序
```

三个组成部分：

| 组件 | 位置 | 职责 |
|---|---|---|
| `update.xml` | 仓库根目录，经 `raw.githubusercontent.com` 公开访问 | 声明最新版本号、下载链接、SHA-256、是否强制更新、更新日志；每次发版由打包脚本重写 |
| `AppUpdateService` | `Services/AppUpdateService.cs` | 拉取并解析清单、版本比较、下载校验（进度 `IProgress<DownloadProgress>`）、启动 Updater。清单地址为常量 `DefaultManifestUrl`，仓库迁移时需同步修改；`HttpClient` 可注入（测试用） |
| `Encodex.Updater.exe` | `Encodex.Updater/Program.cs` | 进程退出等待、zip 解压覆盖（路径穿越防护在 `ResolveEntryPath`，可单测）、自身改名式自替换（失败回滚 `.upd-old`）、重启主程序 |

注意事项：

- 运行中的 exe 无法覆盖自身，故替换必须由独立的 Updater 进程完成；zip 必须整目录包含 exe、全部依赖 DLL、`Encodex.exe.config` 与 `Encodex.Updater.exe`。
- **参数引号陷阱**：`AppContext.BaseDirectory` 以 `\` 结尾，直接拼 `"..."` 会让尾部 `\"` 被命令行解析器当作转义引号导致 Updater 收到损坏参数；启动 Updater 必须走 `AppUpdateService.QuoteWindowsArgument`（有回归测试）。
- 安装目录位于 `Program Files` 下时覆盖需要管理员权限；建议引导用户解压到普通目录。
- 国内访问 `raw.githubusercontent.com` 可能不稳定，必要时可在 `DefaultManifestUrl` 改用镜像地址。

## 3. 发布与更新流程

### 3.1 发新版本（维护者操作）

```powershell
# 1. 修改 AssemblyInfo.cs 中的版本号（例如 1.0.0.2 → 1.1.0.0）
# 2. 运行打包脚本（自动完成：Release 构建 → 排除 pdb → 生成 zip → 计算 SHA-256 → 回写 update.xml）
.\pack-release.ps1 -RepoOwner <GitHub用户名> -Notes "1. xxx  2. xxx"
# 3. 在 GitHub 创建 Release：tag 填 v<版本号>（如 v1.1.0.0），附上 dist\Encodex-<版本号>.zip
# 4. 将脚本回写的 update.xml 提交到 main 分支（客户端读取的就是 main 上的这份）
```

产物位于 `dist/`（已 gitignore）：`Encodex-<版本>.zip`，文件位于 zip 根目录、不含 pdb。

### 3.2 首次发布前的一次性准备

1. `Services/AppUpdateService.cs` 中 `DefaultManifestUrl` 已指向 `AdonisZeng/Encodex` 仓库的 main 分支；
2. 运行 `pack-release.ps1 -RepoOwner AdonisZeng` 后，`update.xml` 中的下载链接会自动回写；
3. 仓库必须为 public（raw.githubusercontent.com 需要公开访问）。

### 3.3 客户端更新行为

- 入口：主界面右下角 🔄 按钮（`MainViewModel.CheckForUpdatesAsync`）；
- 比较规则：`update.xml` 的 `version` 大于本地程序集版本即提示更新；
- 失败处理：网络/解析/校验失败均以 `UpdateException` 的中文消息弹窗提示，不影响主功能；
- 下载显示进度百分比；下载完成并确认后应用才退出，避免误触导致转换中途中断。

## 4. 常用命令

```powershell
dotnet build Encodex.slnx -c Release          # 构建
dotnet test Encodex.Tests\Encodex.Tests.csproj -c Release   # 运行测试
.\pack-release.ps1 -RepoOwner <用户名> -Notes "<更新日志>"    # 打包发布
# CLI 无头转换（脚本/CI 集成；stdout 为 UTF-8，退出码 0=成功 1=有失败 2=参数错误）
.\bin\Release\net481\Encodex.exe --cli --src <文件夹> --target utf-8 [--out <目录> | --overwrite] [--ext .cs,.js]
```

## 5. 维护注意事项（踩坑记录）

- CLI 同步等待异步转换必须 `Task.Run(() => ConvertAsync(...)).GetAwaiter().GetResult()`：直接在 WPF 线程
  `GetResult()` 会因 DispatcherSynchronizationContext 死锁（单测线程无此上下文发现不了，须真实 exe 冒烟验证）。
- `net481` 限制：无 `System.Index`/`Range`（测试勿用 `list[^1]`）；无 `File.Move` overwrite 重载、
  无 `Path.GetRelativePath`（用 `PathHelper`）、无 `Stream.ReadExactly`；`Encoding.GetEncoding("utf-16LE")` 等别名可用。
- 短文本（几字节）Ude 检测不可靠（返回 null → 原样复制）；CLI/检测相关测试需用足够长的中文样本。
- 检测结果统一用对称编码名（`utf-16LE`/`utf-16BE`/`utf-32LE`/`utf-32BE`），转换端 `GetEncoding` 均能解析；
  `UnicodeEncoding(false).WebName` 是 `utf-16`（无 LE 后缀），勿用它做显示名。
- 更新器与更新服务属高危链路：改参数转义、路径穿越防护、SHA 校验、自替换回滚时必须保留/更新对应测试。
