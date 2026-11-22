using System;
using System.Linq;
using Just_Lilith.Core.Contracts;

namespace Just_Lilith.Core.Llm;

public static class BubblePresentationTiming
{
	private const double SpeechPreparationSeconds = 3.0;

	private const double AfterSpeechTailSeconds = 1.25;

	private const double SilentReadingTailSeconds = 1.0;

	private const double ReadingCharactersPerSecond = 6.5;

	private const double TypingCharactersPerSecond = 25.0;

	private const double SlowTypingCharactersPerSecond = 18.0;

	public const string ThinkingText = "少女思考中....";

	public static TimeSpan InitialDelay { get; } = TimeSpan.FromMilliseconds(1250.0);

	public static TimeSpan MinimumVisible { get; } = TimeSpan.FromSeconds(8.0);

	public static TimeSpan MaximumVisible { get; } = TimeSpan.FromSeconds(60.0);

	public static TimeSpan ThinkingVisibleDuration { get; } = TimeSpan.FromSeconds(5.0);

	public static TimeSpan ThinkingRefreshInterval { get; } = TimeSpan.FromMilliseconds(1500.0);

	public static TimeSpan ThinkingMinimumVisible { get; } = TimeSpan.FromSeconds(1.5);

	public static TimeSpan KeepAliveNodeDuration { get; } = TimeSpan.FromSeconds(1.0);

	public static TimeSpan KeepAliveInterval { get; } = TimeSpan.FromMilliseconds(250.0);

	public static TimeSpan MaximumTypingEstimate { get; } = TimeSpan.FromSeconds(30.0);

	public static TimeSpan MaximumTotalVisible { get; } = TimeSpan.FromSeconds(90.0);

	public static TimeSpan PostSpeechTailSeconds { get; } = TimeSpan.FromSeconds(2.0);

	public static bool IsReadyToPresent(DateTimeOffset enqueuedAt, DateTimeOffset now)
	{
		if (now >= enqueuedAt)
		{
			return now - enqueuedAt >= InitialDelay;
		}
		return false;
	}

	public static BubblePresentationSchedule Plan(string displayText, string? speechText, SpeechLanguage speechLanguage, bool speechEnabled)
	{
		if (string.IsNullOrWhiteSpace(displayText))
		{
			throw new ArgumentException("Display text must not be empty.", "displayText");
		}
		string text = ((speechEnabled && !string.IsNullOrWhiteSpace(speechText)) ? speechText : displayText);
		double value = (speechEnabled ? (3.0 + EstimateNarrationSeconds(text, speechLanguage) + 1.25) : (EstimateReadingSeconds(displayText) + 1.0));
		value = Math.Clamp(value, MinimumVisible.TotalSeconds, MaximumVisible.TotalSeconds);
		double num = EstimateTypingSeconds(displayText);
		return new BubblePresentationSchedule(InitialDelay, TimeSpan.FromSeconds(num + value), TimeSpan.FromSeconds(value));
	}

	public static TimeSpan PlanTypingDuration(string? displayText)
	{
		return TimeSpan.FromSeconds(Math.Min((double)CountVisibleCharacters(displayText ?? string.Empty) / 18.0, MaximumTypingEstimate.TotalSeconds));
	}

	private static double EstimateNarrationSeconds(string text, SpeechLanguage language)
	{
		int num = 0;
		double num2 = 0.0;
		foreach (char c in text)
		{
			if (!char.IsWhiteSpace(c) && !char.IsControl(c))
			{
				if (IsLongPause(c))
				{
					num2 += 0.38;
				}
				else if (IsShortPause(c))
				{
					num2 += 0.18;
				}
				else if (char.IsPunctuation(c))
				{
					num2 += 0.1;
				}
				else
				{
					num++;
				}
			}
		}
		double num3 = ((language == SpeechLanguage.Japanese) ? 6.0 : 4.2);
		return (double)num / num3 + num2;
	}

	private static double EstimateReadingSeconds(string text)
	{
		return (double)CountVisibleCharacters(text) / 6.5;
	}

	private static double EstimateTypingSeconds(string text)
	{
		return (double)CountVisibleCharacters(text) / 25.0;
	}

	private static int CountVisibleCharacters(string text)
	{
		return text.Count((char symbol) => !char.IsWhiteSpace(symbol) && !char.IsControl(symbol));
	}

	private static bool IsShortPause(char value)
	{
		switch (value)
		{
		case ',':
		case ':':
		case '、':
		case '，':
		case '：':
			return true;
		default:
			return false;
		}
	}

	private static bool IsLongPause(char value)
	{
		switch (value)
		{
		case '!':
		case ';':
		case '?':
		case '…':
		case '。':
		case '！':
		case '；':
		case '？':
			return true;
		default:
			return false;
		}
	}
}
