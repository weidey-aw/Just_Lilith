using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Just_Lilith.Core.Proactive;

/// <summary>一条"她看到过什么"的场景记忆：只有文字，没有图。</summary>
public sealed record SceneMemoryEntry(DateTimeOffset AtUtc, string Text, string Source)
{
	public const int TextLimit = 200;
}

/// <summary>
/// 场景记忆的落盘与裁剪。环形保留最近 N 条 / D 天（2.4.1）。
/// 坏文件一律当空记忆处理、原文件保留，不把异常抛到 UI。
/// </summary>
public static class SceneMemoryStore
{
	private sealed record EntryFile
	{
		[JsonPropertyName("at")]
		public string At { get; init; } = "";

		[JsonPropertyName("text")]
		public string Text { get; init; } = "";

		[JsonPropertyName("source")]
		public string Source { get; init; } = "";
	}

	private sealed record MemoryFile
	{
		[JsonPropertyName("schema_version")]
		public int SchemaVersion { get; init; } = 1;

		[JsonPropertyName("entries")]
		public EntryFile[] Entries { get; init; } = Array.Empty<EntryFile>();
	}

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	/// <summary>读场景记忆；不存在或读不懂都返回空列表（不动原文件）。</summary>
	public static IReadOnlyList<SceneMemoryEntry> Load(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return Array.Empty<SceneMemoryEntry>();
			}
			MemoryFile? parsed = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(path), JsonOptions);
			if (parsed?.Entries == null)
			{
				return Array.Empty<SceneMemoryEntry>();
			}
			List<SceneMemoryEntry> list = new List<SceneMemoryEntry>(parsed.Entries.Length);
			foreach (EntryFile entry in parsed.Entries)
			{
				if (entry == null || string.IsNullOrWhiteSpace(entry.Text))
				{
					continue;
				}
				if (!DateTimeOffset.TryParse(entry.At, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
				{
					continue;
				}
				list.Add(new SceneMemoryEntry(result, Clip(entry.Text), string.IsNullOrEmpty(entry.Source) ? "screen" : entry.Source));
			}
			return list.AsReadOnly();
		}
		catch (Exception)
		{
			return Array.Empty<SceneMemoryEntry>();
		}
	}

	/// <summary>加一条并裁剪（最近 limit 条、days 天内），返回新的列表；写盘失败只影响落盘。</summary>
	public static IReadOnlyList<SceneMemoryEntry> Append(string path, IReadOnlyList<SceneMemoryEntry> current, string text, DateTimeOffset atUtc, int limit, int days)
	{
		ArgumentNullException.ThrowIfNull(current, "current");
		if (string.IsNullOrWhiteSpace(text))
		{
			return current;
		}
		List<SceneMemoryEntry> list = new List<SceneMemoryEntry>(current.Count + 1);
		list.AddRange(current);
		list.Add(new SceneMemoryEntry(atUtc, Clip(text), "screen"));
		IReadOnlyList<SceneMemoryEntry> trimmed = Trim(list, atUtc, limit, days);
		try
		{
			Save(path, trimmed);
		}
		catch (Exception)
		{
		}
		return trimmed;
	}

	/// <summary>清空（隐私开关/一键清空用）。</summary>
	public static void Clear(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception)
		{
		}
	}

	public static IReadOnlyList<SceneMemoryEntry> Trim(IReadOnlyList<SceneMemoryEntry> entries, DateTimeOffset nowUtc, int limit, int days)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		int keep = Math.Clamp(limit, 0, 100);
		int day = Math.Clamp(days, 0, 365);
		DateTimeOffset cutoff = (day > 0) ? nowUtc.AddDays(-day) : DateTimeOffset.MinValue;
		List<SceneMemoryEntry> list = entries.Where((SceneMemoryEntry entry) => entry.AtUtc >= cutoff).OrderBy((SceneMemoryEntry entry) => entry.AtUtc).ToList();
		if (list.Count > keep)
		{
			list.RemoveRange(0, list.Count - keep);
		}
		return list.AsReadOnly();
	}

	/// <summary>给后续请求用的"我记得刚才看到过…"片段；没有记忆返回 null。</summary>
	public static string? BuildReference(IReadOnlyList<SceneMemoryEntry> entries, int count)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		if (entries.Count == 0 || count <= 0)
		{
			return null;
		}
		List<SceneMemoryEntry> list = entries.Skip(Math.Max(0, entries.Count - count)).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		string text = string.Join("；", list.Select((SceneMemoryEntry entry) => entry.Text));
		return "（你之前瞥见过桌面：" + text + "）";
	}

	public static void Save(string path, IReadOnlyList<SceneMemoryEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
		MemoryFile file = new MemoryFile
		{
			SchemaVersion = 1,
			Entries = entries.Select((SceneMemoryEntry entry) => new EntryFile
			{
				At = entry.AtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
				Text = entry.Text,
				Source = entry.Source
			}).ToArray()
		};
		string temporary = fullPath + ".pending-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
			File.Move(temporary, fullPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static string Clip(string text)
	{
		string text2 = text.Trim();
		if (text2.Length <= SceneMemoryEntry.TextLimit)
		{
			return text2;
		}
		return text2.Substring(0, SceneMemoryEntry.TextLimit);
	}
}
