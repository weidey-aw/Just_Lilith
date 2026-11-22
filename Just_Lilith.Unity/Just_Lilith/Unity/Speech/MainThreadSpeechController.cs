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

	private sealed record WorkResult(long Generation, DateTimeOffset CreatedAt, PcmWaveData? Wave, Exception? Error);

	private const int QueueLimit = 3;

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

	private Task<WorkResult>? _active;

	private Guid _activeMessageId;

	private string? _activeText;

	private readonly TalkActionDriver _talkAction;

	private CancellationTokenSource? _operation;

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
			if (_active != null)
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
		if ((Object)(object)_clip != (Object)null && (Object)(object)_audio != (Object)null && !_audio.isPlaying)
		{
			_bridge.OnSpeechPlaybackFinished(_activeMessageId);
			_talkAction.Finish();
			ReleaseClip();
			SetStatus("播放完成；等待下一条已显示的气泡。");
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
				Enqueue(new PendingSpeech(new SpeechSynthesisRequest(output.MessageId, output.TtsText, output.SpeechLanguage, directive2), output.CreatedAt, _generation));
			}
			else if (output.SpeechEnabled)
			{
				_bridge.OnSpeechPlaybackSkipped(output.MessageId);
			}
		}
		Task<WorkResult> active = _active;
		if (active != null && active.IsCompleted)
		{
			WorkResult result2 = _active.GetAwaiter().GetResult();
			_active = null;
			_operation = null;
			if (result2.Generation == _generation && CanSpeak)
			{
				if (result2.Error != null)
				{
					_bridge.OnSpeechPlaybackSkipped(_activeMessageId);
					Fail(result2.Error);
				}
				else if ((object)result2.Wave != null)
				{
					try
					{
						Play(result2.Wave);
						_bridge.OnSpeechPlaybackStarted(_activeMessageId);
					}
					catch (Exception error2)
					{
						ReleaseClip();
						_bridge.OnSpeechPlaybackSkipped(_activeMessageId);
						Fail(error2);
					}
				}
			}
		}
		if (_active != null || !((Object)(object)_clip == (Object)null) || !CanSpeak)
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
					_operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
					_active = RunSynthesis(pendingSpeech, Settings.ServiceUrl, _operation);
					_activeMessageId = pendingSpeech.Request.RequestId;
					_activeText = pendingSpeech.Request.Text;
					SetStatus("正在合成" + ((pendingSpeech.Request.Language == SpeechLanguage.Japanese) ? "日语" : "中文") + "语音；首次切换需载入对应模型，文字已保留。");
					break;
				}
				Expired("语音排队已超过 45 秒");
			}
		}
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
			_bridge.OnSpeechPlaybackSkipped(pendingSpeech.Request.RequestId);
			_log.LogInfo((object)"module=TTS; event=backlog_trimmed; text_logged=false");
		}
		_pending.Enqueue(next);
	}

	private static async Task<WorkResult> RunSynthesis(PendingSpeech next, string serviceUrl, CancellationTokenSource cancellation)
	{
		try
		{
			using LocalTtsClient client = new LocalTtsClient(serviceUrl);
			PcmWaveData wave = await SpeechSplitSynthesizer.SynthesizeAsync(client, next.Request, cancellation.Token).ConfigureAwait(continueOnCapturedContext: false);
			return new WorkResult(next.Generation, next.CreatedAt, wave, null);
		}
		catch (Exception error)
		{
			return new WorkResult(next.Generation, next.CreatedAt, null, error);
		}
		finally
		{
			cancellation.Dispose();
		}
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
		_talkAction.OnSpeechStarted(_activeText ?? "", (float)wave.DurationSeconds);
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
		Cancel(_operation);
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
		Cancel(_operation);
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
	}
}
