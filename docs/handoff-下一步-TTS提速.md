# 交接提示词：先把 TTS 播放提速做完（阶段 1 + 阶段 2）

> **状态（2026-11-23）：阶段 1 与阶段 2 都已实现、构建、部署并真机验证完成。**
> 实测结果见文末「落地结果」；正文保留为当时的任务说明，不用再照着重做一遍。
> 阶段 3（主动台词预合成）、阶段 4（流式 LLM）仍未做。

> 使用方式：把下面「=== 提示词开始 ===」到「=== 提示词结束 ===」之间的内容整段粘贴给新窗口。
> 配套文档：`docs/主动对话与窥屏-需求整理与可行性.md`（第 5 章是 TTS 方案，5.9 是落地结果）。

---

=== 提示词开始 ===

## 你的任务

给 `D:\Code\Just_Lilith` 这个 BepInEx IL2CPP 插件做 **TTS 播放提速**，只做两件事，不要顺手改别的功能：

1. **阶段 1（低风险，先做）**：连接复用 + 首段小切片；
2. **阶段 2（核心）**：预取流水线（生产者/就绪队列，深度 1），把"播完一段才合成下一段"改成"播第 N 段时已经合成好第 N+1 段"。

完成后要：构建、部署到游戏、真机验证、更新文档、提交 git。

## 环境与工具

- 仓库：`D:\Code\Just_Lilith`（两个程序集：`Just_Lilith.Core`、`Just_Lilith.Unity`，net6.0，`LangVersion 14`）。
- **构建必须用 .NET SDK 10**：`D:\dotnet10\dotnet.exe`。系统 PATH 里只有 SDK 8，直接 `dotnet build` 会报 `CS1617`。
- 构建脚本（推荐）：`powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`
  - 无参数＝编译；`-Package`＝编译并出 `dist\Just_Lilith-0.3.0.zip`；`-Install`＝备份旧 DLL 后覆盖到游戏目录（游戏在运行时会拒绝，先关游戏）。
- 游戏目录：`E:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith`
  - 插件日志：`BepInEx\LogOutput.log`（UTF-8，用 `-Encoding UTF8` 读）
  - TTS 服务日志：`BepInEx\plugins\Just_Lilith\tts\logs\managed-service.log`（里面的 `JLTIME chars=.. syn=..s audio=..s rtf=..` 就是每段耗时）
- 不要动 `.mcp.json`（本机配置、未跟踪）；**不要 `git push`**（用户自己用 UGit 推）。提交前确认 `git status` 只有你的改动。

## 背景：现在的流程（已实现，别破坏）

- 回复按 `SpeechSplitSynthesizer.Split` 切成若干段（每段 ≤56 可读字）。
- **严格串行**：合成第 N 段 → 该段音频就绪后才显示第 N 段文字 → 播放 → 播完才开始合成第 N+1 段。
- 文字显示由 `LlmGameBridge.PresentSpeechChunk(messageId, index, count, text, holdSeconds)` 驱动；
  **调用前必须先 `_talkAction.OnSpeechStarted(段文本, 时长)`**——顺序反了游戏会结束对话、气泡被顶掉（上一轮踩过这个坑）。
- 实测（一次 8 段回复）：音频合计 74.9s、合成合计 48–55s、总耗时约 125s，其中 **40–50s 是段间空档**。
- 目标：总耗时降到 **85–90s**，段间空档 p50 < 1s，同时**不破坏**：文字等音频、逐段对齐、每段说话动画、取消打断、TTS 关闭回退、退出清理。

## 关键代码位置

- `Just_Lilith.Unity/Just_Lilith/Unity/Speech/MainThreadSpeechController.cs`
  - `Update()` 里三块：**播放结束分支**（约 243 行）、**`_readyWave` 呈现块**（约 283 行，含 talk action 与 `PresentSpeechChunk`）、**TTS 出队**（约 317 行）、**启动 pending**（约 405 行）。
  - `StartActiveChunk()`：给当前段建 `_operation` 并启动合成。
  - `RunChunkSynthesis()`：**每段都 `new LocalTtsClient(serviceUrl)`** ← 阶段 1 要改这里。
  - `Play(PcmWaveData)`：创建 `AudioClip`、`AudioSource.Play()`。
  - 字段：`_activeChunks / _activeChunkIndex / _readyWave / _talkStartedForChunk / _speechStartedReported / _activeRequest / _activeMessageId / _activeText`。
- `Just_Lilith.Unity/Just_Lilith/Unity/Speech/SpeechSplitSynthesizer.cs`：`Split(string)`、`SynthesizeChunkAsync(client, request, token)`、私有 `HardSplit`。
- `Just_Lilith.Unity/Just_Lilith/Unity/Game/LlmGameBridge.cs`：`MarkSpeechManaged`、`PresentSpeechChunk`、`ReleaseSpeechManaged`、`OnSpeechPlaybackStarted/Finished/Skipped`。
- `Just_Lilith.Core/Just_Lilith/Core/Speech/LocalTtsClient.cs`：构造函数里 `new HttpClient`（所以每段新建 = 每段新连接）。

## 阶段 1（先做，单独提交更好）

1. **连接复用**：控制器持有一个 `LocalTtsClient` 实例（整条消息或整个会话复用一个），`RunChunkSynthesis` 不再 new；注意它实现 `IDisposable`，不要在中途释放。
2. **首段小切片**：给 `Split` 加可选参数 `firstChunkMaxSpeakable`（建议 22–24）；首段超长时按标点再切一刀，保证"第一句"更快出现（预计首字提前 2–4s）。段数会 +1，`index/count` 要保持自洽。
3. **埋点**（后面验证靠它）：
   - 每段：`module=TTS; event=chunk_timing; index=i; synth_ms=..; audio_s=..; gap_before_ms=..`
   - 整条结束：`module=TTS; event=speech_sequence_done; chunks=n; total_s=..`

## 阶段 2（核心）

把"单 Task 状态机"换成"生产者 + 就绪队列"：

- 新增 `Just_Lilith.Unity/Just_Lilith/Unity/Speech/SpeechPipeline.cs`
  - `internal sealed class SpeechPipeline : IDisposable`
  - 构造参数：`(LocalTtsClient client, string serviceUrl, IReadOnlyList<string> chunks, SpeechLanguage language, SpeechDirective directive, int ahead = 1)`
  - 生产者：串行合成 `chunk 0..N-1`，**最多领先已播放段 `ahead` 段**（用 `SemaphoreSlim(ahead)` 做背压），成功后入 `ConcurrentQueue<ReadyChunk>`；失败记 `chunk_failed` 并跳过该段。
  - 对外：`bool TryTakeReady(out ReadyChunk chunk)`、`bool IsFinished`、`void Cancel()`。
- 控制器改造：
  - 开始一条消息：`Split` → 建 `SpeechPipeline` → 状态"正在合成第 1/M 段"。
  - `Update`：当 `_audio` 不在播放且 `TryTakeReady` 有就绪段 → `_talkAction.OnSpeechStarted(段文本, 时长)` → `PresentSpeechChunk(...)` → `Play(...)`；首段时补 `OnSpeechPlaybackStarted`。
  - 播放结束：还有段 → 立刻取下一段（**不再触发合成**）；生产者仍在跑但没就绪段 → 状态"等待第 N 段合成"。
  - 全部播完：`OnSpeechPlaybackFinished` + `ReleaseSpeechManaged` + `pipeline.Dispose()`。
  - 取消：`RequestStop` / `ShutdownFromMainThread` / 新消息（`_generation` 变化）/ `ClearPendingSpeech` → `pipeline.Cancel()`、清队列、`ReleaseClip()`。
- 硬约束：
  - **文字仍然只在对应音频就绪后显示**，用段号校验顺序，绝不提前播未显示的段。
  - Ready 队列有界（≤2 段，约 3MB）。
  - 单段失败只跳段，不整条中止；日志可定位。
  - VoiceFallback 的 `SuppressFor` 仍在 `PresentSpeechChunk` 内部，不要重复调用。

## 真机验证（必须做）

1. `.\build.ps1 -Install`（先关游戏），启动游戏；等日志出现 `TTS 服务已就绪`。
2. 发一条会产生长回复的消息。手动发送不方便时可以脚本化：
   - `keybd_event` 发 F7 打开聊天框；
   - `Set-Clipboard` + `SendKeys "^v"` 粘贴提示词；
   - 发送按钮坐标由日志 `event=chat_placement; anchor=(x,y)` 与窗口客户区算：
     `x = clientLeft + anchor.x + (780/2 - 122/2) * 0.899`
     `y = clientTop + clientBottom - (anchor.y + 54 * 0.899 / 2)`
     （`GetClientRect` + `ClientToScreen`；先 `SetWindowPos(HWND_TOPMOST)`，否则窗口被挡住点不到）。
3. 从日志确认：
   - `正在合成第 1/M 段` → `speech_chunk_shown index=1` …顺序正确；
   - `chunk_timing` 里 `gap_before_ms` 大多 < 2000；
   - `speech_sequence_done` 的 `total_s` 明显低于改造前的 ~125s（8 段）；
   - **无回归**：不出现 `bubble_hold_lost` / `speech_chunk_shown ... rebubble=1`；每段仍有 `TalkAction; event=started`；
   - 中断测试：播放中再发一条消息，旧音频立刻停、不会在新回复后冒出旧段。
4. 关游戏，确认 `python.exe` 归零（退出清理）。
5. `.\build.ps1 -Package` 出包，提交（消息里写清阶段与实测数字）。

## 验收标准

- 8 段样例总时长 **≤ 95s**（目标 85–90s），段间空档 p50 < 1s；
- 逐段"文字 / 音频 / 口型"对齐不回退，顺序不乱；
- 中断、TTS 关闭回退、退出清理均无回归；
- 构建 0 error，包能生成，已部署并真机验证，文档与提交齐全。

## 参考

- `docs/主动对话与窥屏-需求整理与可行性.md` **第 5 章**：方案对比、服务端 `BoundedSemaphore(2)` + `inference_lock` 约束、数字推算。
- 服务端不能并行：并行发请求只会排队，**预取深度 = 1 是上限也是最优**。

=== 提示词结束 ===

---

# 落地结果（2026-11-23，阶段 1 + 阶段 2 已完成）

## 改了什么

| 文件 | 改动 |
| --- | --- |
| `Unity/Speech/SpeechPipeline.cs`（新增） | `ReadyChunk` + `SpeechPipeline`：生产者线程串行合成、`SemaphoreSlim(ahead)` 背压、`ConcurrentQueue` 就绪队列、`TryTakeReady/IsFinished/Cancel` |
| `Unity/Speech/MainThreadSpeechController.cs` | 单 Task 状态机换成流水线；`BeginSequence/UpdateSpeechSequence/PresentAndPlay/FinishSequence/DisposePipeline`；复用 `LocalTtsClient`；`chunk_timing` / `speech_sequence_done` 埋点 |
| `Unity/Speech/SpeechSplitSynthesizer.cs` | `Split(text, firstChunkMaxSpeakable)`：首段 ≤22 可读字，超长按标点切一刀，后半段仍守 ≤56 |

## 实测（同一台机器、中文、`total_s` 从第 1 段合成起算到末段播完）

| 场景 | 段数 | 音频合计 | 总时长 | 等待开销 | gap p50 |
| --- | --- | --- | --- | --- | --- |
| 改造前（严格串行） | 9 | 86.69s | 133.62s | 46.93s（54%） | 5784ms |
| **预取深度 1** | 10 | 104.56s | **113.41s** | **8.85s（8.5%）** | **0ms** |
| 预取深度 1 | 6 | 69.97s | 74.64s | 4.67s（6.7%） | 0ms |
| 预取深度 1 | 4 | 48.18s | 52.44s | 4.26s（8.8%） | 0ms |

- 等待开销 **46.93s → 4.3–8.9s（约 -81%～-91%）**；除第 1 段（首次合成）和第 2 段外，
  其余每段 `gap_before_ms` 都是 **0**；
- 参考样本（8 段、音频 74.9s）推算 ≈ **81–85s**，满足 ≤95s；
- 无回归：段序正确、每段都有 `TalkAction; event=started`、无 `bubble_hold_lost` /
  `rebubble=1` / `chunk_failed` / `chunk_out_of_order`；TTS 关闭仍整段走文字；
  退出后 `python.exe` 归零（`MainThread; state=stopped; pending_cancelled=true`）。

## 仍然没做的

1. **"播放中发新消息立刻掐掉旧语音"没有实现**：旧的那条仍会播完，新回复排在后面。
   改造前就是这个行为，本次只保证**旧段不会冒到新回复后面**。要做得把聊天提交事件接到控制器上。
2. `——`/`……` 切出的 2–4 字极短段仍会让下一段合成溢出一次（4–6s 空档）；
   要消掉需要"短段合并"，会改断句与停顿，未动。
3. 阶段 3（主动台词预合成）、阶段 4（流式 LLM）未做。
