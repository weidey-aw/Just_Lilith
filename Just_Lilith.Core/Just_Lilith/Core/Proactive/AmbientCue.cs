using System;

namespace Just_Lilith.Core.Proactive;

/// <summary>
/// 一条"由头"：这次主动开口的理由/落点。
/// <para>
/// <see cref="Instruction"/> 只作为本次请求的最后一条系统消息，不落历史、不展示、不统计。
/// </para>
/// </summary>
public sealed record AmbientCue(AmbientCueSource Source, int Weight, string Instruction, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int Priority = 0)
{
	/// <summary>日志里用的短名：idleChat / music / screen。</summary>
	public string SourceTag
	{
		get
		{
			switch (Source)
			{
			case AmbientCueSource.IdleChat:
				return "idleChat";
			case AmbientCueSource.Music:
				return "music";
			case AmbientCueSource.Screen:
				return "screen";
			default:
				return "unknown";
			}
		}
	}

	public bool IsExpired(DateTimeOffset nowUtc)
	{
		return nowUtc >= ExpiresAt;
	}
}
