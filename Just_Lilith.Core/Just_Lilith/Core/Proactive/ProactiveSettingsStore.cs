using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Just_Lilith.Core.Proactive;

/// <summary>
/// 主动对话配置的读写。仿 <c>FoundationOptionsStore</c>：先写 .pending 再原子替换，
/// 读失败/坏文件不覆盖、不把异常抛到 UI（调用方拿到默认值继续跑）。
/// </summary>
public static class ProactiveSettingsStore
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never
	};

	/// <summary>读配置；文件不存在则写一份默认值；文件坏掉则保留原文件并返回默认值。</summary>
	public static ProactiveSettings LoadOrCreate(string path, out string? diagnostic)
	{
		diagnostic = null;
		if (!File.Exists(path))
		{
			ProactiveSettings defaults = new ProactiveSettings();
			try
			{
				Save(path, defaults);
			}
			catch (Exception error)
			{
				diagnostic = "write_failed:" + error.GetType().Name;
			}
			return defaults;
		}
		try
		{
			ProactiveSettings? parsed = JsonSerializer.Deserialize<ProactiveSettings>(File.ReadAllText(path), JsonOptions);
			if (parsed == null)
			{
				diagnostic = "empty_file";
				return new ProactiveSettings();
			}
			return parsed.Validate();
		}
		catch (Exception error)
		{
			// 坏文件保留原样，绝不覆盖；本次按默认值运行。
			diagnostic = "unreadable:" + error.GetType().Name;
			return new ProactiveSettings();
		}
	}

	/// <summary>覆盖写配置（原子替换）。失败会抛出，由调用方决定是否记日志。</summary>
	public static void Save(string path, ProactiveSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings, "settings");
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
		string temporary = fullPath + ".pending-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Validate(), JsonOptions));
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
}
