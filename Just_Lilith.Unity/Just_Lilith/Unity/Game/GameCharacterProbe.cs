using System;
using System.Reflection;
using UnityEngine;

namespace Just_Lilith.Unity.Game;

/// <summary>
/// 判断角色是否已经出场（Spine 渲染器可见）。
/// 原版用 <c>ClothingSnapInterceptor.SyncSeen</c>（换装快照拦截器）作为“模型加载完成”的信号，
/// 本仓库没有那套换装拦截，因此改用等价且更直接的判断：角色渲染器存在且启用。
/// TTS 的延迟启动用它来决定什么时候拉起语音服务，避免开局卡顿。
/// </summary>
internal static class GameCharacterProbe
{
	private const string CharacterTypeName = "CharacterController, Assembly-CSharp";

	private static readonly object Gate = new object();

	private static bool _reflectionResolved;

	private static Type? _characterType;

	private static FieldInfo? _activeCharacterField;

	private static FieldInfo? _cachedSpineRendererField;

	private static PropertyInfo? _skeletonAnimationProperty;

	private static object? _characterInstance;

	private static Component? _characterComponent;

	private static Renderer? _spineRenderer;

	/// <summary>角色是否已出场（渲染器可见）。内部有缓存，可以每帧调用。</summary>
	public static bool IsCharacterPresent()
	{
		if (!TryGetRenderer(out Renderer? renderer))
		{
			return false;
		}
		try
		{
			return renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>取角色 Spine 渲染器的世界包围盒；取不到返回 false。</summary>
	public static bool TryGetBounds(out Bounds bounds)
	{
		bounds = default(Bounds);
		if (!TryGetRenderer(out Renderer? renderer) || renderer == null)
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
		return bounds.size.x > 0.0001f || bounds.size.y > 0.0001f;
	}

	private static bool TryGetRenderer(out Renderer? renderer)
	{
		lock (Gate)
		{
			renderer = _spineRenderer;
			if (renderer != null)
			{
				return true;
			}
			if (!ResolveCharacter())
			{
				return false;
			}
			try
			{
				renderer = _cachedSpineRendererField?.GetValue(_characterInstance) as Renderer;
			}
			catch
			{
				renderer = null;
			}
			if (renderer == null)
			{
				try
				{
					object? skeleton = _skeletonAnimationProperty?.GetValue(_characterInstance);
					if (skeleton is Component component)
					{
						_characterComponent = component;
						renderer = component.GetComponentInChildren<Renderer>();
					}
				}
				catch
				{
					renderer = null;
				}
			}
			if (renderer == null)
			{
				try
				{
					renderer = _characterComponent?.GetComponentInChildren<Renderer>();
				}
				catch
				{
					renderer = null;
				}
			}
			_spineRenderer = renderer;
			return renderer != null;
		}
	}

	private static bool ResolveCharacter()
	{
		if (!_reflectionResolved)
		{
			_reflectionResolved = true;
			try
			{
				_characterType = GameTypeResolver.ResolveLoadedType(CharacterTypeName);
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
		if (_characterInstance != null)
		{
			return true;
		}
		try
		{
			object? instance = _activeCharacterField?.GetValue(null);
			if (instance == null && UnityRuntimeObjectLocator.TryFind(CharacterTypeName, out LocatedUnityObject located))
			{
				instance = located.Instance;
			}
			if (instance == null)
			{
				return false;
			}
			_characterInstance = instance;
			_characterComponent = instance as Component;
			return true;
		}
		catch
		{
			return false;
		}
	}
}
