using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Just_Lilith.Core.Configuration;
using Just_Lilith.Core.Contracts;
using Just_Lilith.Core.Llm;
using Just_Lilith.Core.Proactive;
using Just_Lilith.Core.Speech;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 独立的「MOD 设置」页。
///
/// 阶段 A 只有主动对话 / 窥屏两块；阶段 B 把散在游戏本体设置页里的四块搬了进来
/// （模型 API、语音 TTS、对话与记忆、热键），改成左侧分组 + 右侧内容。
/// 页面本身只管摆控件：真正的读写全部转给 <see cref="LlmConfigurationView"/> 的门面，
/// 业务逻辑仍留在 controller / *Store 里。
///
/// 外观照抄幻境对话框：深色底板 + 白色 2px Outline + 可拖动标题条，
/// 控件交给 <see cref="NativeTrayPageSkin"/> 贴原版皮肤；模板根解析失败就降级成自绘深色皮肤照常打开。
/// </summary>
internal sealed class ModSettingsWindow
{
	private const string Module = "module=ModUI";

	private const string PageTitle = "MOD 设置";

	private const float PageWidth = 980f;

	private const float PageHeight = 800f;

	private const float ContentLeft = 196f;

	private const float ContentWidth = 764f;

	private const float ContentHeight = 626f;

	private const float NavTop = -64f;

	private const float NavStep = 40f;

	private const float NavWidth = 160f;

	private const float NavHeight = 34f;

	/// <summary>页面离桌面可视区边缘的最小留白（屏幕像素）。</summary>
	private const float PlacementMargin = 10f;

	private static readonly string[] NavLabels = new string[7] { "模型 / API", "Agent", "语音 / TTS", "对话与记忆", "热键", "主动对话", "窥屏" };

	private static readonly Color PanelBackground = new Color(0.1f, 0.1f, 0.1f, 0.98f);

	private static readonly Color FieldBackground = new Color(0.62f, 0.62f, 0.6f, 1f);

	private static readonly Color ButtonBackground = new Color(0.22f, 0.22f, 0.21f, 1f);

	private static readonly Color NavActiveBackground = new Color(0.42f, 0.42f, 0.4f, 1f);

	private static readonly Color InputInk = new Color(0.08f, 0.08f, 0.08f, 1f);

	private static readonly Color InputPlaceholder = new Color(0.34f, 0.34f, 0.33f, 1f);

	private static readonly Color Muted = new Color(0.65f, 0.65f, 0.63f, 1f);

	private static readonly Color Warning = new Color(0.9f, 0.86f, 0.72f, 1f);

	private static readonly Color Good = new Color(0.76f, 0.8f, 0.74f, 1f);

	private readonly LlmConfigurationView _owner;

	private readonly Transform _canvasRoot;

	private readonly Font _font;

	private readonly ManualLogSource _log;

	private readonly string _settingsPath;

	private readonly string _llmSettingsPath;

	private readonly List<InputField> _inputs = new List<InputField>();

	private readonly List<string> _profileIds = new List<string>();

	private readonly List<string> _profileLabels = new List<string>();

	private readonly List<GameObject> _groups = new List<GameObject>();

	private readonly List<Button> _navButtons = new List<Button>();

	private readonly List<Image> _navImages = new List<Image>();

	private GameObject _page;

	private RectTransform _pageRect;

	private RectTransform _dragHeader;

	private int _activeGroup;

	private Text _navHint;

	// 主动对话
	private Text _proactiveToggleText;

	private InputField _intervalInput;

	private InputField _capInput;

	private Text _incrementEcho;

	// 窥屏
	private Text _peekToggleText;

	private InputField _weightInput;

	private InputField _peekIntervalInput;

	private InputField _peekDailyInput;

	private Text _visionText;

	// 语音
	private Text _speechToggleText;

	private Button _speechToggleButton;

	private Text _speechLanguageText;

	private Button _speechLanguageButton;

	private Text _speechReferenceText;

	private Button _speechReferenceButton;

	private Text _speechStatusText;

	// 热键
	private Text _hotkeyText;

	private Button _hotkeyButton;

	// 模型
	private GameObject _apiHost;

	private Text _apiHint;

	private bool _apiAttached;

	// Agent
	private Text _agentStatusText;

	private Button _agentToggleButton;

	private Text _agentToggleText;

	private Button _agentPersonaButton;

	private Button _agentModelButton;

	private Text _agentModelText;

	private Button _agentReasoningButton;

	private Text _agentReasoningText;

	private Button _agentSettingsButton;

	private Button _agentCancelButton;

	private AgentUiState _agentState;

	// 底部
	private Text _statusText;

	private Text _footerText;

	private ProactiveSettings _settings = new ProactiveSettings();

	private string _editStatus = "";

	private Color _editStatusColor = Muted;

	private bool _dragging;

	private Vector2 _dragStartPointer;

	private Vector2 _dragStartPosition;

	private float _nextStatusTick;

	private float _nextPlacementTick;

	private bool _placementLogged;

	private bool _disposed;

	internal ModSettingsWindow(LlmConfigurationView owner, Transform canvasRoot, Font font, ManualLogSource log)
	{
		_owner = owner;
		_canvasRoot = canvasRoot;
		_font = font;
		_log = log;
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
			ShowGroup(_activeGroup);
			RefreshWidgets();
			PlaceOnVisibleArea(fresh: true);
			Log("event=opened; path=" + _settingsPath + "; legacy_native_injection=" + Bool(_owner.LegacyNativeInjection) + "; pos=" + DescribePlacement());
		}
		catch (Exception error)
		{
			Log("state=open_failed; error=" + error.GetType().Name + "; message=" + SingleLine(error.Message));
		}
	}

	internal void Close(string reason)
	{
		bool wasOpen = IsOpen;
		_dragging = false;
		_dragHeader = null;
		ReleaseApiPanel();
		if (_page != null)
		{
			ClearOwnSelection();
			_page.SetActive(value: false);
		}
		if (wasOpen)
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
			RefreshLiveWidgets();
			if (!_dragging && Time.unscaledTime >= _nextPlacementTick)
			{
				_nextPlacementTick = Time.unscaledTime + 0.3f;
				PlaceOnVisibleArea(fresh: false);
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
		ReleaseApiPanel();
		foreach (InputField input in _inputs)
		{
			_owner.UntrackModSettingsInput(input);
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
		_groups.Clear();
		_navButtons.Clear();
		_navImages.Clear();
		_apiHost = null;
		_apiHint = null;
		_agentStatusText = null;
		_agentToggleButton = null;
		_agentToggleText = null;
		_agentPersonaButton = null;
		_agentModelButton = null;
		_agentModelText = null;
		_agentReasoningButton = null;
		_agentReasoningText = null;
		_agentSettingsButton = null;
		_agentCancelButton = null;
		_agentState = null;
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

	// ---------------- 建页面 ----------------

	private void Build()
	{
		_page = RectObject("Just_Lilith.ModSettings.Dialog", _canvasRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(PageWidth, PageHeight), Vector2.zero);
		_pageRect = _page.GetComponent<RectTransform>();
		AddImage(_page, PanelBackground).raycastTarget = true;
		Outline outline = _page.AddComponent<Outline>();
		outline.effectColor = Color.white;
		outline.effectDistance = new Vector2(2f, -2f);
		GameObject controls = RectObject("NativeControls", _page.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_dragHeader = RectObject("ModDragHeader", _page.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(PageWidth - 120f, 46f), new Vector2(16f, -10f)).GetComponent<RectTransform>();
		_dragging = false;
		CreateText(controls.transform, "Title", PageTitle, 22, TextAnchor.MiddleLeft, new Vector2(20f, -16f), new Vector2(600f, 34f), FontStyle.Bold, Color.white);
		CreateButton(controls.transform, "CloseModSettings", "关闭", new Vector2(PageWidth - 110f, -16f), new Vector2(90f, 34f), CloseFromButton);
		BuildNav(controls.transform);
		BuildGroups(controls.transform);
		BuildBottomBar(controls.transform);
		ApplySkin(controls);
		_page.SetActive(value: false);
	}

	private void BuildNav(Transform parent)
	{
		CreateText(parent, "NavTitle", "设置分组", 12, TextAnchor.MiddleLeft, new Vector2(20f, -48f), new Vector2(NavWidth, 16f), FontStyle.Normal, Muted);
		for (int i = 0; i < NavLabels.Length; i++)
		{
			int index = i;
			Button button = CreateButton(parent, "Nav" + i, NavLabels[i], new Vector2(20f, NavTop - i * NavStep), new Vector2(NavWidth, NavHeight), delegate
			{
				ShowGroup(index);
			});
			_navButtons.Add(button);
			_navImages.Add(button.GetComponent<Image>());
		}
		_navHint = CreateText(parent, "NavHint", "", 11, TextAnchor.UpperLeft, new Vector2(20f, NavTop - NavLabels.Length * NavStep - 8f), new Vector2(NavWidth, 120f), FontStyle.Normal, Muted);
		_navHint.horizontalOverflow = HorizontalWrapMode.Wrap;
		_navHint.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildGroups(Transform parent)
	{
		Transform content = RectObject("ModContent", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(ContentWidth, ContentHeight), new Vector2(ContentLeft, -64f)).transform;
		for (int i = 0; i < NavLabels.Length; i++)
		{
			GameObject group = RectObject("Group" + i, content, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(ContentWidth, ContentHeight), Vector2.zero);
			_groups.Add(group);
			switch (i)
			{
			case 0:
				BuildApiGroup(group.transform);
				break;
			case 1:
				BuildAgentGroup(group.transform);
				break;
			case 2:
				BuildSpeechGroup(group.transform);
				break;
			case 3:
				BuildConversationGroup(group.transform);
				break;
			case 4:
				BuildHotkeyGroup(group.transform);
				break;
			case 5:
				BuildProactiveGroup(group.transform);
				break;
			default:
				BuildPeekGroup(group.transform);
				break;
			}
		}
	}

	private void BuildApiGroup(Transform group)
	{
		CreateText(group, "ApiHeader", "模型（API / LLM）", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		_apiHint = CreateText(group, "ApiHint", "", 12, TextAnchor.UpperLeft, new Vector2(0f, -34f), new Vector2(ContentWidth, 56f), FontStyle.Normal, Muted);
		_apiHint.horizontalOverflow = HorizontalWrapMode.Wrap;
		_apiHint.verticalOverflow = VerticalWrapMode.Overflow;
		_apiHost = RectObject("ApiHost", group, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(436f, 390f), new Vector2(0f, -96f));
		Text note = CreateText(group, "ApiNote", "三套配置从上到下依次生效；每行改完点行内的「保存」才会写盘。\n顶部那行「顶部配置尚未完成」是当前生效状态。", 12, TextAnchor.UpperLeft, new Vector2(460f, -100f), new Vector2(300f, 120f), FontStyle.Normal, Muted);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		note.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildAgentGroup(Transform group)
	{
		CreateText(group, "AgentHeader", "Agent", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		_agentStatusText = CreateText(group, "AgentStatus", "", 12, TextAnchor.UpperLeft, new Vector2(0f, -36f), new Vector2(ContentWidth, 56f), FontStyle.Normal, Muted);
		_agentStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
		_agentStatusText.verticalOverflow = VerticalWrapMode.Overflow;
		_agentToggleButton = CreateButton(group, "AgentToggle", "Agent：关", new Vector2(0f, -100f), new Vector2(360f, 46f), ToggleAgent);
		_agentToggleText = _agentToggleButton.GetComponentInChildren<Text>();
		_agentPersonaButton = CreateButton(group, "AgentPersona", "Agent 人格", new Vector2(380f, -100f), new Vector2(360f, 46f), OpenAgentPersona);
		_agentModelButton = CreateButton(group, "AgentModel", "模型：-", new Vector2(0f, -160f), new Vector2(360f, 46f), CycleAgentModel);
		_agentModelText = _agentModelButton.GetComponentInChildren<Text>();
		_agentReasoningButton = CreateButton(group, "AgentReasoning", "推理：-", new Vector2(380f, -160f), new Vector2(360f, 46f), CycleAgentReasoning);
		_agentReasoningText = _agentReasoningButton.GetComponentInChildren<Text>();
		_agentSettingsButton = CreateButton(group, "AgentSettings", "Agent 设置", new Vector2(0f, -220f), new Vector2(360f, 46f), OpenAgentSettings);
		_agentCancelButton = CreateButton(group, "AgentCancel", "取消当前对话", new Vector2(380f, -220f), new Vector2(360f, 46f), CancelAgentChat);
		Text hint = CreateText(group, "AgentHint", "模型 / 推理点一下循环切换；Agent 人格与 Agent 设置都走原来的编辑器窗口。\nAgent 关闭时聊天照旧走普通 LLM，只有开启后才由 Agent 接管。", 12, TextAnchor.UpperLeft, new Vector2(0f, -290f), new Vector2(ContentWidth, 60f), FontStyle.Normal, Muted);
		hint.horizontalOverflow = HorizontalWrapMode.Wrap;
		hint.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildSpeechGroup(Transform group)
	{
		CreateText(group, "SpeechHeader", "语音（TTS）", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		CreateText(group, "ServiceLabel", "TTS 服务", 14, TextAnchor.MiddleLeft, new Vector2(0f, -44f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_speechToggleButton = CreateButton(group, "SpeechServiceToggle", "关闭", new Vector2(150f, -44f), new Vector2(160f, 30f), ToggleSpeechService);
		_speechToggleText = _speechToggleButton.GetComponentInChildren<Text>();
		CreateText(group, "ServiceHint", "关闭时聊天仍可用，只是不朗读", 12, TextAnchor.MiddleLeft, new Vector2(330f, -44f), new Vector2(430f, 30f), FontStyle.Normal, Muted);
		CreateText(group, "LanguageLabel", "朗读语言", 14, TextAnchor.MiddleLeft, new Vector2(0f, -90f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_speechLanguageButton = CreateButton(group, "SpeechLanguage", "汉语", new Vector2(150f, -90f), new Vector2(160f, 30f), CycleSpeechLanguage);
		_speechLanguageText = _speechLanguageButton.GetComponentInChildren<Text>();
		CreateText(group, "LanguageHint", "汉语 / 日语", 12, TextAnchor.MiddleLeft, new Vector2(330f, -90f), new Vector2(430f, 30f), FontStyle.Normal, Muted);
		CreateText(group, "ReferenceLabel", "参考风格", 14, TextAnchor.MiddleLeft, new Vector2(0f, -136f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_speechReferenceButton = CreateButton(group, "SpeechReference", "自动", new Vector2(150f, -136f), new Vector2(160f, 30f), CycleSpeechReference);
		_speechReferenceText = _speechReferenceButton.GetComponentInChildren<Text>();
		CreateText(group, "ReferenceHint", "自动 = 按台词情绪挑", 12, TextAnchor.MiddleLeft, new Vector2(330f, -136f), new Vector2(430f, 30f), FontStyle.Normal, Muted);
		_speechStatusText = CreateText(group, "SpeechStatus", "", 12, TextAnchor.UpperLeft, new Vector2(0f, -180f), new Vector2(ContentWidth, 60f), FontStyle.Normal, Muted);
		_speechStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
		_speechStatusText.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildConversationGroup(Transform group)
	{
		CreateText(group, "ConversationHeader", "对话与记忆", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		CreateButton(group, "OpenPersonality", "人格 Prompt", new Vector2(0f, -44f), new Vector2(360f, 46f), OpenPersonality);
		CreateButton(group, "OpenJourneys", "幻境", new Vector2(380f, -44f), new Vector2(360f, 46f), OpenJourneys);
		CreateButton(group, "OpenDreamMemory", "幻境记忆", new Vector2(0f, -104f), new Vector2(360f, 46f), OpenDreamMemory);
		CreateButton(group, "OpenWorldBook", "世界书", new Vector2(380f, -104f), new Vector2(360f, 46f), OpenWorldBook);
		Text hint = CreateText(group, "ConversationHint", "这四项都复用原有的编辑器窗口：人格 Prompt / 世界书 / 幻境记忆走游戏本体的编辑器，幻境列表用自建对话框，改完立即生效。", 12, TextAnchor.UpperLeft, new Vector2(0f, -166f), new Vector2(ContentWidth, 60f), FontStyle.Normal, Muted);
		hint.horizontalOverflow = HorizontalWrapMode.Wrap;
		hint.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildHotkeyGroup(Transform group)
	{
		CreateText(group, "HotkeyHeader", "热键", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		CreateText(group, "HotkeyLabel", "唤出聊天框", 14, TextAnchor.MiddleLeft, new Vector2(0f, -44f), new Vector2(140f, 34f), FontStyle.Normal, Color.white);
		_hotkeyButton = CreateButton(group, "HotkeyButton", "F7", new Vector2(150f, -44f), new Vector2(460f, 34f), ToggleHotkeyCapture);
		_hotkeyText = _hotkeyButton.GetComponentInChildren<Text>();
		Text hint = CreateText(group, "HotkeyHint", "点一下开始录制，然后按新的按键；Esc 取消，退格恢复默认 F7。录制期间其它按键不会被游戏吃掉。", 12, TextAnchor.UpperLeft, new Vector2(0f, -92f), new Vector2(ContentWidth, 60f), FontStyle.Normal, Muted);
		hint.horizontalOverflow = HorizontalWrapMode.Wrap;
		hint.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildProactiveGroup(Transform group)
	{
		CreateText(group, "ProactiveHeader", "主动对话", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		CreateText(group, "ProactiveToggleLabel", "总开关", 14, TextAnchor.MiddleLeft, new Vector2(0f, -44f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_proactiveToggleText = CreateButton(group, "ProactiveToggle", "已关闭", new Vector2(150f, -44f), new Vector2(160f, 30f), ToggleProactive).GetComponentInChildren<Text>();
		CreateText(group, "IntervalLabel", "期望间隔（秒）", 14, TextAnchor.MiddleLeft, new Vector2(0f, -86f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_intervalInput = CreateInput(group, "IntervalInput", "300", new Vector2(150f, -86f), new Vector2(110f, 30f), 6);
		CreateText(group, "IntervalHint", "30–3600，默认 300", 12, TextAnchor.MiddleLeft, new Vector2(272f, -86f), new Vector2(430f, 30f), FontStyle.Normal, Muted);
		CreateText(group, "CapLabel", "每条消息最多几条", 14, TextAnchor.MiddleLeft, new Vector2(0f, -128f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_capInput = CreateInput(group, "CapInput", "3", new Vector2(150f, -128f), new Vector2(90f, 30f), 2);
		CreateText(group, "CapHint", "1–10，默认 3", 12, TextAnchor.MiddleLeft, new Vector2(252f, -128f), new Vector2(430f, 30f), FontStyle.Normal, Muted);
		_incrementEcho = CreateText(group, "IncrementEcho", "", 12, TextAnchor.UpperLeft, new Vector2(0f, -170f), new Vector2(ContentWidth, 44f), FontStyle.Normal, Muted);
		_incrementEcho.horizontalOverflow = HorizontalWrapMode.Wrap;
		_incrementEcho.verticalOverflow = VerticalWrapMode.Overflow;
		CreateButton(group, "SaveProactive", "保存主动对话", new Vector2(0f, -222f), new Vector2(180f, 34f), SaveProactive);
	}

	private void BuildPeekGroup(Transform group)
	{
		CreateText(group, "PeekHeader", "窥屏", 15, TextAnchor.MiddleLeft, new Vector2(0f, -4f), new Vector2(400f, 24f), FontStyle.Bold, Color.white);
		CreateText(group, "PeekToggleLabel", "开关", 14, TextAnchor.MiddleLeft, new Vector2(0f, -44f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_peekToggleText = CreateButton(group, "PeekToggle", "已关闭", new Vector2(150f, -44f), new Vector2(160f, 30f), TogglePeek).GetComponentInChildren<Text>();
		CreateText(group, "WeightLabel", "权重", 14, TextAnchor.MiddleLeft, new Vector2(0f, -86f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_weightInput = CreateInput(group, "WeightInput", "300", new Vector2(150f, -86f), new Vector2(90f, 30f), 4);
		CreateText(group, "WeightHint", "0–1000 = 这一轮窥屏的概率（1000 = 每轮都窥屏）", 12, TextAnchor.MiddleLeft, new Vector2(252f, -86f), new Vector2(500f, 30f), FontStyle.Normal, Muted);
		CreateText(group, "VisionLabel", "视觉模型", 14, TextAnchor.MiddleLeft, new Vector2(0f, -128f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		Button visionButton = CreateButton(group, "VisionChoice", "未配置", new Vector2(150f, -128f), new Vector2(280f, 32f), CycleVisionProfile);
		_visionText = visionButton.GetComponentInChildren<Text>();
		CreateText(group, "VisionHint", "点击切换 API 配置里的 profile", 12, TextAnchor.MiddleLeft, new Vector2(440f, -128f), new Vector2(320f, 30f), FontStyle.Normal, Muted);
		CreateText(group, "PeekIntervalLabel", "最小间隔（秒）", 14, TextAnchor.MiddleLeft, new Vector2(0f, -170f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_peekIntervalInput = CreateInput(group, "PeekIntervalInput", "300", new Vector2(150f, -170f), new Vector2(110f, 30f), 6);
		CreateText(group, "PeekIntervalHint", "两次窥屏至少隔多久，0 = 不限制", 12, TextAnchor.MiddleLeft, new Vector2(272f, -170f), new Vector2(480f, 30f), FontStyle.Normal, Muted);
		CreateText(group, "PeekDailyLabel", "每天上限", 14, TextAnchor.MiddleLeft, new Vector2(0f, -212f), new Vector2(140f, 30f), FontStyle.Normal, Color.white);
		_peekDailyInput = CreateInput(group, "PeekDailyInput", "20", new Vector2(150f, -212f), new Vector2(90f, 30f), 4);
		CreateText(group, "PeekDailyHint", "每天最多几次，0 = 不限制", 12, TextAnchor.MiddleLeft, new Vector2(252f, -212f), new Vector2(480f, 30f), FontStyle.Normal, Muted);
		CreateButton(group, "SavePeek", "保存窥屏设置", new Vector2(0f, -256f), new Vector2(180f, 34f), SavePeek);
		CreateButton(group, "TestPeekNow", "现在试一次窥屏", new Vector2(200f, -256f), new Vector2(180f, 34f), TestPeekNow);
		// T6：把耦合关系写在页面上，别再让人以为“单开窥屏”能生效。
		Text coupling = CreateText(group, "PeekCoupling", "窥屏依附于主动对话开关：主动对话关着的时候，窥屏也不会触发。", 12, TextAnchor.UpperLeft, new Vector2(0f, -304f), new Vector2(ContentWidth, 44f), FontStyle.Normal, Warning);
		coupling.horizontalOverflow = HorizontalWrapMode.Wrap;
		coupling.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void BuildBottomBar(Transform parent)
	{
		CreateButton(parent, "OpenConfigFolder", "打开配置目录", new Vector2(20f, -704f), new Vector2(180f, 34f), OpenConfigFolder);
		CreateButton(parent, "ReloadSettings", "重新载入", new Vector2(212f, -704f), new Vector2(120f, 34f), ReloadSettings);
		_footerText = CreateText(parent, "ModFooter", "", 12, TextAnchor.MiddleLeft, new Vector2(350f, -704f), new Vector2(490f, 34f), FontStyle.Normal, Muted);
		// 第二个「关闭」：万一标题条被裁掉（窗口比屏幕高等情况），底部永远够得着。
		CreateButton(parent, "CloseModSettingsBottom", "关闭", new Vector2(860f, -704f), new Vector2(90f, 34f), CloseFromButton);
		_statusText = CreateText(parent, "ModStatus", "", 12, TextAnchor.UpperLeft, new Vector2(20f, -748f), new Vector2(940f, 40f), FontStyle.Normal, Color.white);
		_statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
		_statusText.verticalOverflow = VerticalWrapMode.Overflow;
	}

	private void ApplySkin(GameObject controls)
	{
		string diagnostic = "skin_root=unavailable";
		try
		{
			Transform settingItemRoot = _owner.ResolveModSkinRoot();
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

	// ---------------- 分组切换 ----------------

	private void ShowGroup(int index)
	{
		if (_groups.Count == 0 || index < 0 || index >= _groups.Count)
		{
			return;
		}
		_activeGroup = index;
		for (int i = 0; i < _groups.Count; i++)
		{
			if (_groups[i] != null)
			{
				_groups[i].SetActive(i == index);
			}
		}
		for (int j = 0; j < _navImages.Count; j++)
		{
			Image image = _navImages[j];
			if (image != null)
			{
				image.color = ((j == index) ? NavActiveBackground : ButtonBackground);
			}
			Button button = _navButtons[j];
			if (button != null)
			{
				Text label = button.GetComponentInChildren<Text>();
				if (label != null)
				{
					label.fontStyle = ((j == index) ? FontStyle.Bold : FontStyle.Normal);
				}
			}
		}
		if (index == 0)
		{
			AttachApiPanel();
		}
		else
		{
			ReleaseApiPanel();
		}
		RefreshWidgets();
		RefreshLiveWidgets();
	}

	private void AttachApiPanel()
	{
		if (_apiAttached || _apiHost == null)
		{
			return;
		}
		if (_owner.LegacyNativeInjection)
		{
			if (_apiHint != null)
			{
				_apiHint.text = "旧的注入还开着（ui.legacy_native_injection = true）：三套 API 配置仍在游戏本体设置页的「API 设置」页签里。\n把基础项配置里的 ui.legacy_native_injection 改成 false，这里就会接管，游戏本体设置页也会变回原版。";
			}
			return;
		}
		_apiAttached = _owner.ModAttachApiPanel(_apiHost.transform);
		if (_apiHint != null)
		{
			_apiHint.text = _apiAttached ? "三套 API 配置 + 顶部状态都在这儿；改动只在点各行的「保存」后生效。" : "API 设置面板没能挂上来，请查看插件日志（module=ModUI）。";
		}
		if (_apiAttached)
		{
			Log("event=api_panel_attached");
		}
	}

	private void ReleaseApiPanel()
	{
		if (!_apiAttached)
		{
			return;
		}
		_apiAttached = false;
		_owner.ModReleaseApiPanel();
	}

	// ---------------- 数据 ----------------

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
		RefreshIncrementEcho();
		RefreshFooter();
		RefreshStatus();
		RefreshLiveWidgets();
	}

	/// <summary>每帧热刷新那几块「外部状态」：TTS / 热键 / 旧注入提示。</summary>
	private void RefreshLiveWidgets()
	{
		RefreshSpeech();
		RefreshHotkey();
		RefreshNavHint();
	}

	private void RefreshNavHint()
	{
		if (_navHint == null)
		{
			return;
		}
		_navHint.text = "旧注入：" + (_owner.LegacyNativeInjection ? "开着\n（设置仍挂在游戏本体设置页）" : "已关闭\n（只在这里改）");
	}

	private void RefreshSpeech()
	{
		if (_speechToggleText == null)
		{
			return;
		}
		SpeechSettings settings = _owner.GetModSpeechSettings();
		TtsServiceState state = _owner.GetModSpeechServiceState();
		bool loaded = _owner.ModSpeechLoaded;
		SpeechServiceRadioState presentation = SpeechRadioSelection.ServicePresentation(state.Phase, loaded);
		bool? selected = presentation.SelectedEnabled;
		_speechToggleText.text = (selected.HasValue ? (selected == true ? presentation.EnableLabel : presentation.DisableLabel) : state.Phase switch
		{
			TtsServicePhase.Starting => "启动中…",
			TtsServicePhase.Stopping => "关闭中…",
			_ => (settings.ServiceEnabled ? "开启" : "关闭")
		});
		if (_speechToggleButton != null)
		{
			_speechToggleButton.interactable = loaded && (presentation.EnableInteractable || presentation.DisableInteractable);
		}
		if (_speechLanguageText != null)
		{
			_speechLanguageText.text = LabelOfLanguage(settings.Language);
		}
		if (_speechLanguageButton != null)
		{
			_speechLanguageButton.interactable = loaded;
		}
		if (_speechReferenceText != null)
		{
			_speechReferenceText.text = LabelOfReference(SpeechRadioSelection.SelectedReference(settings));
		}
		if (_speechReferenceButton != null)
		{
			_speechReferenceButton.interactable = loaded;
		}
		if (_speechStatusText != null)
		{
			string status = _owner.ModSpeechStatus;
			if (string.IsNullOrWhiteSpace(status))
			{
				status = (loaded ? "就绪" : "TTS 配置载入中…");
			}
			_speechStatusText.text = "状态：" + SingleLine(status);
		}
	}

	private void RefreshHotkey()
	{
		if (_hotkeyText == null)
		{
			return;
		}
		string text = _owner.ModHotkeyText;
		if (!string.Equals(_hotkeyText.text, text, StringComparison.Ordinal))
		{
			_hotkeyText.text = text;
		}
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

	private void RefreshIncrementEcho()
	{
		if (_incrementEcho == null)
		{
			return;
		}
		double lower = InterestModel.SolveIncrement(_settings.TargetIntervalSeconds, _settings.MaxPerUserMessage, _settings.CheckPeriodSeconds);
		string minutes = (_settings.TargetIntervalSeconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture);
		_incrementEcho.text = InterestModel.DescribeIncrement(lower) + " · 每 " + _settings.CheckPeriodSeconds.ToString(CultureInfo.InvariantCulture) + " 秒涨一次 · 约 " + minutes + " 分钟掷中一次";
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
		builder.Append("　｜　窥屏：").Append(_settings.Sources.Screen ? "已开启" : "已关闭");
		builder.Append(" · 权重 ").Append(_settings.Weights.Screen.ToString(CultureInfo.InvariantCulture));
		if (string.IsNullOrWhiteSpace(_settings.VisionProfileId))
		{
			builder.Append(_settings.Sources.Screen ? " · 视觉模型未配置：抽中「屏幕」会降级成普通话题" : " · 视觉模型未配置");
		}
		else
		{
			builder.Append(" · 视觉模型 ").Append(_settings.VisionProfileId.Trim());
		}
		if (_settings.Sources.Screen && !_settings.Enabled)
		{
			// T6：主动对话没开时，窥屏开关其实是空的，必须说出来。
			builder.Append("（当前不会触发：主动对话未开启）");
		}
		builder.Append('\n').Append("配置：").Append(_settingsPath).Append("（").Append(Stamp()).Append("）");
		if (!string.IsNullOrEmpty(_editStatus))
		{
			builder.Append("　｜　").Append(_editStatus);
		}
		_statusText.text = builder.ToString();
		_statusText.color = (string.IsNullOrEmpty(_editStatus) ? Color.white : _editStatusColor);
		RefreshAgent();
	}

	// ---------------- Agent ----------------

	private AgentUiState RefreshAgent()
	{
		if (_agentStatusText == null)
		{
			return null;
		}
		AgentUiState state = null;
		try
		{
			state = _owner.GetModAgentState();
		}
		catch (Exception error)
		{
			Log("state=agent_status_failed; error=" + error.GetType().Name);
		}
		_agentState = state;
		if (state == null)
		{
			_agentStatusText.text = "Agent 未就绪（可能还在启动）";
			// 状态拿不到时仍然允许开开关 / 打开设置，否则用户没法自救。
			SetAgentInteractable(toggle: true, persona: true, model: false, reasoning: false, settings: true, cancel: false);
			return null;
		}
		string detail = state.Detail ?? "";
		int firstBreak = detail.IndexOf('\n');
		if (firstBreak >= 0)
		{
			detail = detail.Substring(0, firstBreak);
		}
		_agentStatusText.text = "状态：" + state.Label + (detail.Length == 0 ? "" : ("\n" + SingleLine(detail)));
		if (_agentToggleText != null)
		{
			_agentToggleText.text = ((!state.Enabled) ? "开启 Agent" : (state.Busy ? "Agent 响应中…" : "关闭 Agent"));
		}
		if (_agentModelText != null)
		{
			_agentModelText.text = "模型：" + AgentLabel(state.Model);
		}
		if (_agentReasoningText != null)
		{
			_agentReasoningText.text = "推理：" + AgentLabel(state.ReasoningEffort);
		}
		bool idle = !_owner.ModBusy;
		SetAgentInteractable(toggle: (idle || state.Busy), persona: idle, model: (idle && state.ModelOptions != null && state.ModelOptions.Count > 0), reasoning: (idle && state.ReasoningOptions != null && state.ReasoningOptions.Count > 0), settings: idle, cancel: state.Busy);
		return state;
	}

	private void SetAgentInteractable(bool toggle, bool persona, bool model, bool reasoning, bool settings, bool cancel)
	{
		if (_agentToggleButton != null)
		{
			_agentToggleButton.interactable = toggle;
		}
		if (_agentPersonaButton != null)
		{
			_agentPersonaButton.interactable = persona;
		}
		if (_agentModelButton != null)
		{
			_agentModelButton.interactable = model;
		}
		if (_agentReasoningButton != null)
		{
			_agentReasoningButton.interactable = reasoning;
		}
		if (_agentSettingsButton != null)
		{
			_agentSettingsButton.interactable = settings;
		}
		if (_agentCancelButton != null)
		{
			_agentCancelButton.interactable = cancel;
		}
	}

	private static string AgentLabel(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? "-" : value;
	}

	private void ToggleAgent()
	{
		try
		{
			_owner.ModAgentToggle();
			ReportEdit("已切换 Agent 开关", isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("切换 Agent 失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenAgentPersona()
	{
		try
		{
			_owner.ModAgentPersona();
		}
		catch (Exception error)
		{
			ReportEdit("打开 Agent 人格失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenAgentSettings()
	{
		try
		{
			_owner.ModAgentSettings();
		}
		catch (Exception error)
		{
			ReportEdit("打开 Agent 设置失败：" + error.GetType().Name, isError: true);
		}
	}

	private void CancelAgentChat()
	{
		try
		{
			_owner.ModAgentCancelChat();
			ReportEdit("已请求取消当前对话", isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("取消对话失败：" + error.GetType().Name, isError: true);
		}
	}

	private void CycleAgentModel()
	{
		try
		{
			AgentUiState state = _agentState ?? _owner.GetModAgentState();
			IReadOnlyList<string> options = state?.ModelOptions;
			if (options == null || options.Count == 0)
			{
				ReportEdit("Agent 还没有可选模型", isError: true);
				return;
			}
			int index = -1;
			for (int i = 0; i < options.Count; i++)
			{
				if (string.Equals(options[i], state.Model, StringComparison.Ordinal))
				{
					index = i;
					break;
				}
			}
			string next = options[(index + 1) % options.Count];
			_owner.ModAgentSelectModel(next);
			ReportEdit("Agent 模型：" + next, isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("切换 Agent 模型失败：" + error.GetType().Name, isError: true);
		}
	}

	private void CycleAgentReasoning()
	{
		try
		{
			AgentUiState state = _agentState ?? _owner.GetModAgentState();
			IReadOnlyList<string> options = state?.ReasoningOptions;
			if (options == null || options.Count == 0)
			{
				ReportEdit("Agent 当前模型没有可选的推理强度", isError: true);
				return;
			}
			int index = -1;
			for (int i = 0; i < options.Count; i++)
			{
				if (string.Equals(options[i], state.ReasoningEffort, StringComparison.Ordinal))
				{
					index = i;
					break;
				}
			}
			string next = options[(index + 1) % options.Count];
			_owner.ModAgentSelectReasoning(next);
			ReportEdit("Agent 推理：" + next, isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("切换 Agent 推理失败：" + error.GetType().Name, isError: true);
		}
	}

	private void RefreshFooter()
	{
		if (_footerText != null)
		{
			_footerText.text = "Just_Lilith " + Plugin.Version + " · MOD 设置（阶段 B） · 旧注入：" + (_owner.LegacyNativeInjection ? "开" : "关");
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

	private static string LabelOfLanguage(SpeechLanguage language)
	{
		foreach (SpeechLanguageChoice choice in SpeechRadioSelection.LanguageChoices)
		{
			if (choice.Language == language)
			{
				return choice.Label;
			}
		}
		return language.ToString();
	}

	private static string LabelOfReference(string style)
	{
		string canonical = SpeechReferenceStyles.Canonicalize(style);
		foreach (SpeechReferenceChoice choice in SpeechRadioSelection.ReferenceChoices)
		{
			if (string.Equals(choice.Style, canonical, StringComparison.Ordinal))
			{
				return choice.Label;
			}
		}
		return string.IsNullOrEmpty(canonical) ? "自动" : canonical;
	}

	private void SetTextIfIdle(InputField field, string value)
	{
		if (field != null && !field.isFocused)
		{
			field.text = value;
		}
	}

	// ---------------- 交互 ----------------

	private void ToggleSpeechService()
	{
		try
		{
			SpeechSettings settings = _owner.GetModSpeechSettings();
			bool target = !settings.ServiceEnabled;
			if (!SpeechRadioSelection.CanRequestServiceEnabled(_owner.GetModSpeechServiceState().Phase, target, _owner.ModSpeechLoaded))
			{
				ReportEdit("TTS 服务正在切换中，稍等一下", isError: true);
				return;
			}
			_owner.ModSelectSpeechService(target);
			ReportEdit(target ? "正在开启 TTS 服务…" : "正在关闭 TTS 服务…", isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("切换 TTS 服务失败：" + error.GetType().Name, isError: true);
		}
	}

	private void CycleSpeechLanguage()
	{
		try
		{
			IReadOnlyList<SpeechLanguageChoice> choices = SpeechRadioSelection.LanguageChoices;
			SpeechLanguage current = _owner.GetModSpeechSettings().Language;
			int index = -1;
			for (int i = 0; i < choices.Count; i++)
			{
				if (choices[i].Language == current)
				{
					index = i;
					break;
				}
			}
			SpeechLanguage next = choices[(index + 1) % choices.Count].Language;
			_owner.ModSelectSpeechLanguage(next);
			ReportEdit("朗读语言：" + LabelOfLanguage(next), isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("切换朗读语言失败：" + error.GetType().Name, isError: true);
		}
	}

	private void CycleSpeechReference()
	{
		try
		{
			IReadOnlyList<SpeechReferenceChoice> choices = SpeechRadioSelection.ReferenceChoices;
			string current = SpeechRadioSelection.SelectedReference(_owner.GetModSpeechSettings());
			int index = -1;
			for (int i = 0; i < choices.Count; i++)
			{
				if (string.Equals(choices[i].Style, current, StringComparison.Ordinal))
				{
					index = i;
					break;
				}
			}
			string next = choices[(index + 1) % choices.Count].Style;
			_owner.ModSelectSpeechReference(next);
			ReportEdit("参考风格：" + LabelOfReference(next), isError: false);
		}
		catch (Exception error)
		{
			ReportEdit("切换参考风格失败：" + error.GetType().Name, isError: true);
		}
	}

	private void ToggleHotkeyCapture()
	{
		try
		{
			_owner.ModToggleHotkeyCapture();
		}
		catch (Exception error)
		{
			ReportEdit("热键录制失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenPersonality()
	{
		try
		{
			_owner.ModOpenSystemPrompt();
		}
		catch (Exception error)
		{
			ReportEdit("打开人格 Prompt 失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenJourneys()
	{
		try
		{
			_owner.ModOpenJourneys();
		}
		catch (Exception error)
		{
			ReportEdit("打开幻境失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenDreamMemory()
	{
		try
		{
			_owner.ModOpenDreamMemory();
		}
		catch (Exception error)
		{
			ReportEdit("打开幻境记忆失败：" + error.GetType().Name, isError: true);
		}
	}

	private void OpenWorldBook()
	{
		try
		{
			_owner.ModOpenWorldBook();
		}
		catch (Exception error)
		{
			ReportEdit("打开世界书失败：" + error.GetType().Name, isError: true);
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

	/// <summary>「现在试一次窥屏」：绕过权重与兴趣，但不绕过视觉配置和硬上限。</summary>
	private void TestPeekNow()
	{
		try
		{
			string result = _owner.ModTestPeek();
			bool failed = !string.IsNullOrEmpty(result);
			ReportEdit(failed ? result : "已发起一次窥屏（识图几秒钟，之后她会就着画面说一句）", isError: failed);
		}
		catch (Exception error)
		{
			ReportEdit("试窥屏失败：" + error.GetType().Name, isError: true);
		}
	}

	private void TogglePeek()	{
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
			_pageRect.anchoredPosition = _dragStartPosition + localPoint - _dragStartPointer;
			// 夹紧用桌面可视区，不是画布矩形：窗口比屏幕高时画布边界在屏幕外。
			WindowWorkArea.Clamp(_pageRect, canvasRect, PlacementMargin);
		}
	}

	/// <summary>
	/// 把页面放到桌面可视区里：fresh=true 时居中，否则只做夹紧（拖动/窗口变化后用）。
	/// </summary>
	private void PlaceOnVisibleArea(bool fresh)
	{
		if (_pageRect == null || _canvasRoot == null)
		{
			return;
		}
		RectTransform canvasRect = _canvasRoot.GetComponent<RectTransform>();
		if (canvasRect == null)
		{
			return;
		}
		try
		{
			if (fresh)
			{
				WindowWorkArea.CenterInVisible(_pageRect, canvasRect, PlacementMargin);
			}
			else if (WindowWorkArea.Clamp(_pageRect, canvasRect, PlacementMargin) && !_placementLogged)
			{
				_placementLogged = true;
				Log("event=mod_window_clamped; pos=" + DescribePlacement());
			}
		}
		catch (Exception error)
		{
			Log("state=placement_failed; error=" + error.GetType().Name);
		}
	}

	private string DescribePlacement()
	{
		if (_pageRect == null)
		{
			return "-";
		}
		RectTransform canvasRect = _canvasRoot?.GetComponent<RectTransform>();
		Vector2 size = WindowWorkArea.SizeInScreen(_pageRect, canvasRect);
		Rect visible = WindowWorkArea.VisibleRect();
		return "anchor=(" + _pageRect.anchoredPosition.x.ToString("0") + "," + _pageRect.anchoredPosition.y.ToString("0") + "); size_screen=(" + size.x.ToString("0") + "x" + size.y.ToString("0") + "); visible=(" + visible.xMin.ToString("0") + "," + visible.yMin.ToString("0") + " " + visible.width.ToString("0") + "x" + visible.height.ToString("0") + "); canvas=(" + Screen.width + "x" + Screen.height + ")";
	}

	// ---------------- 控件工厂 ----------------

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
		_owner.TrackModSettingsInput(inputField);
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
