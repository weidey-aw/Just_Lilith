using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Just_Lilith.Core.Proactive;

namespace Just_Lilith.Unity.Game;

/// <summary>用户当前在干什么。只用来决定「换活动了，可以看一眼」，不再参与掷骰。</summary>
internal enum ActivityCategory
{
	None,
	Gaming,
	Video,
	Browsing,
	Coding
}

/// <summary>
/// 主动对话的"现在能不能说话"探针。全部走反射 + 失败即默认，读不到就当作"未知"，
/// 绝不为了探测去改动游戏状态、不采样键鼠、不读窗口标题、不落盘。
///
/// 前台识别分两层：
/// 1. 优先用游戏自带的 <c>GameForegroundDetector</c>（它有自建游戏白名单、视频播放器识别）；
/// 2. 它没覆盖到的类别（浏览器 / 代码编辑器）用**进程名**兜底 —— 只取 exe 名字，不碰窗口标题。
/// </summary>
internal sealed class ProactiveContextProbe
{
	/// <summary>一次探测的结果。各项都是"读不到就是 false"。</summary>
	internal readonly record struct ProbeResult(bool IsSleep, bool IsDrag, bool IsHiddenAway, bool StoryPerforming, bool IsGaming, bool IsBrowsing, bool ForegroundKnown, long? IdleMilliseconds, ActivityCategory Activity, string ForegroundProcess)
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

	/// <summary>浏览器进程名（小写、去扩展名）。</summary>
	private static readonly HashSet<string> BrowserProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "chromium",
		"360se", "360chrome", "qqbrowser", "sogouexplorer", "quark", "quarkpc", "iexplore", "safari"
	};

	/// <summary>代码编辑器 / IDE 进程名（小写、去扩展名）。</summary>
	private static readonly HashSet<string> CodingProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"code", "cursor", "devenv", "idea", "idea64", "pycharm", "pycharm64", "webstorm", "goland",
		"clion", "rider", "rider64", "sublime_text", "notepad++", "atom", "zed", "nvim", "gvim",
		"emacs", "windows-terminal", "wt", "trae", "windsurf", "android-studio", "studio64"
	};

	private readonly ManualLogSource _log;

	private RuntimeValueAccessor? _isSleep;

	private RuntimeValueAccessor? _isDrag;

	private RuntimeValueAccessor? _isHiddenAway;

	private RuntimeValueAccessor? _isOnceStoryPerforming;

	private MethodInfo? _isGaming;

	private MethodInfo? _isVideoPlayer;

	private MethodInfo? _foregroundLooseCached;

	private MethodInfo? _foregroundLowerCached;

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

	private bool _foregroundLogFailed;

	private string _lastActivitySignature = "";

	public ProactiveContextProbe(ManualLogSource log)
	{
		_log = log;
	}

	internal ProbeResult Probe()
	{
		ReadCharacters(out bool isSleep, out bool isDrag, out bool isHiddenAway);
		bool story = ReadStoryPerforming();
		ProbeForeground(out ActivityCategory activity, out string process, out bool isGaming, out bool isBrowsing, out bool known);
		long? idle = ReadIdleMilliseconds();
		return new ProbeResult(isSleep, isDrag, isHiddenAway, story, isGaming, isBrowsing, known, idle, activity, process);
	}

	/// <summary>
	/// 用户状态加成：只由空闲毫秒数与前台布尔量推出。
	/// idle&gt;120s → 惩罚；idle&lt;60s 且正在玩游戏 → 游戏加成；否则在浏览 → 浏览加成；其余 0。
	/// 空闲时长读不到（未知）按 0 处理，<b>不当成挂机</b>。
	/// </summary>
	internal static int ComputeStateBonus(ProactiveUserState userState, long? idleMilliseconds, bool isGaming, bool isBrowsing)
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

	/// <summary>
	/// 前台活动识别。游戏那边 <c>IsGaming</c> / <c>ForegroundIsVideoPlayer</c> / <c>ForegroundLooseCached</c>
	/// 都是**静态方法**（不是属性，早期版本按属性取所以一直是 0），这里按方法调用；
	/// 浏览器 / 编辑器游戏没覆盖，用进程名兜底。
	/// </summary>
	private void ProbeForeground(out ActivityCategory activity, out string process, out bool isGaming, out bool isBrowsing, out bool known)
	{
		activity = ActivityCategory.None;
		process = "";
		isGaming = false;
		isBrowsing = false;
		known = false;
		EnsureForegroundResolved();
		try
		{
			isGaming = CallBool(_isGaming);
			bool isVideo = CallBool(_isVideoPlayer);
			string gameLoose = CallString(_foregroundLooseCached);
			string gameLower = CallString(_foregroundLowerCached);
			process = ForegroundProcessName();
			known = _isGaming != null;
			if (isGaming)
			{
				activity = ActivityCategory.Gaming;
			}
			else if (isVideo)
			{
				activity = ActivityCategory.Video;
			}
			else if (LooksLikeBrowser(process) || LooksLikeBrowser(gameLoose) || LooksLikeBrowser(gameLower))
			{
				activity = ActivityCategory.Browsing;
				isBrowsing = true;
			}
			else if (LooksLikeCoding(process) || LooksLikeCoding(gameLoose) || LooksLikeCoding(gameLower))
			{
				activity = ActivityCategory.Coding;
			}
			LogActivityIfChanged(activity, process, gameLoose, gameLower, isGaming, isVideo);
		}
		catch (Exception error)
		{
			LogOnce(ref _foregroundLogFailed, "module=Proactive; state=foreground_read_failed; code=" + error.GetType().Name);
		}
	}

	/// <summary>只在类别 / 前台进程变化时打一行，方便确认游戏词表（D4：先观测再定映射）。</summary>
	private void LogActivityIfChanged(ActivityCategory activity, string process, string gameLoose, string gameLower, bool isGaming, bool isVideo)
	{
		string signature = activity + "|" + process + "|" + gameLoose;
		if (string.Equals(signature, _lastActivitySignature, StringComparison.Ordinal))
		{
			return;
		}
		_lastActivitySignature = signature;
		Log("module=Proactive; state=activity; category=" + activity.ToString().ToLowerInvariant() + "; proc=" + (string.IsNullOrEmpty(process) ? "-" : process) + "; loose=" + (string.IsNullOrEmpty(gameLoose) ? "-" : gameLoose) + "; lower=" + (string.IsNullOrEmpty(gameLower) ? "-" : gameLower) + "; gaming=" + (isGaming ? "1" : "0") + "; video=" + (isVideo ? "1" : "0"));
	}

	private static bool LooksLikeBrowser(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}
		foreach (string name in BrowserProcesses)
		{
			if (value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	private static bool LooksLikeCoding(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}
		foreach (string name in CodingProcesses)
		{
			if (value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return value.IndexOf("visual studio", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("编辑器", StringComparison.Ordinal) >= 0;
	}

	/// <summary>前台窗口所属进程名（小写、去 .exe）。只读进程名，不读标题。</summary>
	private static string ForegroundProcessName()
	{
		try
		{
			IntPtr hwnd = GetForegroundWindow();
			if (hwnd == IntPtr.Zero)
			{
				return "";
			}
			GetWindowThreadProcessId(hwnd, out uint pid);
			if (pid == 0)
			{
				return "";
			}
			using Process process = Process.GetProcessById((int)pid);
			return (process.ProcessName ?? "").ToLowerInvariant();
		}
		catch
		{
			return "";
		}
	}

	private void EnsureForegroundResolved()
	{
		if (_foregroundResolved)
		{
			return;
		}
		_foregroundResolved = true;
		try
		{
			Type type = GameTypeResolver.ResolveLoadedType(ForegroundDetectorType);
			if (type == null)
			{
				Log("module=Proactive; state=foreground_probe; type_resolved=0");
				return;
			}
			_isGaming = type.GetMethod("IsGaming", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
			_isVideoPlayer = type.GetMethod("ForegroundIsVideoPlayer", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
			_foregroundLooseCached = type.GetMethod("ForegroundLooseCached", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
			_foregroundLowerCached = type.GetMethod("ForegroundLowerCached", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
			Log("module=Proactive; state=foreground_probe; gaming=" + Has(_isGaming) + "; video=" + Has(_isVideoPlayer) + "; loose=" + Has(_foregroundLooseCached) + "; lower=" + Has(_foregroundLowerCached));
		}
		catch (Exception error)
		{
			LogOnce(ref _foregroundResolveFailed, "module=Proactive; state=foreground_probe_failed; code=" + error.GetType().Name);
		}
	}

	private static bool CallBool(MethodInfo method)
	{
		if (method == null)
		{
			return false;
		}
		try
		{
			return method.Invoke(null, null) is bool flag && flag;
		}
		catch
		{
			return false;
		}
	}

	private static string CallString(MethodInfo method)
	{
		if (method == null)
		{
			return "";
		}
		try
		{
			return method.Invoke(null, null) as string ?? "";
		}
		catch
		{
			return "";
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

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
