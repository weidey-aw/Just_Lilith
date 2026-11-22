using System;
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

	public ProactiveChatService(LlmConfigurationController controller, LlmConfigurationView view, LlmGameBridge bridge, MainThreadSpeechController? speech, string settingsPath, ManualLogSource log)
	{
		_controller = controller ?? throw new ArgumentNullException("controller");
		_view = view;
		_bridge = bridge;
		_speech = speech;
		_log = log;
		_settingsPath = settingsPath;
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
		Log("module=Proactive; state=ready; enabled=" + Bool(_settings.Enabled) + "; target_s=" + _settings.TargetIntervalSeconds + "; period_s=" + _settings.CheckPeriodSeconds + "; cap=" + _settings.MaxPerUserMessage + "; increment=" + InterestModel.DescribeIncrement(InterestModel.SolveIncrement(_settings.TargetIntervalSeconds, _settings.MaxPerUserMessage, _settings.CheckPeriodSeconds)));
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
			if (settings.TestMode.Enabled && !_testDelivered && !_generating)
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

	private void StageCue(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, string reason)
	{
		AmbientCue? cue = BuildCue(settings, probe, nowUtc);
		if (cue == null)
		{
			LogEvent("skipped", "-", reason: "no_source", extra: "; gate=" + reason);
			return;
		}
		_staged.TryEnqueue(cue, out AmbientCue? dropped);
		LogEvent("staged", cue.SourceTag, interest: _interest, count: _count, cap: settings.MaxPerUserMessage, reason: reason, extra: "; expires_in=" + Math.Max(5, settings.StageExpireSeconds) + "; staged=" + _staged.Count + ((dropped != null) ? ("; dropped=" + dropped.SourceTag) : ""));
	}

	private void CueAndDeliver(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc, bool test)
	{
		AmbientCue? cue = BuildCue(settings, probe, nowUtc);
		if (cue == null)
		{
			LogEvent("skipped", test ? TestSourceTag : "-", reason: "no_source");
			return;
		}
		Deliver(cue, settings, test, staged: false);
	}

	private AmbientCue? BuildCue(ProactiveSettings settings, ProactiveContextProbe.ProbeResult probe, DateTimeOffset nowUtc)
	{
		AmbientCueSource? source = _generator.PickSource(settings);
		if (!source.HasValue)
		{
			return null;
		}
		int idleSeconds = IdleSeconds(probe) ?? Math.Max(0, (int)(Time.unscaledTime - _lastUserMessageAt));
		return _generator.TryCreate(settings, source.GetValueOrDefault(), idleSeconds, nowUtc);
	}

	private void Deliver(AmbientCue cue, ProactiveSettings settings, bool test, bool staged)
	{
		if (_generating || _disposed)
		{
			LogEvent("skipped", cue.SourceTag, reason: "generating");
			return;
		}
		string tag = test ? TestSourceTag : cue.SourceTag;
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
		if (_controller.IsGenerating || _generating)
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
		Log("module=Proactive; state=settings_reloaded; enabled=" + Bool(reloaded.Enabled) + "; target_s=" + reloaded.TargetIntervalSeconds + "; period_s=" + reloaded.CheckPeriodSeconds + "; cap=" + reloaded.MaxPerUserMessage + "; test_mode=" + Bool(reloaded.TestMode.Enabled) + ((diagnostic != null) ? ("; reason=" + diagnostic) : ""));
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
			_staged.Clear();
		}
	}
}
