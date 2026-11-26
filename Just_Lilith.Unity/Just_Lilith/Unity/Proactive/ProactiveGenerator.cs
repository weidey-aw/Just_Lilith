using System;
using System.Collections.Generic;
using Just_Lilith.Core.Proactive;

namespace Just_Lilith.Unity.Proactive;

/// <summary>
/// 按来源拼本次的 instruction（只作为本次请求的最后一条 System 消息，不落历史、不展示）。
/// <para>P0 支持 idleChat；本版加 screen（窥屏转述）。music 仍然只留分支。</para>
/// </summary>
internal sealed class ProactiveGenerator
{
	/// <summary>记住最近几条主动发言（正文摘要），用来避免复读同一个话题。</summary>
	private const int RecentReplyLimit = 5;

	/// <summary>话题形状轮换：最近两轮用过的形状先不用。</summary>
	private const int ShapeHistoryLimit = 2;

	private readonly Random _random;

	/// <summary>提示词（来自 local.just_lilith.prompts.json，可热更新）。</summary>
	private ProactivePrompts _prompts = new ProactivePrompts();

	private readonly List<string> _recentReplies = new List<string>();

	private readonly List<string> _recentShapes = new List<string>();

	/// <summary>RememberReply 在后台线程调用，构造指令在主线程，两边都要加锁。</summary>
	private readonly object _sync = new object();

	/// <summary>最近一次构造闲聊指令时用的话题形状（只用于日志）。</summary>
	internal string LastTopicShape { get; private set; } = "";

	/// <summary>当前记住的主动发言条数（只用于日志）。</summary>
	internal int RecentReplyCount
	{
		get
		{
			lock (_sync)
			{
				return _recentReplies.Count;
			}
		}
	}

	public ProactiveGenerator(Random random)
	{
		_random = random ?? throw new ArgumentNullException("random");
	}

	/// <summary>换一套提示词（配置文件热更新时由服务调用）。</summary>
	internal ProactivePrompts Prompts
	{
		get
		{
			return _prompts;
		}
		set
		{
			_prompts = value ?? new ProactivePrompts();
		}
	}

	/// <summary>记一条刚刚真的说出去的主动发言（只留前 40 字）。</summary>
	internal void RememberReply(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		string compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
		// 去掉句末标点：下面会用「；」把它们串起来，不然会出现「。；」这种别扭的拼接。
		while (compact.Length > 0 && "。！？…，、,.!?".IndexOf(compact[compact.Length - 1]) >= 0)
		{
			compact = compact.Substring(0, compact.Length - 1).TrimEnd();
		}
		if (compact.Length > 40)
		{
			compact = compact.Substring(0, 40) + "…";
		}
		lock (_sync)
		{
			_recentReplies.Add(compact);
			while (_recentReplies.Count > RecentReplyLimit)
			{
				_recentReplies.RemoveAt(0);
			}
		}
	}

	/// <summary>
	/// 在"已启用且已实现"的来源里挑一个。
	/// 窥屏按「权重 / 1000」直接当概率：1000 = 每轮都窥屏（仍受硬上限约束），
	/// 没抽中窥屏时退回闲聊；闲聊也没开就本轮不开口。
	/// </summary>
	public AmbientCueSource? PickSource(ProactiveSettings settings)
	{
		if (IsUsable(settings, AmbientCueSource.Screen))
		{
			int weight = Math.Clamp(settings.Weights.Of(AmbientCueSource.Screen), 0, 1000);
			if (weight >= 1000)
			{
				return AmbientCueSource.Screen;
			}
			if (weight > 0 && _random.Next(1000) < weight)
			{
				return AmbientCueSource.Screen;
			}
		}
		if (IsUsable(settings, AmbientCueSource.IdleChat))
		{
			return AmbientCueSource.IdleChat;
		}
		return null;
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

	private string? BuildInstruction(AmbientCueSource source, int idleSeconds, string? screenDescription, string? sceneMemory)
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

	/// <summary>
	/// 闲聊由头。以前这里写着「把之前没说完的话题轻轻接下去」，而系统又不知道上一轮说过什么，
	/// 结果就是每轮都绕着同一个话题打转。现在改成：给一个「最近没用的方向」，并且明确要求别复读。
	/// </summary>
	private string BuildIdleChatInstruction(int idleSeconds, string? sceneMemory)
	{
		int minutes = Math.Max(0, idleSeconds / 60);
		string duration = ((minutes >= 1) ? (minutes + " 分钟") : "一会儿");
		string shape = PickTopicShape();
		return _prompts.IdleInstruction.Replace("{duration}", duration).Replace("{shape}", shape).Replace("{recent_clause}", BuildAvoidRepeatClause())
			.Replace("{memory}", AppendMemory(sceneMemory));
	}

	/// <summary>最近几轮说过的话 + 明确要求换话题（模板在 avoid_repeat_clause）。</summary>
	private string BuildAvoidRepeatClause()
	{
		lock (_sync)
		{
			if (_recentReplies.Count == 0)
			{
				return "";
			}
			string joined = string.Join("；", _recentReplies);
			return _prompts.AvoidRepeatClause.Replace("{recent}", joined);
		}
	}

	/// <summary>挑一个最近两轮没用过的话题形状（候选来自 topic_shapes）。</summary>
	private string PickTopicShape()
	{
		lock (_sync)
		{
			IReadOnlyList<string> shapes = _prompts.TopicShapes;
			if (shapes == null || shapes.Count == 0)
			{
				return "";
			}
			List<string> available = new List<string>(shapes.Count);
			foreach (string shape in shapes)
			{
				if (!_recentShapes.Contains(shape))
				{
					available.Add(shape);
				}
			}
			string picked = ((available.Count > 0) ? available[_random.Next(available.Count)] : shapes[_random.Next(shapes.Count)]);
			_recentShapes.Add(picked);
			while (_recentShapes.Count > ShapeHistoryLimit)
			{
				_recentShapes.RemoveAt(0);
			}
			LastTopicShape = picked;
			return picked;
		}
	}

	/// <summary>
	/// 窥屏由头。除了给画面，还把表达约束一起写进这条系统指令：
	/// 只挑最在意的一点、不逐条念画面、隐私不出口、不把屏幕当用户发言、不描述自己的窗口。
	/// 措辞在 prompts.json 的 screen_cue_instruction 里。
	/// </summary>
	private string BuildScreenInstruction(string description, string? sceneMemory)
	{
		return _prompts.ScreenCueInstruction.Replace("{description}", description.Trim()).Replace("{memory}", AppendMemory(sceneMemory));
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
