using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Just_Lilith.Core.Llm;
using Just_Lilith.Core.Proactive;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 独立的「MOD 设置」页（阶段 A）。
///
/// 设计约束（见 docs/handoff-下一步-MOD设置页.md）：
/// - 只做「目前完全没有 UI」的两项：主动对话、窥屏；加上打开配置目录与状态回显。
/// - 外观照抄幻境对话框：深色底板 + 白色 2px Outline + 可拖动标题条，控件交给 <see cref="NativeTrayPageSkin"/> 贴原版皮肤。
/// - 不碰游戏本体设置，也不依赖 <c>_lilithItemRoot</c>：皮肤模板根由外部注入（解析失败就降级，页面照常打开）。
/// </summary>
internal sealed class ModSettingsWindow
{
	private const string Module = "module=ModUI";

	private const string PageTitle = "MOD 设置";

	private static readonly Color PanelBackground = new Color(0.1f, 0.1f, 0.1f, 0.98f);

	private static readonly Color FieldBackground = new Color(0.62f, 0.62f, 0.6f, 1f);

	private static readonly Color ButtonBackground = new Color(0.22f, 0.22f, 0.21f, 1f);

	private static readonly Color InputInk = new Color(0.08f, 0.08f, 0.08f, 1f);

	private static readonly Color InputPlaceholder = new Color(0.34f, 0.34f, 0.33f, 1f);

	private static readonly Color Muted = new Color(0.65f, 0.65f, 0.63f, 1f);

	private static readonly Color Warning = new Color(0.9f, 0.86f, 0.72f, 1f);

	private static readonly Color Good = new Color(0.76f, 0.8f, 0.74f, 1f);

	private readonly Transform _canvasRoot;

	private readonly Font _font;

	private readonly ManualLogSource _log;

	private readonly Func<Transform> _skinRootProvider;

	private readonly Action<InputField> _trackInput;

	private readonly Action<InputField> _untrackInput;

	private readonly string _settingsPath;

	private readonly string _llmSettingsPath;

	private readonly List<InputField> _inputs = new List<InputField>();

	private readonly List<string> _profileIds = new List<string>();

	private readonly List<string> _profileLabels = new List<string>();

	private GameObject _page;

	private RectTransform _pageRect;

	private RectTransform _dragHeader;

	private Text _proactiveToggleText;

	private InputField _intervalInput;

	private InputField _capInput;

	private Text _peekToggleText;

	private InputField _weightInput;

	private InputField _peekIntervalInput;

	private InputField _peekDailyInput;

	private Button _visionButton;

	private Text _visionText;

	private Text _statusText;

	private Text _footerText;

	private ProactiveSettings _settings = new ProactiveSettings();

	private string _editStatus = "";

	private Color _editStatusColor = Muted;

	private bool _dragging;

	private Vector2 _dragStartPointer;

	private Vector2 _dragStartPosition;

	private float _nextStatusTick;

	private bool _disposed;

	internal ModSettingsWindow(Transform canvasRoot, Font font, ManualLogSource log, Func<Transform> skinRootProvider, Action<InputField> trackInput, Action<InputField> untrackInput)
	{
		_canvasRoot = canvasRoot;
		_font = font;
		_log = log;
		_skinRootProvider = skinRootProvider;
		_trackInput = trackInput;
		_untrackInput = untrackInput;
		_settingsPath = Path.Combine(Paths.ConfigPath, ProactiveSettings.FileName);
		_llmSettingsPath = Path.Combine(Paths.ConfigPath, "local.just_lilith.llm.json");
	}

	internal bool IsOpen => _page != null && _page.activeSelf;

	internal void Open()
	{
		if (_disposed)
		{
			return;
		}
		if (_canvasRoot == null || _font == null)
		{
			Log("state=open_failed; reason=canvas_or_font_missing");
			return;
		}
		try
		{
			if (_page == null)
			{
				Build();
			}
			if (_page == null)
			{
				return;
			}
			LoadSettings();
			LoadProfiles();
			_editStatus = "";
			_page.SetActive(value: true);
			_page.transform.SetAsLastSibling();
			RefreshWidgets();
			Log("event=opened; path=" + _settingsPath);
		}
		catch (Exception error)
		{
			Log("state=open_failed; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
	}

	internal void Close(string reason)
	{
		bool num = IsOpen;
		_dragging = false;
		_dragHeader = null;
		if (_page != null)
		{
			ClearOwnSelection();
			_page.SetActive(value: false);
		}
		if (num)
		{
			Log("event=closed; reason=" + reason);
		}
	}

	private void CloseFromButton()
	{
		Close("button");
	}

	internal void Tick()
	{
		if (_disposed || !IsOpen)
		{
			return;
		}
		try
		{
			MaintainDrag();
			if (Input.GetKeyDown(KeyCode.Escape) && !AnyFieldFocused())
			{
				Close("escape");
				return;
			}
			if (Time.unscaledTime >= _nextStatusTick)
			{
				_nextStatusTick = Time.unscaledTime + 0.5f;
				RefreshStatus();
			}
		}
		catch (Exception error)
		{
			Log("state=tick_failed; error=" + error.GetType().Name);
		}
	}

	internal void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_dragging = false;
		ClearOwnSelection();
		foreach (InputField input in _inputs)
		{
			_untrackInput?.Invoke(input);
		}
		_inputs.Clear();
		if (_page != null)
		{
			try
			{
				_page.SetActive(value: false);
				UnityEngine.Object.Destroy(_page);
			}
			catch (Exception)
			{
			}
		}
		_page = null;
		_pageRect = null;
		_dragHeader = null;
		_proactiveToggleText = null;
		_intervalInput = null;
		_capInput = null;
		_peekToggleText = null;
		_weightInput = null;
		_peekIntervalInput = null;
		_peekDailyInput = null;
		_visionButton = null;
		_visionText = null;
		_statusText = null;
		_footerText = null;
	}

	private void ClearOwnSelection()
	{
		try
		{
			GameObject selected = EventSystem.current?.currentSelectedGameObject;
			if (selected != null && _page != null && selected.transform.IsChildOf(_page.transform))
			{
				EventSystem.current.SetSelectedGameObject(null);
			}
		}
		catch (Exception)
		{
		}
	}

	private bool AnyFieldFocused()
	{
		foreach (InputField input in _inputs)
		{
			if (input != null && input.isFocused)
			{
				return true;
			}
		}
		return false;
	}

	private void Build()
	{
		_page = RectObject("Just_Lilith.ModSettings.Dialog", _canvasRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(860f, 700f), Vector2.zero);
		_pageRect = _page.GetComponent<RectTransform>();
		AddImage(_page, PanelBackground).raycastTarget = true;
		Outline outline = _page.AddComponent<Outline>();
		outline.effectColor = Color.white;
		outline.effectDistance = new Vector2(2f, -2f);
		GameObject controls = RectObject("NativeControls", _page.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_dragHeader = RectObject("ModDragHeader", _page.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(700f, 46f), new Vector2(16f, -10f)).GetComponent<RectTransform>();
		_dragging = false;
		CreateText(controls.transform, "Title", PageTitle, 22, TextAnchor.MiddleLeft, new Vector2(20f, -16f), new Vector2(600f, 34f), FontStyle.Bold, Color.white);
		CreateButton(controls.transform, "CloseModSettings", "关闭", new Vector2(750f, -16f), new Vector2(90f, 34f), CloseFromButton);
		// ---- 主动对话 ----
		CreateText(controls.transform, "ProactiveHeader", "主动对话", 16, TextAnchor.MiddleLeft, new Vector2(20f, -64f), new Vector2(300f, 26f), FontStyle.Bold, Color.white);
		CreateText(controls.transform, "ProactiveToggleLabel", "总开关", 14, TextAnchor.MiddleLeft, new Vector2(20f, -96f), new Vector2(120f, 30f), FontStyle.Normal, Color.white);
		_proactiveToggleText = CreateButton(controls.transform, "ProactiveToggle", "已关闭", new Vector2(150f, -96f), new Vector2(150f, 30f), ToggleProactive).GetComponentInChildren<Text>();
		CreateText(controls.transform, "IntervalLabel", "期望间隔（秒）", 14, TextAnchor.MiddleLeft, new Vector2(20f, -134f), new Vector2(160f, 30f), FontStyle.Normal, Color.white);
		_intervalInput = CreateInput(controls.transform, "IntervalInput", "300", new Vector2(190f, -134f), new Vector2(110f, 30f), 6);
		CreateText(controls.transform, "IntervalHint", "30–3600，默认 300", 12, TextAnchor.MiddleLeft, new Vector2(312f, -134f), new Vector2(320f, 30f), FontStyle.Normal, Muted);
		CreateText(controls.transform, "CapLabel", "每条消息最多几条", 14, TextAnchor.MiddleLeft, new Vector2(20f, -172f), new Vector2(180f, 30f), FontStyle.Normal, Color.white);
		_capInput = CreateInput(controls.transform, "CapInput", "3", new Vector2(210f, -172f), new Vector2(90f, 30f), 2);
		CreateText(controls.transform, "CapHint", "1–10，默认 3", 12, TextAnchor.MiddleLeft, new Vector2(312f, -172f), new Vector2(320f, 30f), FontStyle.Normal, Muted);
		CreateButton(controls.transform, "SaveProactive", "保存主动对话", new Vector2(20f, -212f), new Vector2(180f, 34f), SaveProactive);
		// ---- 窥屏 ----
		CreateText(controls.transform, "PeekHeader", "窥屏", 16, TextAnchor.MiddleLeft, new Vector2(20f, -262f), new Vector2(300f, 26f), FontStyle.Bold, Color.white);
		CreateText(controls.transform, "PeekToggleLabel", "开关", 14, TextAnchor.MiddleLeft, new Vector2(20f, -294f), new Vector2(120f, 30f), FontStyle.Normal, Color.white);
		_peekToggleText = CreateButton(controls.transform, "PeekToggle", "已关闭", new Vector2(150f, -294f), new Vector2(150f, 30f), TogglePeek).GetComponentInChildren<Text>();
		CreateText(controls.transform, "WeightLabel", "权重", 14, TextAnchor.MiddleLeft, new Vector2(20f, -332f), new Vector2(120f, 30f), FontStyle.Normal, Color.white);
		_weightInput = CreateInput(controls.transform, "WeightInput", "30", new Vector2(150f, -332f), new Vector2(90f, 30f), 4);
		CreateText(controls.transform, "WeightHint", "0–1000，默认 30", 12, TextAnchor.MiddleLeft, new Vector2(252f, -332f), new Vector2(320f, 30f), FontStyle.Normal, Muted);
		CreateText(controls.transform, "VisionLabel", "视觉模型", 14, TextAnchor.MiddleLeft, new Vector2(20f, -370f), new Vector2(120f, 30f), FontStyle.Normal, Color.white);
		_visionButton = CreateButton(controls.transform, "VisionChoice", "未配置", new Vector2(150f, -370f), new Vector2(280f, 32f), CycleVisionProfile);
		_visionText = _visionButton.GetComponentInChildren<Text>();
		CreateText(controls.transform, "VisionHint", "点击切换 API 配置里的 profile", 12, TextAnchor.MiddleLeft, new Vector2(440f, -370f), new Vector2(400f, 30f), FontStyle.Normal, Muted);
		CreateText(controls.transform, "PeekIntervalLabel", "最小间隔（秒）", 14, TextAnchor.MiddleLeft, new Vector2(20f, -408f), new Vector2(160f, 30f), FontStyle.Normal, Color.white);
		_peekIntervalInput = CreateInput(controls.transform, "PeekIntervalInput", "300", new Vector2(190f, -408f), new Vector2(110f, 30f), 6);
		CreateText(controls.transform, "PeekIntervalHint", "两次窥屏至少隔多久，0 = 不限制", 12, TextAnchor.MiddleLeft, new Vector2(312f, -408f), new Vector2(500f, 30f), FontStyle.Normal, Muted);
		CreateText(controls.transform, "PeekDailyLabel", "每天上限", 14, TextAnchor.MiddleLeft, new Vector2(20f, -446f), new Vector2(120f, 30f), FontStyle.Normal, Color.white);
		_peekDailyInput = CreateInput(controls.transform, "PeekDailyInput", "20", new Vector2(150f, -446f), new Vector2(90f, 30f), 4);
		CreateText(controls.transform, "PeekDailyHint", "每天最多几次，0 = 不限制", 12, TextAnchor.MiddleLeft, new Vector2(252f, -446f), new Vector2(500f, 30f), FontStyle.Normal, Muted);
		CreateButton(controls.transform, "SavePeek", "保存窥屏设置", new Vector2(20f, -486f), new Vector2(180f, 34f), SavePeek);
		// ---- 目录与回显 ----
		CreateButton(controls.transform, "OpenConfigFolder", "打开配置目录", new Vector2(20f, -536f), new Vector2(180f, 34f), OpenConfigFolder);
		CreateButton(controls.transform, "ReloadSettings", "重新载入", new Vector2(212f, -536f), new Vector2(120f, 34f), ReloadSettings);
		_statusText = CreateText(controls.transform, "ModStatus", "", 12, TextAnchor.UpperLeft, new Vector2(20f, -580f), new Vector2(820f, 56f), FontStyle.Normal, Color.white);
		_statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
		_statusText.verticalOverflow = VerticalWrapMode.Overflow;
		_footerText = CreateText(controls.transform, "ModFooter", "", 12, TextAnchor.UpperLeft, new Vector2(20f, -646f), new Vector2(820f, 40f), FontStyle.Normal, Muted);
		_footerText.horizontalOverflow = HorizontalWrapMode.Wrap;
		_footerText.verticalOverflow = VerticalWrapMode.Overflow;
		ApplySkin(controls);
		_page.SetActive(value: false);
	}

	private void ApplySkin(GameObject controls)
	{
		string diagnostic = "skin_root=unavailable";
		try
		{
			Transform settingItemRoot = _skinRootProvider?.Invoke();
			if (settingItemRoot != null && NativeTrayPageSkin.TryApply(controls, settingItemRoot, out diagnostic))
			{
				Log("state=skin_applied; " + diagnostic);
				return;
			}
			if (settingItemRoot != null)
			{
				diagnostic = "state=skin_unresolved; " + diagnostic;
			}
		}
		catch (Exception error)
		{
			diagnostic = "state=skin_unresolved; error=" + error.GetType().Name;
		}
		Log(diagnostic);
	}

	private void LoadSettings()
	{
		_settings = ProactiveSettingsStore.LoadOrCreate(_settingsPath, out string diagnostic);
		if (!string.IsNullOrEmpty(diagnostic))
		{
			Log("state=settings_read_degraded; reason=" + diagnostic);
		}
	}

	private void LoadProfiles()
	{
		_profileIds.Clear();
		_profileLabels.Clear();
		try
		{
			LlmSettings llm = new LlmSettingsStore(_llmSettingsPath).Load();
			if (llm?.Profiles != null)
			{
				foreach (LlmProfileSettings profile in llm.Profiles)
				{
					string id = (profile.ProfileId ?? "").Trim();
					if (id.Length == 0)
					{
						continue;
					}
					string name = (profile.DisplayName ?? "").Trim();
					_profileIds.Add(id);
					_profileLabels.Add(name.Length == 0 ? id : (name + " (" + id + ")"));
				}
			}
		}
		catch (Exception error)
		{
			Log("state=profiles_unavailable; error=" + error.GetType().Name);
		}
	}

	private void RefreshWidgets()
	{
		SetTextIfIdle(_intervalInput, _settings.TargetIntervalSeconds.ToString(CultureInfo.InvariantCulture));
		SetTextIfIdle(_capInput, _settings.MaxPerUserMessage.ToString(CultureInfo.InvariantCulture));
		SetTextIfIdle(_weightInput, _settings.Weights.Screen.ToString(CultureInfo.InvariantCulture));
		SetTextIfIdle(_peekIntervalInput, _settings.PeekMinIntervalSeconds.ToString(CultureInfo.InvariantCulture));
		SetTextIfIdle(_peekDailyInput, _settings.PeekMaxPerDay.ToString(CultureInfo.InvariantCulture));
		if (_proactiveToggleText != null)
		{
			_proactiveToggleText.text = (_settings.Enabled ? "已开启" : "已关闭");
		}
		if (_peekToggleText != null)
		{
			_peekToggleText.text = (_settings.Sources.Screen ? "已开启" : "已关闭");
		}
		RefreshVision();
		RefreshStatus();
		RefreshFooter();
	}

	private void RefreshVision()
	{
		if (_visionText == null)
		{
			return;
		}
		string id = (_settings.VisionProfileId ?? "").Trim();
		if (id.Length == 0)
		{
			_visionText.text = "未配置";
			return;
		}
		int index = _profileIds.IndexOf(id);
		_visionText.text = ((index >= 0) ? _profileLabels[index] : (id + "（不在配置列表里）"));
	}

	private void RefreshStatus()
	{
		if (_statusText == null)
		{
			return;
		}
		StringBuilder builder = new StringBuilder();
		builder.Append("主动对话：").Append(_settings.Enabled ? "已开启" : "已关闭");
		builder.Append(" · 每条最多 ").Append(_settings.MaxPerUserMessage.ToString(CultureInfo.InvariantCulture)).Append(" 次");
		builder.Append(" · 期望间隔 ").Append(_settings.TargetIntervalSeconds.ToString(CultureInfo.InvariantCulture)).Append(" 秒");
		builder.Append('\n');
		builder.Append("窥屏：").Append(_settings.Sources.Screen ? "已开启" : "已关闭");
		builder.Append(" · 权重 ").Append(_settings.Weights.Screen.ToString(CultureInfo.InvariantCulture));
		if (string.IsNullOrWhiteSpace(_settings.VisionProfileId))
		{
			builder.Append(_settings.Sources.Screen ? " · 视觉模型未配置：抽中「屏幕」会降级成普通话题" : " · 视觉模型未配置");
		}
		else
		{
			builder.Append(" · 视觉模型 ").Append(_settings.VisionProfileId.Trim());
		}
		builder.Append('\n').Append("配置：").Append(_settingsPath).Append("（").Append(Stamp()).Append("）");
		if (!string.IsNullOrEmpty(_editStatus))
		{
			builder.Append('\n').Append(_editStatus);
		}
		_statusText.text = builder.ToString();
		_statusText.color = (string.IsNullOrEmpty(_editStatus) ? Color.white : _editStatusColor);
	}

	private void RefreshFooter()
	{
		if (_footerText != null)
		{
			// 皮肤/模板的解析细节只进日志，页面上不铺调试串。
			_footerText.text = "Just_Lilith " + Plugin.Version + " · MOD 设置（阶段 A：主动对话 / 窥屏）";
		}
	}

	private string Stamp()
	{
		try
		{
			if (File.Exists(_settingsPath))
			{
				return File.GetLastWriteTime(_settingsPath).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			}
		}
		catch (Exception)
		{
		}
		return "文件尚未创建";
	}

	private void SetTextIfIdle(InputField field, string value)
	{
		if (field != null && !field.isFocused)
		{
			field.text = value;
		}
	}

	private void ToggleProactive()
	{
		try
		{
			ProactiveSettings next = _settings with
			{
				Enabled = !_settings.Enabled
			};
			Commit(next, _settings.Enabled ? "已关闭主动对话" : "已开启主动对话");
		}
		catch (Exception error)
		{
			ReportEdit("切换主动对话失败：" + error.GetType().Name, isError: true);
		}
	}

	private void TogglePeek()
	{
		try
		{
			bool enabled = !_settings.Sources.Screen;
			ProactiveSettings next = _settings with
			{
				Sources = _settings.Sources with
				{
					Screen = enabled
				}
			};
			Commit(next, enabled ? "已开启窥屏" : "已关闭窥屏");
		}
		catch (Exception error)
		{
			ReportEdit("切换窥屏失败：" + error.GetType().Name, isError: true);
		}
	}

	private void CycleVisionProfile()
	{
		try
		{
			string current = (_settings.VisionProfileId ?? "").Trim();
			int index = _profileIds.IndexOf(current);
			int next = index + 1;
			string nextId = ((next >= _profileIds.Count) ? "" : _profileIds[next]);
			ProactiveSettings updated = _settings with
			{
				VisionProfileId = nextId
			};
			Commit(updated, nextId.Length == 0 ? "视觉模型已清空（只能走普通话题）" : ("视觉模型：" + nextId));
		}
		catch (Exception error)
		{
			ReportEdit("切换视觉模型失败：" + error.GetType().Name, isError: true);
		}
	}

	private void SaveProactive()
	{
		try
		{
			if (!TryReadInt(_intervalInput, "期望间隔", 30, 3600, out int interval))
			{
				return;
			}
			if (!TryReadInt(_capInput, "每条消息最多几条", 1, 10, out int cap))
			{
				return;
			}
			ProactiveSettings updated = _settings with
			{
				TargetIntervalSeconds = interval,
				MaxPerUserMessage = cap
			};
			Commit(updated, "已保存主动对话设置");
		}
		catch (Exception error)
		{
			ReportEdit("保存主动对话失败：" + error.GetType().Name, isError: true);
		}
	}

	private void SavePeek()
	{
		try
		{
			if (!TryReadInt(_weightInput, "窥屏权重", 0, 1000, out int weight))
			{
				return;
			}
			if (!TryReadInt(_peekIntervalInput, "最小间隔", 0, 86400, out int interval))
			{
				return;
			}
			if (!TryReadInt(_peekDailyInput, "每天上限", 0, 500, out int daily))
			{
				return;
			}
			ProactiveSettings updated = _settings with
			{
				Weights = _settings.Weights with
				{
					Screen = weight
				},
				PeekMinIntervalSeconds = interval,
				PeekMaxPerDay = daily
			};
			Commit(updated, "已保存窥屏设置");
		}
		catch (Exception error)
		{
			ReportEdit("保存窥屏设置失败：" + error.GetType().Name, isError: true);
		}
	}

	private void ReloadSettings()
	{
		try
		{
			LoadSettings();
			LoadProfiles();
			RefreshWidgets();
			ReportEdit("已从磁盘重新载入", isError: false);
			RefreshStatus();
		}
		catch (Exception error)
		{
			ReportEdit("重新载入失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenConfigFolder()
	{
		try
		{
			Process.Start("explorer.exe", Paths.ConfigPath);
			ReportEdit("已打开配置目录", isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("打开配置目录失败：" + error.GetType().Name, isError: true);
		}
	}

	/// <summary>写盘 + 回读。主动对话服务按文件 mtime 热载入，所以保存即生效。</summary>
	private void Commit(ProactiveSettings proposed, string message)
	{
		try
		{
			ProactiveSettingsStore.Save(_settingsPath, proposed);
		}
		catch (Exception error)
		{
			ReportEdit("写盘失败：" + error.GetType().Name + "（原配置保持不变）", isError: true);
			return;
		}
		_settings = ProactiveSettingsStore.LoadOrCreate(_settingsPath, out string diagnostic);
		if (!string.IsNullOrEmpty(diagnostic))
		{
			Log("state=settings_read_degraded; reason=" + diagnostic);
		}
		RefreshWidgets();
		ReportEdit(message + "（1 秒内热载入）", isError: false);
		RefreshStatus();
		Log("event=settings_saved; enabled=" + Bool(_settings.Enabled) + "; peek=" + Bool(_settings.Sources.Screen) + "; vision=" + (_settings.VisionProfileId ?? "") + "; target_s=" + _settings.TargetIntervalSeconds.ToString(CultureInfo.InvariantCulture) + "; cap=" + _settings.MaxPerUserMessage.ToString(CultureInfo.InvariantCulture));
	}

	private void ReportEdit(string message, bool isError)
	{
		_editStatus = message;
		_editStatusColor = (isError ? Warning : Good);
		RefreshStatus();
		if (isError)
		{
			Log("state=edit_rejected; message=" + SingleLine(message));
		}
	}

	private bool TryReadInt(InputField field, string label, int minimum, int maximum, out int value)
	{
		value = 0;
		if (field == null)
		{
			ReportEdit(label + "：控件不存在", isError: true);
			return false;
		}
		string text = (field.text ?? "").Trim();
		if (text.Length == 0)
		{
			ReportEdit(label + "：不能为空", isError: true);
			return false;
		}
		if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
		{
			ReportEdit(label + "：请填整数", isError: true);
			return false;
		}
		if (value < minimum || value > maximum)
		{
			ReportEdit(label + "：范围是 " + minimum + "–" + maximum + "，当前 " + value, isError: true);
			return false;
		}
		return true;
	}

	private void MaintainDrag()
	{
		if (_pageRect == null || _dragHeader == null || _canvasRoot == null)
		{
			_dragging = false;
			return;
		}
		RectTransform canvasRect = _canvasRoot.GetComponent<RectTransform>();
		if (canvasRect == null)
		{
			_dragging = false;
			return;
		}
		if (!Input.GetMouseButton(0))
		{
			_dragging = false;
			return;
		}
		if (!_dragging)
		{
			if (!Input.GetMouseButtonDown(0) || !RectTransformUtility.RectangleContainsScreenPoint(_dragHeader, Input.mousePosition, null) || !RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, Input.mousePosition, null, out _dragStartPointer))
			{
				return;
			}
			_dragStartPosition = _pageRect.anchoredPosition;
			_dragging = true;
			_pageRect.SetAsLastSibling();
		}
		if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, Input.mousePosition, null, out Vector2 localPoint))
		{
			Vector2 vector = _dragStartPosition + localPoint - _dragStartPointer;
			Rect rect = canvasRect.rect;
			float halfWidth = _pageRect.rect.width / 2f;
			float halfHeight = _pageRect.rect.height / 2f;
			float minX = rect.xMin + halfWidth;
			float maxX = rect.xMax - halfWidth;
			float minY = rect.yMin + halfHeight;
			float maxY = rect.yMax - halfHeight;
			_pageRect.anchoredPosition = new Vector2((minX <= maxX) ? Mathf.Clamp(vector.x, minX, maxX) : rect.center.x, (minY <= maxY) ? Mathf.Clamp(vector.y, minY, maxY) : rect.center.y);
		}
	}

	private InputField CreateInput(Transform parent, string name, string placeholder, Vector2 topLeft, Vector2 size, int characterLimit)
	{
		GameObject gameObject = RectObject(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), size, topLeft);
		AddImage(gameObject, FieldBackground);
		gameObject.AddComponent<RectMask2D>();
		InputField inputField = gameObject.AddComponent<InputField>();
		Text text = CreateText(gameObject.transform, "Text", "", 14, TextAnchor.MiddleLeft, new Vector2(8f, -2f), new Vector2(size.x - 16f, size.y - 4f), FontStyle.Normal, InputInk);
		Text text2 = CreateText(gameObject.transform, "Placeholder", placeholder, 14, TextAnchor.MiddleLeft, new Vector2(8f, -2f), new Vector2(size.x - 16f, size.y - 4f), FontStyle.Italic, InputPlaceholder);
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		inputField.textComponent = text;
		inputField.placeholder = text2;
		inputField.lineType = InputField.LineType.SingleLine;
		inputField.contentType = InputField.ContentType.IntegerNumber;
		inputField.characterLimit = characterLimit;
		inputField.caretColor = InputInk;
		inputField.selectionColor = new Color(0.72f, 0.72f, 0.7f, 0.42f);
		_inputs.Add(inputField);
		_trackInput?.Invoke(inputField);
		return inputField;
	}

	private Button CreateButton(Transform parent, string name, string label, Vector2 topLeft, Vector2 size, Action onClick)
	{
		GameObject gameObject = RectObject(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), size, topLeft);
		Image targetGraphic = AddImage(gameObject, ButtonBackground);
		Button button = gameObject.AddComponent<Button>();
		button.targetGraphic = targetGraphic;
		ColorBlock colors = button.colors;
		colors.normalColor = ButtonBackground;
		colors.highlightedColor = new Color(0.34f, 0.34f, 0.33f, 1f);
		colors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
		colors.selectedColor = new Color(0.3f, 0.3f, 0.29f, 1f);
		colors.disabledColor = new Color(0.16f, 0.16f, 0.16f, 0.75f);
		button.colors = colors;
		RectTransform rectTransform = CreateText(gameObject.transform, "Label", label, 14, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, FontStyle.Normal, Color.white).rectTransform;
		rectTransform.anchorMin = Vector2.zero;
		rectTransform.anchorMax = Vector2.one;
		rectTransform.offsetMin = new Vector2(4f, 2f);
		rectTransform.offsetMax = new Vector2(-4f, -2f);
		button.onClick.AddListener((Action)delegate
		{
			RunUiCallback(name, onClick);
		});
		return button;
	}

	private Text CreateText(Transform parent, string name, string value, int size, TextAnchor alignment, Vector2 topLeft, Vector2 dimensions, FontStyle style, Color color)
	{
		Text text = RectObject(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), dimensions, topLeft).AddComponent<Text>();
		text.font = _font;
		text.fontSize = size;
		text.fontStyle = style;
		text.alignment = alignment;
		text.color = color;
		text.supportRichText = false;
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		text.verticalOverflow = VerticalWrapMode.Truncate;
		text.text = value;
		text.raycastTarget = false;
		return text;
	}

	private static GameObject RectObject(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 topLeft)
	{
		GameObject obj = new GameObject(name, Il2CppType.Of<RectTransform>());
		obj.layer = parent.gameObject.layer;
		obj.transform.SetParent(parent, worldPositionStays: false);
		RectTransform component = obj.GetComponent<RectTransform>();
		component.anchorMin = anchorMin;
		component.anchorMax = anchorMax;
		component.pivot = ((anchorMin == anchorMax) ? anchorMin : new Vector2(0.5f, 0.5f));
		component.sizeDelta = size;
		component.anchoredPosition = topLeft;
		return obj;
	}

	private static Image AddImage(GameObject root, Color color)
	{
		Image image = root.AddComponent<Image>();
		image.color = color;
		return image;
	}

	private void RunUiCallback(string callback, Action action)
	{
		try
		{
			action?.Invoke();
		}
		catch (Exception error)
		{
			Log("state=callback_failed; callback=" + callback + "; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
			ReportEdit(callback + " 执行失败：" + error.GetType().Name, isError: true);
		}
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
