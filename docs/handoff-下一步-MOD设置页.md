# 交接提示词：MOD 独立设置页（先 A 后 B）

> 配套阅读：`docs/主动对话与窥屏-需求整理与可行性.md`（功能一/二已实现）、
> `docs/handoff-下一步-主动对话.md`（历史交接风格）。

---

=== 提示词开始 ===

## 你的任务

把 Just_Lilith 这个 BepInEx IL2CPP 插件的设置从游戏本体设置里**拆出来**，做成一个独立的
**「MOD设置」**页，界面风格与原版一致，并且**不污染游戏本体设置**。

两阶段，**必须先 A 再 B**：

- **阶段 A（先做，可独立验收）**：只做**两个入口** + 一个**空壳 MOD 设置页**。
  页内只放"目前完全没有 UI 的两项"（主动对话、窥屏）+ 打开配置目录 + 状态回显。
  **不动**任何现有原生注入（API 页签、TTS 行、Lilith 行、热键行全部原样保留）。
- **阶段 B**：把散在游戏设置里的 mod 设置搬进独立页，旧注入用开关灰度，稳定后再删。

两个入口（A 阶段都要）：

1. **右下角系统托盘图标右键菜单**：在游戏原本的「设置」项**正下方**插入「MOD设置」。
2. **人物右键弹出的气泡菜单**：在菜单**最下方**追加「MOD设置」。

## 环境与工具

- 仓库：`D:\Code\Just_Lilith`（`Just_Lilith.Core`、`Just_Lilith.Unity`，net6.0，LangVersion 14）。
- **构建必须用 .NET SDK 10**：`D:\dotnet10\dotnet.exe`（PATH 里只有 SDK 8，直接 `dotnet build` 会 CS1617）。
- 构建脚本：
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`（编译）
  - `... -File .\build.ps1 -Install`（备份旧 DLL 后覆盖到游戏；**游戏在运行时会拒绝，先关游戏**）
  - `... -File .\build.ps1 -Package`（出 `dist\Just_Lilith-0.3.0.zip`）
- 游戏目录：`E:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith`
  - 插件日志：`BepInEx\LogOutput.log`（**每次启动会重建**，别用跨启动的字节偏移去 tail）
  - 游戏 interop：`BepInEx\interop\Assembly-CSharp.dll`（只有签名没有逻辑，够用）
- 反编译工具：`C:\Users\Administrator\.dotnet\tools\ilspycmd.cmd`
  - 例：`ilspycmd -t ShowSystemTray "<游戏>\BepInEx\interop\Assembly-CSharp.dll" -o <临时目录>`
  - 输出文件名是 `<Type>.decompiled.cs`；文件很大，用 `Select-String` 找，不要整读。
- **不要动** `.mcp.json`（本机配置、未跟踪）；**不要 `git push`**（用户自己推送）。
- `.gitignore` 里已经加了 `docs/reference MOD/The-NOexistenceN-of-Lilith-Mod-main`，别去改它。

## 现状盘点（动手前先读这几处）

| 位置 | 作用 | 备注 |
| --- | --- | --- |
| `Unity/Ui/LlmConfigurationView.cs`（2794 行） | 所有 UI 的宿主 | `_uiRoot`（Canvas 根）、`_settingsPanel`（自建 API 设置页，约 L1023）、`Update()`（约 L784）、`AttachSpeechController`（约 L2952）、`AttachProactiveChat`（约 L2957） |
| 同上：幻境对话框（约 L2615–L2656） | **独立窗口壳的现成范本** | 深色面板 + `Outline` + 标题 + 关闭 + 可拖动 + `NativeTrayPageSkin.TryApply` |
| `Unity/Ui/NativeTrayPageSkin.cs`（271 行） | 把自建控件贴成原版皮肤 | `TryApply(GameObject page, Transform settingItemRoot, out string diagnostic)`，从 `SettingPlanelPrefabs` 抄 `Item_EditBox` / `Item_ButtonBIg` |
| `Unity/Ui/NativeSpeechLanguageRows.cs`（595 行） | TTS 4 行注入到原版语音页 | **阶段 B 要迁走** |
| `Unity/Ui/NativeLilithConversationRows.cs`（757 行） | 人格/幻境/记忆/世界书 4 行 | **阶段 B 要迁走** |
| `Unity/Ui/NativeHotkeySettingsInjector.cs`（402 行） | 热键行 | **阶段 B 要迁走** |
| `Unity/Ui/LlmConfigurationView.MaintainNativeApiTab`（约 L1700–1840） | 把 `_settingsPanel` 作为**额外页签**塞进原版设置窗 | **阶段 B 要拆**；`ResolveTraySettings` 绑定 `UI.TraySettingNew.TraySettingNewView` |
| `Unity/Game/VoiceFallbackService.cs`（约 L196–L240） | **Harmony 用法范本** | `new Harmony(...)` + `PatchByArity(...)` → `harmony.Patch(method, prefix, null, null, null)` |
| `Unity/Plugin.cs` | 主线程装配 | 约 L125–L168 构造各模块；`Unload()` 约 L190 起释放 |
| `Core/Proactive/ProactiveSettingsStore.cs` | 主动/窥屏配置读写 | 服务按文件 mtime 热加载，所以 UI 直接 `Save` 即可生效 |

配置面（B 阶段要覆盖的）：`BepInEx/config/local.just_lilith*.json` 共 11 个
（llm / agent / tts / proactive / balance / localcloset / lyrics / music / music-convert / music_identity / 基础项）。

## 已核实的 API 事实（这次调研的成果，别再重新摸一遍）

### 托盘（入口 1）

- 游戏托盘是**真正的 Windows 通知区图标**：`SystemTray` 里有
  `Shell_NotifyIconW` / `RegisterClassExW` / `CreateWindowExW` / `CreatePopupMenu` / `AppendMenuW` / `TrackPopupMenuEx`。
  → 加进去的项由**游戏自己用 Win32 渲染**，风格 100% 原生，不用你画。
- `ISystemTray` 的公开接口：
  `SetTrayIcon(string)`、`SetTitle(string)`、`AddItem(string label, Il2CppSystem.Action)`、
  `AddSubMenu(string, IReadOnlyList<(string, Action)>)`、`AddSeparator()`、
  `ShowNotification(string title, string text, int durationMs)`、`Tick()`。
- `ShowSystemTray` 的公开成员（单例）：
  `static ShowSystemTray instance`、`ISystemTray tray`、`bool initialized`、
  **`List<MenuItemData> menuItems`**、`CreateTray()`、`AddAllMenuItemsSequentially()`（**IEnumerator**）、
  `AddMenuItemWithLocalization(MenuItemData)`（**IEnumerator**）、`OpenTraySettings_Btn()`、
  `RequestOpenTraySettings()`、`OnOpenTraySettingsRequested` 事件。
- `MenuItemData` 是 `ShowSystemTray` 的嵌套**结构体**，字段：
  `tableEntryKey`、`literalText`、`callback`、`isSeparator`、`children`。
  → **`literalText` 可以直接写中文，不需要本地化表**。
- `SystemTray` 内部还有 `_menuActions`（`List<Action>`）、`_hMenu`、`_nextMenuId`、`_menuOpen`，
  说明 `AddItem` 是**按调用顺序追加**的。

### 人物右键（入口 2）

- 右键气泡 = `PlayerLineController.ShowOptions(IReadOnlyList<string> options, Il2CppSystem.Action<int> onSelected, Il2CppSystem.Func<bool> validWhile = null)`，**public**。
  → 菜单项就是字符串列表，追加一项即可，UI 是游戏自己的选项控件。
- 右键入口 = `CharacterInteractionHandler.OpenChatPanel()`；右键请求先经过 `HandleHookChatRequest()`。
- **同一个 `ShowOptions` 还被这些菜单用**：`TryShowSleepOptions()`、`TryShowColdAskOptions()`、
  `NearWake*`、`OnGameChaperoneChoice()` 等。→ **必须做作用域限定**，否则"MOD设置"会出现在睡觉/叫醒菜单里。
- 历史佐证：本仓库旧版反编译 `_decomp/unity/Just_Lilith.Unity.Game/NativeClothingMenuEntry.cs`
  就 patch 过 `ShowOptions`(3 参) / `ShowEntryOptions`(2 参) / `ShowEntryOptionsPaged`(3 参)，
  说明这条路在这个游戏上走得通。

### Harmony

- 插件**已经在用** Harmony patch 游戏方法：`VoiceFallbackService.cs` 里
  `new Harmony("local.just_lilith.voice_fallback")` + `PatchByArity(harmony, dm, "Say", 6, ...)`。
- 运行时具备：`BepInEx\core\0Harmony.dll`、`Il2CppInterop.HarmonySupport.dll`。
- **复用同一套写法**（反射取 MethodInfo + `new HarmonyMethod(...)`），不要另起炉灶。

## 阶段 A：两个入口 + 空壳页（先做这个）

### A1. 页面壳

照抄幻境对话框（`LlmConfigurationView` 约 L2615）：

```csharp
GameObject page = RectObject("Just_Lilith.ModSettings.Dialog", _uiRoot.transform,
    new Vector2(0.5f,0.5f), new Vector2(0.5f,0.5f), new Vector2(860f, 700f), Vector2.zero);
AddImage(page, new Color(0.1f,0.1f,0.1f,0.98f)).raycastTarget = true;
Outline outline = page.AddComponent<Outline>();
outline.effectColor = Color.white; outline.effectDistance = new Vector2(2f, -2f);
GameObject controls = RectObject("NativeControls", page.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
CreateText(controls.transform, "Title", "MOD 设置", 22, TextAnchor.MiddleLeft, new Vector2(20f,-16f), new Vector2(600f,34f), FontStyle.Bold);
CreateButton(controls.transform, "CloseMod", "关闭", new Vector2(750f,-16f), new Vector2(90f,34f), CloseModSettings);
// …… A 阶段的内容（见 A3）……
NativeTrayPageSkin.TryApply(controls, <皮肤模板根>, out string diagnostic);
page.transform.SetAsLastSibling();
```

⚠️ **隐藏耦合（A 阶段就要解决）**：`NativeTrayPageSkin.TryApply` 需要传一个「原版设置页里的
item root」，它只用来找 `SettingPlanelPrefabs` 下的 `Item_EditBox` / `Item_ButtonBIg`。
现有代码传的是 `_lilithItemRoot`（Lilith 原生行的父级）——**阶段 B 拆掉 Lilith 行注入后这个根会没有**。
所以 A 阶段请把"皮肤模板根"独立解析出来（例如从 `UI.TraySettingNew.TraySettingNewView` 的
tab item root 取，或启动时缓存一次），不要依赖 `_lilithItemRoot`。
解析失败要能降级：页面仍能开，只记一条 `state=skin_unresolved` 日志。

### A2. 入口 1（托盘，插在「设置」正下方）

思路：**在游戏建菜单之前，往 `ShowSystemTray.menuItems` 里插一条**，让游戏自己渲染。

```csharp
// 主线程轮询（0.5s 一次即可）或 Harmony postfix ShowSystemTray.CreateTray
ShowSystemTray host = ShowSystemTray.instance;
if (host == null || !host.initialized || host.tray == null) return;

var items = host.menuItems;                      // Il2Cpp List<MenuItemData>
if (HasOurEntry(items)) return;                  // 幂等：按 literalText/tableEntryKey 查重
int idx = FindIndexByCallbackName(items, "OpenTraySettings_Btn");
if (idx >= 0)
{
    items.Insert(idx + 1, new ShowSystemTray.MenuItemData {
        tableEntryKey = "mod_settings",
        literalText  = "MOD设置",
        callback     = new Il2CppSystem.Action(OpenModSettings),
        isSeparator  = false
    });
}
else
{
    // 兜底：定位不到「设置」就追加到末尾（风格仍原生，只是位置不理想）
    host.tray.AddSeparator();
    host.tray.AddItem("MOD设置", new Il2CppSystem.Action(OpenModSettings));
}
```

要点：

- `OpenTraySettings_Btn()` 就是游戏「设置」项的回调，按
  `callback.Method.Name == "OpenTraySettings_Btn"` 定位最稳（`tableEntryKey` 的值未知）。
- **幂等**：重进游戏、切换语言、托盘重建都会再走一遍，别插出两条。
  `ShowSystemTray` 有 `OnLocaleChanged` / `AddAllMenuItemsSequentially`，最省事是**轮询 + 查重**。
- **兜底必须有**：插入失败不能让入口消失，宁可退到末尾。
- 时机：托盘要等 `initialized == true`；参考 Mod 还叠了 2.5s 延迟防抖，建议照做。

### A3. 入口 2（人物右键，追加到最下方）

```csharp
// 1) 作用域：只有右键那一次 OpenChatPanel 里才追加
[HarmonyPatch] static class OpenChatScopePatch {
    static void Prefix(CharacterInteractionHandler __instance)  => ModMenuScope.Enter();
    static void Postfix(CharacterInteractionHandler __instance) => ModMenuScope.Leave();
}

// 2) 追加：包一层列表 + 回调，然后自己调用一次原方法并跳过原调用
static bool ShowOptionsPrefix(PlayerLineController __instance,
                              ref Il2CppSystem.Collections.Generic.IReadOnlyList<string> options,
                              ref Il2CppSystem.Action<int> onSelected,
                              Il2CppSystem.Func<bool> validWhile)
{
    if (!ModMenuScope.Active || Reinjecting) return true;   // 不在右键里 / 防重入
    var original = onSelected;
    ModMenuScope.KeepAlive(original);                       // ← 必须保活，否则点菜单崩
    var list = new Il2CppSystem.Collections.Generic.List<string>();
    foreach (var s in options) list.Add(s);
    list.Add("MOD设置");
    int ours = list.Count - 1;
    var wrapped = new Il2CppSystem.Action<int>(index =>
    {
        if (index == ours) { OpenModSettings(); return; }
        original?.Invoke(index);
    });
    ModMenuScope.KeepAlive(wrapped);
    Reinjecting = true;
    try { __instance.ShowOptions(list, wrapped, validWhile); }
    finally { Reinjecting = false; }
    return false;                                           // 跳过原调用
}
```

要点与坑：

- **作用域**：`OpenChatPanel` 若是协程/延迟到下一帧才 `ShowOptions`，`Prefix/Postfix` 之间就来不及；
  实现成"进入帧 + 下一帧内有效"更稳（`Time.frameCount` 比较）。
- **委托保活**：`Il2CppSystem.Action<int>` 被 GC 掉会导致点菜单直接崩。原回调和新回调都要放进静态字段，
  菜单关闭后再释放。
- **防重入**：自己调 `ShowOptions` 会再次命中 Prefix，必须用 `Reinjecting` 挡住。
- `validWhile` 原样透传，不要丢。
- 只追加文案，不要自己画气泡——**不要**新建 UI，否则就不是"原生气泡"了。

### A4. A 阶段页内内容（刻意很小）

1. 主动对话：总开关、期望间隔（秒）、每条消息最多几次 —— 直接读写
   `ProactiveSettingsStore.LoadOrCreate/Save(ProactiveSettings.FileName)`；
   服务按文件 mtime 热加载，**不用改服务**。
2. 窥屏：开关（`sources.screen`）、权重（`weights.screen`）、视觉模型（`visionProfileId`）、
   5 分钟/每天上限 —— 同上。**未配置视觉模型要能提示**（只读回显即可）。
3. 一个「打开配置目录」按钮：`Process.Start("explorer.exe", Paths.ConfigPath)`。
4. 只读回显：版本号、主动/窥屏当前状态（可读 `ProactiveChatService` 的状态字段，或干脆显示配置文件路径）。
5. 标题写 **`MOD 设置`**（菜单项文案必须是 **`MOD设置`**，两个入口一致）。

### A5. A 阶段新增/修改文件（建议）

| 文件 | 内容 |
|---|---|
| `Unity/Ui/ModSettingsWindow.cs`（新） | 页面壳 + A4 内容；`Open()/Close()/IsOpen` |
| `Unity/Ui/ModSettingsMenuIntegration.cs`（新） | 托盘插位 + `ShowOptions` 追加 + Harmony 注册/卸载 |
| `Unity/Ui/LlmConfigurationView.cs`（改） | 暴露 `OpenModSettings()`、皮肤模板根；`OnDestroy/Unload` 里反注册 |
| `Unity/Plugin.cs`（改） | 主线程里构造并 attach；`Unload()` 里 `UnpatchSelf()` |

## 阶段 B：内容搬迁 + 去污染（A 验收通过后再做）

按这个顺序，**每一步都要能回退**：

1. **B1 门面**：给 MOD 页一个"设置读写门面"（LLM/Agent/TTS/热键/主动/窥屏），
   内部复用现有 controller / `SpeechSettingsStore` / `ProactiveSettingsStore`，不要复制业务逻辑。
2. **B2 搬迁（先加后拆）**
   - API/LLM/Agent：把现有 `_settingsContent`（3 套 profile + Agent 行）挂进 MOD 页 → 最简单。
   - TTS 4 项：用自建控件重画，逻辑复用 `SpeechRadioSelection` + `SpeechUiCallbacks`。
   - 幻境/人格 Prompt/幻境记忆/世界书：4 个按钮，幻境对话框已存在。
   - 热键：行重画，捕获逻辑复用 `ChatHotkeyController`。
   - 主动/窥屏：A 阶段已有，补回显（"每轮约 X–Y 点"）。
3. **B3 灰度**：加一个隐藏开关（配置文件即可）`ui.legacyNativeInjection = true/false`，
   默认 **true**（行为不变），验证新页稳定后翻成 false。
4. **B4 拆除**：删掉 API 页签挂载、TTS 行、Lilith 行、热键行；确认游戏自带设置页
   **页签数、行数、间距与改动前完全一致**（这是"不污染"的验收证据）。
5. **B5 回归**：F7 聊天、TTS 逐段、说话动画、主动对话、窥屏、幻境切换、热键、退出清理。

## UI 规范（两阶段都遵守）

- 文案：菜单项 **`MOD设置`**（无空格）；页面标题 **`MOD 设置`**。
- 风格来源只有两个：① 游戏原生控件（托盘项、右键项）；② `NativeTrayPageSkin` 贴过的自建控件。
  **不要**引入新字体/新配色，深色底板沿用幻境对话框的 `0.1,0.1,0.1,0.98` + 白色 2px Outline。
- 布局用固定像素 + `CanvasScaler`（现有页面都是这样），进程内不要用 `Screen.width` 做自适应。
- 页面必须可关闭（关闭按钮 + Esc 可选），关闭要销毁或隐藏，不能留残留面板挡输入。
- 设置项数量在 B 阶段会到 40+，建议**左侧分组 + 右侧内容**；A 阶段可以只做单列。

## 边界与禁区

- **不要改** TTS / 气泡 / 逐段对齐 / 说话动画（这些刚做完，别回退）。
- **不要改** 主动对话与窥屏的判定逻辑，只改它们的"设置入口"。
- 不要把游戏**本体设置**的控件挪走/改样式——目标是"不污染"，不是"重做原版设置"。
- 不要在托盘里加子菜单（`AddSubMenu`）来规避插位问题，用户要的就是「设置」正下方一项。
- 不要为右键菜单自绘 UI，也不要替换掉游戏的原菜单。
- 不新增网络请求、不写用户目录以外的文件。
- 不 `git push`；不动 `.mcp.json`；不提交 `docs/reference MOD/`。

## 验收（真机，必须逐条过）

**阶段 A**

1. 托盘右键 → 「MOD设置」紧贴游戏「设置」**正下方**，且**只有一条**。
2. 点它 → 打开 MOD 设置页；关闭后不留残留、不影响点击。
3. 人物右键 → 气泡**最下方**出现「MOD设置」；点它打开**同一个页面**。
4. **不误伤**：睡觉菜单（TryShowSleepOptions）、叫醒/陪坐/陪玩（NearWake / Chaperone）、
   受凉询问（ColdAsk）里 **都不出现**「MOD设置」。
5. 打开游戏本体设置：页签数/行数/间距与改造前**完全一致**（A 阶段没拆注入，应该本来就没变）。
6. 切换游戏语言、重开游戏 → 托盘项仍在且唯一（幂等生效）。
7. 日志里能看到入口注册/皮肤降级的诊断行，便于排障（建议 `module=ModUI; event=...`）。

**阶段 B**

8. 游戏本体设置页里**不再有**任何 mod 东西（页签、行、热键项都消失），页签数回到原版。
9. MOD 页内 6 块设置都能读能写、能立即生效（TTS 开关、语言、参考风格；热键重绑；主动/窥屏开关与数值）。
10. 回归：F7 聊天、TTS 逐段播放、说话动画、主动对话触发、窥屏触发、幻境列表、退出后 `python.exe` 归零。
11. 旧开关 `legacyNativeInjection=true` 时行为与改造前一致（回退能力可用）。

## 排期参考

| 阶段 | 内容 | 工期 |
|---|---|---|
| A 前置 spike | 托盘插位 + 右键追加的真机验证（各半天） | 1–1.5 人日 |
| A | 两个入口 + 空壳页 + 主动/窥屏两项 | 2–2.5 人日 |
| **A 合计** | | **3–4 人日** |
| B | 门面 + 6 块搬迁 + 灰度 + 拆注入 + 回归 | 6–8 人日 |

## 踩过的坑（直接复用，别再踩一遍）

- **读源码用 read 工具**，不要用 `Get-Content`（PS 5.1 会按 GBK 解 UTF-8，中文变乱码）。
- 提交长中文消息：`git commit -m` 在 PowerShell 下会被拆坏 → 把消息写进文件再 `git commit -F <file>`
  （用 `[IO.File]::WriteAllText(path, text, New-Object System.Text.UTF8Encoding($false))` 避免 BOM）。
- `build.ps1 -Install` 在游戏运行时直接抛错，先关游戏（`CloseMainWindow()` 后等 8–10s）。
- `BepInEx\LogOutput.log` 每次启动重建，**不要**用跨启动的字节偏移 tail。
- 真机发消息（验证 UI 交互时会用到）：F7 开关聊天框；`Set-Clipboard` + `SendKeys "^v"` 粘贴；
  发送按钮坐标
  `x = clientLeft + anchor.x + (780/2 - 122/2) * 0.899`、
  `y = clientBottom - (anchor.y + 54 * 0.899 / 2)`，
  `anchor` 从日志 `event=chat_placement` 取；先 `SetWindowPos(HWND_TOPMOST)` 再点。
- 截图取证：`Graphics.CopyFromScreen` 存 PNG 后用图像工具看（验证页面观感最直接）。
- 托盘接口是 IL2CPP 对象：所有跨帧持有的 `Il2CppSystem.Action` / 委托**必须静态保活**。
- **委托转换写法要按当前 interop 版本来**：不需要 `new Il2CppSystem.Action(...)`，通常直接
  `(Il2CppSystem.Action)(System.Action)OnClick` 更稳（参考 Mod 里是
  `_showChat ??= (System.Action)(() => ...)` 然后当 `Il2CppSystem.Action` 传参）。
  先写一个 3 行 demo 确认能编译能跑，再写正式逻辑。
- **构造 `MenuItemData`（Il2Cpp 值类型）时字段要全量赋值**（`isSeparator`、`children` 别忘），
  而且要先确认它有没有 `()` 无参构造可直接 `new`；没有的话走 `Activator.CreateInstance` 再赋值。
  这一步最容易翻车，**先写最小 demo（只插一条 no-op 项）验证能渲染，再接入**。
- `ShowSystemTray.menuItems` 是 Il2Cpp 的 `List<MenuItemData>`，`Insert` 可用，但**不要跨帧持有它**，
  每次用之前重新取一次 `ShowSystemTray.instance`（托盘可能被销毁重建）。

=== 提示词结束 ===

---

## 实现状态（阶段 A 已落地，2026-11-23）

真机验收跑过一轮，**入口按用户要求改成了「只保留托盘一个，且放最底下」**（原计划的「设置正下方」+「人物右键」暂不做）。

### 交付

| 文件 | 内容 |
| --- | --- |
| `Unity/Ui/ModSettingsWindow.cs`（新） | 「MOD 设置」页壳（深色面板 + 白色 2px Outline + 可拖动标题条 + Esc/关闭按钮）+ 主动对话/窥屏两组设置 + 打开配置目录 + 状态回显 |
| `Unity/Ui/ModSettingsMenuIntegration.cs`（新） | 托盘入口：`AddSeparator()` + `AddItem("MOD设置")` 追加到菜单最底下；Harmony 静默检测重建（切语言/重进游戏）后自动补一次 |
| `Unity/Ui/LlmConfigurationView.cs`（改） | `OpenModSettings()`、独立解析皮肤模板根、MOD 页输入框并进 `_allInputs`、`Update()` 里 tick、`CleanupOwnedUi()` 里 dispose |
| `Unity/Plugin.cs` | **未改**（入口全部由 view 自己装配，`Dispose` 里 `UnpatchSelf()`） |

页面内容（A4）：主动对话（总开关/期望间隔/每条上限）、窥屏（开关/权重/视觉模型/最小间隔/每天上限）、
打开配置目录、重新载入、状态回显（当前值 + 配置文件路径与最后修改时间 + 视觉模型未配置提示）。
读写直接走 `ProactiveSettingsStore`，服务按文件 mtime 热载入，**没有改服务代码**。

### 与原提示词的差异（有意为之）

- 托盘项**放在菜单最底下**（`[打开我的文件夹][呼唤莉莉丝][设置][再见][---][MOD设置]`），不做「设置正下方」。
- **只有一个入口**：人物右键气泡的 `ShowOptions` 追加（A3）本轮未做，`OpenChatPanel` / `ShowOptions` 的 patch 也一并去掉了。

### 实测记录（真机）

- 托盘菜单 dump：`count=6; [0:打开我的文件夹][1:呼唤莉莉丝][2:设置][3:再见][4:---][5:MOD设置]`
  —— 在底部、只有一条；切语言（`Settings` → `设置`）重建菜单后仍是唯一一条。
- 点托盘项 → `module=ModUI; event=opened`；`state=skin_applied; templates=resolved; buttons=8; inputs=5`（原版皮肤贴上）。
- 主动总开关：点一次 → 配置 `enabled` 翻转、`event=settings_saved`、服务侧 `state=settings_reloaded; enabled=1`（热载入生效）。
- 视觉模型循环：`"" → profile-1 → profile-2 → profile-3 → ""`，显示为 `配置 1 (profile-1)`。
- 数值校验：`期望间隔` 填 `5` 保存 → `state=edit_rejected; message=期望间隔：范围是 30–3600，当前 5`，不写盘。
- 关闭：`event=closed; reason=button`；Esc 关闭记为 `reason=escape`；关掉后无残留面板挡点击。
- 游戏本体设置未动（A 阶段本就没拆任何原生注入），页签/行数不变。

### 已知限制 / 下一步

- 托盘项在菜单建完约 0.6s 后追加，刚启动那一会儿立刻右键可能还看不到；之后一直在。
- 插件卸载时 `UnpatchSelf()` 已做，但已经追加进 Win32 菜单的那一条不会被移除（进程退出即消失）。
- 阶段 B 未开始：API/TTS/幻境/热键四块仍在游戏本体设置里，`legacyNativeInjection` 灰度开关、拆注入、回归都还没做。

### 复用到的 API 事实（补充）

- 托盘菜单是 `ShowSystemTray` 把 `menuItems` 逐条喂给 `ISystemTray.AddItem` 建出来的；
  **运行时 `AddSeparator()` / `AddItem()` 会直接改 Win32 菜单**，比往 `menuItems` 里 `Insert` 可靠得多
  （实测 `MenuItemData` 插进 `List<MenuItemData>` 后 `literalText` 回读失败，也没渲染出来）。
- 给 `WindowsSystemTrayAdapter.AddItem` 挂**后缀**可以精确知道「游戏刚重建过菜单」，用来触发幂等追加；
  注意自己调 `AddItem` 也会经过这个后缀，必须加 `_appendInFlight` 挡自激（否则 0.5s 一次无限追加）。

