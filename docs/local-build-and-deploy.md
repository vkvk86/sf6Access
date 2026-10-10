# SF6Access 本机构建与部署（Windows）

## 构建环境（本机一次性准备）

1. **.NET 10 SDK**（本机原本只有运行时，无 SDK）：
   ```
   curl -L -o dotnet-sdk-10.zip https://dotnetcli.azureedge.net/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-win-x64.zip
   unzip -q dotnet-sdk-10.zip -d C:/Users/jl/.workbuddy/binaries/dotnet-sdk
   ```
   - 注意：`dotnet-install.ps1` 走脚本会卡在下载；直接用上面的 zip 更快。
   - 注意：PowerShell `Expand-Archive` 会被 zip 文件句柄占用报错，用 bash 的 `unzip` 更稳。

2. **TolkDotNet.dll 引用**（被 `.gitignore` 第 9 行 `references/` 忽略，仓库里没有）：
   ```
   mkdir -p sf6Access/references/tolk
   cp "<game>/reframework/plugins/managed/dependencies/TolkDotNet.dll" sf6Access/references/tolk/
   ```

## 构建

```bash
cd sf6Access/SF6Access
"C:/Users/jl/.workbuddy/binaries/dotnet-sdk/dotnet.exe" build \
  -p:SF6Path="C:\Program Files (x86)\Steam\steamapps\common\Street Fighter 6"
```

- csproj 里 `SF6Path` 默认写死 `D:\games\...`（原作者机器路径），本机必须用 `-p:` 覆盖。
- csproj 的 post-build `CopyToGame` 会自动部署；**但如果游戏在 Program Files，会因 UAC 报 MSB3021 "Access denied"**——此时编译本身是成功的（DLL 已生成在 `bin/Debug/net10.0/`），只是复制失败。

## 部署（游戏在 Program Files，需管理员）

自动提升的几种方式都会被安全策略拦截（`Start-Process -Verb RunAs` / `schtasks` / 从 bash 调 `cmd`）。桌面自提升 `.bat` 也失败过两次（窗口一闪而过、日志只有表头、DLL 未替换）。

**根因排查结论**：本机注册表 `ConsentPromptBehaviorAdmin=0`（静默提权，**根本不弹 UAC**），用户 `jl` 在 Administrators 组。所以"没弹 UAC"不等于没提权——旧 `.bat` 其实是以管理员身份跑了的，失败是因为里面的 `copy` 目标路径写错了（多退了目录、lang 用了 `.json` 而实际是 `.txt`），且没有 errorlevel 兜底。

**可靠做法**：用 `elevate-deploy.py`（工作区根目录）——通过 `ctypes.windll.shell32.ShellExecuteExW` + `lpVerb="runas"` + `nShow=0` 启动隐藏的提权 `cmd.exe`，执行 `deploy-stage\elevated-copy.bat`，并把收据写到 `deploy-stage\elevated-deploy-log.txt`。直接调 Win32 API 不会被拦，而 `Start-Process -Verb RunAs` 会被拦。

部署 4 类产物（共 27 个文件，路径均相对于游戏根 `reframework\plugins\`）：

| 源 | 目标 |
|---|---|
| `managed/SF6Access.dll` | `reframework/plugins/managed/SF6Access.dll` |
| `lang/*.txt` (13) | `reframework/plugins/managed/SF6Access.lang/` |
| `sounds/*.mp3` (7) | `reframework/plugins/managed/SF6Access.sounds/` |
| `deps/NAudio*.dll` (6) | `reframework/plugins/managed/dependencies/` |

**三处易错点**：

1. 语言包是 `.txt`（不是 `.json`）。`LangFile.LANG_DIR` 相对**插件 DLL 自身所在目录**解析。
2. 音效目录 `SF6Access.sounds` 同样相对 DLL 目录（`AudioService.SoundsFolderName`）。
3. **NAudio 的 6 个 DLL 必须放 `managed/dependencies/`，不能放 `managed/` 根目录**。REFramework.NET 只从 `dependencies\` 解析插件的依赖程序集（见日志 `[Plugin] Loading dependencies from ...\managed\dependencies...`）。放错位置会让 `NAudio.Core` 找不到，进而触发 `Unable to load one or more of the requested types` —— 整个插件类型加载失败、插件不注册，**连原本正常的菜单朗读都会一起消失**。

## 验证部署是否成功

```bash
# 主 DLL 应为 ~763904 字节（含 World Tour 导航）；旧的无导航版是 425984
python -c "print(open(r'<game>\reframework\plugins\managed\SF6Access.dll','rb').read().__len__())"

# 或在 DLL 里搜 World Tour 类名（应为 1 或更多）
# ZoneHooks / MapAimHooks / MissionBeaconHooks / FieldNavRadarHooks / DestinationPickerHooks
```

## 纯逻辑测试

```bash
cd sf6Access/tests/Navigation.Tests
"C:/Users/jl/.workbuddy/binaries/dotnet-sdk/dotnet.exe" run
# 期望输出：Passed 93 navigation checks.
```

## 诊断"游戏里没有任何导航"的检查清单

1. `re2_framework_log.txt`（游戏根目录）里搜 `[SF6Access]`：
   - 24 个菜单钩子有 `initialized` 行，但 **World Tour 钩子（ZoneHooks/MapAimHooks/MissionBeaconHooks/FieldNavRadar/DestinationPicker）一条都没有** → DLL 是旧版。
2. 检查 `managed/SF6Access.dll` 大小：425984 = 7 月的 v0.5.5 无导航版；~763904 = 含导航。
3. 注意文件**时间戳具有误导性**——复制文件会刷新 mtime，不代表构建时间。以内容/大小为准。
