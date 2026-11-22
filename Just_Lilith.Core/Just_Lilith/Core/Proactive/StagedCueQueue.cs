using System;
using System.Collections.Generic;

namespace Just_Lilith.Core.Proactive;

/// <summary>
/// 暂存队列：闸门不通过时把由头先存着，等能说话时再投。
/// <para>
/// 规则：先清过期（过期只回调、不补说），再按<b>优先级降序、创建时间升序</b>取一个；
/// 队列有上限（默认 8 条），满了丢最旧的那条。
/// </para>
/// </summary>
public sealed class StagedCueQueue
{
	private readonly object _gate = new object();

	private readonly List<AmbientCue> _items = new List<AmbientCue>();

	private readonly int _limit;

	public StagedCueQueue(int limit)
	{
		_limit = Math.Max(1, limit);
	}

	public int Limit => _limit;

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _items.Count;
			}
		}
	}

	/// <summary>清掉已过期的条目，返回清掉的条数；每条都会回调一次（给日志用）。</summary>
	public int Prune(DateTimeOffset nowUtc, Action<AmbientCue>? onExpired = null)
	{
		List<AmbientCue>? expired = null;
		lock (_gate)
		{
			for (int i = _items.Count - 1; i >= 0; i--)
			{
				if (_items[i].IsExpired(nowUtc))
				{
					(expired ??= new List<AmbientCue>()).Add(_items[i]);
					_items.RemoveAt(i);
				}
			}
		}
		if (expired == null)
		{
			return 0;
		}
		if (onExpired != null)
		{
			foreach (AmbientCue item in expired)
			{
				onExpired(item);
			}
		}
		return expired.Count;
	}

	/// <summary>入队。队列满时丢掉最旧的一条并通过 <paramref name="dropped"/> 交回。</summary>
	public bool TryEnqueue(AmbientCue cue, out AmbientCue? dropped)
	{
		ArgumentNullException.ThrowIfNull(cue, "cue");
		dropped = null;
		lock (_gate)
		{
			if (_items.Count >= _limit)
			{
				int oldest = 0;
				for (int i = 1; i < _items.Count; i++)
				{
					if (_items[i].CreatedAt < _items[oldest].CreatedAt)
					{
						oldest = i;
					}
				}
				dropped = _items[oldest];
				_items.RemoveAt(oldest);
			}
			_items.Add(cue);
		}
		return true;
	}

	/// <summary>取一个可投的由头（先清过期）。没有可投的返回 false。</summary>
	public bool TryTake(DateTimeOffset nowUtc, out AmbientCue? cue)
	{
		return TryTake(nowUtc, null, out cue);
	}

	/// <summary>取一个可投的由头（先清过期，过期条目逐个回调）。</summary>
	public bool TryTake(DateTimeOffset nowUtc, Action<AmbientCue>? onExpired, out AmbientCue? cue)
	{
		Prune(nowUtc, onExpired);
		lock (_gate)
		{
			if (_items.Count == 0)
			{
				cue = null;
				return false;
			}
			int best = 0;
			for (int i = 1; i < _items.Count; i++)
			{
				if (_items[i].Priority > _items[best].Priority || (_items[i].Priority == _items[best].Priority && _items[i].CreatedAt < _items[best].CreatedAt))
				{
					best = i;
				}
			}
			cue = _items[best];
			_items.RemoveAt(best);
			return true;
		}
	}

	public void Clear()
	{
		lock (_gate)
		{
			_items.Clear();
		}
	}

	public IReadOnlyList<AmbientCue> Snapshot()
	{
		lock (_gate)
		{
			return _items.ToArray();
		}
	}
}
