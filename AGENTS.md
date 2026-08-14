# AGENTS.md

本文档面向 AI 编码助手与新加入的开发者，描述 Encodex 项目的整体情况与版本发布流程。

## 1. 项目概况

**Encodex** 是一个 Windows 桌面工具，用于批量扫描项目文件夹中的文本文件、自动检测其编码（BOM / 空字节启发式 / Ude 统计检测），并统一转换为目标编码（UTF-8、UTF-8 BOM、GBK 等）。

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
│   ├── Models/               ConversionStatus、EncodingOption、ExtensionOption、FileConversionItem
│   ├── Services/             FileScanner（扫描）、EncodingDetector（检测）、EncodingConverter（转换）、
│   │                         ExtensionProfile（扩展名配置）、AppSettingsStore（设置持久化）、
│   │                         PathHelper、AppUpdateService（联网更新检查）
│   ├── ViewModels/           MainViewModel（驱动全部界面行为）
│   ├── MainWindow.xaml(.cs)  主界面（四个 Tab：配置 → 文件列表 → 转换 → 报告）
│   ├── App.xaml(.cs)         启动入口：注册编码提供程序、恢复主题
│   └── AssemblyInfo.cs       程序集元数据；版本号唯一来源（见第 3 节）
├── Encodex.Updater/          独立更新器（控制台，net481），见第 2 节
└── Encodex.Tests/            xUnit 测试，经 ProjectReference 引用主项目
```

### 关键约定

- 版本号**只维护在** `AssemblyInfo.cs` 的 `AssemblyVersion` / `AssemblyFileVersion`，未使用自动版本注入。
- 用户设置存放在 `%APPDATA%\Encodex\settings.json`（不在程序目录），覆盖式更新不会丢失用户配置。
- 主项目通过 `Compile Remove` 排除 `Encodex.Tests/**` 与 `Encodex.Updater/**`；Updater 通过 `ProjectReference` 编译进主程序输出目录。
- 目标机器需预装 .NET Framework 4.8.1 运行时（框架依赖部署，不支持单文件发布）。

## 2. 联网更新机制

采用 **update.xml 清单 + 独立更新器 + zip 整目录覆盖** 方案：

```
用户点击 🔄 检查更新
  → AppUpdateService 拉取 update.xml，比较版本
  → 发现新版：提示 → 下载 zip 到 %TEMP% → SHA-256 校验
  → 启动 Encodex.Updater.exe（参数：主进程PID、zip路径、安装目录）后退出
  → Updater 等待主进程退出（文件锁释放）→ 解压覆盖安装目录 → 重启主程序
```

三个组成部分：

| 组件 | 位置 | 职责 |
|---|---|---|
| `update.xml` | 仓库根目录，经 `raw.githubusercontent.com` 公开访问 | 声明最新版本号、下载链接、SHA-256、是否强制更新、更新日志；每次发版由打包脚本重写 |
| `AppUpdateService` | `Services/AppUpdateService.cs` | 拉取并解析清单、版本比较、下载校验、启动 Updater。清单地址为常量 `DefaultManifestUrl`，仓库迁移时需同步修改 |
| `Encodex.Updater.exe` | `Encodex.Updater/Program.cs` | 进程退出等待、zip 解压覆盖（含路径穿越防护）、自身改名式自替换（`.upd-old` 残留下次运行清理）、重启主程序 |

注意事项：

- 运行中的 exe 无法覆盖自身，故替换必须由独立的 Updater 进程完成；zip 必须整目录包含 exe、全部依赖 DLL、`Encodex.exe.config` 与 `Encodex.Updater.exe`。
- 安装目录位于 `Program Files` 下时覆盖需要管理员权限；建议引导用户解压到普通目录。
- 国内访问 `raw.githubusercontent.com` 可能不稳定，必要时可在 `DefaultManifestUrl` 改用镜像地址。

## 3. 发布与更新流程

### 3.1 发新版本（维护者操作）

```powershell
# 1. 修改 AssemblyInfo.cs 中的版本号（例如 1.0.0.0 → 1.1.0.0）
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
- 下载完成并确认后应用才退出，避免误触导致转换中途中断。

## 4. 常用命令

```powershell
dotnet build Encodex.slnx -c Release          # 构建
dotnet test Encodex.Tests\Encodex.Tests.csproj -c Release   # 运行测试
.\pack-release.ps1 -RepoOwner <用户名> -Notes "<更新日志>"    # 打包发布
```
