using System;
using Just_Lilith.Core.Proactive;

namespace Just_Lilith.Unity.Proactive;

/// <summary>
/// 按来源拼本次的 instruction（只作为本次请求的最后一条 System 消息，不落历史、不展示）。
/// <para>P0 只实现 <see cref="AmbientCueSource.IdleChat"/>；音乐/屏幕在这里留分支，
/// 等做窥屏和音乐由头时再补，不会影响调用方。</para>
/// </summary>
internal sealed class ProactiveGenerator
{
	private readonly Random _random;

	public ProactiveGenerator(Random random)
	{
		_random = random ?? throw new ArgumentNullException("random");
	}

	/// <summary>在"已启用且已实现"的来源里按权重随机挑一个。没有可用来源返回 null。</summary>
	public AmbientCueSource? PickSource(ProactiveSettings settings)
	{
		int total = 0;
		total += WeightIfUsable(settings, AmbientCueSource.IdleChat);
		total += WeightIfUsable(settings, AmbientCueSource.Music);
		total += WeightIfUsable(settings, AmbientCueSource.Screen);
		if (total <= 0)
		{
			return null;
		}
		int ticket = _random.Next(total);
		foreach (AmbientCueSource candidate in new AmbientCueSource[3]
		{
			AmbientCueSource.IdleChat,
			AmbientCueSource.Music,
			AmbientCueSource.Screen
		})
		{
			int weight = WeightIfUsable(settings, candidate);
			if (weight > 0)
			{
				if (ticket < weight)
				{
					return candidate;
				}
				ticket -= weight;
			}
		}
		return AmbientCueSource.IdleChat;
	}

	/// <summary>按来源造一条由头。P0 只有 idleChat 会返回非空。</summary>
	public AmbientCue? TryCreate(ProactiveSettings settings, AmbientCueSource source, int idleSeconds, DateTimeOffset nowUtc)
	{
		string instruction = BuildInstruction(source, idleSeconds);
		if (instruction == null)
		{
			return null;
		}
		return new AmbientCue(source, settings.Weights.Of(source), instruction, nowUtc, nowUtc.AddSeconds(Math.Max(5, settings.StageExpireSeconds)), settings.Weights.Of(source));
	}

	private static string? BuildInstruction(AmbientCueSource source, int idleSeconds)
	{
		switch (source)
		{
		case AmbientCueSource.IdleChat:
			return BuildIdleChatInstruction(idleSeconds);
		case AmbientCueSource.Music:
			// 音乐由头留到后续阶段实现。
			return null;
		case AmbientCueSource.Screen:
			// 窥屏由头由"功能二"单独做。
			return null;
		default:
			return null;
		}
	}

	private static string BuildIdleChatInstruction(int idleSeconds)
	{
		int minutes = Math.Max(0, idleSeconds / 60);
		string duration = ((minutes >= 1) ? (minutes + " 分钟") : "一会儿");
		return "（本次是你自己主动开口，不是在回应某条提问。）" + "用户已经 " + duration + "没有新的输入了，也没有在等你回答什么。" + "请你自己找个由头说一两句，语气自然、轻松，可以是关心、闲聊，或者把之前没说完的话题轻轻接下去；" + "不要解释你为什么突然说话，不要提到“主动”“系统”“提示”这类词，也不要复述这段说明。";
	}

	private static int WeightIfUsable(ProactiveSettings settings, AmbientCueSource source)
	{
		if (!settings.Sources.IsEnabled(source))
		{
			return 0;
		}
		if (!IsImplemented(source))
		{
			return 0;
		}
		return Math.Max(0, settings.Weights.Of(source));
	}

	private static bool IsImplemented(AmbientCueSource source)
	{
		return source == AmbientCueSource.IdleChat;
	}
}
