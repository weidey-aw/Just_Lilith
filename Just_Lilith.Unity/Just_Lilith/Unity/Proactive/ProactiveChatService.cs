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

	/// <summary>上一次真正发起主动发言的时刻：额度自愈除了看用户静默，也看这条，避免刚说完就立刻再放额度。</summary>
	private double _lastProactiveAt;

	private double _interest;

	private int _count;

	private bool _generating;

	private bool _testDelivered;

	private bool _disposed;

	private const string TestSourceTag = "idleChat(test)";

	/// <summary>窥屏转述进行中：期间不再开第二次截屏，也挡掉其它投放。</summary>
	private bool _peeking;

	private DateTimeOffset _lastPeekAtUtc = DateTimeOffset.MinValue;

	/// <summary>上一轮窥屏是从什么时候开始的（看门狗用，超时就强制复位）。</summary>
	private double _peekStartedAt;

	/// <summary>上一次窥屏失败的时间：失败不占用「最小间隔」，但要有个短退避，别死循环刷视觉接口。</summary>
	private DateTimeOffset _lastPeekFailureAtUtc = DateTimeOffset.MinValue;

	private int _peekFailureCount;

	private const double PeekFailureBackoffSeconds = 30.0;

	private const double PeekWatchdogSeconds = 30.0;

	private DateTimeOffset _peekDayUtc = DateTimeOffset.MinValue;

	private int _peekCountToday;

	private bool _privacyNoticeLogged;

	private IReadOnlyList<SceneMemoryEntry> _sceneMemory = Array.Empty<SceneMemoryEntry>();

	private string _sceneMemoryPath = "";

	private const int ScreenPeekTimeoutSeconds = 20;

	/// <summary>活动要持续这么久才算「用户真的在干这件事」（抖一下不算）。</summary>
	private static readonly TimeSpan ActivityConfirmWindow = TimeSpan.FromSeconds(10.0);

	/// <summary>确认之后再过这么久才截图，避免刚切过去就拍。</summary>
	private const double ActivityPeekDelaySeconds = 20.0;

	/// <summary>同一活动（或换回来）在这段时间内只触发一次窥屏。</summary>
	private const double ActivityPeekCooldownSeconds = 1800.0;

	private ActivityCategory _lastActivity = ActivityCategory.None;

	private DateTimeOffset _activitySinceUtc = DateTimeOffset.MinValue;

	private double _activityPeekDueAt;

	private ActivityCategory _activityPeekCategory = ActivityCategory.None;

	private DateTimeOffset _lastActivityPeekUtc = DateTimeOffset.MinValue;

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
			ResetStuckPeekIfNeeded(now);
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
		RelaxCapAfterLongSilence(settings, now);
		ProactiveContextProbe.ProbeResult probe = _probe.Probe();
		int bonus = ProactiveContextProbe.ComputeStateBonus(settings.UserState, probe.IdleMilliseconds, probe.IsGaming, probe.IsBrowsing);
		string gate = EvaluateGate(probe);
		LogEvent("tick", forced ? TestSourceTag : "-", interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: (gate.Length == 0) ? "ok" : gate, extra: "; idle_s=" + (IdleSeconds(probe)?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + "; bonus=" + bonus + "; staged=" + _staged.Count + "; expired=" + expired + "; character=" + (string.IsNullOrEmpty(probe.CharacterState) ? "-" : probe.CharacterState) + "; story=" + (probe.StoryPerforming ? "1" : "0") + "; activity=" + probe.Activity.ToString().ToLowerInvariant());

		// 换活动 → 延迟一次性窥屏：跟主动对话的掷骰完全独立（不看权重、不看兴趣、不占额度）。
		if (UpdateActivityPeek(settings, probe, nowUtc, now, gate))
		{
			return;
		}

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

	/// <summary>窥屏卡住时强制复位：转述请求万一不回调，_peeking 会一直是 true，把整条链路堵死。</summary>
	private void ResetStuckPeekIfNeeded(double now)
	{
		if (!_peeking || _peekStartedAt <= 0.0)
		{
			return;
		}
		if (now - _peekStartedAt < PeekWatchdogSeconds)
		{
			return;
		}
		_peeking = false;
		_peekStartedAt = 0.0;
		_lastPeekFailureAtUtc = DateTimeOffset.UtcNow;
		_peekFailureCount++;
		EndThinkingSafe();
		Log("module=Proactive; event=peek_watchdog_reset; waited_s=" + (int)(now - _peekStartedAt) + "; failures=" + _peekFailureCount);
	}

	/// <summary>
	/// 额度自愈：<c>maxPerUserMessage</c> 只按「用户发过消息」清零的话，用户一直不说话
	/// 就会永久停在 cap_reached。静默超过 2 个期望间隔后把额度放出来。
	/// </summary>
	private void RelaxCapAfterLongSilence(ProactiveSettings settings, double now)
	{
		if (settings.MaxPerUserMessage <= 0 || _count < settings.MaxPerUserMessage)
		{
			return;
		}
		double idle = now - _lastUserMessageAt;
		double relaxAfter = Math.Max(60.0, (double)settings.TargetIntervalSeconds * 2.0);
		if (idle < relaxAfter)
		{
			return;
		}
		// 也要隔开两条主动发言：否则额度一会就自愈，变成每拍都能开口。
		if (now - _lastProactiveAt < relaxAfter)
		{
			return;
		}
		_count = 0;
		LogEvent("cap_relaxed", "-", interest: _interest, count: 0, cap: settings.MaxPerUserMessage, reason: "idle_s=" + (int)idle + "; relax_after_s=" + (int)relaxAfter);
	}

	/// <summary>
	/// 用户换了活动（打游戏 / 看视频 / 上网 / 写代码）→ 确认 10s 后，再过 20s 固定窥一次屏。
	/// 与主动对话的掷骰完全独立：不看权重、不看兴趣、不占 <c>maxPerUserMessage</c> 额度，
	/// 只受「窥屏开关 + 视觉配置 + 最小间隔 + 每天上限」约束；同一次活动 30 分钟内只触发一次。
	/// </summary>
	/// <returns>true = 这一拍已经发起窥屏，后续掷骰跳过。</returns>
	private bool UpdateActivityPeek(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, double now, string gate)
	{
		ActivityCategory category = probe.Activity;
		if (category != _lastActivity)
		{
			_lastActivity = category;
			_activitySinceUtc = nowUtc;
			_activityPeekDueAt = 0.0;
			_activityPeekCategory = ActivityCategory.None;
			if (category != ActivityCategory.None)
			{
				LogEvent("activity_changed", category.ToString().ToLowerInvariant(), extra: "; proc=" + (string.IsNullOrEmpty(probe.ForegroundProcess) ? "-" : probe.ForegroundProcess));
			}
		}
		if (category == ActivityCategory.None || nowUtc - _activitySinceUtc < ActivityConfirmWindow)
		{
			return false;
		}
		if (_lastActivityPeekUtc != DateTimeOffset.MinValue && (nowUtc - _lastActivityPeekUtc).TotalSeconds < ActivityPeekCooldownSeconds)
		{
			return false;
		}
		if (_activityPeekDueAt <= 0.0)
		{
			_activityPeekDueAt = now + ActivityPeekDelaySeconds;
			_activityPeekCategory = category;
			LogEvent("activity_peek_scheduled", category.ToString().ToLowerInvariant(), extra: "; delay_s=" + (int)ActivityPeekDelaySeconds);
			return false;
		}
		if (_activityPeekCategory != category)
		{
			// 中途换了活动：重新计时，别拿旧活动的截图凑数。
			_activityPeekCategory = category;
			_activityPeekDueAt = now + ActivityPeekDelaySeconds;
			return false;
		}
		if (now < _activityPeekDueAt)
		{
			return false;
		}
		if (!settings.Sources.Screen)
		{
			_activityPeekDueAt = 0.0;
			LogEvent("skipped", "screen", reason: "activity_peek_screen_off");
			return false;
		}
		if (gate.Length > 0)
		{
			// 闸门不过（她在睡觉/正在生成/输入框有焦点…）：下一拍再试。
			return false;
		}
		if (!CanPeek(settings, out string reason))
		{
			_activityPeekDueAt = 0.0;
			LogEvent("skipped", "screen", reason: "activity_peek_unavailable:" + reason);
			return false;
		}
		_activityPeekDueAt = 0.0;
		_lastActivityPeekUtc = nowUtc;
		LogEvent("activity_peek", category.ToString().ToLowerInvariant(), extra: "; proc=" + (string.IsNullOrEmpty(probe.ForegroundProcess) ? "-" : probe.ForegroundProcess));
		BeginPeek(settings, probe, nowUtc, test: false);
		return true;
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
			if (CanPeek(settings, out string peekReason))
			{
				BeginPeek(settings, probe, nowUtc, test);
				return;
			}
			LogEvent("skipped", "screen", reason: "peek_unavailable:" + peekReason);
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

	/// <summary>
	/// 现在能不能截屏：窥屏开关 + 视觉配置 + Q8 的硬上限 + 失败退避。
	/// <paramref name="reason"/> 会写进日志和页面提示，别再让「没触发」变成黑盒。
	/// </summary>
	private bool CanPeek(ProactiveSettings settings, out string reason)
	{
		reason = "";
		if (!settings.Sources.Screen)
		{
			reason = "source_off";
			return false;
		}
		if (string.IsNullOrWhiteSpace(settings.VisionProfileId))
		{
			reason = "vision_unconfigured";
			return false;
		}
		DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
		if (_lastPeekFailureAtUtc != DateTimeOffset.MinValue && (nowUtc - _lastPeekFailureAtUtc).TotalSeconds < PeekFailureBackoffSeconds)
		{
			reason = "failure_backoff; left_s=" + (int)Math.Ceiling(PeekFailureBackoffSeconds - (nowUtc - _lastPeekFailureAtUtc).TotalSeconds) + "; failures=" + _peekFailureCount;
			return false;
		}
		if (settings.PeekMinIntervalSeconds > 0 && _lastPeekAtUtc != DateTimeOffset.MinValue)
		{
			double left = (double)settings.PeekMinIntervalSeconds - (nowUtc - _lastPeekAtUtc).TotalSeconds;
			if (left > 0.0)
			{
				reason = "min_interval; left_s=" + (int)Math.Ceiling(left);
				return false;
			}
		}
		if (settings.PeekMaxPerDay > 0)
		{
			RollPeekDay(nowUtc);
			if (_peekCountToday >= settings.PeekMaxPerDay)
			{
				reason = "daily_cap; used=" + _peekCountToday + "; cap=" + settings.PeekMaxPerDay;
				return false;
			}
		}
		return true;
	}

	/// <summary>把内部原因翻成人话，给页面提示用。</summary>
	internal static string DescribePeekDenial(string reason)
	{
		if (string.IsNullOrEmpty(reason))
		{
			return "";
		}
		if (reason.StartsWith("vision_unconfigured", StringComparison.Ordinal))
		{
			return "窥屏没开：还没选视觉模型（去「模型 / API」里挑一个 profile 填到「视觉模型」）";
		}
		if (reason.StartsWith("source_off", StringComparison.Ordinal))
		{
			return "窥屏没开：窥屏开关是关的";
		}
		if (reason.StartsWith("min_interval", StringComparison.Ordinal))
		{
			return "窥屏太频繁：距上次不足最小间隔（" + reason.Substring("min_interval; ".Length) + "）";
		}
		if (reason.StartsWith("daily_cap", StringComparison.Ordinal))
		{
			return "窥屏已达当天上限（" + reason.Substring("daily_cap; ".Length) + "）";
		}
		if (reason.StartsWith("failure_backoff", StringComparison.Ordinal))
		{
			return "上一次窥屏失败了，稍后再试（" + reason.Substring("failure_backoff; ".Length) + "）";
		}
		return reason;
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
		_peekStartedAt = Time.unscaledTime;
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
			_peekStartedAt = 0.0;
			MarkPeekFailed();
			DegradeToIdle(settings, probe, nowUtc, test, "capture_failed; code=" + error.GetType().Name);
			return;
		}
		if (!capture.Ok)
		{
			_peeking = false;
			_peekStartedAt = 0.0;
			MarkPeekFailed();
			DegradeToIdle(settings, probe, nowUtc, test, "capture_" + capture.Reason);
			return;
		}
		byte[] jpeg = capture.Jpeg!;
		LogEvent("peek", "screen", extra: "; capture_ms=" + capture.ElapsedMs + "; bytes=" + jpeg.Length + "; w=" + capture.Width + "; h=" + capture.Height + "; vision=" + settings.VisionProfileId + "; test=" + Bool(test));
		string? sceneReference = SceneMemoryStore.BuildReference(_sceneMemory, settings.SceneMemoryInjectCount);
		_controller.TranscribeScreen(jpeg, settings.VisionProfileId, ScreenPeekTimeoutSeconds, delegate(string? description)
		{
			_peeking = false;
			_peekStartedAt = 0.0;
			if (_disposed)
			{
				EndThinkingSafe();
				return;
			}
			if (string.IsNullOrWhiteSpace(description))
			{
				MarkPeekFailed();
				DegradeToIdle(settings, probe, nowUtc, test, "peek_failed");
				return;
			}
			// 成功才计入「最小间隔」和「每天上限」：失败不该让用户白等 5 分钟。
			_lastPeekAtUtc = DateTimeOffset.UtcNow;
			_peekFailureCount = 0;
			if (settings.PeekMaxPerDay > 0)
			{
				RollPeekDay(_lastPeekAtUtc);
				_peekCountToday++;
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

	private void MarkPeekFailed()
	{
		_lastPeekFailureAtUtc = DateTimeOffset.UtcNow;
		_peekFailureCount++;
	}

	/// <summary>
	/// 「现在试一次窥屏」：绕过兴趣与权重，但不绕过视觉配置、最小间隔、当天上限与失败退避。
	/// 返回空串表示已经发起；否则是给人看的原因。
	/// </summary>
	internal string RequestTestPeek()
	{
		if (_disposed)
		{
			return "主动对话服务未就绪";
		}
		ProactiveSettings settings = _settings;
		if (!settings.Enabled)
		{
			return "先打开「主动对话」总开关（窥屏依附于主动对话）";
		}
		if (!CanPeek(settings, out string reason))
		{
			return DescribePeekDenial(reason);
		}
		if (_peeking)
		{
			return "上一次窥屏还没结束，等一下";
		}
		try
		{
			BeginPeek(settings, _probe.Probe(), DateTimeOffset.UtcNow, test: true);
			LogEvent("peek_manual", "screen");
			return "";
		}
		catch (Exception error)
		{
			return "发起窥屏失败：" + error.GetType().Name;
		}
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
		_lastProactiveAt = Time.unscaledTime;
		LogEvent("delivered", tag, interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: staged ? "staged" : (test ? "test_mode" : "rolled"), extra: "; topic=" + ((_generator.LastTopicShape.Length == 0) ? "-" : _generator.LastTopicShape) + "; recent=" + _generator.RecentReplyCount);
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
			}, tag, delegate(string? reply)
			{
				// 记住她刚主动说过什么：下一轮指令里会明确要求别复读同一个话题。
				_generator.RememberReply(reply);
			});
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
