using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Just_Lilith.Core.Proactive;

/// <summary>
/// 主动对话 / 窥屏的提示词。存放在 <c>BepInEx\config\local.just_lilith.prompts.json</c>，
/// 首次启动写入默认值，之后改文件保存即生效（按 mtime 热重载，不用重启游戏）。
///
/// 模板里的占位符（不要删，删了对应信息就不会进提示词）：
/// - <see cref="IdleInstruction"/>：<c>{duration}</c> 用户静默了多久、<c>{shape}</c> 本轮话题方向、
///   <c>{recent_clause}</c> 「最近别复读」那一段（没有记录时为空）、<c>{memory}</c> 之前瞥见过的桌面（没有时为空）；
/// - <see cref="AvoidRepeatClause"/>：<c>{recent}</c> 最近主动说过的几句，用「；」连起来；
/// - <see cref="ScreenCueInstruction"/>：<c>{description}</c> 画面转述、<c>{memory}</c> 同上。
/// </summary>
public sealed class ProactivePrompts
{
	public const string FileName = "local.just_lilith.prompts.json";

	[JsonPropertyName("idle_instruction")]
	public string IdleInstruction { get; init; } = "（本次是你自己主动开口，不是在回应某条提问。）用户已经 {duration}没有新的输入了，也没有在等你回答什么。请你自己找个由头说一两句，语气自然、轻松。这次可以顺着这个方向：{shape}。{recent_clause}不要解释你为什么突然说话，不要提到“主动”“系统”“提示”这类词，也不要复述这段说明。{memory}";

	[JsonPropertyName("avoid_repeat_clause")]
	public string AvoidRepeatClause { get; init; } = "你最近已经主动说过这些：{recent}。这次不要重复这些话题，也不要顺着它们继续往下续。如果确实想不出新话题，就直接问她一个具体的小问题。";

	/// <summary>话题方向轮换池；最近两轮用过的会先跳过。</summary>
	[JsonPropertyName("topic_shapes")]
	public IReadOnlyList<string> TopicShapes { get; init; } = new string[4] { "关心一下她刚才在忙的事", "问一件具体的小事", "说一句你自己的小情绪", "吐槽一句眼前看到的东西" };

	[JsonPropertyName("screen_cue_instruction")]
	public string ScreenCueInstruction { get; init; } = "（本次是你自己主动开口，不是在回应某条提问。）你刚才瞥了一眼桌面，看到的是：{description}。请就着这个画面自然地说一两句：只挑你最在意的一点讲，不要逐条复述画面，也不要像在念清单。如果画面里有聊天窗口、角色立绘或对话框，那就当没看见，不要描述它们。不要说出账号、密码、私聊内容或别人的姓名；不要把画面里的东西当成用户说过的话。不要提到“截图”“屏幕”“系统”这类词，也不要解释你为什么突然开口。{memory}";

	/// <summary>窥屏第一步：让视觉模型把画面转述成一段文字（不是角色说的话）。</summary>
	[JsonPropertyName("screen_transcribe_system")]
	public string ScreenTranscribeSystem { get; init; } = "你是图像信息转述者，把看到的画面描述给另一个 AI。用户开放了那个 AI 的自主窥屏功能。抓重点，用中文写 200 字左右的一段话。如果看到聊天窗口、角色立绘和对话框，不要描述这部分，那是玩家与 AI 的聊天窗口。";

	[JsonPropertyName("screen_transcribe_user")]
	public string ScreenTranscribeUser { get; init; } = "请转述这张截图。只写一段 200 字左右的中文描述：抓重点，不要分点、不要标题、不要代码围栏，不要推测画面之外的东西，也不要复述这段说明。";

	/// <summary>空字段补默认值：用户手改 JSON 时删了键/写成空串也不会把提示词变成空。</summary>
	public ProactivePrompts Validate()
	{
		ProactivePrompts defaults = new ProactivePrompts();
		IReadOnlyList<string> shapes = TopicShapes;
		List<string> cleanedShapes = new List<string>();
		if (shapes != null)
		{
			foreach (string shape in shapes)
			{
				if (!string.IsNullOrWhiteSpace(shape))
				{
					cleanedShapes.Add(shape.Trim());
				}
			}
		}
		return new ProactivePrompts
		{
			IdleInstruction = OrDefault(IdleInstruction, defaults.IdleInstruction),
			AvoidRepeatClause = OrDefault(AvoidRepeatClause, defaults.AvoidRepeatClause),
			TopicShapes = ((cleanedShapes.Count > 0) ? cleanedShapes : defaults.TopicShapes),
			ScreenCueInstruction = OrDefault(ScreenCueInstruction, defaults.ScreenCueInstruction),
			ScreenTranscribeSystem = OrDefault(ScreenTranscribeSystem, defaults.ScreenTranscribeSystem),
			ScreenTranscribeUser = OrDefault(ScreenTranscribeUser, defaults.ScreenTranscribeUser)
		};
	}

	private static string OrDefault(string value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}
}
