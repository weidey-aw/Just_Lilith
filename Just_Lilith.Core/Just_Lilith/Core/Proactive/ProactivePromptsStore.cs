using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Just_Lilith.Core.Proactive;

/// <summary>
/// 提示词 JSON 的读写，和 <see cref="ProactiveSettingsStore"/> 一样：
/// 先写 .pending 再原子替换；读失败/坏文件保留原文件、本次按默认值跑，不把异常抛给 UI。
/// 写入时不做 \uXXXX 转义 —— 这份文件是给人改的，中文要能直接读。
/// </summary>
public static class ProactivePromptsStore
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never
	};

	/// <summary>读提示词；文件不存在则写一份默认值；文件坏掉则保留原文件并返回默认值。</summary>
	public static ProactivePrompts LoadOrCreate(string path, out string? diagnostic)
	{
		diagnostic = null;
		if (!File.Exists(path))
		{
			ProactivePrompts defaults = new ProactivePrompts();
			diagnostic = TrySave(path, defaults) ? null : "write_failed";
			return defaults;
		}
		try
		{
			ProactivePrompts? parsed = JsonSerializer.Deserialize<ProactivePrompts>(File.ReadAllText(path), JsonOptions);
			if (parsed == null)
			{
				diagnostic = "empty_file";
				return new ProactivePrompts();
			}
			return parsed.Validate();
		}
		catch (Exception error)
		{
			// 坏文件保留原样，绝不覆盖；本次按默认值运行。
			diagnostic = "unreadable:" + error.GetType().Name;
			return new ProactivePrompts();
		}
	}

	/// <summary>文件不存在时补一份默认值（设置面板的「提示词文件」按钮用）。</summary>
	public static bool EnsureExists(string path)
	{
		if (File.Exists(path))
		{
			return true;
		}
		return TrySave(path, new ProactivePrompts());
	}

	/// <summary>覆盖写（原子替换）。失败会抛出，由调用方决定是否记日志。</summary>
	public static void Save(string path, ProactivePrompts prompts)
	{
		ArgumentNullException.ThrowIfNull(prompts, "prompts");
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
		string temporary = fullPath + ".pending-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(prompts.Validate(), JsonOptions));
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

	private static bool TrySave(string path, ProactivePrompts prompts)
	{
		try
		{
			Save(path, prompts);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
