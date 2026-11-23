using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Just_Lilith.Core.Speech;

namespace Just_Lilith.Unity.Speech;

internal static class SpeechSplitSynthesizer
{
	private readonly record struct SpeechChunk(string Text, float PauseBeforeSeconds);

	private const int AdaptiveSpeakableThreshold = 60;

	private const int MaxChunkSpeakable = 56;

	private const float SentenceJoinPauseSeconds = 0.12f;

	private const string LongPauseDash = "——";

	private const string LongPauseEllipsis = "……";

	public static async Task<PcmWaveData> SynthesizeAsync(LocalTtsClient client, SpeechSynthesisRequest request, CancellationToken token)
	{
		string text = SpeechTextSanitizer.StripTrailingSignature(request.Text);
		if (text.Length > 0 && !string.Equals(text, request.Text, StringComparison.Ordinal))
		{
			request = request with
			{
				Text = text
			};
		}
		int num = SpeakableCount(request.Text);
		if (!request.Text.Contains("——", StringComparison.Ordinal) && !request.Text.Contains("……", StringComparison.Ordinal) && num <= 60)
		{
			return await SynthesizeSingleValidatedAsync(client, request, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		List<SpeechChunk> list = BuildChunks(request.Text);
		if (list.Count <= 1)
		{
			return await SynthesizeSingleValidatedAsync(client, request, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		PcmWaveData pcmWaveData = await TryChunkedAsync(client, request, list, token).ConfigureAwait(continueOnCapturedContext: false);
		if ((object)pcmWaveData == null)
		{
			pcmWaveData = await SynthesizeSingleValidatedAsync(client, request, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		return pcmWaveData;
	}

	private static List<SpeechChunk> BuildChunks(string text)
	{
		List<SpeechChunk> list = new List<SpeechChunk>();
		float num = 0f;
		string[] array = Regex.Split(text, "(——|……)");
		foreach (string text2 in array)
		{
			if (text2 == "——")
			{
				num += 0.4f;
				continue;
			}
			if (text2 == "……")
			{
				num++;
				continue;
			}
			string text3 = text2.Trim();
			if (text3.Length != 0)
			{
				if (SpeakableCount(text3) <= 3)
				{
					return new List<SpeechChunk>();
				}
				List<string> list2 = SplitLongSegment(text3);
				for (int j = 0; j < list2.Count; j++)
				{
					float pauseBeforeSeconds = ((j == 0) ? num : 0.12f);
					list.Add(new SpeechChunk(list2[j], pauseBeforeSeconds));
				}
				num = 0f;
			}
		}
		return CoalesceShortChunks(list);
	}

	private static List<string> SplitLongSegment(string segment)
	{
		List<string> list = new List<string>();
		if (SpeakableCount(segment) <= 56)
		{
			list.Add(segment);
			return list;
		}
		return ChunkByBoundaries(segment, 56);
	}

	/// <summary>
	/// 按「句末标点 &gt; 分句标点 &gt; 逗号顿号 &gt; 硬切」的优先级把文本切成不超过 max 的段。
	/// 关键点：切点是在整个窗口里找**最后一个**标点，而不是像以前只在 max-8 那 8 个字里找 ——
	/// 中文长句常常整段只有一个逗号，旧逻辑找不到就按字数硬切，听感就是「一句话被劈开」。
	/// </summary>
	private static List<string> ChunkByBoundaries(string text, int maxSpeakable)
	{
		List<string> list = new List<string>();
		string rest = text.Trim();
		int guard = 0;
		while (rest.Length > 0 && guard++ < 128)
		{
			if (SpeakableCount(rest) <= maxSpeakable)
			{
				list.Add(rest);
				break;
			}
			int cut = FindCutIndex(rest, maxSpeakable, Math.Max(6, maxSpeakable * 2 / 5));
			if (cut <= 0 || cut >= rest.Length)
			{
				list.Add(rest);
				break;
			}
			string head = rest.Substring(0, cut).Trim();
			rest = rest.Substring(cut).Trim();
			if (head.Length > 0)
			{
				list.Add(head);
			}
		}
		return list;
	}

	/// <summary>
	/// 在不超过 <paramref name="maxSpeakable"/> 的范围内挑切点，返回切点后的下标；挑不到返回 -1。
	/// 句末标点只要出现在「够长」的位置（≥ 70% 上限）就优先用，否则退到逗号顿号，再不行硬切。
	/// </summary>
	private static int FindCutIndex(string text, int maxSpeakable, int minSpeakable)
	{
		int speakable = 0;
		int hardCut = -1;
		int sentenceCut = -1;
		int clauseCut = -1;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
			{
				speakable++;
			}
			int cut = i + 1;
			if (speakable >= maxSpeakable && hardCut < 0)
			{
				hardCut = cut;
			}
			if (speakable > maxSpeakable)
			{
				break;
			}
			if (speakable < minSpeakable)
			{
				continue;
			}
			if (IsSentenceBoundary(c))
			{
				sentenceCut = cut;
			}
			else if (IsClauseBoundary(c))
			{
				clauseCut = cut;
			}
		}
		int lateThreshold = Math.Max(minSpeakable, maxSpeakable * 7 / 10);
		if (sentenceCut > 0 && SpeakableCount(text.Substring(0, sentenceCut)) >= lateThreshold)
		{
			return sentenceCut;
		}
		if (clauseCut > 0)
		{
			return clauseCut;
		}
		if (sentenceCut > 0)
		{
			return sentenceCut;
		}
		return hardCut;
	}

	private static bool IsSentenceBoundary(char c)
	{
		return c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '\n' || c == '；' || c == ';' || c == '：' || c == ':';
	}

	private static bool IsClauseBoundary(char c)
	{
		return c == '，' || c == '、' || c == ',' || c == ' ';
	}

	/// <summary>兼容旧调用点：按上限切，走同一套标点优先规则。</summary>
	private static List<string> HardSplit(string sentence, int maxSpeakable)
	{
		return ChunkByBoundaries(sentence, maxSpeakable);
	}

	private static List<SpeechChunk> CoalesceShortChunks(List<SpeechChunk> chunks)
	{
		if (chunks.Count <= 1)
		{
			return chunks;
		}
		List<SpeechChunk> list = new List<SpeechChunk>(chunks.Count);
		foreach (SpeechChunk chunk in chunks)
		{
			if (list.Count > 0 && SpeakableCount(chunk.Text) <= 6 && SpeakableCount(list[list.Count - 1].Text) + SpeakableCount(chunk.Text) <= MaxChunkSpeakable)
			{
				SpeechChunk speechChunk = list[list.Count - 1];
				list[list.Count - 1] = speechChunk with
				{
					Text = speechChunk.Text + "，" + chunk.Text
				};
			}
			else
			{
				list.Add(chunk);
			}
		}
		if (list.Count > 1 && SpeakableCount(list[0].Text) <= 6 && SpeakableCount(list[0].Text) + SpeakableCount(list[1].Text) <= MaxChunkSpeakable)
		{
			SpeechChunk speechChunk2 = list[0];
			SpeechChunk speechChunk3 = list[1];
			list.RemoveAt(0);
			list[0] = speechChunk3 with
			{
				Text = speechChunk2.Text + "，" + speechChunk3.Text,
				PauseBeforeSeconds = speechChunk2.PauseBeforeSeconds
			};
		}
		return list;
	}

	private static async Task<PcmWaveData> SynthesizeSingleValidatedAsync(LocalTtsClient client, SpeechSynthesisRequest request, CancellationToken token)
	{
		PcmWaveData first = await client.SynthesizeAsync(request, token).ConfigureAwait(continueOnCapturedContext: false);
		if (!IsBadWave(first, SpeakableCount(request.Text)))
		{
			return first;
		}
		try
		{
			PcmWaveData pcmWaveData = await client.SynthesizeAsync(request with
			{
				RequestId = Guid.NewGuid()
			}, token).ConfigureAwait(continueOnCapturedContext: false);
			if (!IsBadWave(pcmWaveData, SpeakableCount(request.Text)))
			{
				return pcmWaveData;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
		}
		return first;
	}

	private static async Task<PcmWaveData?> TryChunkedAsync(LocalTtsClient client, SpeechSynthesisRequest request, List<SpeechChunk> chunks, CancellationToken token)
	{
		try
		{
			int sampleRate = 0;
			int channels = 0;
			List<float> samples = new List<float>();
			bool flag = true;
			foreach (SpeechChunk chunk in chunks)
			{
				string text = EnsureTerminalPunctuation(chunk.Text);
				int speakable = SpeakableCount(chunk.Text);
				SpeechDirective directive = (flag ? request.Directive : request.Directive with
				{
					ReactionId = null
				});
				PcmWaveData pcmWaveData = await SynthesizeValidatedChunkAsync(client, request, text, directive, speakable, token).ConfigureAwait(continueOnCapturedContext: false);
				if (pcmWaveData == null)
				{
					return null;
				}
				flag = false;
				if (sampleRate == 0)
				{
					sampleRate = pcmWaveData.SampleRate;
					channels = pcmWaveData.Channels;
				}
				else if (pcmWaveData.SampleRate != sampleRate || pcmWaveData.Channels != channels)
				{
					return null;
				}
				if (chunk.PauseBeforeSeconds > 0f && samples.Count > 0)
				{
					int num = (int)MathF.Round(chunk.PauseBeforeSeconds * (float)sampleRate);
					samples.AddRange(new float[num * channels]);
				}
				samples.AddRange(pcmWaveData.Samples);
			}
			if (samples.Count == 0 || sampleRate == 0)
			{
				return null;
			}
			return new PcmWaveData(sampleRate, channels, samples.ToArray());
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static async Task<PcmWaveData?> SynthesizeValidatedChunkAsync(LocalTtsClient client, SpeechSynthesisRequest request, string text, SpeechDirective directive, int speakable, CancellationToken token)
	{
		for (int attempt = 0; attempt < 2; attempt++)
		{
			PcmWaveData pcmWaveData = await client.SynthesizeAsync(request with
			{
				RequestId = Guid.NewGuid(),
				Text = text,
				Directive = directive
			}, token).ConfigureAwait(continueOnCapturedContext: false);
			if (!IsBadWave(pcmWaveData, speakable))
			{
				return pcmWaveData;
			}
		}
		return null;
	}

	private static bool IsBadWave(PcmWaveData wave, int speakable)
	{
		if (wave.Samples.Length == 0 || wave.SampleFrames == 0 || IsEffectivelySilent(wave))
		{
			return true;
		}
		double num = Math.Max(0.12, (double)speakable * 0.07);
		return wave.DurationSeconds < num;
	}

	private static string EnsureTerminalPunctuation(string text)
	{
		if (text.Length == 0)
		{
			return text;
		}
		char value = text[text.Length - 1];
		if ("。！？…，、；：”’」』】）.!?;:,".IndexOf(value) < 0)
		{
			return text + "，";
		}
		return text;
	}

	/// <summary>
	/// 只做分片，不合成。聊天侧按这些切片逐段合成、逐段显示，
	/// 用于让气泡文字与语音一段一段对齐。
	/// </summary>
	/// <param name="text">整段回复。</param>
	/// <param name="firstChunkMaxSpeakable">
	/// 首段可读字上限（0 或负数＝不限制）。首段超长时按标点再切一刀，
	/// 让第一段音频更早出现；切出的后半段顺延为第 2 段，段数会 +1。
	/// </param>
	internal static IReadOnlyList<string> Split(string? text, int firstChunkMaxSpeakable = 0)
	{
		string source = SpeechTextSanitizer.StripTrailingSignature(text);
		if (string.IsNullOrWhiteSpace(source))
		{
			return Array.Empty<string>();
		}
		List<SpeechChunk> chunks = BuildChunks(source);
		if (chunks.Count <= 1)
		{
			// 整段只有一片：照旧直接用原文，只在首段过长时切出前半句。
			if (TryCutHead(source, firstChunkMaxSpeakable, out string sourceHead, out string sourceTail) && SpeakableCount(sourceTail) > 3)
			{
				// 后半段仍要守住每段 ≤56 的约定：否则会留下一整段几十秒的长音频，
				// 逐段对齐和预取都白做了。
				List<string> tailChunks = HardSplit(sourceTail, MaxChunkSpeakable);
				List<string> capped = new List<string>(tailChunks.Count + 1) { sourceHead };
				capped.AddRange(tailChunks);
				return capped;
			}
			return new string[1] { source };
		}
		List<string> texts = new List<string>(chunks.Count + 1);
		if (TryCutHead(chunks[0].Text, firstChunkMaxSpeakable, out string head, out string tail))
		{
			texts.Add(head);
			// 小尾巴并进下一段，避免出现只有两三个字的气泡。
			if (SpeakableCount(tail) <= 3 && SpeakableCount(tail) + SpeakableCount(chunks[1].Text) <= MaxChunkSpeakable)
			{
				texts.Add(tail + "，" + chunks[1].Text);
				for (int i = 2; i < chunks.Count; i++)
				{
					AddChunkText(texts, chunks[i].Text);
				}
			}
			else
			{
				texts.Add(tail);
				for (int i = 1; i < chunks.Count; i++)
				{
					AddChunkText(texts, chunks[i].Text);
				}
			}
		}
		else
		{
			for (int i = 0; i < chunks.Count; i++)
			{
				AddChunkText(texts, chunks[i].Text);
			}
		}
		return texts.Count == 0 ? new string[1] { source } : texts;
	}

	private static void AddChunkText(List<string> texts, string? text)
	{
		if (!string.IsNullOrWhiteSpace(text))
		{
			texts.Add(text);
		}
	}

	/// <summary>
	/// 按可读字上限把一段文本切成前后两截。切点优先落在标点上（与 HardSplit 的取舍一致），
	/// 兜底才按字数硬切；切不动（本来就够短/切完有空段）返回 false。
	/// </summary>
	private static bool TryCutHead(string text, int maxSpeakable, out string head, out string tail)
	{
		head = text;
		tail = "";
		if (maxSpeakable <= 0 || string.IsNullOrEmpty(text) || SpeakableCount(text) <= maxSpeakable)
		{
			return false;
		}
		int cut = FindCutIndex(text, maxSpeakable, Math.Max(6, maxSpeakable * 2 / 5));
		if (cut <= 0 || cut >= text.Length)
		{
			return false;
		}
		string candidateHead = text.Substring(0, cut).Trim();
		string candidateTail = text.Substring(cut).Trim();
		if (candidateHead.Length == 0 || candidateTail.Length == 0)
		{
			return false;
		}
		head = candidateHead;
		tail = candidateTail;
		return true;
	}

	/// <summary>合成单个切片（带静音/坏波重试）。</summary>
	internal static Task<PcmWaveData> SynthesizeChunkAsync(LocalTtsClient client, SpeechSynthesisRequest request, CancellationToken token)
	{
		return SynthesizeSingleValidatedAsync(client, request, token);
	}

	private static int SpeakableCount(string text)
	{
		int num = 0;
		foreach (char c in text)
		{
			if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
			{
				num++;
			}
		}
		return num;
	}

	private static bool IsEffectivelySilent(PcmWaveData wave)
	{
		float num = 0f;
		float[] samples = wave.Samples;
		for (int i = 0; i < samples.Length; i++)
		{
			float num2 = MathF.Abs(samples[i]);
			if (num2 > num)
			{
				num = num2;
			}
		}
		return num < 0.0005f;
	}
}
