# 构建与打包

Just_Lilith 由两个 .NET 程序集组成，都是 BepInEx IL2CPP 插件：

| 程序集 | 作用 | 依赖 |
| --- | --- | --- |
| `Just_Lilith.Core` | 对话、记忆、语音、Agent 通道等与引擎无关的逻辑 | 仅 BCL（`net6.0`） |
| `Just_Lilith.Unity` | BepInEx 入口、U 与游戏内桥接 | `Just_Lilith.Core` + BepInEx 与游戏 interop 程序集 |

## 环境要求

1. **.NET SDK 10 或更高**。
   两个项目都写了 `<LangVersion>14.0</LangVersion>`，SDK 8 的 Roslyn 会直接报
   `error CS1617: /langversion 的选项"14.0"无效`。
   没有 SDK 10 时可以不装系统范围，装到单独目录即可：

   ```powershell
   Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
   .\dotnet-install.ps1 -Channel 10.0 -InstallDir D:\dotnet10 -NoPath
   ```

2. **一份已安装并能正常启动的游戏**，用来提供 BepInEx 的 `core` 与 `interop` 程序集。
   本项目编译需要其中这些文件：
   - `BepInEx\core\BepInEx.Core.dll`、`BepInEx.Unity.IL2CPP.dll`、`Il2CppInterop.Runtime.dll`、`0Harmony.dll`
   - `BepInEx\interop\Assembly-CSharp.dll`、`UnityEngine.*.dll`、`Il2Cppmscorlib.dll`、`Unity.TextMeshPro.dll`

   `interop` 目录是 BepInEx 在这台机器上**第一次成功启动游戏后**生成的，仓库里不包含，
   所以不能只靠源码离线编译 `Just_Lilith.Unity`。

3. Windows PowerShell 5.1 及以上（`build.ps1` 用到了 `Compress-Archive`）。

## 一键构建

```powershell
# 编译两个程序集
.\build.ps1

# 编译并生成发布包 dist\Just_Lilith-0.3.0.zip
.\build.ps1 -Package

# 编译并覆盖到游戏插件目录（会先备份被覆盖的 DLL）
.\build.ps1 -Install

# 指定 SDK / 游戏 BepInEx 目录
.\build.ps1 -Package -Dotnet D:\dotnet10\dotnet.exe `
             -BepInExDir 'E:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith\BepInEx'
```

`build.ps1` 的查找顺序：

| 参数 | 顺序 |
| --- | --- |
| `-Dotnet` | 参数 → `D:\dotnet10\dotnet.exe` → `$env:DOTNET_ROOT` → `%LOCALAPPDATA%\Microsoft\dotnet` → `C:\Program Files\dotnet` → `PATH` |
| `-BepInExDir` | 参数 → `$env:LILITH_BEPINEX_DIR` → 内置的常见 Steam/YYGameBox 路径 |
| `-GameDir` | 参数 → `$env:LILITH_GAME_DIR` → 由 `BepInExDir` 的上级目录推断 |

找不到 SDK 10 或 `interop` 时脚本会直接给出可执行的下一步提示，不会留下一半的构建产物。

## 手动构建

```powershell
dotnet build Just_Lilith.Core\Just_Lilith.Core.csproj -c Release
dotnet build Just_Lilith.Unity\Just_Lilith.Unity.csproj -c Release `
       -p:LilithBepInExDir="E:\...\The NOexistenceN of Lilith\BepInEx"
```

`Just_Lilith.Unity.csproj` 里的 `LilithBepInExDir` 默认值是 `..\..\Just_Lilith\BepInEx`
（即仓库上一级的 `Just_Lilith\BepInEx`），在别的目录布局下需要用 `-p:` 或环境变量覆盖。

## 产物与发布包

编译产物：

```text
Just_Lilith.Core\bin\Release\net6.0\Just_Lilith.Core.dll
Just_Lilith.Unity\bin\Release\net6.0\Just_Lilith.Unity.dll
```

`-Package` 生成的 zip 与官方发布包结构一致：

```text
Just_Lilith\BepInEx\plugins\Just_Lilith\Just_Lilith.Core.dll
Just_Lilith\BepInEx\plugins\Just_Lilith\Just_Lilith.Unity.dll
```

完整运行环境（`winhttp.dll`、`doorstop_config.ini`、`dotnet\`、`BepInEx\core`、TTS 运行时与模型）
由发布包的 framework 与 TTS 组件提供，不在本仓库构建范围内。

## 已知注意点

- **这份源码与商店里安装的 `0.3.0-ext.*` 插件不是同一份构建。**
  安装版的 `Just_Lilith.Core.dll` 里还有 `MemoryBook`、`ConversationArchive`、`DayLog` 等本仓库没有的类型，
  文件也大得多。用本仓库的产物覆盖游戏目录，会把这些功能一起换掉；需要保留时先备份。
- 仓库按发布习惯跟踪了 `bin/Release` 与 `obj/Release`，重新编译后它们会出现在 diff 里。
  `dist\`、`bin\Debug`、`obj\Debug`、`obj\pkg`、`_backup\` 已在 `.gitignore` 中忽略。
- `-Install` 把被覆盖的旧 DLL 备份到 `BepInEx\plugins-backup\<时间戳>\`，**不要**备份进
  `BepInEx\plugins\`：BepInEx 6 会递归扫描 plugins 目录，备份中的同名程序集会被当作重复插件
  （日志出现 `Skipping [Just_Lilith 0.3.0] because a newer version exists`）。游戏运行中会拒绝覆盖。
- 编译时的 `CS8632`（`#nullable` 上下文外的可空注解）、`CS4014`（未 await）等警告是既有代码的，
  不影响产物，也不建议为了消警告大面积改源码。
