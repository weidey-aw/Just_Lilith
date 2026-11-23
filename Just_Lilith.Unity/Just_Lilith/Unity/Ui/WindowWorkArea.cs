using System;
using System.Reflection;
using UnityEngine;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 把自建面板夹进「桌面上真正看得见的那块区域」。
///
/// 为什么要单独算：游戏的窗口是比屏幕还高的桌宠分层窗口（日志里 rect=(480,-120 960x1200)），
/// 画布 = 窗口客户区，所以按画布中心居中时页面顶部会跑到屏幕外（「关闭」按钮点不到）。
/// 这里用游戏自己的 <c>LilithWindow.CharacterWorkArea</c>（桌面像素，已排除任务栏）
/// 经 <c>LilithWindow.DesktopToUnityScreen</c> 换算成「窗口屏幕坐标」，再与画布矩形求交，
/// 得到一块一定在桌面上的矩形，所有定位 / 拖动夹紧都以它为准。
///
/// 坐标口径说明：
/// - 外部拿到的可视矩形是**屏幕像素**；
/// - 但所有比较都统一换到 `canvasRect` 的**局部坐标**里做（用
///   <c>RectTransformUtility.ScreenPointToLocalPointInRectangle</c>），
///   这样不用手工除 scaleFactor，也不用碰 <c>GetWorldCorners</c>
///   （那个接口在 IL2CPP 下要传 <c>Il2CppStructArray</c>，传普通数组会静默拿到 0）。
/// - 面板是画布的直接子节点、pivot 与 anchor 都在中心，所以它的中心就是 `anchoredPosition`。
/// </summary>
internal static class WindowWorkArea
{
	private const string WindowTypeName = "LilithWindow, Assembly-CSharp";

	private const float DegenerateThreshold = 64f;

	private static Type? _windowType;

	private static PropertyInfo? _workAreaProperty;

	private static MethodInfo? _desktopToUnityScreen;

	private static bool _resolved;

	private static bool _failed;

	/// <summary>桌面可视区域在屏幕坐标里的矩形；取不到就退回整块画布。Fail-safe，不抛异常。</summary>
	internal static Rect VisibleRect()
	{
		if (TryGetWorkAreaRect(out Rect workArea))
		{
			return workArea;
		}
		return new Rect(0f, 0f, Mathf.Max(1f, Screen.width), Mathf.Max(1f, Screen.height));
	}

	/// <summary>只做换算，取不到返回 false（调用方自己决定退路）。</summary>
	internal static bool TryGetWorkAreaRect(out Rect rect)
	{
		rect = default(Rect);
		try
		{
			if (!EnsureResolved())
			{
				return false;
			}
			if (!(_workAreaProperty!.GetValue(null) is RectInt boxed))
			{
				return false;
			}
			// 左上 / 右下两个角换过来就是矩形；游戏窗口可能整个盖住屏幕，所以再和画布求交。
			if (!TryToScreen(new Vector2(boxed.x, boxed.y), out Vector2 topLeft) || !TryToScreen(new Vector2(boxed.x + boxed.width, boxed.y + boxed.height), out Vector2 bottomRight))
			{
				return false;
			}
			Rect mapped = Rect.MinMaxRect(Mathf.Min(topLeft.x, bottomRight.x), Mathf.Min(topLeft.y, bottomRight.y), Mathf.Max(topLeft.x, bottomRight.x), Mathf.Max(topLeft.y, bottomRight.y));
			Rect canvasRect = new Rect(0f, 0f, Mathf.Max(1f, Screen.width), Mathf.Max(1f, Screen.height));
			float xMin = Mathf.Max(mapped.xMin, canvasRect.xMin);
			float yMin = Mathf.Max(mapped.yMin, canvasRect.yMin);
			float xMax = Mathf.Min(mapped.xMax, canvasRect.xMax);
			float yMax = Mathf.Min(mapped.yMax, canvasRect.yMax);
			if (xMax - xMin < DegenerateThreshold || yMax - yMin < DegenerateThreshold)
			{
				return false;
			}
			rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把面板夹进可视区。返回是否真的动过。</summary>
	internal static bool Clamp(RectTransform target, RectTransform canvasRect, float margin)
	{
		if (target == null || canvasRect == null)
		{
			return false;
		}
		if (!TryGetVisibleLocalRect(canvasRect, out Rect visible))
		{
			return false;
		}
		Vector2 size = target.rect.size;
		Vector2 center = target.anchoredPosition;
		Vector2 clamped = ClampCenter(center, size, visible, margin);
		if ((clamped - center).sqrMagnitude < 0.25f)
		{
			return false;
		}
		target.anchoredPosition = clamped;
		return true;
	}

	/// <summary>把面板放到可视区正中（面板比可视区大时改为顶部对齐，保证标题和「关闭」看得见）。</summary>
	internal static void CenterInVisible(RectTransform target, RectTransform canvasRect, float margin)
	{
		if (target == null || canvasRect == null)
		{
			return;
		}
		if (!TryGetVisibleLocalRect(canvasRect, out Rect visible))
		{
			return;
		}
		Vector2 size = target.rect.size;
		target.anchoredPosition = ClampCenter(new Vector2(visible.center.x, visible.center.y), size, visible, margin);
	}

	/// <summary>面板在屏幕像素里的尺寸（只用于日志）。</summary>
	internal static Vector2 SizeInScreen(RectTransform target, RectTransform canvasRect)
	{
		if (target == null || canvasRect == null)
		{
			return Vector2.zero;
		}
		if (!TryScreenToLocal(canvasRect, new Vector2(0f, 0f), out Vector2 a) || !TryScreenToLocal(canvasRect, new Vector2(1f, 1f), out Vector2 b))
		{
			return Vector2.zero;
		}
		// 1 屏幕像素对应多少局部单位，倒数就是缩放。
		float unitsPerPixelX = Mathf.Abs(b.x - a.x);
		float unitsPerPixelY = Mathf.Abs(b.y - a.y);
		if (unitsPerPixelX <= 0.0001f || unitsPerPixelY <= 0.0001f)
		{
			return Vector2.zero;
		}
		Vector2 size = target.rect.size;
		return new Vector2(size.x / unitsPerPixelX, size.y / unitsPerPixelY);
	}

	private static Vector2 ClampCenter(Vector2 center, Vector2 size, Rect visible, float margin)
	{
		float halfWidth = size.x * 0.5f;
		float halfHeight = size.y * 0.5f;
		float minX = visible.xMin + margin + halfWidth;
		float maxX = visible.xMax - margin - halfWidth;
		float minY = visible.yMin + margin + halfHeight;
		float maxY = visible.yMax - margin - halfHeight;
		float x = ((minX <= maxX) ? Mathf.Clamp(center.x, minX, maxX) : visible.center.x);
		// 面板比可视区还高：贴顶端，别把标题条和「关闭」推到屏幕外。
		float y = ((minY <= maxY) ? Mathf.Clamp(center.y, minY, maxY) : (visible.yMax - margin - halfHeight));
		return new Vector2(x, y);
	}

	/// <summary>可视区（屏幕像素）换到画布局部坐标。</summary>
	private static bool TryGetVisibleLocalRect(RectTransform canvasRect, out Rect local)
	{
		local = default(Rect);
		Rect visible = VisibleRect();
		if (!TryScreenToLocal(canvasRect, new Vector2(visible.xMin, visible.yMin), out Vector2 min) || !TryScreenToLocal(canvasRect, new Vector2(visible.xMax, visible.yMax), out Vector2 max))
		{
			return false;
		}
		local = Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
		return local.width > 1f && local.height > 1f;
	}

	private static bool TryScreenToLocal(RectTransform canvasRect, Vector2 screenPoint, out Vector2 local)
	{
		local = default(Vector2);
		try
		{
			return RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPoint, null, out local);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryToScreen(Vector2 desktopPoint, out Vector2 screenPoint)
	{
		screenPoint = default(Vector2);
		try
		{
			if (_desktopToUnityScreen == null)
			{
				return false;
			}
			object? result = _desktopToUnityScreen.Invoke(null, new object[2] { desktopPoint.x, desktopPoint.y });
			if (result is Vector2 vector)
			{
				screenPoint = vector;
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool EnsureResolved()
	{
		if (_resolved)
		{
			return !_failed;
		}
		_resolved = true;
		try
		{
			_windowType = Type.GetType(WindowTypeName, throwOnError: false);
			if (_windowType == null)
			{
				_failed = true;
				return false;
			}
			_workAreaProperty = _windowType.GetProperty("CharacterWorkArea", BindingFlags.Static | BindingFlags.Public);
			_desktopToUnityScreen = _windowType.GetMethod("DesktopToUnityScreen", BindingFlags.Static | BindingFlags.Public, null, new Type[2] { typeof(float), typeof(float) }, null);
			if (_workAreaProperty == null || _desktopToUnityScreen == null)
			{
				_failed = true;
				return false;
			}
			return true;
		}
		catch
		{
			_failed = true;
			return false;
		}
	}
}
