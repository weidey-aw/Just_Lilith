using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 「MOD设置」的唯一入口（阶段 A）：系统托盘右键菜单最底下追加一项。
///
/// 游戏托盘是真 Windows 通知区菜单 —— <c>ShowSystemTray</c> 把 <c>menuItems</c> 逐条喂给
/// <c>ISystemTray.AddItem</c>，底层 <c>SystemTray</c> 用 <c>AppendMenuW</c> 建菜单，所以由游戏自己
/// 加进去的项 100% 原生风格，不用我们画任何控件。
///
/// 做法：给 <c>WindowsSystemTrayAdapter.AddItem</c> 挂后缀，只用来记录「游戏刚重建过菜单」；
/// 等这批添加停下来（<see cref="SettleSeconds"/>）再 <c>AddSeparator()</c> + <c>AddItem(...)</c>
/// 追加到最底下。切语言、重进游戏都会重建菜单，所以每次重建后都会补一次，天然幂等。
///
/// 入口只调 <see cref="LlmConfigurationView.OpenModSettings"/>，页面本身见 <see cref="ModSettingsWindow"/>。
/// </summary>
internal sealed class ModSettingsMenuIntegration : IDisposable
{
	private const string Module = "module=ModUI";

	internal const string EntryLabel = "MOD设置";

	private const string HarmonyId = "local.just_lilith.mod_settings";

	private const float PollIntervalSeconds = 0.5f;

	/// <summary>最后一次 AddItem 之后静默这么久，认为这一轮菜单已经建完。</summary>
	private const float SettleSeconds = 0.6f;

	/// <summary>AddItem 钩子始终没触发时，托盘就绪后等这么久再退到无钩子追加。</summary>
	private const float HookGraceSeconds = 3f;

	private static ModSettingsMenuIntegration _active;

	private static Il2CppSystem.Action _trayActionNative;

	private readonly ManualLogSource _log;

	private readonly Action _open;

	private readonly Harmony _harmony = new Harmony(HarmonyId);

	private readonly System.Collections.Generic.List<MethodInfo> _patched = new System.Collections.Generic.List<MethodInfo>();

	private bool _installed;

	private bool _disposed;

	private float _nextPoll;

	private float _nextAppendAt = -1f;

	private bool _appendPending;

	private bool _appendInFlight;

	private bool _everAppended;

	private float _trayReadySince = -1f;

	private string _lastDiagnostic = "";

	internal ModSettingsMenuIntegration(ManualLogSource log, Action open)
	{
		_log = log;
		_open = open;
	}

	internal bool IsInstalled => _installed;

	private static Il2CppSystem.Action TrayAction
	{
		get
		{
			if (_trayActionNative == null)
			{
				_trayActionNative = (System.Action)OnTrayEntryClicked;
			}
			return _trayActionNative;
		}
	}

	internal bool Install()
	{
		if (_installed)
		{
			return true;
		}
		if (_disposed)
		{
			return false;
		}
		_active = this;
		int ok = 0;
		int failed = 0;
		try
		{
			Type adapterType = ResolveType("WindowsSystemTrayAdapter");
			Type trayType = ResolveType("ShowSystemTray");
			// 只用来监听「菜单正在重建」，真正的追加由 Tick 在静默后做。
			if (!TryPatch(adapterType, "AddItem", 2, "AdapterAddItemPostfix", ref ok, ref failed))
			{
				TryPatch(ResolveType("SystemTray"), "AddItem", 2, "TrayAddItemPostfix", ref ok, ref failed);
			}
			// 切语言会重建菜单：标记一次，等静默后重新追加。
			TryPatch(trayType, "OnLocaleChanged", 1, "TrayLocalePostfix", ref ok, ref failed);
		}
		catch (Exception error)
		{
			failed++;
			Log("state=install_error; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
		_installed = ok > 0;
		Log("state=installed; ok=" + ok + "; failed=" + failed);
		return _installed;
	}

	internal void Tick()
	{
		if (_disposed || !_installed)
		{
			return;
		}
		if (Time.unscaledTime < _nextPoll)
		{
			return;
		}
		_nextPoll = Time.unscaledTime + PollIntervalSeconds;
		try
		{
			TryAppendTrayEntry();
		}
		catch (Exception error)
		{
			LogOnce("state=tray_poll_error; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			_harmony.UnpatchSelf();
			Log("state=uninstalled; patched=" + _patched.Count);
		}
		catch (Exception error)
		{
			Log("state=uninstall_failed; error=" + error.GetType().Name);
		}
		_patched.Clear();
		_installed = false;
		if (ReferenceEquals(_active, this))
		{
			_active = null;
		}
	}

	private void OnEntrySelected()
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			Log("event=entry_selected; source=tray");
			_open?.Invoke();
		}
		catch (Exception error)
		{
			Log("state=open_failed; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
	}

	// ---------------- 托盘追加 ----------------

	/// <summary>游戏往托盘菜单加过一项：记下时间，等这一轮建菜单停下来再追加我们的项。</summary>
	private void NotifyTrayBuildActivity()
	{
		// 我们自己追加的那一次也会经过 AddItem，别把它当成"游戏在重建菜单"，否则会无限自激。
		if (_disposed || _appendInFlight)
		{
			return;
		}
		float now = Time.unscaledTime;
		if (!_appendPending)
		{
			_appendPending = true;
			LogOnce("event=tray_menu_building");
		}
		_nextAppendAt = now + SettleSeconds;
	}

	private void TryAppendTrayEntry()
	{
		if (_disposed)
		{
			return;
		}
		if (!_appendPending)
		{
			// 兜底：AddItem 钩子一次都没触发，也不能让入口消失；等托盘就绪 3 秒后再退到无钩子追加。
			if (_everAppended)
			{
				return;
			}
			ShowSystemTray probe = ShowSystemTray.instance;
			if (probe == null || !probe.initialized || probe.tray == null)
			{
				return;
			}
			if (_trayReadySince < 0f)
			{
				_trayReadySince = Time.unscaledTime;
				return;
			}
			if (Time.unscaledTime - _trayReadySince < HookGraceSeconds)
			{
				return;
			}
			LogOnce("state=tray_hook_missing; fallback=append_bottom");
			_appendPending = true;
			_nextAppendAt = Time.unscaledTime;
		}
		if (Time.unscaledTime < _nextAppendAt)
		{
			return;
		}
		ShowSystemTray host = ShowSystemTray.instance;
		if (host == null || !host.initialized || host.tray == null)
		{
			return;
		}
		_appendPending = false;
		_appendInFlight = true;
		try
		{
			host.tray.AddSeparator();
			host.tray.AddItem(EntryLabel, TrayAction);
			_everAppended = true;
			Log("event=tray_entry_appended; position=bottom");
		}
		catch (Exception error)
		{
			Log("state=tray_append_failed; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
		finally
		{
			_appendInFlight = false;
		}
	}

	// ---------------- Harmony 补丁方法（静态入口） ----------------

	private static void AdapterAddItemPostfix(string label)
	{
		try
		{
			_active?.NotifyTrayBuildActivity();
		}
		catch (Exception error)
		{
			_active?.LogOnce("state=tray_hook_error; error=" + error.GetType().Name);
		}
	}

	private static void TrayAddItemPostfix(string label)
	{
		try
		{
			_active?.NotifyTrayBuildActivity();
		}
		catch (Exception error)
		{
			_active?.LogOnce("state=tray_hook_error; error=" + error.GetType().Name);
		}
	}

	private static void TrayLocalePostfix()
	{
		try
		{
			ModSettingsMenuIntegration integration = _active;
			if (integration == null || integration._disposed)
			{
				return;
			}
			// 切语言会重建菜单：等它建完再补一次（重复调用无副作用）。
			integration._appendPending = true;
			integration._nextAppendAt = Time.unscaledTime + SettleSeconds;
			integration.Log("event=tray_locale_changed");
		}
		catch (Exception error)
		{
			_active?.LogOnce("state=tray_locale_error; error=" + error.GetType().Name);
		}
	}

	private static void OnTrayEntryClicked()
	{
		_active?.OnEntrySelected();
	}

	// ---------------- Harmony 安装/工具 ----------------

	private bool TryPatch(Type type, string name, int arity, string patchName, ref int ok, ref int failed)
	{
		MethodInfo target = FindMethod(type, name, arity);
		MethodInfo patch = typeof(ModSettingsMenuIntegration).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic);
		if (target == null || patch == null)
		{
			failed++;
			LogOnce("state=patch_skipped; target=" + name + ":" + arity + "; resolved=" + Bool(target != null) + "; patch=" + Bool(patch != null));
			return false;
		}
		try
		{
			_harmony.Patch(target, null, new HarmonyMethod(patch), null, null, null);
			_patched.Add(target);
			ok++;
			return true;
		}
		catch (Exception error)
		{
			failed++;
			LogOnce("state=patch_failed; target=" + name + ":" + arity + "; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
			return false;
		}
	}

	private static MethodInfo FindMethod(Type type, string name, int parameterCount)
	{
		if (type == null)
		{
			return null;
		}
		MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		foreach (MethodInfo method in methods)
		{
			if (method.Name == name && method.GetParameters().Length == parameterCount)
			{
				return method;
			}
		}
		return null;
	}

	private static Type ResolveType(string name)
	{
		Type type = Type.GetType(name + ", Assembly-CSharp", throwOnError: false);
		if (type != null)
		{
			return type;
		}
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (string.Equals(assembly.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
			{
				type = assembly.GetType(name);
				if (type != null)
				{
					return type;
				}
			}
		}
		return null;
	}

	// ---------------- 日志 ----------------

	private void LogOnce(string message)
	{
		if (string.Equals(_lastDiagnostic, message, StringComparison.Ordinal))
		{
			return;
		}
		_lastDiagnostic = message;
		Log(message);
	}

	private void Log(string message)
	{
		try
		{
			_log?.LogInfo(Module + "; " + message);
		}
		catch (Exception)
		{
		}
	}

	private static string Bool(bool value)
	{
		return value ? "true" : "false";
	}

	private static string SingleLine(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}
		return value.Replace('\r', ' ').Replace('\n', ' ');
	}
}
