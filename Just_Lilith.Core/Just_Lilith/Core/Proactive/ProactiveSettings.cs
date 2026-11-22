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
				IdleChat = Clamp(weights.IdleChat, 0, 1000, 60),
				Music = Clamp(weights.Music, 0, 1000, 10),
				Screen = Clamp(weights.Screen, 0, 1000, 30)
			},
			UserState = userState with
			{
				IdlePenalty = Clamp(userState.IdlePenalty, -50, 50, -15),
				GamingBonus = Clamp(userState.GamingBonus, -50, 50, 20),
				BrowsingBonus = Clamp(userState.BrowsingBonus, -50, 50, 10)
			},
			TestMode = testMode with
			{
				ForceAfterSeconds = Clamp(testMode.ForceAfterSeconds, 5, 3600, 20)
			}
		};
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
