using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using Just_Lilith.Core.Proactive;
using Just_Lilith.Unity.Game;
using Just_Lilith.Unity.Speech;
using Just_Lilith.Unity.Ui;
using UnityEngine;

namespace Just_Lilith.Unity.Proactive;

/// <summary>
/// 主动对话 P0：固定周期检查"现在能不能自己开口"，能就攒由头、掷骰、投出去。
///
/// 关键约束：
/// - 单生成锁：投放前先看 <see cref="LlmConfigurationController.IsGenerating"/>，
///   锁被占用就本轮放弃（不排队、不消耗兴趣）；
/// - 由头只是本次请求的一条 System 消息，不落历史、不展示、不统计；
/// - 闸门不过就入暂存队列（带过期时间），过期只记日志、不补说；
/// - 用户一说话：兴趣、次数、测试钩子、暂存全部清零。
/// </summary>
internal sealed class ProactiveChatService : IDisposable
{
	private const string Module = "module=Proactive";

	private readonly LlmConfigurationController _controller;

	private readonly LlmConfigurationView _view;

	private readonly LlmGameBridge _bridge;

	private readonly MainThreadSpeechController? _speech;

	private readonly ProactiveContextProbe _probe;

	private readonly ProactiveGenerator _generator;

	private readonly ManualLogSource _log;

	private readonly string _settingsPath;

	private readonly System.Random _random = new System.Random();

	private ProactiveSettings _settings;

	private StagedCueQueue _staged;

	private DateTime _settingsStamp = DateTime.MinValue;

	private double _nextTickAt;

	private double _lastUserMessageAt;

	private double _interest;

	private int _count;

	private bool _generating;

	private bool _testDelivered;

	private bool _disposed;

	private const string TestSourceTag = "idleChat(test)";

	/// <summary>窥屏转述进行中：期间不再开第二次截屏，也挡掉其它投放。</summary>
	private bool _peeking;

	private DateTimeOffset _lastPeekAtUtc = DateTimeOffset.MinValue;

	private DateTimeOffset _peekDayUtc = DateTimeOffset.MinValue;

	private int _peekCountToday;

	private bool _privacyNoticeLogged;

	private IReadOnlyList<SceneMemoryEntry> _sceneMemory = Array.Empty<SceneMemoryEntry>();

	private string _sceneMemoryPath = "";

	private const int ScreenPeekTimeoutSeconds = 20;

	public ProactiveChatService(LlmConfigurationController controller, LlmConfigurationView view, LlmGameBridge bridge, MainThreadSpeechController? speech, string settingsPath, ManualLogSource log)
	{
		_controller = controller ?? throw new ArgumentNullException("controller");
		_view = view;
		_bridge = bridge;
		_speech = speech;
		_log = log;
		_settingsPath = settingsPath;
		try
		{
			_sceneMemoryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".", "local.just_lilith.scene-memory.json");
			_sceneMemory = SceneMemoryStore.Load(_sceneMemoryPath);
		}
		catch (Exception)
		{
			_sceneMemoryPath = settingsPath + ".scene-memory.json";
			_sceneMemory = Array.Empty<SceneMemoryEntry>();
		}
		_probe = new ProactiveContextProbe(log);
		_generator = new ProactiveGenerator(_random);
		_settings = ProactiveSettingsStore.LoadOrCreate(settingsPath, out string? diagnostic);
		_staged = new StagedCueQueue(_settings.StageLimit);
		_settingsStamp = ReadStamp();
		_lastUserMessageAt = Time.unscaledTime;
		_nextTickAt = Time.unscaledTime;
		if (diagnostic != null)
		{
			Log("module=Proactive; state=settings_fallback; reason=" + diagnostic + "; path_preserved=true");
		}
		Log("module=Proactive; state=ready; enabled=" + Bool(_settings.Enabled) + "; target_s=" + _settings.TargetIntervalSeconds + "; period_s=" + _settings.CheckPeriodSeconds + "; cap=" + _settings.MaxPerUserMessage + "; increment=" + InterestModel.DescribeIncrement(InterestModel.SolveIncrement(_settings.TargetIntervalSeconds, _settings.MaxPerUserMessage, _settings.CheckPeriodSeconds)) + "; peek=" + Bool(_settings.Sources.Screen && !string.IsNullOrWhiteSpace(_settings.VisionProfileId)) + "; scene_memory=" + _sceneMemory.Count);
		if (_settings.Enabled && _settings.Sources.Screen)
		{
			LogPrivacyNotice();
		}
	}

	/// <summary>2.7：首次开启窥屏时把隐私说明说清楚（目前以日志形式给出一次，不弹窗）。</summary>
	private void LogPrivacyNotice()
	{
		if (_privacyNoticeLogged)
		{
			return;
		}
		_privacyNoticeLogged = true;
		Log("module=Proactive; state=privacy_notice; screen_capture=on; detail=抽中窥屏时会截取一次桌面画面并只发送给你配置的视觉模型；截图不落盘、不进日志、不进记忆，只保留一段文字描述");
	}

	/// <summary>每帧由 view 调用，内部按 checkPeriodSeconds 节流。</summary>
	public void Update()
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			double now = Time.unscaledTime;
			ReloadSettingsIfChanged();
			ProactiveSettings settings = _settings;
			if (!settings.Enabled)
			{
				return;
			}
			if (settings.TestMode.Enabled && !_testDelivered && !_generating && !_peeking)
			{
				double due = _lastUserMessageAt + Math.Max(5, settings.TestMode.ForceAfterSeconds);
				if (now >= due)
				{
					_testDelivered = true;
					Tick(now, forced: true);
					return;
				}
			}
			if (now < _nextTickAt)
			{
				return;
			}
			_nextTickAt = now + Math.Max(10, settings.CheckPeriodSeconds);
			Tick(now, forced: false);
		}
		catch (Exception error)
		{
			Log("module=Proactive; event=skipped; reason=update_failed; code=" + error.GetType().Name);
		}
	}

	/// <summary>用户发了一条消息：兴趣、次数、测试钩子、暂存全部清零。</summary>
	public void NotifyUserMessage()
	{
		if (_disposed)
		{
			return;
		}
		_interest = 0.0;
		_count = 0;
		_testDelivered = false;
		_lastUserMessageAt = Time.unscaledTime;
		_nextTickAt = _lastUserMessageAt;
		_staged.Clear();
		LogEvent("reset", "-", reason: "user_message");
	}

	private void Tick(double now, bool forced)
	{
		ProactiveSettings settings = _settings;
		if (!settings.Enabled)
		{
			return;
		}
		if (_peeking)
		{
			// 上一轮的转述还没回来：这一拍跳过，别叠第二次截屏。
			return;
		}
		DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
		int expired = _staged.Prune(nowUtc, delegate(AmbientCue cue)
		{
			LogEvent("expired", cue.SourceTag, interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: "stage_timeout");
		});
		ProactiveContextProbe.ProbeResult probe = _probe.Probe();
		int bonus = ProactiveContextProbe.ComputeStateBonus(settings.UserState, probe.IdleMilliseconds, probe.IsGaming, probe.IsBrowsing);
		string gate = EvaluateGate(probe);
		LogEvent("tick", forced ? TestSourceTag : "-", interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: (gate.Length == 0) ? "ok" : gate, extra: "; idle_s=" + (IdleSeconds(probe)?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + "; bonus=" + bonus + "; staged=" + _staged.Count + "; expired=" + expired + "; character=" + (string.IsNullOrEmpty(probe.CharacterState) ? "-" : probe.CharacterState) + "; story=" + (probe.StoryPerforming ? "1" : "0"));

		if (gate.Length > 0)
		{
			// 投不出去：本轮该开口就先攒着，不该开口就什么都不做。
			if (_staged.Count > 0)
			{
				return;
			}
			if (forced)
			{
				StageCue(settings, probe, nowUtc, gate);
				return;
			}
			if (TryDecide(settings, bonus, forced: false))
			{
				StageCue(settings, probe, nowUtc, gate);
			}
			return;
		}

		// 闸门通过：先投攒下的，再考虑本 tick 该不该开新口。
		AmbientCue? staged;
		if (_staged.TryTake(nowUtc, out staged) && staged != null)
		{
			Deliver(staged, settings, test: false, staged: true);
			return;
		}
		if (forced)
		{
			CueAndDeliver(settings, probe, nowUtc, test: true);
			return;
		}
		if (TryDecide(settings, bonus, forced: false))
		{
			CueAndDeliver(settings, probe, nowUtc, test: false);
		}
	}

	/// <summary>兴趣累积 + 掷骰。返回 true 表示这一轮该开口（已消费兴趣并 count++）。</summary>
	private bool TryDecide(ProactiveSettings settings, int bonus, bool forced)
	{
		if (forced)
		{
			return true;
		}
		if (_count >= settings.MaxPerUserMessage)
		{
			LogEvent("roll", "-", interest: _interest, count: _count, cap: settings.MaxPerUserMessage, chance: 0.0, reason: "cap_reached");
			return false;
		}
		double lower = InterestModel.SolveIncrement(settings.TargetIntervalSeconds, settings.MaxPerUserMessage, settings.CheckPeriodSeconds);
		_interest = InterestModel.ClampInterest(_interest + InterestModel.NextIncrement(_random, lower));
		if (!(_interest > InterestModel.RollThreshold))
		{
			LogEvent("roll", "-", interest: _interest, count: _count, cap: settings.MaxPerUserMessage, chance: 0.0, reason: "below_threshold");
			return false;
		}
		double chance = InterestModel.RollChance(_interest, bonus, settings.MaxPerUserMessage, _count, settings.DecayProbability);
		bool hit = chance > 0.0 && _random.NextDouble() < chance;
		LogEvent("roll", "-", interest: _interest, count: _count, cap: settings.MaxPerUserMessage, chance: chance, reason: hit ? "hit" : "miss");
		if (!hit)
		{
			return false;
		}
		// 生成由头即：兴趣清零、count++（没生成由头就不消耗）。
		_interest = 0.0;
		_count++;
		return true;
	}

	/// <summary>
	/// 闸门不过时暂存。只暂存"闲聊"由头：窥屏要现拍，攒下来的画面描述会过期，没意义。
	/// </summary>
	private void StageCue(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, string reason)
	{
		AmbientCue? cue = BuildIdleCue(settings, probe, nowUtc);
		if (cue == null)
		{
			LogEvent("skipped", "-", reason: "no_source", extra: "; gate=" + reason);
			return;
		}
		_staged.TryEnqueue(cue, out AmbientCue? dropped);
		LogEvent("staged", cue.SourceTag, interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: reason, extra: "; expires_in=" + Math.Max(5, settings.StageExpireSeconds) + "; staged=" + _staged.Count + ((dropped != null) ? ("; dropped=" + dropped.SourceTag) : ""));
	}

	/// <summary>闸门通过、该开口了：按权重选来源；选中窥屏就先截屏+转述，否则直接造闲聊由头。</summary>
	private void CueAndDeliver(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, bool test)
	{
		AmbientCueSource? picked = _generator.PickSource(settings);
		if (!picked.HasValue)
		{
			LogEvent("skipped", test ? TestSourceTag : "-", reason: "no_source");
			return;
		}
		AmbientCueSource source = picked.GetValueOrDefault();
		if (source == AmbientCueSource.Screen)
		{
			if (CanPeek(settings))
			{
				BeginPeek(settings, probe, nowUtc, test);
				return;
			}
			LogEvent("skipped", "screen", reason: "peek_unavailable");
			source = AmbientCueSource.IdleChat;
		}
		if (source != AmbientCueSource.IdleChat)
		{
			LogEvent("skipped", test ? TestSourceTag : "-", reason: "no_source");
			return;
		}
		AmbientCue? cue = BuildIdleCue(settings, probe, nowUtc);
		if (cue == null)
		{
			LogEvent("skipped", test ? TestSourceTag : "-", reason: "no_source");
			return;
		}
		Deliver(cue, settings, test, staged: false);
	}

	private AmbientCue? BuildIdleCue(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc)
	{
		if (!ProactiveGenerator.IsUsable(settings, AmbientCueSource.IdleChat))
		{
			return null;
		}
		int idleSeconds = IdleSeconds(probe) ?? Math.Max(0, (int)(Time.unscaledTime - _lastUserMessageAt));
		return _generator.TryCreate(settings, AmbientCueSource.IdleChat, idleSeconds, nowUtc, null, SceneMemoryStore.BuildReference(_sceneMemory, settings.SceneMemoryInjectCount));
	}

	/// <summary>现在允许截屏吗：窥屏开关 + 视觉配置 + Q8 的硬上限。</summary>
	private bool CanPeek(ProactiveSettings settings)
	{
		if (!settings.Sources.Screen || string.IsNullOrWhiteSpace(settings.VisionProfileId))
		{
			return false;
		}
		DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
		if (settings.PeekMinIntervalSeconds > 0 && _lastPeekAtUtc != DateTimeOffset.MinValue && (nowUtc - _lastPeekAtUtc).TotalSeconds < (double)settings.PeekMinIntervalSeconds)
		{
			return false;
		}
		if (settings.PeekMaxPerDay > 0)
		{
			RollPeekDay(nowUtc);
			if (_peekCountToday >= settings.PeekMaxPerDay)
			{
				return false;
			}
		}
		return true;
	}

	private void RollPeekDay(DateTimeOffset nowUtc)
	{
		if (_peekDayUtc == DateTimeOffset.MinValue || _peekDayUtc.UtcDateTime.Date != nowUtc.UtcDateTime.Date)
		{
			_peekDayUtc = nowUtc;
			_peekCountToday = 0;
		}
	}

	/// <summary>
	/// 截屏 → 视觉转述 → 造窥屏由头 → 投放。全程失败都按 2.5 降级成普通话题，
	/// 闲聊也关就本轮不开口（兴趣已消耗，不补偿）。
	/// </summary>
	private void BeginPeek(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, bool test)
	{
		if (_peeking)
		{
			LogEvent("skipped", "screen", reason: "peeking");
			return;
		}
		_peeking = true;
		// Q9：识图这几秒也显示"思考中"，避免看起来像卡住。
		ShowThinkingSafe();
		ScreenCapture.CaptureResult capture;
		try
		{
			capture = ScreenCapture.TryCapture();
		}
		catch (Exception error)
		{
			_peeking = false;
			DegradeToIdle(settings, probe, nowUtc, test, "capture_failed; code=" + error.GetType().Name);
			return;
		}
		if (!capture.Ok)
		{
			_peeking = false;
			DegradeToIdle(settings, probe, nowUtc, test, "capture_" + capture.Reason);
			return;
		}
		DateTimeOffset attemptAt = DateTimeOffset.UtcNow;
		_lastPeekAtUtc = attemptAt;
		if (settings.PeekMaxPerDay > 0)
		{
			RollPeekDay(attemptAt);
			_peekCountToday++;
		}
		byte[] jpeg = capture.Jpeg!;
		LogEvent("peek", "screen", extra: "; capture_ms=" + capture.ElapsedMs + "; bytes=" + jpeg.Length + "; w=" + capture.Width + "; h=" + capture.Height + "; vision=" + settings.VisionProfileId);
		string? sceneReference = SceneMemoryStore.BuildReference(_sceneMemory, settings.SceneMemoryInjectCount);
		_controller.TranscribeScreen(jpeg, settings.VisionProfileId, ScreenPeekTimeoutSeconds, delegate(string? description)
		{
			_peeking = false;
			if (_disposed)
			{
				EndThinkingSafe();
				return;
			}
			if (string.IsNullOrWhiteSpace(description))
			{
				DegradeToIdle(settings, probe, nowUtc, test, "peek_failed");
				return;
			}
			// 2.4 ②：写场景记忆（只有文字，环形保留）。
			_sceneMemory = SceneMemoryStore.Append(_sceneMemoryPath, _sceneMemory, description!, nowUtc, settings.SceneMemoryLimit, settings.SceneMemoryDays);
			AmbientCue? cue = _generator.TryCreate(settings, AmbientCueSource.Screen, 0, nowUtc, description, sceneReference);
			if (cue == null)
			{
				DegradeToIdle(settings, probe, nowUtc, test, "peek_cue_missing");
				return;
			}
			// 先把我们自己的"思考中"收掉再复检闸门：否则 IsConversationActive 会被自己的思考气泡点亮，
			// 每一轮窥屏都会误判成 conversation_active。
			EndThinkingSafe();
			string gate = EvaluateGate(_probe.Probe());
			if (gate.Length > 0)
			{
				_staged.TryEnqueue(cue, out AmbientCue? dropped);
				LogEvent("staged", cue.SourceTag, reason: gate, extra: "; expires_in=" + Math.Max(5, settings.StageExpireSeconds) + "; staged=" + _staged.Count + ((dropped != null) ? ("; dropped=" + dropped.SourceTag) : ""));
				return;
			}
			Deliver(cue, settings, test, staged: false);
		});
	}

	/// <summary>窥屏失败 → 退化成普通闲聊由头；闲聊也关就本轮不开口（不补说、不弹错）。</summary>
	private void DegradeToIdle(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, bool test, string reason)
	{
		AmbientCue? cue = BuildIdleCue(settings, probe, nowUtc);
		if (cue == null)
		{
			EndThinkingSafe();
			LogEvent("skipped", "screen", reason: reason + "; idle_chat_off");
			return;
		}
		LogEvent("degraded", "idleChat", reason: reason);
		Deliver(cue, settings, test, staged: false);
	}

	private void ShowThinkingSafe()
	{
		try
		{
			_bridge.ShowThinking();
		}
		catch (Exception)
		{
		}
	}

	private void EndThinkingSafe()
	{
		try
		{
			_bridge.EndThinking();
		}
		catch (Exception)
		{
		}
	}

	private void Deliver(AmbientCue cue, ProactiveSettings settings, bool test, bool staged)
	{
		if (_generating || _disposed)
		{
			LogEvent("skipped", cue.SourceTag, reason: "generating");
			return;
		}
		string tag = test ? (cue.SourceTag + "(test)") : cue.SourceTag;
		if ((_controller.IsGenerating))
		{
			// 聊天正在生成：本轮放弃，不排队、不消耗兴趣。
			LogEvent("skipped", tag, interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: "generating");
			return;
		}
		_generating = true;
		LogEvent("delivered", tag, interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: staged ? "staged" : (test ? "test_mode" : "rolled"));
		try
		{
			_bridge.ShowThinking();
		}
		catch (Exception error)
		{
			_generating = false;
			LogEvent("skipped", tag, reason: "thinking_failed; code=" + error.GetType().Name);
			return;
		}
		try
		{
			_controller.SendProactive(cue.Instruction, delegate(bool ok)
			{
				_generating = false;
				try
				{
					_bridge.EndThinking();
				}
				catch (Exception)
				{
				}
				if (!ok)
				{
					LogEvent("skipped", tag, reason: "generate_failed");
				}
			}, tag);
		}
		catch (Exception error2)
		{
			_generating = false;
			try
			{
				_bridge.EndThinking();
			}
			catch (Exception)
			{
			}
			LogEvent("skipped", tag, reason: "send_failed; code=" + error2.GetType().Name);
		}
	}

	private string EvaluateGate(ProactiveContextProbe.ProbeResult probe)
	{
		if (_view != null && _view.IsChatInputFocused)
		{
			return "chat_focused";
		}
		if (_controller.IsGenerating || _generating || _peeking)
		{
			return "generating";
		}
		if (_speech != null && _speech.SpeechActive)
		{
			return "speech_active";
		}
		if (_bridge != null && _bridge.IsConversationActive)
		{
			return "conversation_active";
		}
		if (probe.StoryPerforming)
		{
			return "story";
		}
		if (probe.CharacterBlocked)
		{
			return "character_state";
		}
		return "";
	}

	private int? IdleSeconds(ProactiveContextProbe.ProbeResult probe)
	{
		if (!probe.IdleMilliseconds.HasValue)
		{
			return null;
		}
		return (int)(probe.IdleMilliseconds.GetValueOrDefault() / 1000);
	}

	private void ReloadSettingsIfChanged()
	{
		DateTime stamp = ReadStamp();
		if (stamp == _settingsStamp)
		{
			return;
		}
		_settingsStamp = stamp;
		ProactiveSettings previous = _settings;
		ProactiveSettings reloaded = ProactiveSettingsStore.LoadOrCreate(_settingsPath, out string? diagnostic);
		_settings = reloaded;
		if (reloaded.StageLimit != previous.StageLimit)
		{
			_staged = new StagedCueQueue(reloaded.StageLimit);
		}
		if (!reloaded.Enabled)
		{
			_staged.Clear();
			_testDelivered = false;
		}
		else if (!previous.Enabled)
		{
			_lastUserMessageAt = Time.unscaledTime;
			_nextTickAt = _lastUserMessageAt;
			_testDelivered = false;
		}
		// 新加"很久没说话"的由头时把隐私说明补一次；窥屏刚被打开时也说一次。
		if (reloaded.Enabled && reloaded.Sources.Screen && !previous.Sources.Screen)
		{
			LogPrivacyNotice();
		}
		// 用户主动关掉窥屏：把计时清掉，重新打开时不会被硬上限挡住。
		if (previous.Sources.Screen && !reloaded.Sources.Screen)
		{
			_lastPeekAtUtc = DateTimeOffset.MinValue;
			Log("module=Proactive; state=screen_capture_off; detail=已停止截屏；已存在的场景记忆保留，可用 sceneMemoryLimit=0 清空");
		}
		Log("module=Proactive; state=settings_reloaded; enabled=" + Bool(reloaded.Enabled) + "; target_s=" + reloaded.TargetIntervalSeconds + "; period_s=" + reloaded.CheckPeriodSeconds + "; cap=" + reloaded.MaxPerUserMessage + "; test_mode=" + Bool(reloaded.TestMode.Enabled) + "; peek=" + Bool(reloaded.Sources.Screen && !string.IsNullOrWhiteSpace(reloaded.VisionProfileId)) + ((diagnostic != null) ? ("; reason=" + diagnostic) : ""));
	}

	private DateTime ReadStamp()
	{
		try
		{
			return File.Exists(_settingsPath) ? File.GetLastWriteTimeUtc(_settingsPath) : DateTime.MinValue;
		}
		catch (Exception)
		{
			return DateTime.MinValue;
		}
	}

	private void LogEvent(string node, string source, double? interest = null, double? chance = null, int? count = null, int? cap = null, string? reason = null, string? extra = null)
	{
		string text = "module=Proactive; event=" + node + "; source=" + (string.IsNullOrEmpty(source) ? "-" : source);
		if (interest.HasValue)
		{
			text += "; interest=" + interest.GetValueOrDefault().ToString("0.0", CultureInfo.InvariantCulture);
		}
		if (chance.HasValue)
		{
			text += "; chance=" + chance.GetValueOrDefault().ToString("0.000", CultureInfo.InvariantCulture);
		}
		if (count.HasValue)
		{
			text += "; count=" + count.GetValueOrDefault();
		}
		if (cap.HasValue)
		{
			text += "; cap=" + cap.GetValueOrDefault();
		}
		if (reason != null)
		{
			text += "; reason=" + reason;
		}
		if (extra != null)
		{
			text += extra;
		}
		Log(text);
	}

	private void Log(string message)
	{
		ManualLogSource log = _log;
		if (log != null)
		{
			log.LogInfo(message);
		}
	}

	private static string Bool(bool value)
	{
		return value ? "1" : "0";
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_peeking = false;
			_staged.Clear();
		}
	}
}
