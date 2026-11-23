using System;
using System.Text.Json.Serialization;

namespace Just_Lilith.Core.Proactive;

/// <summary>主动对话的可用由头来源。P0 只实现 <see cref="IdleChat"/>，其余留位。</summary>
public enum AmbientCueSource
{
	IdleChat,
	Music,
	Screen
}

public sealed record ProactiveSources
{
	[JsonPropertyName("idleChat")]
	public bool IdleChat { get; init; } = true;

	[JsonPropertyName("music")]
	public bool Music { get; init; }

	[JsonPropertyName("screen")]
	public bool Screen { get; init; }

	public bool IsEnabled(AmbientCueSource source)
	{
		switch (source)
		{
		case AmbientCueSource.IdleChat:
			return IdleChat;
		case AmbientCueSource.Music:
			return Music;
		case AmbientCueSource.Screen:
			return Screen;
		default:
			return false;
		}
	}
}

public sealed record ProactiveWeights
{
	[JsonPropertyName("idleChat")]
	public int IdleChat { get; init; } = 60;

	[JsonPropertyName("music")]
	public int Music { get; init; } = 10;

	[JsonPropertyName("screen")]
	public int Screen { get; init; } = 30;

	public int Of(AmbientCueSource source)
	{
		switch (source)
		{
		case AmbientCueSource.IdleChat:
			return IdleChat;
		case AmbientCueSource.Music:
			return Music;
		case AmbientCueSource.Screen:
			return Screen;
		default:
			return 0;
		}
	}
}

/// <summary>"用户此刻在做什么"换算成的掷骰加成。只由前台布尔量与空闲毫秒数推出，不落盘。</summary>
public sealed record ProactiveUserState
{
	[JsonPropertyName("idlePenalty")]
	public int IdlePenalty { get; init; } = -15;

	[JsonPropertyName("gamingBonus")]
	public int GamingBonus { get; init; } = 20;

	[JsonPropertyName("browsingBonus")]
	public int BrowsingBonus { get; init; } = 10;
}

/// <summary>验收钩子：开启后忽略兴趣值，到点直接产出一条由头。发布包里默认关闭。</summary>
public sealed record ProactiveTestMode
{
	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; }

	[JsonPropertyName("forceAfterSeconds")]
	public int ForceAfterSeconds { get; init; } = 20;
}

/// <summary>
/// 主动对话配置。落盘为 BepInEx/config/local.just_lilith.proactive.json。
/// 默认全关（P0 发布包里 enabled=false，需要在文件里打开或通过 UI 打开）。
/// </summary>
public sealed record ProactiveSettings
{
	public const string FileName = "local.just_lilith.proactive.json";

	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; }

	[JsonPropertyName("maxPerUserMessage")]
	public int MaxPerUserMessage { get; init; } = 3;

	[JsonPropertyName("targetIntervalSeconds")]
	public int TargetIntervalSeconds { get; init; } = 300;

	[JsonPropertyName("checkPeriodSeconds")]
	public int CheckPeriodSeconds { get; init; } = 30;

	[JsonPropertyName("stageExpireSeconds")]
	public int StageExpireSeconds { get; init; } = 180;

	[JsonPropertyName("stageLimit")]
	public int StageLimit { get; init; } = 8;

	[JsonPropertyName("decayProbability")]
	public bool DecayProbability { get; init; } = true;

	[JsonPropertyName("sources")]
	public ProactiveSources Sources { get; init; } = new ProactiveSources();

	[JsonPropertyName("weights")]
	public ProactiveWeights Weights { get; init; } = new ProactiveWeights();

	[JsonPropertyName("userState")]
	public ProactiveUserState UserState { get; init; } = new ProactiveUserState();

	[JsonPropertyName("testMode")]
	public ProactiveTestMode TestMode { get; init; } = new ProactiveTestMode();

	/// <summary>
	/// 窥屏用的视觉模型：填三套配置里的 <c>profile-id</c>（如 profile-1）。
	/// 留空 = 未配置视觉模型，抽中"屏幕"由头时按 2.5 降级成普通话题。
	/// </summary>
	[JsonPropertyName("visionProfileId")]
	public string VisionProfileId { get; init; } = "";

	/// <summary>窥屏硬上限（Q8）：两次之间至少隔多久；0 = 不限制。</summary>
	[JsonPropertyName("peekMinIntervalSeconds")]
	public int PeekMinIntervalSeconds { get; init; } = 300;

	/// <summary>窥屏硬上限（Q8）：每天最多几次；0 = 不限制。</summary>
	[JsonPropertyName("peekMaxPerDay")]
	public int PeekMaxPerDay { get; init; } = 20;

	/// <summary>场景记忆环形保留条数；0 = 不保留。</summary>
	[JsonPropertyName("sceneMemoryLimit")]
	public int SceneMemoryLimit { get; init; } = 10;

	/// <summary>场景记忆保留天数；0 = 不限天数。</summary>
	[JsonPropertyName("sceneMemoryDays")]
	public int SceneMemoryDays { get; init; } = 7;

	/// <summary>后续请求注入最近几条场景记忆（1–2）。</summary>
	[JsonPropertyName("sceneMemoryInjectCount")]
	public int SceneMemoryInjectCount { get; init; } = 2;

	/// <summary>
	/// 把配置夹到可用范围后返回（不抛异常：坏值就地纠正，用户文件不会被拒）。
	/// 空的可选段（JSON 里写成 null）回落到默认值。
	/// </summary>
	public ProactiveSettings Validate()
	{
		ProactiveSources sources = Sources ?? new ProactiveSources();
		ProactiveWeights weights = Weights ?? new ProactiveWeights();
		ProactiveUserState userState = UserState ?? new ProactiveUserState();
		ProactiveTestMode testMode = TestMode ?? new ProactiveTestMode();
		return this with
		{
			MaxPerUserMessage = Clamp(MaxPerUserMessage, 1, 10, 3),
			TargetIntervalSeconds = Clamp(TargetIntervalSeconds, 30, 3600, 300),
			CheckPeriodSeconds = Clamp(CheckPeriodSeconds, 10, 600, 30),
			StageExpireSeconds = Clamp(StageExpireSeconds, 5, 3600, 180),
			StageLimit = Clamp(StageLimit, 1, 32, 8),
			Sources = sources,
			Weights = weights with
			{
				// 权重和加减成里 0 是"关掉/不加成"的合法值，不能当成缺省回落成默认值。
				IdleChat = ClampAllowZero(weights.IdleChat, 0, 1000),
				Music = ClampAllowZero(weights.Music, 0, 1000),
				Screen = ClampAllowZero(weights.Screen, 0, 1000)
			},
			UserState = userState with
			{
				IdlePenalty = ClampAllowZero(userState.IdlePenalty, -50, 50),
				GamingBonus = ClampAllowZero(userState.GamingBonus, -50, 50),
				BrowsingBonus = ClampAllowZero(userState.BrowsingBonus, -50, 50)
			},
			TestMode = testMode with
			{
				ForceAfterSeconds = Clamp(testMode.ForceAfterSeconds, 5, 3600, 20)
			},
			VisionProfileId = (VisionProfileId ?? "").Trim(),
			PeekMinIntervalSeconds = ClampAllowZero(PeekMinIntervalSeconds, 0, 86400),
			PeekMaxPerDay = ClampAllowZero(PeekMaxPerDay, 0, 500),
			SceneMemoryLimit = ClampAllowZero(SceneMemoryLimit, 0, 100),
			SceneMemoryDays = ClampAllowZero(SceneMemoryDays, 0, 365),
			SceneMemoryInjectCount = ClampAllowZero(SceneMemoryInjectCount, 0, 5)
		};
	}

	/// <summary>这几个 0 是"关闭/不限制"的合法值，不能被当成缺省回落。</summary>
	private static int ClampAllowZero(int value, int minimum, int maximum)
	{
		if (value == 0)
		{
			return 0;
		}
		return Math.Clamp(value, minimum, maximum);
	}

	private static int Clamp(int value, int minimum, int maximum, int fallback)
	{
		if (value == 0 && fallback != 0)
		{
			return fallback;
		}
		return Math.Clamp(value, minimum, maximum);
	}
}
