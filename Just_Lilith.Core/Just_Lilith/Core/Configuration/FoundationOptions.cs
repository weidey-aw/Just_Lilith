using System.Text.Json.Serialization;

namespace Just_Lilith.Core.Configuration;

/// <summary>
/// UI 相关的基础开关。放在基础项配置文件（local.just_lilith.json）里，不进任何界面。
/// </summary>
public sealed record FoundationUiOptions
{
	/// <summary>
	/// 旧的原生注入（API 页签 / TTS 行 / Lilith 行 / 热键行）是否继续挂在游戏本体设置页里。
	/// 默认 false = 这些设置只出现在独立的「MOD 设置」页里，游戏本体设置页保持原版；
	/// 改成 true 就回到阶段 B 改造前的行为，用来回退排查。
	/// </summary>
	[JsonPropertyName("legacy_native_injection")]
	public bool LegacyNativeInjection { get; init; }

	/// <summary>
	/// 是否把角色台词原文与 TTS 切片结果写进 BepInEx 日志（排查断句用）。
	/// 默认 true，因为日志是本地的、每次启动重建；不想留文本可以关掉。
	/// </summary>
	[JsonPropertyName("log_speech_text")]
	public bool LogSpeechText { get; init; } = true;
}

public sealed record FoundationOptions
{
	[JsonPropertyName("schema_version")]
	public int SchemaVersion { get; init; } = 1;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; } = true;

	[JsonPropertyName("verbose_logging")]
	public bool VerboseLogging { get; init; }

	[JsonPropertyName("ui")]
	public FoundationUiOptions Ui { get; init; } = new FoundationUiOptions();
}
