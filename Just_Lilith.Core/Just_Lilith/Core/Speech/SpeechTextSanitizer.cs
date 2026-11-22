using System.Text.RegularExpressions;

namespace Just_Lilith.Core.Speech;

public static class SpeechTextSanitizer
{
	private static readonly Regex TrailingSignature = new Regex("(?:——|—|–|--|-)\\s*(?:莉莉丝|Lilith)\\s*[。．.!！?？…～~]*\\s*[”’」』】）)]?\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	public static string StripTrailingSignature(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text ?? "";
		}
		string text2 = text.TrimEnd();
		Match match = TrailingSignature.Match(text2);
		if (!match.Success)
		{
			return text;
		}
		return text2.Substring(0, match.Index).TrimEnd();
	}
}
