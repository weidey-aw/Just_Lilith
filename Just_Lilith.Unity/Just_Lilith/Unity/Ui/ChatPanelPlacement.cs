using System;
using System.Reflection;
using BepInEx.Logging;
using Just_Lilith.Unity.Game;
using UnityEngine;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 聊天输入条的屏幕位置计算。
///
/// 游戏的窗口是一个比屏幕还高的桌宠分层窗口（日志里 rect=(480,-120 960x1200)），
/// 底边贴着屏幕底、顶边在屏幕外，所以把输入条固定在画布底部会被任务栏和角色挡掉。
/// 这里改为贴着角色头顶放，并处理角色缩放与走动：
///
/// 角色 Spine 渲染器世界包围盒
///   → LilithWindow.WorldBoundsToDesktopRect  得到桌面像素矩形（左上原点）
///   → LilithWindow.DesktopToUnityScreen       得到窗口内屏幕坐标（左下原点）
///   → RectTransformUtility                    得到画布局部坐标
///
/// 角色缩放时包围盒会变，输入条跟着移动；角色走动时用平滑跟随避免跳动，
/// 输入框获得焦点（正在打字）时冻结位置。取不到角色时退回固定位置，
/// 两种情况都会按桌面工作区（任务栏之外）夹紧，保证不会被任务栏或屏幕边缘吃掉。
/// </summary>
internal sealed class ChatPanelPlacement
{
	private const string CharacterTypeName = "CharacterController, Assembly-CSharp";

	private const string WindowTypeName = "LilithWindow, Assembly-CSharp";

	private const float VerticalGapPixels = 10f;

	private const float EdgeMarginPixels = 18f;

	private const float RecomputeIntervalSeconds = 0.2f;

	private const float FollowSharpness = 12f;

	private readonly RectTransform _panel;

	private readonly RectTransform _canvasRect;

	private readonly Canvas _canvas;

	private readonly ManualLogSource _log;

	private Type? _windowType;

	private PropertyInfo? _windowReadyProperty;

	private PropertyInfo? _windowWorkAreaProperty;

	private MethodInfo? _worldBoundsToDesktopRect;

	private MethodInfo? _desktopToUnityScreen;

	private Type? _characterType;

	private FieldInfo? _activeCharacterField;

	private FieldInfo? _cachedSpineRendererField;

	private PropertyInfo? _skeletonAnimationProperty;

	private object? _lastSource;

	private float _nextRecomputeAt;

	private Vector2 _targetAnchoredPosition;

	private bool _hasTarget;

	private string _lastPlacementSource = "";

	private bool _probeLogged;

	private bool _missingLogged;

	public ChatPanelPlacement(RectTransform panel, RectTransform canvasRect, Canvas canvas, ManualLogSource log)
	{
		_panel = panel ?? throw new ArgumentNullException("panel");
		_canvasRect = canvasRect ?? throw new ArgumentNullException("canvasRect");
		_canvas = canvas ?? throw new ArgumentNullException("canvas");
		_log = log ?? throw new ArgumentNullException("log");
		ResolveReflection();
	}

	/// <summary>正在打字时冻结位置，避免输入框在角色走动时滑走。</summary>
	public bool HoldSteady { get; set; }

	/// <summary>面板显示期间调用：按固定间隔重算目标位置，其余帧平滑跟随。</summary>
	public void Tick()
	{
		if (!IsPanelVisible())
		{
			return;
		}
		float now = Time.unscaledTime;
		if (now >= _nextRecomputeAt)
		{
			_nextRecomputeAt = now + RecomputeIntervalSeconds;
			if (!HoldSteady)
			{
				Probe(forceLog: false);
			}
		}
		FollowTarget();
	}

	/// <summary>打开聊天框时调用一次：立刻定位、不做平滑，并记录一次诊断日志。</summary>
	public void ApplyNow()
	{
		if (_panel == null)
		{
			return;
		}
		_nextRecomputeAt = Time.unscaledTime + RecomputeIntervalSeconds;
		if (!Probe(forceLog: true))
		{
			return;
		}
		_panel.anchoredPosition = _targetAnchoredPosition;
	}

	private bool IsPanelVisible()
	{
		if (_panel == null || !_panel.gameObject.activeInHierarchy)
		{
			return false;
		}
		return true;
	}

	private void FollowTarget()
	{
		if (_hasTarget)
		{
			float num = 1f - Mathf.Exp((0f - FollowSharpness) * Mathf.Max(0f, Time.unscaledDeltaTime));
			_panel.anchoredPosition = Vector2.Lerp(_panel.anchoredPosition, _targetAnchoredPosition, num);
		}
	}

	/// <summary>重算目标画布坐标（含可见区间夹紧），返回是否算出了结果。</summary>
	private bool Probe(bool forceLog)
	{
		if (_panel == null)
		{
			return false;
		}
		if (!TryResolveScreenAnchor(out Vector2 anchor, out string source))
		{
			return false;
		}
		if (!TryClampToVisibleArea(ref anchor))
		{
			return false;
		}
		Vector2 canvasPoint;
		try
		{
			if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, anchor, null, out canvasPoint))
			{
				return false;
			}
		}
		catch
		{
			return false;
		}
		Vector2 canvasSize = _canvasRect.rect.size;
		RectTransform panelRect = _panel;
		panelRect.anchorMin = Vector2.zero;
		panelRect.anchorMax = Vector2.zero;
		panelRect.pivot = new Vector2(0.5f, 0f);
		_targetAnchoredPosition = new Vector2(canvasPoint.x + canvasSize.x * 0.5f, canvasPoint.y + canvasSize.y * 0.5f);
		_hasTarget = true;
		if (forceLog || !string.Equals(_lastPlacementSource, source, StringComparison.Ordinal))
		{
			_lastPlacementSource = source;
			SafeLogInfo($"module=UI; event=chat_placement; source={source}; anchor=({anchor.x:0},{anchor.y:0}); canvas=({canvasSize.x:0}x{canvasSize.y:0}); screen=({Screen.width}x{Screen.height})");
		}
		return true;
	}

	/// <summary>计算输入条底边中点的屏幕坐标。</summary>
	private bool TryResolveScreenAnchor(out Vector2 anchor, out string source)
	{
		anchor = default(Vector2);
		source = "fallback";
		if (TryGetCharacterHeadScreen(out float headScreenY, out float centerScreenX))
		{
			anchor = new Vector2(centerScreenX, headScreenY + VerticalGapPixels);
			source = "character_head";
			return true;
		}
		if (TryGetVisibleBand(out float bandBottom, out float bandTop))
		{
			anchor = new Vector2((float)Screen.width * 0.5f, Mathf.Lerp(bandBottom, bandTop, 0.72f));
			source = "fallback_band";
			return true;
		}
		float num = (float)Screen.height * 0.7f;
		if (num <= 1f)
		{
			return false;
		}
		anchor = new Vector2((float)Screen.width * 0.5f, num);
		source = "fallback_screen";
		return true;
	}

	/// <summary>用角色 Spine 渲染器的世界包围盒，换算出头顶在窗口屏幕坐标里的位置。</summary>
	private bool TryGetCharacterHeadScreen(out float headScreenY, out float centerScreenX)
	{
		headScreenY = 0f;
		centerScreenX = 0f;
		if ((object)_worldBoundsToDesktopRect == null || (object)_desktopToUnityScreen == null)
		{
			return false;
		}
		if (!TryGetCharacterBounds(out Bounds bounds))
		{
			return false;
		}
		object? desktopRectBoxed;
		try
		{
			desktopRectBoxed = _worldBoundsToDesktopRect.Invoke(null, new object[1] { bounds });
		}
		catch
		{
			return false;
		}
		if (!(desktopRectBoxed is RectInt rectInt))
		{
			return false;
		}
		// RectInt 的 y 在桌面坐标系里是上边缘（Windows 左上原点）。
		float desktopCenterX = (float)rectInt.x + (float)rectInt.width * 0.5f;
		float desktopTopY = rectInt.y;
		try
		{
			object? screenPoint = _desktopToUnityScreen.Invoke(null, new object[2] { desktopCenterX, desktopTopY });
			if (!(screenPoint is Vector2 vector))
			{
				return false;
			}
			headScreenY = vector.y;
			centerScreenX = vector.x;
		}
		catch
		{
			return false;
		}
		if (!_probeLogged)
		{
			_probeLogged = true;
			SafeLogInfo($"module=UI; event=chat_placement_probe; bounds={bounds}; desktop=({rectInt.x},{rectInt.y},{rectInt.width}x{rectInt.height}); screen=({centerScreenX:0},{headScreenY:0})");
		}
		return true;
	}

	private bool TryGetCharacterBounds(out Bounds bounds)
	{
		bounds = default(Bounds);
		object? instance = null;
		try
		{
			instance = _activeCharacterField?.GetValue(null);
		}
		catch
		{
		}
		if (instance == null)
		{
			try
			{
				if (!UnityRuntimeObjectLocator.TryFind(CharacterTypeName, out LocatedUnityObject located))
				{
					MissingCharacter();
					return false;
				}
				instance = located.Instance;
			}
			catch
			{
				MissingCharacter();
				return false;
			}
		}
		if (instance == null)
		{
			MissingCharacter();
			return false;
		}
		_lastSource = instance;
		Renderer? renderer = null;
		try
		{
			renderer = _cachedSpineRendererField?.GetValue(instance) as Renderer;
		}
		catch
		{
		}
		if (renderer == null)
		{
			try
			{
				object? skeleton = _skeletonAnimationProperty?.GetValue(instance);
				if (skeleton is Component component)
				{
					renderer = component.GetComponentInChildren<Renderer>();
				}
			}
			catch
			{
			}
		}
		if (renderer == null)
		{
			return false;
		}
		try
		{
			bounds = renderer.bounds;
		}
		catch
		{
			return false;
		}
		if (bounds.size.x <= 0.0001f && bounds.size.y <= 0.0001f)
		{
			return false;
		}
		return true;
	}

	private void MissingCharacter()
	{
		if (!_missingLogged)
		{
			_missingLogged = true;
			SafeLogInfo("module=UI; event=chat_placement_character_missing; fallback=band");
		}
	}

	/// <summary>桌面工作区换到窗口屏幕坐标，作为“不会被任务栏挡住”的可见区间。</summary>
	private bool TryGetVisibleBand(out float bandBottom, out float bandTop)
	{
		bandBottom = 0f;
		bandTop = 0f;
		try
		{
			if ((object)_desktopToUnityScreen == null || (object)_windowWorkAreaProperty == null)
			{
				return false;
			}
			object? boxed = _windowWorkAreaProperty.GetValue(null);
			if (!(boxed is RectInt rectInt))
			{
				return false;
			}
			object? top = _desktopToUnityScreen.Invoke(null, new object[2] { (float)rectInt.x + 1f, rectInt.y });
			object? bottom = _desktopToUnityScreen.Invoke(null, new object[2] { (float)rectInt.x + 1f, rectInt.y + rectInt.height });
			if (!(top is Vector2 vector) || !(bottom is Vector2 vector2))
			{
				return false;
			}
			bandTop = Mathf.Max(vector.y, vector2.y);
			bandBottom = Mathf.Min(vector.y, vector2.y);
			return bandTop - bandBottom > 32f;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把输入条夹在可见区间内（考虑面板自身尺寸和画布缩放）。</summary>
	private bool TryClampToVisibleArea(ref Vector2 anchor)
	{
		float scale;
		try
		{
			scale = Mathf.Max(0.0001f, _canvas.scaleFactor);
		}
		catch
		{
			scale = 1f;
		}
		float halfWidth = _panel.rect.width * scale * 0.5f;
		float panelHeight = _panel.rect.height * scale;
		float screenWidth = Screen.width;
		float screenHeight = Screen.height;
		if (screenWidth <= 1f || screenHeight <= 1f)
		{
			return false;
		}
		float min = EdgeMarginPixels + halfWidth;
		float max = screenWidth - EdgeMarginPixels - halfWidth;
		anchor.x = (min <= max) ? Mathf.Clamp(anchor.x, min, max) : screenWidth * 0.5f;
		float min2 = EdgeMarginPixels;
		float max2 = screenHeight - EdgeMarginPixels - panelHeight;
		if (TryGetVisibleBand(out float bandBottom, out float bandTop))
		{
			min2 = Mathf.Max(min2, bandBottom + EdgeMarginPixels);
			max2 = Mathf.Min(max2, bandTop - EdgeMarginPixels - panelHeight);
		}
		anchor.y = (min2 <= max2) ? Mathf.Clamp(anchor.y, min2, max2) : screenHeight * 0.5f;
		return true;
	}

	private void ResolveReflection()
	{
		try
		{
			_windowType = Type.GetType(WindowTypeName, throwOnError: false);
			if ((object)_windowType != null)
			{
				_windowReadyProperty = _windowType.GetProperty("IsReady", BindingFlags.Static | BindingFlags.Public);
				_windowWorkAreaProperty = _windowType.GetProperty("CharacterWorkArea", BindingFlags.Static | BindingFlags.Public);
				_worldBoundsToDesktopRect = _windowType.GetMethod("WorldBoundsToDesktopRect", BindingFlags.Static | BindingFlags.Public);
				_desktopToUnityScreen = _windowType.GetMethod("DesktopToUnityScreen", BindingFlags.Static | BindingFlags.Public);
			}
		}
		catch
		{
			_windowType = null;
		}
		try
		{
			_characterType = Type.GetType(CharacterTypeName, throwOnError: false);
			if ((object)_characterType != null)
			{
				_activeCharacterField = _characterType.GetField("s_activeInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				_cachedSpineRendererField = _characterType.GetField("_cachedSpineRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				_skeletonAnimationProperty = _characterType.GetProperty("SkeletonAnimationInternal", BindingFlags.Instance | BindingFlags.Public);
			}
		}
		catch
		{
			_characterType = null;
		}
	}

	private void SafeLogInfo(string message)
	{
		try
		{
			_log.LogInfo(message);
		}
		catch
		{
		}
	}
}
