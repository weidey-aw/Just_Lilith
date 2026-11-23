using System;
using Just_Lilith.Core.Proactive;

namespace Just_Lilith.Unity.Proactive;

/// <summary>
/// 按来源拼本次的 instruction（只作为本次请求的最后一条 System 消息，不落历史、不展示）。
/// <para>P0 支持 idleChat；本版加 screen（窥屏转述）。music 仍然只留分支。</para>
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
		foreach (AmbientCueSource candidate in AllSources)
		{
			total += WeightIfUsable(settings, candidate);
		}
		if (total <= 0)
		{
			return null;
		}
		int ticket = _random.Next(total);
		foreach (AmbientCueSource candidate2 in AllSources)
		{
			int weight = WeightIfUsable(settings, candidate2);
			if (weight > 0)
			{
				if (ticket < weight)
				{
					return candidate2;
				}
				ticket -= weight;
			}
		}
		return AmbientCueSource.IdleChat;
	}

	/// <summary>这个来源现在能不能用（开关 + 权重 + 已实现）。</summary>
	public static bool IsUsable(ProactiveSettings settings, AmbientCueSource source)
	{
		return WeightIfUsable(settings, source) > 0;
	}

	/// <summary>
	/// 造一条由头。idleChat 只用 idleSeconds；screen 需要 <paramref name="screenDescription"/>（转述结果）。
	/// <paramref name="sceneMemory"/> 是"之前看到过"的片段，拼进同一条 System 指令里（不进历史）。
	/// </summary>
	public AmbientCue? TryCreate(ProactiveSettings settings, AmbientCueSource source, int idleSeconds, DateTimeOffset nowUtc, string? screenDescription = null, string? sceneMemory = null)
	{
		string? instruction = BuildInstruction(source, idleSeconds, screenDescription, sceneMemory);
		if (instruction == null)
		{
			return null;
		}
		return new AmbientCue(source, settings.Weights.Of(source), instruction, nowUtc, nowUtc.AddSeconds(Math.Max(5, settings.StageExpireSeconds)), settings.Weights.Of(source));
	}

	private static string? BuildInstruction(AmbientCueSource source, int idleSeconds, string? screenDescription, string? sceneMemory)
	{
		switch (source)
		{
		case AmbientCueSource.IdleChat:
			return BuildIdleChatInstruction(idleSeconds, sceneMemory);
		case AmbientCueSource.Music:
			// 音乐由头留到后续阶段实现。
			return null;
		case AmbientCueSource.Screen:
			if (string.IsNullOrWhiteSpace(screenDescription))
			{
				return null;
			}
			return BuildScreenInstruction(screenDescription!, sceneMemory);
		default:
			return null;
		}
	}

	private static string BuildIdleChatInstruction(int idleSeconds, string? sceneMemory)
	{
		int minutes = Math.Max(0, idleSeconds / 60);
		string duration = ((minutes >= 1) ? (minutes + " 分钟") : "一会儿");
		return "（本次是你自己主动开口，不是在回应某条提问。）" + "用户已经 " + duration + "没有新的输入了，也没有在等你回答什么。" + "请你自己找个由头说一两句，语气自然、轻松，可以是关心、闲聊，或者把之前没说完的话题轻轻接下去；" + "不要解释你为什么突然说话，不要提到“主动”“系统”“提示”这类词，也不要复述这段说明。" + AppendMemory(sceneMemory);
	}

	/// <summary>
	/// 窥屏由头。除了给画面，还把 2.6 的表达约束一起写进这条系统指令：
	/// 只挑最在意的一点、不逐条念画面、隐私不出口、不把屏幕当用户发言、不描述自己的窗口。
	/// </summary>
	private static string BuildScreenInstruction(string description, string? sceneMemory)
	{
		return "（本次是你自己主动开口，不是在回应某条提问。）" + "你刚才瞥了一眼桌面，看到的是：" + description.Trim() + "。" + "请就着这个画面自然地说一两句：只挑你最在意的一点讲，不要逐条复述画面，也不要像在念清单。" + "如果画面里有聊天窗口、角色立绘或对话框，那就当没看见，不要描述它们。" + "不要说出账号、密码、私聊内容或别人的姓名；不要把画面里的东西当成用户说过的话。" + "不要提到“截图”“屏幕”“系统”这类词，也不要解释你为什么突然开口。" + AppendMemory(sceneMemory);
	}

	private static string AppendMemory(string? sceneMemory)
	{
		if (string.IsNullOrWhiteSpace(sceneMemory))
		{
			return "";
		}
		return sceneMemory!.Trim();
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
		return source == AmbientCueSource.IdleChat || source == AmbientCueSource.Screen;
	}

	private static readonly AmbientCueSource[] AllSources = new AmbientCueSource[3]
	{
		AmbientCueSource.IdleChat,
		AmbientCueSource.Music,
		AmbientCueSource.Screen
	};
}
