using System;
using System.Globalization;

namespace Just_Lilith.Core.Proactive;

/// <summary>
/// 兴趣值与掷骰模型。
///
/// 模型：每 tick 涨 <c>U(a, 2a)</c>（均值 <c>1.5a</c>），涨过 50 才开始掷骰；
/// 掷中即清零并记一次，掷不中继续攒。默认 <c>a = 4.8</c>，
/// 配 30s 的 tick 就是"每 tick 约 7.2 点、大约 5 分钟掷中一次"。
/// </summary>
public static class InterestModel
{
	/// <summary>兴趣值上限。</summary>
	public const double MaxInterest = 100.0;

	/// <summary>低于等于这个值不掷骰。</summary>
	public const double RollThreshold = 50.0;

	/// <summary>U(a,2a) 的均值系数。</summary>
	public const double MeanMultiplier = 1.5;

	/// <summary>默认增量下限（对应 targetIntervalSeconds=300、checkPeriodSeconds=30）。</summary>
	public const double DefaultIncrementLower = 4.8;

	// 过了 50 之后平均还要几个 tick 才掷中（p 从 0 慢慢爬上来），用于把"期望间隔"换算成增量。
	private const double RollAttemptTicks = 3.0;

	/// <summary>
	/// 按"期望多久掷中一次"反解每 tick 增量下限 a（二分法的闭式解，含 U(a,2a) 均值）。
	/// 公式：<c>a = 50 / (1.5 * (ticks - 3))</c>，<c>ticks = interval / period</c>。
	/// <para>
	/// <paramref name="maxCount"/> 不影响增量：它只决定"一条用户消息之后最多主动几次"，
	/// 以及概率衰减的强度；这里保留参数是为了让调用点读起来完整。
	/// </para>
	/// </summary>
	public static double SolveIncrement(int intervalSeconds, int maxCount, int periodSeconds)
	{
		int period = Math.Max(1, periodSeconds);
		double ticks = Math.Max(1.0, (double)intervalSeconds / (double)period);
		double climbTicks = Math.Max(1.0, ticks - RollAttemptTicks);
		double lower = RollThreshold / (MeanMultiplier * climbTicks);
		return Math.Clamp(lower, 0.2, 40.0);
	}

	/// <summary>本 tick 的增量，服从 <c>U(lowerBound, 2*lowerBound)</c>。</summary>
	public static double NextIncrement(Random random, double lowerBound)
	{
		ArgumentNullException.ThrowIfNull(random, "random");
		double lower = Math.Max(0.05, lowerBound);
		return lower + random.NextDouble() * lower;
	}

	/// <summary>
	/// 掷骰概率：<c>clamp((interest + stateBonus - 50)/50, 0, 1)</c>，
	/// 再乘概率衰减 <c>max(0.15, (N-count)/N)</c>（<paramref name="decay"/> 关闭时不乘）。
	/// </summary>
	public static double RollChance(double interest, int stateBonus, int maxCount, int count, bool decay)
	{
		double chance = Math.Clamp((interest + (double)stateBonus - RollThreshold) / RollThreshold, 0.0, 1.0);
		if (decay && maxCount > 0)
		{
			chance *= Math.Max(0.15, (double)(maxCount - count) / (double)maxCount);
		}
		return Math.Clamp(chance, 0.0, 1.0);
	}

	public static double ClampInterest(double value)
	{
		return Math.Clamp(value, 0.0, MaxInterest);
	}

	/// <summary>UI 回显用："每轮约 4.8–9.6 点"。</summary>
	public static string DescribeIncrement(double lowerBound)
	{
		return string.Format(CultureInfo.InvariantCulture, "每轮约 {0:0.0}–{1:0.0} 点", lowerBound, lowerBound * 2.0);
	}
}
