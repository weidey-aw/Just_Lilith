using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Just_Lilith.Core.Contracts;
using Just_Lilith.Core.Llm;
using Just_Lilith.Core.Speech;
using Just_Lilith.Unity.Game;
using Just_Lilith.Unity.Ui;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Just_Lilith.Unity.Speech;

public sealed class MainThreadSpeechController
{
	private sealed record PendingSpeech(SpeechSynthesisRequest Request, DateTimeOffset CreatedAt, long Generation);

	private const int QueueLimit = 3;

	/// <summary>首段可读字上限：第一句短一点，第一段音频能更早出现（0＝不限制）。</summary>
	private const int FirstChunkMaxSpeakable = 22;

	private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(45.0);

	private readonly LlmGameBridge _bridge;

	private readonly LlmConfigurationView _view;

	private readonly SpeechSettingsStore _store;

	private readonly ManualLogSource _log;

	private readonly ITtsServiceManager _service;

	private TtsServiceState? _lastServiceState;

	private readonly Queue<PendingSpeech> _pending = new Queue<PendingSpeech>();

	private readonly ConcurrentQueue<Action> _mainActions = new ConcurrentQueue<Action>();

	private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

	private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);

	private readonly object _preferenceGate = new object();

	private Task<SpeechSettings>? _loading;

	private Task _lastSave = Task.CompletedTask;

	/// <summary>当前这条回复的预取流水线（阶段 2）：生产者合成、主线程取就绪段播放。</summary>
	private SpeechPipeline? _pipeline;

	/// <summary>已经从队列取出、但这一帧还没能推上气泡的段（PresentSpeechChunk 失败要重试）。</summary>
	private ReadyChunk? _pendingPresent;

	/// <summary>下一条要呈现的段用第几个显示序号（0 起）。跳段时序号仍然连续，保证首段走 index=0 分支。</summary>
	private int _nextPresentIndex;

	/// <summary>已经呈现过的最大源段号，用来校验段序、挡住重复/倒退的段。</summary>
	private int _lastSourceIndex = -1;

	/// <summary>"等待第 N 段合成"文案的去重，避免每帧刷 UI。</summary>
	private int _waitingStatusIndex = -1;

	private Guid _activeMessageId;

	private string? _activeText;

	private SpeechSynthesisRequest? _activeRequest;

	private IReadOnlyList<string> _activeChunks = Array.Empty<string>();

	private int _activeChunkIndex;

	private bool _speechStartedReported;

	private bool _talkStartedForChunk;

	private readonly TalkActionDriver _talkAction;

	/// <summary>整条消息复用的 TTS 客户端（阶段 1：连接复用，不再每段 new 一个 HttpClient）。</summary>
	private LocalTtsClient? _ttsClient;

	private string _ttsClientUrl = "";

	/// <summary>服务地址变了之后退役的客户端：等手上没有在跑的合成再释放。</summary>
	private LocalTtsClient? _retiredTtsClient;

	private double _sequenceStartedAt = -1.0;

	/// <summary>播放器开始空闲的时刻（段间空档从这里算起）。</summary>
	private double _playbackIdleSince = -1.0;

	/// <summary>上一帧是否在播放：用来捕捉"刚播完"这一帧。</summary>
	private bool _audioWasPlaying;

	private double _sequenceAudioSeconds;

	private int _sequenceChunkCount;

	private GameObject? _audioObject;

	private AudioSource? _audio;

	private AudioClip? _clip;

	private long _generation;

	private int _expiredCount;

	private int _stopped;

	private bool _loaded;

	private bool _shutdown;

	private DateTimeOffset _ignoreBefore = DateTimeOffset.MinValue;

	private bool _deferredEnable;

	private DateTimeOffset _deferredEnableDeadline;

	private DateTimeOffset _syncSeenAt = DateTimeOffset.MaxValue;

	private static readonly TimeSpan DeferredFallbackDelay = TimeSpan.FromSeconds(40.0);

	private static readonly TimeSpan SyncGracePeriod = TimeSpan.FromSeconds(20.0);

	private string _status = "正在载入 TTS 服务偏好；首次默认关闭。";

	public SpeechSettings Settings { get; private set; } = new SpeechSettings();

	internal bool SettingsLoaded => _loaded;

	public long Generation => Volatile.Read(ref _generation);

	public bool CanSpeak
	{
		get
		{
			if (_loaded && Settings.Enabled && Settings.ServiceEnabled && _service.State.Phase == TtsServicePhase.Ready)
			{
				return Volatile.Read(ref _stopped) == 0;
			}
			return false;
		}
	}

	internal bool SpeechActive
	{
		get
		{
			if (_pipeline != null)
			{
				return true;
			}
			AudioSource audio = _audio;
			if ((Object)(object)audio != (Object)null && audio.isPlaying)
			{
				return true;
			}
			lock (_pending)
			{
				return _pending.Count > 0;
			}
		}
	}

	internal void SpeakVoiceOnly(string text, SpeechLanguage language)
	{
		if (CanSpeak && !string.IsNullOrWhiteSpace(text))
		{
			Enqueue(new PendingSpeech(new SpeechSynthesisRequest(Guid.NewGuid(), text, language, new SpeechDirective()), DateTimeOffset.UtcNow, _generation));
			_log.LogInfo((object)("module=VoiceFallback; event=queued; len=" + text.Length));
		}
	}

	internal MainThreadSpeechController(LlmGameBridge bridge, LlmConfigurationView view, string settingsPath, ManualLogSource log, ITtsServiceManager service)
	{
		_bridge = bridge;
		_view = view;
		_store = new SpeechSettingsStore(settingsPath);
		_log = log;
		_service = service;
		_talkAction = new TalkActionDriver(log);
	}

	public bool IsCurrentGeneration(long generation)
	{
		if (generation == Generation)
		{
			return Volatile.Read(ref _stopped) == 0;
		}
		return false;
	}

	internal void Initialize()
	{
		_view.InitializeSpeech(new SpeechUiCallbacks
		{
			SelectServiceEnabled = SelectServiceEnabled,
			SelectLanguage = (SpeechLanguage language) =>
			{
				Change(SpeechRadioSelection.WithLanguage(Settings, language));
			},
			SelectReference = (string? style) =>
			{
				Change(SpeechRadioSelection.WithReference(Settings, style));
			}
		});
		Present();
		_loading = Task.Run((Func<SpeechSettings>)_store.Load);
	}

	internal void Update()
	{
		if (_shutdown)
		{
			return;
		}
		_talkAction.Tick(Time.unscaledTime);
		if (Volatile.Read(ref _stopped) != 0)
		{
			ShutdownFromMainThread(applicationQuitting: false);
			return;
		}
		Action result;
		while (_mainActions.TryDequeue(out result))
		{
			result();
		}
		DrainRetiredTtsClients();
		Task<SpeechSettings> loading = _loading;
		if (loading != null && loading.IsCompleted)
		{
			try
			{
				Settings = _loading.GetAwaiter().GetResult();
				_loaded = true;
				if (Settings.Enabled && Settings.ServiceEnabled)
				{
					_deferredEnable = true;
					_deferredEnableDeadline = DateTimeOffset.UtcNow + DeferredFallbackDelay;
					_lastServiceState = _service.State;
					SetStatus("语音服务将在人物出场后自动开启（避免启动卡顿）；之前跟她说话，语音会等它就绪后补上。");
				}
				else
				{
					_service.SetEnabled(enabled: false, Settings.ServiceUrl, Settings.ExternalService);
					RefreshServiceState(force: true);
				}
			}
			catch (Exception error)
			{
				Settings = Settings with
				{
					Enabled = false,
					ServiceEnabled = false
				};
				SetStatus("语音配置读取失败；保留原文件，请检查后重启。");
				LogFailure(error);
			}
			_loading = null;
			Present();
		}
		if (_deferredEnable)
		{
			if (GameCharacterProbe.IsCharacterPresent() && _syncSeenAt == DateTimeOffset.MaxValue)
			{
				_syncSeenAt = DateTimeOffset.UtcNow;
			}
			if ((_syncSeenAt != DateTimeOffset.MaxValue && DateTimeOffset.UtcNow >= _syncSeenAt + SyncGracePeriod) || DateTimeOffset.UtcNow >= _deferredEnableDeadline)
			{
				StartDeferredService();
			}
		}
		RefreshServiceState();
		if (!CanSpeak && _activeRequest != null && !_speechStartedReported)
		{
			// 服务掉线或语音被关掉：把显示权交还桥，别让整条回复卡在“等语音”上。
			_bridge.ReleaseSpeechManaged(_activeMessageId);
			DisposePipeline();
			_activeChunks = Array.Empty<string>();
			_activeChunkIndex = 0;
			_pendingPresent = null;
			_activeRequest = null;
		}
		bool audioPlaying = IsAudioPlaying();
		if (_audioWasPlaying && !audioPlaying)
		{
			// 上一段刚播完：段间空档（gap_before_ms）从这一帧算起。
			_playbackIdleSince = Time.unscaledTime;
		}
		_audioWasPlaying = audioPlaying;
		if (_pipeline != null && !audioPlaying)
		{
			if (!CanSpeak)
			{
				// 语音中途不可用：收掉这一条，别让队列继续往前跑。
				FinishSequence(aborted: true);
			}
			else
			{
				UpdateSpeechSequence();
				if (IsAudioPlaying())
				{
					// 这一帧刚开播：和旧流程一样，不再往下推其它事。
					return;
				}
			}
		}
		OrdinaryChatOutput output;
		while (_bridge.TryDequeueTtsText(out output))
		{
			if (output.CreatedAt < _ignoreBefore)
			{
				ManualLogSource log = _log;
				if (log != null)
				{
					log.LogInfo((object)("module=SpeechDiag; source=controller; event=dequeued_but_skipped; can_speak=" + (CanSpeak ? "1" : "0") + "; too_old=1; phase=" + _service.State.Phase));
				}
				_bridge.OnSpeechPlaybackSkipped(output.MessageId);
				continue;
			}
			if (!CanSpeak)
			{
				if (output.SpeechDiagnostic == null && output.SpeechEnabled && output.SpeechLanguage == Settings.Language && _loaded && Settings.Enabled && Settings.ServiceEnabled && (_deferredEnable || _service.State.Phase == TtsServicePhase.Starting))
				{
					if (_deferredEnable)
					{
						StartDeferredService();
					}
					SpeechDirective speechDirective = SpeechReferenceSelection.Resolve(output.Speech, Settings);
					SpeechDirective directive = (Settings.ReactionsEnabled ? speechDirective : speechDirective with
					{
						ReactionId = null
					});
					_bridge.MarkSpeechManaged(output.MessageId);
					Enqueue(new PendingSpeech(new SpeechSynthesisRequest(output.MessageId, output.TtsText, output.SpeechLanguage, directive), output.CreatedAt, _generation));
				}
				else
				{
					ManualLogSource log2 = _log;
					if (log2 != null)
					{
						log2.LogInfo((object)("module=SpeechDiag; source=controller; event=dequeued_but_skipped; can_speak=" + (CanSpeak ? "1" : "0") + "; too_old=0; phase=" + _service.State.Phase));
					}
					_bridge.OnSpeechPlaybackSkipped(output.MessageId);
				}
				continue;
			}
			ManualLogSource log3 = _log;
			if (log3 != null)
			{
				log3.LogInfo((object)("module=SpeechDiag; source=controller; event=dequeued_ok; speech_enabled=" + (output.SpeechEnabled ? "1" : "0") + "; lang_match=" + ((output.SpeechLanguage == Settings.Language) ? "1" : "0") + "; diagnostic=" + (output.SpeechDiagnostic ?? "<null>") + "; tts_len=" + ((output.TtsText == null) ? (-1) : output.TtsText.Length)));
			}
			if (output.SpeechDiagnostic != null)
			{
				SetStatus("本轮语音未生成：" + output.SpeechDiagnostic + "；文字已保留。");
				_bridge.OnSpeechPlaybackSkipped(output.MessageId);
			}
			else if (output.SpeechEnabled && output.SpeechLanguage == Settings.Language)
			{
				if (DateTimeOffset.UtcNow - output.CreatedAt > MaximumAge)
				{
					Expired("气泡等待已超过 45 秒");
					_bridge.OnSpeechPlaybackSkipped(output.MessageId);
					continue;
				}
				SpeechDirective speechDirective2 = SpeechReferenceSelection.Resolve(output.Speech, Settings);
				SpeechDirective directive2 = (Settings.ReactionsEnabled ? speechDirective2 : speechDirective2 with
				{
					ReactionId = null
				});
				_bridge.MarkSpeechManaged(output.MessageId);
				Enqueue(new PendingSpeech(new SpeechSynthesisRequest(output.MessageId, output.TtsText, output.SpeechLanguage, directive2), output.CreatedAt, _generation));
			}
			else if (output.SpeechEnabled)
			{
				_bridge.OnSpeechPlaybackSkipped(output.MessageId);
			}
		}
		if (_pipeline != null || IsAudioPlaying() || !CanSpeak)
		{
			return;
		}
		while (_pending.Count > 0)
		{
			PendingSpeech pendingSpeech = _pending.Dequeue();
			if (pendingSpeech.Generation == _generation)
			{
				if (!(DateTimeOffset.UtcNow - pendingSpeech.CreatedAt > MaximumAge))
				{
					BeginSequence(pendingSpeech.Request);
					break;
				}
				Expired("语音排队已超过 45 秒");
				_bridge.ReleaseSpeechManaged(pendingSpeech.Request.RequestId);
			}
		}
	}

	/// <summary>开始一条新回复：切片 → 建预取流水线（生产者立刻开始合成第 1 段）。</summary>
	private void BeginSequence(SpeechSynthesisRequest request)
	{
		_activeMessageId = request.RequestId;
		_activeText = request.Text;
		_activeRequest = request;
		IReadOnlyList<string> chunks = SpeechSplitSynthesizer.Split(request.Text, FirstChunkMaxSpeakable);
		_activeChunks = ((chunks.Count > 0) ? chunks : new string[1] { request.Text });
		_activeChunkIndex = 0;
		_speechStartedReported = false;
		_talkStartedForChunk = false;
		_pendingPresent = null;
		_nextPresentIndex = 0;
		_lastSourceIndex = -1;
		_waitingStatusIndex = -1;
		_audioWasPlaying = false;
		_sequenceStartedAt = Time.unscaledTime;
		_playbackIdleSince = _sequenceStartedAt;
		_sequenceAudioSeconds = 0.0;
		_sequenceChunkCount = _activeChunks.Count;
		DisposePipeline();
		_pipeline = new SpeechPipeline(AcquireTtsClient(Settings.ServiceUrl), Settings.ServiceUrl, _activeChunks, request.Language, request.Directive, 1, _log, _lifetime.Token);
		SetStatus("正在合成第 1/" + _activeChunks.Count + " 段" + LanguageName(request.Language) + "语音；文字会跟着语音逐段出现。");
	}

	private static string LanguageName(SpeechLanguage language)
	{
		return (language == SpeechLanguage.Japanese) ? "日语" : "中文";
	}

	/// <summary>
	/// 播放器空闲时推进这条回复：取一段就绪音频 → 起口型 → 推气泡文字 → 播放。
	/// 就绪队列空且生产者还在跑就等着，生产者跑完就收尾。
	/// </summary>
	private void UpdateSpeechSequence()
	{
		SpeechPipeline pipeline = _pipeline;
		if (pipeline == null)
		{
			return;
		}
		if (_pendingPresent == null)
		{
			ReadyChunk ready;
			while (pipeline.TryTakeReady(out ready))
			{
				if (ready.Index > _lastSourceIndex)
				{
					_pendingPresent = ready;
					break;
				}
				_log.LogWarning("module=TTS; event=chunk_out_of_order; index=" + (ready.Index + 1) + "; last_shown=" + (_lastSourceIndex + 1) + "; text_logged=false");
			}
		}
		if (_pendingPresent != null)
		{
			PresentAndPlay(_pendingPresent);
			return;
		}
		if (pipeline.IsFinished)
		{
			// 生产者已经彻底结束：这时候队列才不会再变，可以安全判定“整条播完”。
			ReadyChunk leftover;
			if (pipeline.TryTakeReady(out leftover) && leftover.Index > _lastSourceIndex)
			{
				_pendingPresent = leftover;
				PresentAndPlay(leftover);
				return;
			}
			FinishSequence(aborted: false);
			return;
		}
		// 生产者还在合成：等它就绪，别每帧都刷 UI。
		int waiting = Math.Min(pipeline.TotalChunks, pipeline.Handled + 1);
		if (_waitingStatusIndex != waiting)
		{
			_waitingStatusIndex = waiting;
			SetStatus("正在合成第 " + waiting + "/" + pipeline.TotalChunks + " 段" + LanguageName(_activeRequest?.Language ?? Settings.Language) + "语音；等它就绪后接着播。");
		}
	}

	/// <summary>把一段就绪音频推上气泡并播放。返回 false 表示这一帧还没能放进去（下一帧重试）。</summary>
	private bool PresentAndPlay(ReadyChunk chunk)
	{
		int count = Math.Max(1, _activeChunks.Count);
		int displayIndex = _nextPresentIndex;
		if (!_talkStartedForChunk)
		{
			// 必须先起说话动画再推气泡：顺序反了游戏会结束对话、气泡被顶掉。
			_talkAction.OnSpeechStarted(chunk.Text, (float)chunk.Wave.DurationSeconds);
			_talkStartedForChunk = true;
		}
		float holdSeconds = (float)chunk.Wave.DurationSeconds + 15f;
		if (!_bridge.PresentSpeechChunk(_activeMessageId, displayIndex, count, chunk.Text, holdSeconds))
		{
			return false;
		}
		long gapMs = (long)Math.Round(Math.Max(0.0, (double)Time.unscaledTime - Math.Max(0.0, _playbackIdleSince)) * 1000.0);
		_log.LogInfo("module=TTS; event=chunk_timing; index=" + (chunk.Index + 1) + "; synth_ms=" + chunk.SynthMs + "; audio_s=" + chunk.Wave.DurationSeconds.ToString("0.00") + "; gap_before_ms=" + gapMs + "; display_index=" + (displayIndex + 1) + "; total=" + count);
		_sequenceAudioSeconds += chunk.Wave.DurationSeconds;
		try
		{
			Play(chunk.Wave);
		}
		catch (Exception error)
		{
			ReleaseClip();
			_pendingPresent = null;
			Fail(error);
			FinishSequence(aborted: true);
			return false;
		}
		_pendingPresent = null;
		_lastSourceIndex = chunk.Index;
		_activeChunkIndex = chunk.Index;
		_nextPresentIndex = displayIndex + 1;
		_talkStartedForChunk = false;
		_waitingStatusIndex = -1;
		if (!_speechStartedReported)
		{
			_bridge.OnSpeechPlaybackStarted(_activeMessageId);
			_speechStartedReported = true;
		}
		return true;
	}

	/// <summary>收掉这一条：结束口型、放掉音频、交还显示权、销毁流水线。</summary>
	private void FinishSequence(bool aborted)
	{
		Guid messageId = _activeMessageId;
		int failed = _pipeline?.Failed ?? 0;
		if (_speechStartedReported)
		{
			_bridge.OnSpeechPlaybackFinished(messageId);
		}
		else
		{
			// 一段都没放出来：让桥按原逻辑把文字补显示，别把整条回复吞掉。
			_bridge.OnSpeechPlaybackSkipped(messageId);
		}
		_talkAction.Finish();
		ReleaseClip();
		_bridge.ReleaseSpeechManaged(messageId);
		LogSpeechSequenceDone(failed);
		DisposePipeline();
		_pendingPresent = null;
		_nextPresentIndex = 0;
		_lastSourceIndex = -1;
		_waitingStatusIndex = -1;
		_audioWasPlaying = false;
		_activeChunks = Array.Empty<string>();
		_activeChunkIndex = 0;
		_activeRequest = null;
		_activeText = null;
		_speechStartedReported = false;
		_talkStartedForChunk = false;
		if (!aborted)
		{
			SetStatus("播放完成；等待下一条已显示的气泡。");
		}
	}

	private void DisposePipeline()
	{
		SpeechPipeline pipeline = _pipeline;
		_pipeline = null;
		if (pipeline == null)
		{
			return;
		}
		try
		{
			pipeline.Dispose();
		}
		catch (Exception error)
		{
			_log.LogWarning("module=TTS; event=pipeline_dispose_failed; code=" + error.GetType().Name);
		}
	}

	private bool IsAudioPlaying()
	{
		AudioSource audio = _audio;
		return (Object)(object)audio != (Object)null && audio.isPlaying;
	}

	/// <summary>
	/// 取本次要用的 TTS 客户端。整条会话复用同一个实例以复用 TCP 连接；
	/// 只有服务地址变了才换新的（旧实例等空下来再释放，避免打断在跑的请求）。
	/// </summary>
	private LocalTtsClient AcquireTtsClient(string serviceUrl)
	{
		if (_ttsClient != null && string.Equals(_ttsClientUrl, serviceUrl, StringComparison.Ordinal))
		{
			return _ttsClient;
		}
		LocalTtsClient? previous = _ttsClient;
		_ttsClient = new LocalTtsClient(serviceUrl);
		_ttsClientUrl = serviceUrl;
		if (previous != null)
		{
			if (_pipeline == null)
			{
				RetireTtsClient(previous);
			}
			else
			{
				_retiredTtsClient = previous;
			}
		}
		return _ttsClient;
	}

	private static void RetireTtsClient(LocalTtsClient client)
	{
		try
		{
			client.Dispose();
		}
		catch (Exception)
		{
		}
	}

	private void DrainRetiredTtsClients()
	{
		LocalTtsClient? retired = _retiredTtsClient;
		if (retired == null || _pipeline != null)
		{
			return;
		}
		_retiredTtsClient = null;
		RetireTtsClient(retired);
	}

	/// <summary>整条回复播完：输出本次的端到端耗时，供真机验证对比。</summary>
	private void LogSpeechSequenceDone(int failedChunks)
	{
		if (_sequenceStartedAt < 0.0)
		{
			return;
		}
		double totalSeconds = Math.Max(0.0, (double)Time.unscaledTime - _sequenceStartedAt);
		_sequenceStartedAt = -1.0;
		_log.LogInfo("module=TTS; event=speech_sequence_done; chunks=" + _sequenceChunkCount + "; total_s=" + totalSeconds.ToString("0.00") + "; audio_s=" + _sequenceAudioSeconds.ToString("0.00") + "; failed=" + failedChunks);
	}

	private void Expired(string reason)
	{
		_expiredCount++;
		SetStatus(reason + "，本条已丢弃；文字与会话保持不变。");
		_log.LogInfo((object)("module=TTS; event=stale_voice_discarded; total=" + _expiredCount));
	}

	private void Enqueue(PendingSpeech next)
	{
		if (_pending.Count >= 3)
		{
			PendingSpeech pendingSpeech = _pending.Dequeue();
			_bridge.ReleaseSpeechManaged(pendingSpeech.Request.RequestId);
			_bridge.OnSpeechPlaybackSkipped(pendingSpeech.Request.RequestId);
			_log.LogInfo((object)"module=TTS; event=backlog_trimmed; text_logged=false");
		}
		_pending.Enqueue(next);
	}

	private void Play(PcmWaveData wave)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Expected Obj, but got Unknown
		if ((Object)(object)_audioObject == (Object)null)
		{
			_audioObject = new GameObject("Just_Lilith.TTS.Audio");
			Object.DontDestroyOnLoad((Object)(object)_audioObject);
			_audio = _audioObject.AddComponent<AudioSource>();
			_audio.playOnAwake = false;
			_audio.loop = false;
			_audio.spatialBlend = 0f;
		}
		_clip = AudioClip.Create("Just_Lilith.TTS", wave.SampleFrames, wave.Channels, wave.SampleRate, false);
		if (!_clip.SetData(wave.Samples, 0))
		{
			throw new InvalidOperationException("AudioClip.SetData failed.");
		}
		_audio.clip = _clip;
		_audio.volume = Settings.Volume;
		_audio.Play();
		SetStatus("Unity 正在播放：可选反应音 → TTS 正文。");
	}

	private void Change(SpeechSettings proposed)
	{
		lock (_preferenceGate)
		{
			if (_loaded && Volatile.Read(ref _stopped) == 0 && !(proposed == Settings))
			{
				CancelPlayback();
				Settings = proposed;
				SetStatus(DescribeService(_service.State) + "；偏好后台保存中。");
				Present();
				_lastSave = SaveAsync(proposed);
			}
		}
	}

	private void StartDeferredService()
	{
		if (_deferredEnable)
		{
			_deferredEnable = false;
			_service.SetEnabled(enabled: true, Settings.ServiceUrl, Settings.ExternalService);
			_lastServiceState = _service.State;
			SetStatus("语音服务启动中（启动延迟结束）；此前排队的语音会在就绪后补上。");
			ManualLogSource log = _log;
			if (log != null)
			{
				log.LogInfo((object)("module=TTS; event=deferred_service_started; t=" + Time.unscaledTime.ToString("0.00")));
			}
		}
	}

	private void SelectServiceEnabled(bool enabled)
	{
		lock (_preferenceGate)
		{
			if (_loaded && Volatile.Read(ref _stopped) == 0 && SpeechRadioSelection.CanRequestServiceEnabled(_service.State.Phase, enabled, _loaded))
			{
				CancelPlayback();
				SpeechSettings speechSettings = SpeechRadioSelection.WithServiceEnabled(Settings, enabled);
				bool flag = speechSettings != Settings;
				Settings = speechSettings;
				_deferredEnable = false;
				_service.SetEnabled(enabled, Settings.ServiceUrl, Settings.ExternalService);
				_lastServiceState = _service.State;
				SetStatus(DescribeService(_lastServiceState));
				if (flag)
				{
					_lastSave = SaveAsync(speechSettings);
				}
			}
		}
	}

	private void RefreshServiceState(bool force = false)
	{
		if (!_loaded || Volatile.Read(ref _stopped) != 0)
		{
			return;
		}
		TtsServiceState state = _service.State;
		if (force || !(state == _lastServiceState))
		{
			TtsServiceState lastServiceState = _lastServiceState;
			if ((object)lastServiceState != null && lastServiceState.Phase == TtsServicePhase.Ready && state.Phase != TtsServicePhase.Ready && Settings.ServiceEnabled)
			{
				CancelPlayback();
			}
			_lastServiceState = state;
			SetStatus(DescribeService(state));
		}
	}

	private string DescribeService(TtsServiceState state)
	{
		string text = state.Message;
		if (state.Phase == TtsServicePhase.Ready)
		{
			text += ((Settings.Language == SpeechLanguage.Japanese) ? "；朗读语言：日语" : "；朗读语言：中文");
		}
		if (state.Phase == TtsServicePhase.Ready && !Settings.Enabled)
		{
			text += "；兼容语音开关为关闭，继续保留文字";
		}
		return text;
	}

	private async Task SaveAsync(SpeechSettings proposed)
	{
		try
		{
			await _saveGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				await Task.Run(() => _store.Save(proposed)).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				_saveGate.Release();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception error)
		{
			LogFailure(error);
			_mainActions.Enqueue(() =>
			{
				SetStatus("语音设置保存失败；当前值仅本次有效，原文件保留。");
			});
		}
	}

	private void CancelPlayback()
	{
		_generation++;
		_ignoreBefore = DateTimeOffset.UtcNow;
		_pending.Clear();
		_bridge.ClearPendingSpeech();
		// 预取流水线必须跟着一起取消：否则生产者还会继续合成旧段，
		// 旧段会冒到新回复后面。
		DisposePipeline();
		_pendingPresent = null;
		_talkAction.Finish();
		ReleaseClip();
	}

	private void ReleaseClip()
	{
		if ((Object)(object)_audio != (Object)null)
		{
			_audio.Stop();
			_audio.clip = null;
		}
		if ((Object)(object)_clip != (Object)null)
		{
			Object.Destroy((Object)(object)_clip);
		}
		_clip = null;
	}

	private void Fail(Exception error)
	{
		if (!(error is OperationCanceledException))
		{
			SetStatus("语音处理失败；请检查本地服务，文字与会话保持不变。" + ((error is SpeechException ex) ? (" [" + ex.Code + "]") : ""));
			LogFailure(error);
		}
	}

	private void LogFailure(Exception error)
	{
		_log.LogWarning((object)("module=TTS; event=operation_failed; code=" + ((error is SpeechException ex) ? ex.Code : error.GetType().Name)));
	}

	private void SetStatus(string value)
	{
		_status = value;
		Present();
	}

	private void Present()
	{
		_view.ApplySpeechState(Settings, _status + ((_expiredCount == 0) ? "" : $"（本次过期丢弃 {_expiredCount} 条）"), _loaded, _service.State);
	}

	private static void Cancel(CancellationTokenSource? source)
	{
		try
		{
			source?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	public void RequestStop()
	{
		_talkAction.Finish();
		Task lastSave;
		lock (_preferenceGate)
		{
			if (Interlocked.Exchange(ref _stopped, 1) != 0)
			{
				return;
			}
			lastSave = _lastSave;
		}
		Cancel(_lifetime);
		// _lifetime 是流水线的父 token，上面这一步已经让生产者停下；
		// 这里只做非阻塞的队列清理，真正的 Dispose 留给主线程收尾。
		_pipeline?.Cancel();
		_service.RequestStop();
		if (!lastSave.Wait(TimeSpan.FromSeconds(2.0)))
		{
			_log.LogWarning((object)"module=TTS; event=preference_flush_timeout; latest_preference_may_not_be_saved");
		}
	}

	internal void ShutdownFromMainThread(bool applicationQuitting)
	{
		if (_shutdown)
		{
			return;
		}
		_shutdown = true;
		RequestStop();
		_generation++;
		_pending.Clear();
		if (_activeMessageId != Guid.Empty)
		{
			_bridge.ReleaseSpeechManaged(_activeMessageId);
			_activeMessageId = Guid.Empty;
		}
		_activeChunks = Array.Empty<string>();
		_activeChunkIndex = 0;
		DisposePipeline();
		_pendingPresent = null;
		_activeRequest = null;
		_bridge.ClearPendingSpeech();
		Action result;
		while (_mainActions.TryDequeue(out result))
		{
		}
		if (!applicationQuitting)
		{
			ReleaseClip();
			if ((Object)(object)_audioObject != (Object)null)
			{
				Object.Destroy((Object)(object)_audioObject);
			}
		}
		_clip = null;
		_audio = null;
		_audioObject = null;
		LocalTtsClient? ttsClient = _ttsClient;
		_ttsClient = null;
		_ttsClientUrl = "";
		if (ttsClient != null)
		{
			RetireTtsClient(ttsClient);
		}
		LocalTtsClient? retiredClient = _retiredTtsClient;
		_retiredTtsClient = null;
		if (retiredClient != null)
		{
			RetireTtsClient(retiredClient);
		}
	}
}
