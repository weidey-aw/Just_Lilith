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
		string[] array = Regex.Split(segment, "(?<=[。！？!?；;])");
		string text = "";
		string[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			string text2 = array2[i].Trim();
			if (text2.Length == 0)
			{
				continue;
			}
			if (SpeakableCount(text2) > 56)
			{
				if (text.Length > 0)
				{
					list.Add(text);
					text = "";
				}
				foreach (string item in HardSplit(text2, 56))
				{
					list.Add(item);
				}
			}
			else if (text.Length > 0 && SpeakableCount(text) + SpeakableCount(text2) > 56)
			{
				list.Add(text);
				text = text2;
			}
			else
			{
				text += text2;
			}
		}
		if (text.Length > 0)
		{
			list.Add(text);
		}
		return list;
	}

	private static List<string> HardSplit(string sentence, int maxSpeakable)
	{
		List<string> list = new List<string>();
		string text = sentence;
		while (SpeakableCount(text) > maxSpeakable)
		{
			int num = 0;
			int num2 = -1;
			int num3 = -1;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
				{
					num++;
				}
				if (num >= maxSpeakable && num3 < 0)
				{
					num3 = i + 1;
				}
				if (num > maxSpeakable)
				{
					break;
				}
				if (num >= maxSpeakable - 8 && (c == '，' || c == '、' || c == '；' || c == '：' || c == ' ' || c == ',' || c == ';' || c == ':'))
				{
					num2 = i + 1;
				}
			}
			if (num2 <= 0)
			{
				num2 = ((num3 > 0) ? num3 : text.Length);
			}
			string text2 = text.Substring(0, num2).Trim();
			if (text2.Length > 0)
			{
				list.Add(text2);
			}
			text = text.Substring(num2).Trim();
		}
		if (text.Length > 0)
		{
			list.Add(text);
		}
		return list;
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
			if (list.Count > 0 && SpeakableCount(chunk.Text) <= 3)
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
		if (list.Count > 1 && SpeakableCount(list[0].Text) <= 3)
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
	internal static IReadOnlyList<string> Split(string? text)
	{
		string text2 = SpeechTextSanitizer.StripTrailingSignature(text);
		if (string.IsNullOrWhiteSpace(text2))
		{
			return Array.Empty<string>();
		}
		List<SpeechChunk> list = BuildChunks(text2);
		if (list.Count <= 1)
		{
			return new string[1] { text2 };
		}
		List<string> list2 = new List<string>(list.Count);
		foreach (SpeechChunk item in list)
		{
			if (!string.IsNullOrWhiteSpace(item.Text))
			{
				list2.Add(item.Text);
			}
		}
		if (list2.Count == 0)
		{
			return new string[1] { text2 };
		}
		return list2;
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
