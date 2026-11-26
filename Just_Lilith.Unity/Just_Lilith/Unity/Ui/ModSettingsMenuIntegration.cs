using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 「MOD设置」的两个入口。
///
/// 1. 系统托盘右键菜单：最底下追加一项。游戏托盘是真 Windows 通知区菜单 ——
///    <c>ShowSystemTray</c> 把 <c>menuItems</c> 逐条喂给 <c>ISystemTray.AddItem</c>，
///    底层 <c>SystemTray</c> 用 <c>AppendMenuW</c> 建菜单，所以由游戏自己加进去的项 100% 原生风格。
///    做法：给 <c>WindowsSystemTrayAdapter.AddItem</c> 挂后缀，只用来记录「游戏刚重建过菜单」；
///    等这批添加停下来（<see cref="SettleSeconds"/>）再 <c>AddSeparator()</c> + <c>AddItem(...)</c>。
///    切语言、重进游戏都会重建菜单，所以每次重建后都会补一次，天然幂等。
///
/// 2. 人物右键气泡菜单：在游戏「打开设置」那一项的正下方加一项。
///    实测调用链：<c>OpenChatPanel → PlayerLineController.Show
///    → RefreshEntries</c>（组好 9 条并**直接**把按钮建完）<c>→ PlayerLineDatabase.GetRootEntries</c>。
///    右键有两条入口：单击穿透开着走全局钩子 <c>HandleHookChatRequest</c>，关着走 Unity 正常输入
///    <c>HandleMouseUp</c>，最后都落到 <c>OpenChatPanel</c> —— 所以这两处都点亮作用域，
///    <c>Show()</c> 之后再按「最后一项是不是设置项」兜底，两种模式表现一致。
///    面板一页最多 <c>MaxOptionCount = 9</c> 项，而根菜单正好 9 项占满，上限又是只读的，
///    所以插队进 <c>_currentEntries</c> 必然画不出来（也会被 <c>SetEntryContents</c> 按数据库校验丢掉）。
///    这里的做法：<c>Show()</c> 之后用游戏自己的分页入口 <c>ShowEntryOptionsPaged</c> 重画一版
///    「原 9 项 + MOD设置」，游戏自己渲染第 2 页，点原项仍然交回 <c>MarkEntryPicked</c> 执行。
///    F7 打开聊天等不经过 <c>Show()</c> 的路径完全不受影响。
///
/// 两个入口都只调 <see cref="LlmConfigurationView.OpenModSettings"/>，页面本身见 <see cref="ModSettingsWindow"/>。
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

	/// <summary>气泡菜单里我们自己新增的条目 id（负数，避开游戏自己的 id）。</summary>
	private const int BubbleEntryId = -9911;

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
			// 人物右键气泡菜单：在「打开设置」下方加一项。
			// 实测链路 HandleHookChatRequest → OpenChatPanel → PlayerLineController.Show
			//   → RefreshEntries（组好 9 条并直接建好按钮）→ GetRootEntries；
			// 面板一页最多 MaxOptionCount=9 项且上限是只读的，根菜单正好占满，
			// 所以 Show() 之后用游戏自己的分页入口重画一版：原 9 项 + 「MOD设置」。
			Type lineType = ResolveType("PlayerLineController");
			Type handlerType = ResolveType("CharacterInteractionHandler");
			// 右键两条入口都要认：
			//  - 单击穿透开着时走全局钩子 HandleHookChatRequest；
			//  - 单击穿透关着时走 Unity 正常输入 HandleMouseUp，最后一样落到 OpenChatPanel。
			// 所以钩子和 OpenChatPanel 都点一下作用域，Show() 后面再按「菜单里有没有设置项」兜底。
			TryPatchPrefix(handlerType, "HandleHookChatRequest", 0, "ArmRightClickScopePrefix", ref ok, ref failed);
			TryPatchPrefix(handlerType, "OpenChatPanel", 0, "ArmRightClickScopePrefix", ref ok, ref failed);
			TryPatch(lineType, "Show", 0, "ShowPostfix", ref ok, ref failed);
			TryPatchPrefix(lineType, "MarkEntryPicked", 1, "MarkEntryPickedPrefix", ref ok, ref failed);
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
	
	// ---------------- 人物右键气泡菜单 ----------------

	/// <summary>点选回调：选中我们那条就开页面，其余的照旧透传。</summary>
	private static bool MarkEntryPickedPrefix(PlayerLineEntry entry)
	{
		ModSettingsMenuIntegration integration = _active;
		if (integration == null || integration._disposed)
		{
			return true;
		}
		try
		{
			if (entry == null || entry.id != BubbleEntryId)
			{
				return true;
			}
			integration.OpenFromBubble("MarkEntryPicked");
			// 这个 id 在游戏数据库里不存在，别让它再走一次查找。
			return false;
		}
		catch (Exception error)
		{
			integration.LogOnce("state=bubble_pick_failed; error=" + error.GetType().Name);
			return true;
		}
	}

	private static bool LooksLikeSettings(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		return text.IndexOf("设置", StringComparison.Ordinal) >= 0 || text.IndexOf("設定", StringComparison.Ordinal) >= 0 || text.IndexOf("Setting", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private void OpenFromBubble(string source)
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			Log("event=entry_selected; source=bubble_" + source);
			_open?.Invoke();
		}
		catch (Exception error)
		{
			Log("state=open_failed; source=bubble; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
	}

	// ---------------- 右键气泡：换成「原 9 项 + MOD设置」 ----------------
	//
	// 实测链路：HandleHookChatRequest → OpenChatPanel → PlayerLineController.Show
	//          → RefreshEntries（组好 9 条并直接建好按钮）→ GetRootEntries。
	// 按钮建完后再往 _currentEntries 里插会被 SetEntryContents 按数据库校验丢掉，
	// 所以沿用参考 mod 的做法：在 Show() 之后用游戏自己的 ShowOptions(IReadOnlyList<string>) 重画一版，
	// 内容 = 原来 9 项的文字 + 「MOD设置」，点原来的项仍然交回 MarkEntryPicked 执行。

	private static bool _rightClickScope;

	/// <summary>点亮作用域那一帧；Show() 万一同帧没走到（被剧情之类挡住），隔两帧就作废。</summary>
	private static int _rightClickFrame = -1;

	private static Il2CppSystem.Action<PlayerLineEntry> _replacedEntryCallback;

	/// <summary>右键请求：标记作用域，只处理紧接着那一次 Show()。</summary>
	private static bool ArmRightClickScopePrefix()
	{
		_rightClickScope = true;
		_rightClickFrame = Time.frameCount;
		_active?.LogOnce("event=right_click_scope");
		return true;
	}

	/// <summary>Show() 之后把气泡列表换成「原项 + MOD设置」。</summary>
	private static void ShowPostfix(PlayerLineController __instance)
	{
		ModSettingsMenuIntegration integration = _active;
		if (integration == null || integration._disposed)
		{
			return;
		}
		bool armed = _rightClickScope;
		if (armed && _rightClickFrame >= 0 && Time.frameCount - _rightClickFrame > 2)
		{
			// 这次右键最后没走 Show()（被剧情之类挡了），别去篡改下一次无关的菜单。
			_rightClickScope = false;
			_active?.LogOnce("state=bubble_scope_expired");
			return;
		}
		_rightClickScope = false;
		try
		{
			if (__instance == null)
			{
				return;
			}
			Il2CppSystem.Collections.Generic.List<PlayerLineEntry> current = __instance._currentEntries;
			if (current == null || current.Count == 0)
			{
				integration.LogOnce("state=bubble_skipped; source=Show; reason=no_current_entries");
				return;
			}
			int count = current.Count;
			int settingsId = 0;
			try
			{
				settingsId = PlayerLineController.SettingsEntryId;
			}
			catch (Exception)
			{
				settingsId = 0;
			}
			int settingsIndex = -1;
			for (int i = 0; i < count; i++)
			{
				PlayerLineEntry item = current[i];
				if (item == null)
				{
					continue;
				}
				if ((settingsId != 0 && item.id == settingsId) || LooksLikeSettings(item.text))
				{
					settingsIndex = i;
				}
			}
			if (settingsIndex < 0)
			{
				integration.LogOnce("state=bubble_skipped; source=Show; reason=no_settings_entry; count=" + count);
				return;
			}
			// 作用域没点亮时的兜底：只有「最后一项就是设置项」才认成交互根菜单
			// （单击穿透关闭走的是 Unity 正常输入，未必经过上面两个入口）。
			bool settingsLast = settingsIndex == count - 1;
			if (!armed && !settingsLast)
			{
				integration.LogOnce("state=bubble_skipped; source=Show; reason=not_root_menu; count=" + count + "; settings_index=" + settingsIndex);
				return;
			}
			// 气泡面板一页最多 MaxOptionCount=9 项，根菜单 9 项正好占满；上限是只读的，抬不动。
			// 所以走游戏自己的分页入口 ShowEntryOptionsPaged：第 10 项落在第 2 页「打开设置」下面。
			Il2CppSystem.Collections.Generic.List<PlayerLineEntry> entryList = new Il2CppSystem.Collections.Generic.List<PlayerLineEntry>();
			for (int j = 0; j < count; j++)
			{
				entryList.Add(current[j]);
			}
			PlayerLineEntry ours = new PlayerLineEntry();
			ours.id = BubbleEntryId;
			ours.LineID = BubbleEntryId;
			ours.viewLimit = 0;
			ours.text = EntryLabel;
			entryList.Add(ours);
			Il2CppSystem.Action<PlayerLineEntry> entryCallback = (System.Action<PlayerLineEntry>)delegate(PlayerLineEntry picked)
			{
				if (picked != null && picked.id == BubbleEntryId)
				{
					integration.OpenFromBubble("ShowEntryOptionsPaged");
					return;
				}
				try
				{
					PlayerLineController.MarkEntryPicked(picked);
				}
				catch (Exception error)
				{
					integration.LogOnce("state=bubble_pick_failed; error=" + error.GetType().Name);
				}
			};
			_replacedEntryCallback = entryCallback;
			__instance.ShowEntryOptionsPaged(entryList, entryCallback, 0);
			integration.Log("event=bubble_menu_paged; count=" + entryList.Count + "; max=" + PlayerLineController.MaxOptionCount);
		}
		catch (Exception error)
		{
			integration.LogOnce("state=bubble_replace_failed; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
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

	/// <summary>挂前缀补丁（气泡菜单用：要在原方法建按钮之前改列表）。</summary>
	private bool TryPatchPrefix(Type type, string name, int arity, string patchName, ref int ok, ref int failed)
	{
		MethodInfo target = FindMethod(type, name, arity);
		MethodInfo patch = typeof(ModSettingsMenuIntegration).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic);
		if (target == null || patch == null)
		{
			failed++;
			LogOnce("state=patch_skipped; target=" + name + ":" + arity + "; prefix=true; resolved=" + Bool(target != null) + "; patch=" + Bool(patch != null));
			return false;
		}
		try
		{
			_harmony.Patch(target, new HarmonyMethod(patch), null, null, null, null);
			_patched.Add(target);
			ok++;
			return true;
		}
		catch (Exception error)
		{
			failed++;
			LogOnce("state=patch_failed; target=" + name + ":" + arity + "; prefix=true; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
			return false;
		}
	}

	private static MethodInfo FindMethod(Type type, string name, int parameterCount)	{
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
