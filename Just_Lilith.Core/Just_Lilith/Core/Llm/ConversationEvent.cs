using System;

namespace Just_Lilith.Core.Llm;

/// <summary>
/// 会话里的一条事件。正常一轮是 UserInput + LlmReply 一对；
/// 主动对话的一轮同样是"一对"，但两条的 <see cref="Ambient"/> 都为 true，
/// 且 UserInput 的 Content 为空字符串（历史里不出现空白用户发言靠 UI 过滤）。
/// </summary>
public sealed record ConversationEvent(ConversationEventType Type, string Content, DateTimeOffset OccurredAtUtc, bool Ambient = false);
