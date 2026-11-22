using System;

namespace Just_Lilith.Core.Llm;

/// <summary>
/// 一轮对话。<see cref="IsAmbient"/> 为 true 表示这一轮不是用户搭话引出的，
/// 而是角色自己主动说的：此时 <see cref="User"/> 为空、<see cref="Assistant"/> 才是正文。
/// 旧会话文件没有这个字段，反序列化后即为 false。
/// </summary>
public sealed record ConversationTurn(string User, string Assistant, DateTimeOffset UserOccurredAtUtc, DateTimeOffset AssistantOccurredAtUtc, bool IsAmbient = false);
