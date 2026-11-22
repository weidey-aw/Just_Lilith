using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace Just_Lilith.Unity.Game;

/// <summary>
/// 主动对话的"现在能不能说话"探针。全部走反射 + 失败即默认，读不到就当作"未知"，
/// 绝不为了探测去改动游戏状态、不采样键鼠、不读窗口标题、不落盘。
/// </summary>
internal sealed class ProactiveContextProbe
{
	/// <summary>一次探测的结果。各项都是"读不到就是 false"。</summary>
	internal readonly record struct ProbeResult(bool IsSleep, bool IsDrag, bool IsHiddenAway, bool StoryPerforming, bool IsGaming, bool IsBrowsing, bool ForegroundKnown, long? IdleMilliseconds)
	{
		public bool CharacterBlocked => IsSleep || IsDrag || IsHiddenAway;

		public string CharacterState
		{
			get
			{
				string text = "";
				if (IsSleep)
				{
					text = "sleep";
				}
				if (IsDrag)
				{
					text = string.IsNullOrEmpty(text) ? "drag" : (text + "+drag");
				}
				if (IsHiddenAway)
				{
					text = string.IsNullOrEmpty(text) ? "hiddenAway" : (text + "+hiddenAway");
				}
				return text;
			}
		}
	}

	private const string CharacterControllerType = "CharacterController, Assembly-CSharp";

	private const string DialogueManagerType = "DialogueManager, Assembly-CSharp";

	private const string ForegroundDetectorType = "GameForegroundDetector, Assembly-CSharp";

	private readonly ManualLogSource _log;

	private RuntimeValueAccessor? _isSleep;

	private RuntimeValueAccessor? _isDrag;

	private RuntimeValueAccessor? _isHiddenAway;

	private RuntimeValueAccessor? _isOnceStoryPerforming;

	private PropertyInfo? _isGaming;

	private PropertyInfo? _isBrowsing;

	private PropertyInfo? _gamingElapsedSeconds;

	private object? _characterController;

	private object? _dialogueManager;

	private bool _characterResolved;

	private bool _storyResolved;

	private bool _foregroundResolved;

	private bool _characterResolveFailed;

	private bool _characterReadFailed;

	private bool _storyResolveFailed;

	private bool _foregroundResolveFailed;

	private bool _idleProbeFailed;

	public ProactiveContextProbe(ManualLogSource log)
	{
		_log = log;
	}

	internal ProbeResult Probe()
	{
		ReadCharacters(out bool isSleep, out bool isDrag, out bool isHiddenAway);
		bool story = ReadStoryPerforming();
		ReadForeground(out bool isGaming, out bool isBrowsing, out bool foregroundKnown);
		long? idle = ReadIdleMilliseconds();
		return new ProbeResult(isSleep, isDrag, isHiddenAway, story, isGaming, isBrowsing, foregroundKnown, idle);
	}

	/// <summary>
	/// 用户状态加成：只由空闲毫秒数与前台布尔量推出。
	/// idle&gt;120s → 惩罚；idle&lt;60s 且正在玩游戏 → 游戏加成；否则在浏览 → 浏览加成；其余 0。
	/// 空闲时长读不到（未知）按 0 处理，<b>不当成挂机</b>。
	/// </summary>
	internal static int ComputeStateBonus(Just_Lilith.Core.Proactive.ProactiveUserState userState, long? idleMilliseconds, bool isGaming, bool isBrowsing)
	{
		if (!idleMilliseconds.HasValue)
		{
			return 0;
		}
		long idle = idleMilliseconds.GetValueOrDefault();
		if (idle > 120000)
		{
			return userState.IdlePenalty;
		}
		if (idle < 60000)
		{
			if (isGaming)
			{
				return userState.GamingBonus;
			}
			if (isBrowsing)
			{
				return userState.BrowsingBonus;
			}
		}
		return 0;
	}

	private void ReadCharacters(out bool isSleep, out bool isDrag, out bool isHiddenAway)
	{
		isSleep = false;
		isDrag = false;
		isHiddenAway = false;
		if (!_characterResolved)
		{
			_characterResolved = true;
			try
			{
				if (UnityRuntimeObjectLocator.TryFind(CharacterControllerType, out var located))
				{
					_isSleep = RuntimeValueAccessor.Find(located.ReflectedType, "IsSleep");
					_isDrag = RuntimeValueAccessor.Find(located.ReflectedType, "IsDrag");
					_isHiddenAway = RuntimeValueAccessor.Find(located.ReflectedType, "IsHiddenAway");
					_characterController = located.Instance;
					Log("module=Proactive; state=character_probe; sleep=" + Has(_isSleep) + "; drag=" + Has(_isDrag) + "; hiddenAway=" + Has(_isHiddenAway) + "; type_resolved=1");
				}
				else
				{
					Log("module=Proactive; state=character_probe; type_resolved=0");
				}
			}
			catch (Exception error)
			{
				LogOnce(ref _characterResolveFailed, "module=Proactive; state=character_probe_failed; code=" + error.GetType().Name);
			}
		}
		if (_isSleep == null && _isDrag == null && _isHiddenAway == null)
		{
			return;
		}
		try
		{
			isSleep = Read(_isSleep, _characterController);
			isDrag = Read(_isDrag, _characterController);
			isHiddenAway = Read(_isHiddenAway, _characterController);
		}
		catch (Exception error2)
		{
			LogOnce(ref _characterReadFailed, "module=Proactive; state=character_read_failed; code=" + error2.GetType().Name);
		}
	}

	private bool ReadStoryPerforming()
	{
		if (!_storyResolved)
		{
			_storyResolved = true;
			try
			{
				if (UnityRuntimeObjectLocator.TryFind(DialogueManagerType, out var located))
				{
					_isOnceStoryPerforming = RuntimeValueAccessor.Find(located.ReflectedType, "IsOnceStoryPerforming");
					_dialogueManager = located.Instance;
					Log("module=Proactive; state=story_probe; accessor=" + Has(_isOnceStoryPerforming));
				}
				else
				{
					Log("module=Proactive; state=story_probe; type_resolved=0");
				}
			}
			catch (Exception error)
			{
				LogOnce(ref _storyResolveFailed, "module=Proactive; state=story_probe_failed; code=" + error.GetType().Name);
			}
		}
		if (_isOnceStoryPerforming == null)
		{
			return false;
		}
		try
		{
			return Read(_isOnceStoryPerforming, _dialogueManager);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void ReadForeground(out bool isGaming, out bool isBrowsing, out bool known)
	{
		isGaming = false;
		isBrowsing = false;
		known = false;
		if (!_foregroundResolved)
		{
			_foregroundResolved = true;
			try
			{
				Type type = GameTypeResolver.ResolveLoadedType(ForegroundDetectorType);
				if ((object)type != null)
				{
					_isGaming = FindStatic(type, "IsGaming");
					_isBrowsing = FindStatic(type, "Loose");
					_gamingElapsedSeconds = FindStatic(type, "GamingElapsedSeconds");
					Log("module=Proactive; state=foreground_probe; gaming=" + Has(_isGaming) + "; loose=" + Has(_isBrowsing) + "; elapsed=" + Has(_gamingElapsedSeconds));
				}
				else
				{
					Log("module=Proactive; state=foreground_probe; type_resolved=0");
				}
			}
			catch (Exception error)
			{
				LogOnce(ref _foregroundResolveFailed, "module=Proactive; state=foreground_probe_failed; code=" + error.GetType().Name);
			}
		}
		if ((object)_isGaming == null && (object)_isBrowsing == null)
		{
			return;
		}
		known = true;
		try
		{
			isGaming = ReadStaticBool(_isGaming);
			isBrowsing = ReadStaticBool(_isBrowsing);
		}
		catch (Exception)
		{
			isGaming = false;
			isBrowsing = false;
		}
	}

	/// <summary>
	/// 距上次键鼠输入多少毫秒。直接用 user32 的 GetLastInputInfo（游戏里那个同名封装也是它），
	/// 读不到就返回 null —— 调用方按"未知"处理，不当挂机。
	/// </summary>
	private long? ReadIdleMilliseconds()
	{
		try
		{
			LASTINPUTINFO lastInput = new LASTINPUTINFO
			{
				cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
			};
			if (!GetLastInputInfo(ref lastInput))
			{
				LogOnce(ref _idleProbeFailed, "module=Proactive; state=idle_probe; reason=api_false");
				return null;
			}
			uint tickCount = (uint)Environment.TickCount;
			return (long)(tickCount - lastInput.dwTime);
		}
		catch (Exception error)
		{
			LogOnce(ref _idleProbeFailed, "module=Proactive; state=idle_probe_failed; reason=" + error.GetType().Name);
			return null;
		}
	}

	private static PropertyInfo? FindStatic(Type type, string name)
	{
		return type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
	}

	private static bool ReadStaticBool(PropertyInfo? property)
	{
		if ((object)property == null)
		{
			return false;
		}
		object value = property.GetValue(null);
		return value is bool && (bool)value;
	}

	private static bool Read(RuntimeValueAccessor? accessor, object? instance)
	{
		if (accessor == null || instance == null)
		{
			return false;
		}
		object value = accessor.GetValue(instance);
		return value is bool && (bool)value;
	}

	private static string Has(object? value)
	{
		return ((object)value != null) ? "1" : "0";
	}

	private void LogOnce(ref bool flag, string message)
	{
		if (!flag)
		{
			flag = true;
			Log(message);
		}
	}

	private void Log(string message)
	{
		ManualLogSource log = _log;
		if (log != null)
		{
			log.LogInfo(message);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct LASTINPUTINFO
	{
		public uint cbSize;

		public uint dwTime;
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
