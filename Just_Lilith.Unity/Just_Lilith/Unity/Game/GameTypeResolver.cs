using System;
using System.Linq;
using System.Reflection;

namespace Just_Lilith.Unity.Game;

/// <summary>
/// 按 "类型名, 程序集名" 解析已加载的游戏类型。
/// 与原版 <c>PetActionInvoker.ResolveLoadedType</c> 行为一致：先走 <see cref="Type.GetType(string,bool)"/>，
/// 失败后再到已加载程序集里按名字找，避免插件在缺少某个程序集时直接抛异常。
/// </summary>
internal static class GameTypeResolver
{
	internal static Type? ResolveLoadedType(string assemblyQualifiedTypeName)
	{
		if (string.IsNullOrWhiteSpace(assemblyQualifiedTypeName))
		{
			return null;
		}
		Type type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
		if ((object)type != null)
		{
			return type;
		}
		int num = assemblyQualifiedTypeName.IndexOf(',');
		if (num < 0)
		{
			return null;
		}
		string name = assemblyQualifiedTypeName.Substring(0, num).Trim();
		string asmName = assemblyQualifiedTypeName.Substring(num + 1).Trim().Split(',')[0].Trim();
		try
		{
			return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly assembly) => string.Equals(assembly.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase))?.GetType(name, throwOnError: false, ignoreCase: false);
		}
		catch
		{
			return null;
		}
	}
}
