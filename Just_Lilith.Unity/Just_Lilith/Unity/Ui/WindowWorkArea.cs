using System;
using UnityEngine;

namespace Just_Lilith.Unity.Ui;

/// <summary>
/// 把自建面板定位成「贴在桌面上的独立弹窗」，并提供游戏自带的「临时全屏」开关。
///
/// 桌宠窗口本身比屏幕小、还会跟着角色挪（日志里 rect=(480,-120 960x1200)），
/// 直接按画布坐标居中就会跑出屏幕。这里统一用桌面坐标：
/// 1. 用 <c>LilithWindow.Rect</c>（窗口桌面矩形）∩ <c>LilithWindow.MonitorWorkAreaAt</c>
///    （窗口所在显示器的工作区，已排除任务栏）得到「桌面上真正看得见的区域」——跟角色在哪无关；
/// 2. 页面记住一个**桌面像素**中心点，靠 <c>DesktopToUnityScreen</c> 换回画布坐标，
///    所以窗口怎么挪，页面在桌面上都不动；
/// 3. 打不开时用游戏自己的 <c>RequestFullMonitor</c> 把窗口临时扩到整个显示器
///    （引用计数、按 token 释放，关页面就还回去），这样画布就是从屏幕左上角到右下角。
///
/// 所有夹紧都在画布局部坐标里算（<c>ScreenPointToLocalPointInRectangle</c>），
/// 不碰 <c>GetWorldCorners</c>（IL2CPP 下传普通数组会静默拿到 0）。
/// </summary>
internal static class WindowWorkArea
{
	private const float DegenerateThreshold = 64f;

	/// <summary>最近一次定位失败的原因（只用于日志排查）。</summary>
	internal static string LastDiagnostic { get; private set; } = "";

	private static void Fail(string reason)
	{
		LastDiagnostic = reason;
	}

	/// <summary>桌面可视区域在屏幕坐标里的矩形；取不到就退回整块画布。Fail-safe，不抛异常。</summary>
	internal static Rect VisibleRect()
	{
		if (TryGetVisibleScreenRect(out Rect screenRect))
		{
			return screenRect;
		}
		return new Rect(0f, 0f, Mathf.Max(1f, Screen.width), Mathf.Max(1f, Screen.height));
	}

	/// <summary>
	/// 桌面上真正看得见的窗口区域：窗口矩形 ∩ 窗口所在显示器的工作区（桌面像素）。
	/// 只跟窗口位置有关，跟角色在哪无关。
	/// </summary>
	internal static bool TryGetDesktopVisible(out RectInt visible)
	{
		visible = default(RectInt);
		try
		{
			RectInt window = LilithWindow.Rect;
			if (window.width <= 0 || window.height <= 0)
			{
				Fail("window_rect_invalid");
				return false;
			}
			Vector2 windowCenter = new Vector2(window.xMin + window.width * 0.5f, window.yMin + window.height * 0.5f);
			RectInt area = LilithWindow.MonitorWorkAreaAt(windowCenter);
			if (area.width <= 0 || area.height <= 0)
			{
				Fail("work_area_invalid");
				return false;
			}
			int left = Mathf.Max(window.xMin, area.xMin);
			int bottom = Mathf.Max(window.yMin, area.yMin);
			int right = Mathf.Min(window.xMax, area.xMax);
			int top = Mathf.Min(window.yMax, area.yMax);
			if (right - left < DegenerateThreshold || top - bottom < DegenerateThreshold)
			{
				Fail("overlap_too_small; window=" + window.ToString() + "; area=" + area.ToString());
				return false;
			}
			visible = new RectInt(left, bottom, right - left, top - bottom);
			return true;
		}
		catch (Exception error)
		{
			Fail("desktop_visible_failed; " + error.GetType().Name);
			return false;
		}
	}

	/// <summary>桌面可视区中心（桌面像素）。页面打开时就以它为锚点。</summary>
	internal static bool TryGetDesktopCenter(out Vector2 center)
	{
		center = default(Vector2);
		if (!TryGetDesktopVisible(out RectInt visible))
		{
			return false;
		}
		center = new Vector2(visible.x + visible.width * 0.5f, visible.y + visible.height * 0.5f);
		return true;
	}

	/// <summary>桌面像素点 → 画布局部坐标（页面中心就是 anchoredPosition）。</summary>
	internal static bool TryDesktopToLocal(RectTransform canvasRect, Vector2 desktopPoint, out Vector2 local)
	{
		local = default(Vector2);
		if (canvasRect == null)
		{
			return false;
		}
		try
		{
			Vector2 screen = LilithWindow.DesktopToUnityScreen(desktopPoint.x, desktopPoint.y);
			return TryScreenToLocal(canvasRect, screen, out local);
		}
		catch (Exception error)
		{
			Fail("desktop_to_local_failed; " + error.GetType().Name);
			return false;
		}
	}

	/// <summary>画布局部坐标 → 桌面像素点（拖动后回写锚点用）。</summary>
	internal static bool TryLocalToDesktop(RectTransform canvasRect, Vector2 local, out Vector2 desktopPoint)
	{
		desktopPoint = default(Vector2);
		if (canvasRect == null)
		{
			return false;
		}
		try
		{
			Vector3 world = canvasRect.TransformPoint(new Vector3(local.x, local.y, 0f));
			Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, world);
			desktopPoint = LilithWindow.UnityScreenToDesktop(screen.x, screen.y);
			return true;
		}
		catch (Exception error)
		{
			Fail("local_to_desktop_failed; " + error.GetType().Name);
			return false;
		}
	}

	/// <summary>
	/// 临时把游戏窗口扩到整个显示器（游戏自带、按 token 引用计数；token 一般是我们的页面对象，
	/// 页面销毁就自动失效）。关页面时用同一个 token 调 <see cref="ReleaseFullMonitor"/> 还回去。
	/// </summary>
	internal static bool RequestFullMonitor(UnityEngine.Object token)
	{
		if (token == null)
		{
			return false;
		}
		try
		{
			LilithWindow.RequestFullMonitor(token, 0f);
			return true;
		}
		catch (Exception error)
		{
			Fail("request_full_monitor_failed; " + error.GetType().Name);
			return false;
		}
	}

	internal static void ReleaseFullMonitor(UnityEngine.Object token)
	{
		if (token == null)
		{
			return;
		}
		try
		{
			LilithWindow.ReleaseFullMonitor(token);
		}
		catch (Exception error)
		{
			Fail("release_full_monitor_failed; " + error.GetType().Name);
		}
	}

	/// <summary>当前是否已经拿着全屏（用于日志/判断要不要重新居中）。</summary>
	internal static bool FullMonitorRequested
	{
		get
		{
			try
			{
				return LilithWindow.FullMonitorRequested;
			}
			catch (Exception)
			{
				return false;
			}
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

	/// <summary>桌面可视区（桌面像素）→ 屏幕坐标矩形。</summary>
	private static bool TryGetVisibleScreenRect(out Rect screenRect)
	{
		screenRect = default(Rect);
		if (!TryGetDesktopVisible(out RectInt visible))
		{
			return false;
		}
		try
		{
			screenRect = LilithWindow.DesktopRectToUnityScreen(visible);
			return screenRect.width >= DegenerateThreshold && screenRect.height >= DegenerateThreshold;
		}
		catch (Exception error)
		{
			Fail("desktop_rect_to_screen_failed; " + error.GetType().Name);
			return false;
		}
	}

	/// <summary>桌面可视区（屏幕坐标）换到画布局部坐标。</summary>
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
}
