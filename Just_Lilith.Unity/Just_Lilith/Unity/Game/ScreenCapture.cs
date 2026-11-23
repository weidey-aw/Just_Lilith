using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Just_Lilith.Unity.Game;

/// <summary>
/// 窥屏用的一次性截屏：GDI 抓整个虚拟桌面 → 缩到 ≤1024×768 → 编码成 JPEG。
/// <para>
/// 只在<b>主线程</b>调用（Unity 的 Texture2D/ImageConversion 要求），单次 &lt;50ms。
/// 截图只存在于内存，用完即弃：不落盘、不进日志、不进记忆。
/// </para>
/// <para>
/// 故意不带 <c>CAPTUREBLT</c>：这样通常不会把游戏自己的分层窗口（气泡/聊天框）拍进去。
/// 抓不到（独占全屏 → 全黑 / DC 失败）就返回失败，由调用方按 2.5 降级。
/// </para>
/// </summary>
internal static class ScreenCapture
{
	internal readonly record struct CaptureResult(byte[]? Jpeg, int Width, int Height, string Reason, long ElapsedMs)
	{
		internal bool Ok => Jpeg != null;
	}

	private const int SRCCOPY = 13369376;

	private const int HALFTONE = 4;

	private const uint DIB_RGB_COLORS = 0u;

	private const int SM_XVIRTUALSCREEN = 76;

	private const int SM_YVIRTUALSCREEN = 77;

	private const int SM_CXVIRTUALSCREEN = 78;

	private const int SM_CYVIRTUALSCREEN = 79;

	private const string LilithWindowType = "LilithWindow, Assembly-CSharp";

	/// <summary>抓一张 JPEG。失败时 <see cref="CaptureResult.Jpeg"/> 为 null，<see cref="CaptureResult.Reason"/> 说明原因。</summary>
	internal static CaptureResult TryCapture(int maxWidth = 1024, int maxHeight = 768, int jpegQuality = 70)
	{
		long startedTicks = Environment.TickCount64;
		if (!TryGetVirtualScreen(out int screenX, out int screenY, out int screenWidth, out int screenHeight))
		{
			return Fail("screen_rect", startedTicks);
		}
		double scale = Math.Min((double)maxWidth / (double)screenWidth, (double)maxHeight / (double)screenHeight);
		scale = Math.Min(1.0, scale);
		int destWidth = Math.Max(1, (int)Math.Round((double)screenWidth * scale));
		int destHeight = Math.Max(1, (int)Math.Round((double)screenHeight * scale));
		byte[]? pixels = GrabPixels(screenX, screenY, screenWidth, screenHeight, destWidth, destHeight);
		if (pixels == null)
		{
			return Fail("bitblt", startedTicks);
		}
		if (IsEffectivelyBlack(pixels))
		{
			// 独占全屏 / 桌面被独占时 GDI 只能拿到黑图：本轮直接放弃，不做全屏特判。
			return Fail("black_frame", startedTicks);
		}
		byte[]? jpeg = EncodeJpeg(pixels, destWidth, destHeight, jpegQuality);
		if (jpeg == null || jpeg.Length == 0)
		{
			return Fail("encode", startedTicks);
		}
		return new CaptureResult(jpeg, destWidth, destHeight, "", Environment.TickCount64 - startedTicks);
	}

	private static CaptureResult Fail(string reason, long startedTicks)
	{
		return new CaptureResult(null, 0, 0, reason, Environment.TickCount64 - startedTicks);
	}

	/// <summary>
	/// 桌面矩形：先用游戏算好的 <c>LilithWindow.VirtualScreen</c>（多屏含负坐标也对），
	/// 读不到再退回 GetSystemMetrics。
	/// </summary>
	private static bool TryGetVirtualScreen(out int x, out int y, out int width, out int height)
	{
		x = 0;
		y = 0;
		width = 0;
		height = 0;
		try
		{
			Type type = GameTypeResolver.ResolveLoadedType(LilithWindowType);
			PropertyInfo? property = type?.GetProperty("VirtualScreen", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null && property.GetValue(null) is Rect rect && rect.width >= 1f && rect.height >= 1f)
			{
				x = (int)Math.Floor(rect.x);
				y = (int)Math.Floor(rect.y);
				width = (int)Math.Ceiling(rect.width);
				height = (int)Math.Ceiling(rect.height);
				return true;
			}
		}
		catch (Exception)
		{
		}
		try
		{
			x = GetSystemMetrics(SM_XVIRTUALSCREEN);
			y = GetSystemMetrics(SM_YVIRTUALSCREEN);
			width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
			height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
			return width >= 1 && height >= 1;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static byte[]? GrabPixels(int sourceX, int sourceY, int sourceWidth, int sourceHeight, int destWidth, int destHeight)
	{
		IntPtr screenDc = IntPtr.Zero;
		IntPtr memoryDc = IntPtr.Zero;
		IntPtr dib = IntPtr.Zero;
		IntPtr previous = IntPtr.Zero;
		try
		{
			// CreateDC("DISPLAY") 覆盖整个虚拟桌面；拿不到再退回主屏 DC。
			screenDc = CreateDCW("DISPLAY", null, null, IntPtr.Zero);
			if (screenDc == IntPtr.Zero)
			{
				screenDc = GetDC(IntPtr.Zero);
			}
			if (screenDc == IntPtr.Zero)
			{
				return null;
			}
			memoryDc = CreateCompatibleDC(screenDc);
			if (memoryDc == IntPtr.Zero)
			{
				return null;
			}
			BITMAPINFO bitmapInfo = new BITMAPINFO
			{
				bmiHeader = new BITMAPINFOHEADER
				{
					biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
					biWidth = destWidth,
					// 正的 biHeight = 自底向上，正好对上 Unity 的 BGRA32 期望的行序。
					biHeight = destHeight,
					biPlanes = 1,
					biBitCount = 32,
					biCompression = 0
				}
			};
			dib = CreateDIBSection(memoryDc, ref bitmapInfo, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
			if (dib == IntPtr.Zero || bits == IntPtr.Zero)
			{
				return null;
			}
			previous = SelectObject(memoryDc, dib);
			if (previous == IntPtr.Zero)
			{
				return null;
			}
			SetStretchBltMode(memoryDc, HALFTONE);
			SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);
			if (!StretchBlt(memoryDc, 0, 0, destWidth, destHeight, screenDc, sourceX, sourceY, sourceWidth, sourceHeight, SRCCOPY))
			{
				return null;
			}
			byte[] array = new byte[destWidth * destHeight * 4];
			Marshal.Copy(bits, array, 0, array.Length);
			return array;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
			{
				SelectObject(memoryDc, previous);
			}
			if (dib != IntPtr.Zero)
			{
				DeleteObject(dib);
			}
			if (memoryDc != IntPtr.Zero)
			{
				DeleteDC(memoryDc);
			}
			if (screenDc != IntPtr.Zero)
			{
				ReleaseDC(IntPtr.Zero, screenDc);
			}
		}
	}

	/// <summary>抽样判断是不是一整张黑图（独占全屏的典型表现）。</summary>
	private static bool IsEffectivelyBlack(byte[] pixels)
	{
		int step = 4 * 257;
		for (int i = 0; i + 2 < pixels.Length; i += step)
		{
			if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
			{
				return false;
			}
		}
		return true;
	}

	private static byte[]? EncodeJpeg(byte[] bgraPixels, int width, int height, int quality)
	{
		Texture2D? texture = null;
		try
		{
			texture = new Texture2D(width, height, TextureFormat.BGRA32, false);
			texture.LoadRawTextureData(bgraPixels);
			texture.Apply(false, false);
			return ImageConversion.EncodeToJPG(texture, quality);
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			if (texture != null)
			{
				UnityEngine.Object.Destroy(texture);
			}
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAPINFOHEADER
	{
		public int biSize;

		public int biWidth;

		public int biHeight;

		public short biPlanes;

		public short biBitCount;

		public int biCompression;

		public int biSizeImage;

		public int biXPelsPerMeter;

		public int biYPelsPerMeter;

		public int biClrUsed;

		public int biClrImportant;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAPINFO
	{
		public BITMAPINFOHEADER bmiHeader;

		public int bmiColors;
	}

	[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateDCW(string? driver, string? device, string? port, IntPtr initData);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr GetDC(IntPtr window);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int ReleaseDC(IntPtr window, IntPtr dc);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern IntPtr CreateCompatibleDC(IntPtr dc);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern bool DeleteDC(IntPtr dc);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern bool DeleteObject(IntPtr obj);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern int SetStretchBltMode(IntPtr dc, int mode);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern bool StretchBlt(IntPtr destDc, int destX, int destY, int destWidth, int destHeight, IntPtr sourceDc, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);
}
