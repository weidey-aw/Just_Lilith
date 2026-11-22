# 交接提示词：实现「主动对话」（功能一 P0）

> 使用方式：把「=== 提示词开始 ===」到「=== 提示词结束 ===」整段粘给同一个编码窗口。
> 配套文档：`docs/主动对话与窥屏-需求整理与可行性.md` 第 1 章、第 6 章（Q5–Q15 已定默认值）。

---

=== 提示词开始 ===

## 任务

在现有插件里实现 **功能一「主动对话」的 P0 版本**：用户长时间不互动时，角色自己找由头说一轮，走与普通聊天完全相同的生成、气泡、TTS 管线。

本轮**只做主动对话**，不做窥屏（下一步单独做），不做音乐由头、特殊日子、到点提醒；给"外部事件队列"留一个空接口即可。

TTS 提速、逐段对齐、说话动画已经做完，**不要改它们**，只复用。

## 需要复用/参考的现状（先读一遍再动手）

- `LlmConfigurationController`
  - `Send(string input)`（约 804 行）→ `SendAsync(profile, revision, input, sentAtUtc, petState, generation, speechLanguage, speechEnabled, speechGeneration, operation)`（约 1055 行）。
  - `SendAsync` 的顺序：`_activeConversation.LoadOrCreate` → `EnsureCanAppend` → `_worldBook.Search(input)` → `DreamMemoryConsolidator.ConsolidateAsync` → `_conversation.LoadSystemPrompt()` + `ConversationContextBuilder.BuildRecent(session, input)` → `_client.CompleteAsync(...)` → `StructuredChatReply.Parse` → `_activeConversation.AppendCompletedTurn(...)` → `OnUi` 里构造 `OrdinaryChatOutput` → `_output.Publish(output)`。
  - 单锁：`_chatBusy`（`Send` 开头检查、`SendAsync` 结束复位）。
- `Just_Lilith.Core/Llm/ChatRole.cs`：**只有 `User`/`Assistant`**；`OpenAiCompatibleClient.cs:171` 用 `Role == User ? "user" : "assistant"`。
- `ConversationTurn(string User, string Assistant, DateTimeOffset UserOccurredAtUtc, DateTimeOffset AssistantOccurredAtUtc)`。
- `ActiveConversationWorkspaceStore`：`LoadOrCreate`、`EnsureCanAppend`、`AppendCompletedTurn`（原子写盘 + revision 校验）。
- `ConversationContextBuilder.BuildRecent(session, input)`：固定把 `input` 追加成 user，**不要改它的行为**。
- `LlmGameBridge`：`ShowThinking()` / `EndThinking()` / `Publish(output)` / `IsConversationActive` / `ClearPendingSpeech`。
- `MainThreadSpeechController.SpeechActive`（internal）。
- `LlmConfigurationView.Update()` 每帧调用 `_gameBridge.Update()`、`_speechController.Update()`；`_chatInput` 是私有字段。
- 游戏侧（反射，全部可空/失败即默认）：
  - `DialogueManager.IsOnceStoryPerforming`
  - `CharacterController.IsSleep` / `IsDrag` / `IsHiddenAway`
  - `GameForegroundDetector.IsGaming` / `Loose` / `GamingElapsedSeconds`
  - `WindowsNativeAPI.GetLastInputInfo(ref LASTINPUTINFO)`（拿到"距上次输入多少毫秒"）

## 需要新增/修改的文件

### Core（可单测，先写这部分）

| 文件 | 内容 |
| --- | --- |
| `Llm/ChatRole.cs` | 增加 `System`；同步改 `OpenAiCompatibleClient.cs:171` 的角色映射（`User→"user"`、`Assistant→"assistant"`、`System→"system"`），Responses 分支同理检查 |
| `Llm/ConversationTurn.cs` | 增加 `bool IsAmbient`（默认 false，作为 record 参数默认值，旧 JSON 缺字段即 false） |
| `Llm/ConversationContextBuilder.cs` | 新增 `BuildProactive(session, instruction)`：历史轮次照常，`IsAmbient` 的轮次渲染成"（她主动说的）…"前缀；最后追加一条 `ChatMessage(ChatRole.System, instruction)`，**不追加 user** |
| `Llm/ActiveConversationWorkspaceStore.cs` | 新增 `AppendAmbientTurn(sessionId, expectedRevision, assistant, assistantAt, expectedEventsFingerprint, expectedActivationToken)`：复刻 `AppendCompletedTurn` 的 revision 校验与原子写，`User=""`、`IsAmbient=true` |
| `Proactive/ProactiveSettings.cs` | record + 默认值 + `Validate()`（见下方配置） |
| `Proactive/ProactiveSettingsStore.cs` | JSON 读写（仿 `FoundationOptionsStore`：原子替换、坏文件保留、失败不抛到 UI） |
| `Proactive/InterestModel.cs` | 兴趣值、上限、概率衰减、`SolveIncrement(intervalSeconds, maxCount, periodSeconds)`（二分法，含 `U(a,2a)` 模型） |
| `Proactive/AmbientCue.cs` | `Source`（IdleChat/Music/Screen）、`Weight`、`Instruction`、`CreatedAt`、`ExpiresAt`、`Priority` |
| `Proactive/StagedCueQueue.cs` | 入队/清理过期/按优先级取一个/上限（建议 8 条） |

### Unity

| 文件 | 内容 |
| --- | --- |
| `Game/ProactiveContextProbe.cs`（新） | 反射读上面那些游戏信号；字符状态任一命中即返回"不可说话"；`GetLastInputInfo` 解析失败按"未知"处理（不要当挂机） |
| `Proactive/ProactiveChatService.cs`（新） | 30s tick 状态机 + 投放；`Update()` 由 view 每帧调用、内部节流；`NotifyUserMessage()` |
| `Proactive/ProactiveGenerator.cs`（新） | 按来源拼本次 `instruction`；P0 只有 IdleChat（"用户已经 23 分钟没说话…"），音乐/屏幕留 `switch` 分支抛 `NotSupported` 或直接返回 null |
| `Ui/LlmConfigurationView.cs`（改） | 新增 `internal bool IsChatInputFocused => _chatInput != null && _chatInput.isFocused;`；`AttachProactiveChat(ProactiveChatService)`；`Update()` 里调用 `_proactive?.Update()`；设置页加 3 行（总开关 / 期望间隔 / 最大次数），其余只读回显 |
| `Ui/LlmConfigurationController.cs`（改） | 新增 `internal bool IsGenerating => _chatBusy;`；新增 `internal void SendProactive(string instruction, Action<bool> onDone)`：复用 `SendAsync` 的初始化/解析/发布逻辑，但 **不写 user 消息**、用 `BuildProactive` + `AppendAmbientTurn`、不调用 `_view.SetReply`、失败只 `LogInfo/Warning` |
| `Unity/Plugin.cs`（改） | 主线程初始化里构造 `ProactiveChatService`（注入 controller/probe/settings/bridge/view），`_view.AttachProactiveChat(...)`；`Unload` 里释放 |

## 关键规则（实现前先对齐）

1. **单生成锁**：投放前检查 `IsGenerating`；进入生成后与聊天共用 `_chatBusy`。锁被占用 → 本轮放弃（不排队、不消耗兴趣）。
2. **没有旁白**：`instruction` 只进本次请求的 System 消息，**不落历史、不展示、不统计**；历史里只多一条 `IsAmbient=true` 的 assistant-only 轮次。
3. **思考气泡**：生成前 `ShowThinking()`；放弃/暂存/失败/异常必须 `EndThinking()`，不能卡在思考态。
4. **兴趣值**：
   - 范围 0..100，`≤50` 不掷骰；
   - 每 tick 增量 `U(a, 2a)`，默认 `a=4.8`（约 7.2 点/轮、5 分钟一次）；用户改"期望间隔"时用 `SolveIncrement` 重算并回显"每轮约 X–Y 点"；
   - 掷骰 `p = clamp((interest + stateBonus - 50)/50, 0, 1)`，**再乘 `max(0.15, (N-count)/N)`**（`decayProbability` 可关）；
   - 生成由头即：兴趣清零、count++；**没生成由头就不消耗**；
   - 用户发消息：兴趣/次数清零。
5. **暂存**：闸门不通过时入队（`expiresAt = now + 180s`）；每 tick 先清过期、再按优先级尝试投一个；过期只 log 不补说。
6. **投放闸门**（任一为真 → 不投）：`IsChatInputFocused`、`IsGenerating`、`_speech.SpeechActive`、`_bridge.IsConversationActive`、剧情播放中（`IsOnceStoryPerforming`）、她的状态（`IsSleep || IsDrag || IsHiddenAway`）。
7. **用户状态**：只读 `GetLastInputInfo` 的毫秒数与 `GameForegroundDetector` 的布尔量；不采样键鼠、不读窗口标题、不落盘。
   - 建议映射：`idle>120s → -15`；`IsGaming && idle<60s → +20`；`Loose && idle<60s → +10`；其余 0。
8. **配置**（`BepInEx/config/local.just_lilith.proactive.json`）：
   ```jsonc
   {
     "enabled": false,
     "maxPerUserMessage": 3,
     "targetIntervalSeconds": 300,
     "checkPeriodSeconds": 30,
     "stageExpireSeconds": 180,
     "stageLimit": 8,
     "decayProbability": true,
     "sources": { "idleChat": true, "music": false, "screen": false },
     "weights": { "idleChat": 60, "music": 10, "screen": 30 },
     "userState": { "idlePenalty": -15, "gamingBonus": 20, "browsingBonus": 10 },
     "testMode": { "enabled": false, "forceAfterSeconds": 20 }
   }
   ```
   `testMode` 是验收用的钩子：开启后忽略兴趣值，`forceAfterSeconds` 到点直接生成一条 `source=idleChat(test)` 的由头。
9. **日志埋点**（验收全靠它）：
   `module=Proactive; event=tick|roll|staged|delivered|expired|reset|skipped; source=..; interest=..; chance=..; count=..; cap=..; reason=..`
10. **失败静默**：未配置模型、网络失败、解析失败 → `event=skipped; reason=...`，不弹窗、不写 UI 错误。

## 验收（必须真机跑一遍）

1. **总开关关**：`enabled=false` 跑 3 分钟，日志无 `delivered`，也不应有 `tick` 里的探测副作用（可在 tick 入口直接 return）。
2. **测试钩子**：`enabled=true, testMode.forceAfterSeconds=20` → 约 20s 后 `delivered`；气泡出现、TTS 逐段播报、说话动画正常（复用现有管线，不应有回归）。
3. **兴趣值路径**：`targetIntervalSeconds=60`，观察 `roll` 的 `interest/chance` 递增、最多 3 次；第 2/3 次明显更慢（概率衰减生效）。
4. **输入中拦截**：打开聊天框并让输入框保持焦点 → 手动触发（`testMode`）→ `staged`；关闭面板/让语音播完后再触发 → `delivered`。
5. **暂存过期**：`stageExpireSeconds=10` + 输入框保持焦点 → `expired`，之后不再补说。
6. **未配置模型**：清空/不填 API → `skipped; reason=unconfigured`，无错误弹窗。
7. **历史**：会话文件中出现 `IsAmbient=true`、`User=""` 的轮次；会话列表/聊天记录不出现空白的用户行；`DreamMemoryConsolidator` 不把它当用户发言（可在日志或摘要内容里确认）。
8. **不打扰**：她在睡觉/被拖拽/隐藏时触发 → `skipped; reason=character_state`。
9. **退出**：关游戏，`python.exe` 正常退出（TTS 服务清理不受影响）。

## 边界与禁区

- 不要改 `ConversationContextBuilder.BuildRecent` 的现有行为；新增 `BuildProactive` 即可。
- 不要在主动路径调用 `EnsureCanAppend`（空 user 会触发校验）；自己校验 `expectedRevision`。
- 不要为主动对话新开锁；不要绕过 `_output.Publish` 自己弹气泡。
- 不要给主动台词加"旁白/系统提示"式 UI 元素。
- 不要做窥屏、音乐、提醒、特殊日子；`ProactiveGenerator` 留好扩展点即可。
- 不改 TTS/气泡/语音相关代码；不动 `.mcp.json`；不 push。

## 交付

- 分两次提交：① Core（ChatRole/ConversationTurn/Store/Proactive 纯逻辑 + 单测式验证）；② Unity 接线 + UI + 插件注册。
- 提交信息写清实测：`testMode` 触发、兴趣值 roll 数据、暂存/过期、历史 ambient 轮次。
- 最后 `.\build.ps1 -Package` 出包，并在 `docs/主动对话与窥屏-需求整理与可行性.md` 第 1 章补一节"实现状态（已完成/未做）"。

=== 提示词结束 ===
