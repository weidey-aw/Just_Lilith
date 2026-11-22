using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Just_Lilith.Unity.Game;

internal static class AudioSessionProbe
{
	[ComImport]
	[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
	private class MMDeviceEnumeratorComObject
	{
	}

	[ComImport]
	[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

		int GetDevice(string id, out IMMDevice device);

		int RegisterEndpointNotificationCallback(IntPtr client);

		int UnregisterEndpointNotificationCallback(IntPtr client);
	}

	[ComImport]
	[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);

		int OpenPropertyStore(int stgmAccess, out IntPtr properties);

		int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

		int GetState(out int state);
	}

	[ComImport]
	[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioSessionManager2
	{
		int GetAudioSessionControl(ref Guid sessionId, uint streamFlags, out IntPtr sessionControl);

		int GetSimpleAudioVolume(ref Guid sessionId, uint streamFlags, out IntPtr audioVolume);

		[PreserveSig]
		int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

		int RegisterSessionNotification(IntPtr notification);

		int UnregisterSessionNotification(IntPtr notification);

		int RegisterDuckNotification(IntPtr sessionId, IntPtr notification);

		int UnregisterDuckNotification(IntPtr sessionId);
	}

	[ComImport]
	[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioSessionEnumerator
	{
		[PreserveSig]
		int GetCount(out int sessionCount);

		[PreserveSig]
		int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl2 session);
	}

	[ComImport]
	[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioSessionControl2
	{
		[PreserveSig]
		int GetState(out int state);

		int GetDisplayName(out IntPtr displayName);

		int SetDisplayName(IntPtr displayName, ref Guid eventContext);

		int GetIconPath(out IntPtr iconPath);

		int SetIconPath(IntPtr iconPath, ref Guid eventContext);

		int GetGroupingParam(out Guid groupingId);

		int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

		int RegisterAudioSessionNotification(IntPtr client);

		int UnregisterAudioSessionNotification(IntPtr client);

		int GetSessionIdentifier(out IntPtr retVal);

		int GetSessionInstanceIdentifier(out IntPtr retVal);

		[PreserveSig]
		int GetProcessId(out uint retVal);

		[PreserveSig]
		int IsSystemSoundsSession();

		int SetDuckingPreference(bool optOut);
	}

	private const int StateActive = 1;

	private const long CacheMs = 2000L;

	private static long _cachedAtMs = long.MinValue;

	private static readonly object _queryLock = new object();

	private static HashSet<string>? _cached;

	private static bool _failedLogged;

	internal static HashSet<string>? ActiveProcessNames()
	{
		long tickCount = Environment.TickCount64;
		if (_cached != null && tickCount - _cachedAtMs < 2000)
		{
			return _cached;
		}
		HashSet<string> hashSet;
		lock (_queryLock)
		{
			hashSet = Query();
		}
		if (hashSet != null)
		{
			_cached = hashSet;
			_cachedAtMs = tickCount;
		}
		return hashSet;
	}

	private static HashSet<string>? Query()
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		object obj = null;
		try
		{
			obj = new MMDeviceEnumeratorComObject();
			if (((IMMDeviceEnumerator)obj).GetDefaultAudioEndpoint(0, 1, out var endpoint) != 0)
			{
				return null;
			}
			try
			{
				Guid iid = typeof(IAudioSessionManager2).GUID;
				if (endpoint.Activate(ref iid, 23u, IntPtr.Zero, out var iface) != 0)
				{
					return null;
				}
				try
				{
					if (((IAudioSessionManager2)iface).GetSessionEnumerator(out var sessionEnum) != 0)
					{
						return null;
					}
					try
					{
						if (sessionEnum.GetCount(out var sessionCount) != 0)
						{
							return null;
						}
						for (int i = 0; i < sessionCount; i++)
						{
							IAudioSessionControl2 session = null;
							try
							{
								if (sessionEnum.GetSession(i, out session) == 0 && session != null && session.GetState(out var state) == 0 && state == 1 && session.GetProcessId(out var retVal) == 0 && retVal != 0)
								{
									hashSet.Add(ProcessNameOf(retVal));
								}
							}
							catch
							{
							}
							finally
							{
								if (session != null)
								{
									Marshal.ReleaseComObject(session);
								}
							}
						}
						return hashSet;
					}
					finally
					{
						Marshal.ReleaseComObject(sessionEnum);
					}
				}
				finally
				{
					Marshal.ReleaseComObject(iface);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(endpoint);
			}
		}
		catch (Exception ex)
		{
			if (!_failedLogged)
			{
				_failedLogged = true;
			}
			return null;
		}
		finally
		{
			if (obj != null)
			{
				Marshal.ReleaseComObject(obj);
			}
		}
	}

	private static string ProcessNameOf(uint pid)
	{
		try
		{
			using Process process = Process.GetProcessById((int)pid);
			return process.ProcessName.ToLowerInvariant();
		}
		catch
		{
			return "";
		}
	}
}
