using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Just_Lilith.Core.Contracts;
using Just_Lilith.Core.Speech;

namespace Just_Lilith.Unity.Speech;

/// <summary>一段已经合成好、等着上屏播放的音频。</summary>
internal sealed record ReadyChunk(int Index, string Text, PcmWaveData Wave, long SynthMs);

/// <summary>
/// 预取流水线（阶段 2）：后台按顺序串行合成 chunk 0..N-1，主线程播一段取一段。
///
/// 关键约束：
/// - 生产者最多领先"已被取走"的段 <c>ahead</c> 段（默认 1）：服务端推理是串行的，
///   发更多请求只会排队，领先 1 段正好是"播第 N 段时第 N+1 段已经好了"。
/// - 就绪队列因此天然有界（≤ ahead 段），不需要额外的容量判断。
/// - 单段失败只记 <c>chunk_failed</c> 并跳段，不打断后面的段。
/// - 取消后队列立即清空，旧段不会再冒到新回复后面。
/// </summary>
internal sealed class SpeechPipeline : IDisposable
{
	private readonly ConcurrentQueue<ReadyChunk> _ready = new ConcurrentQueue<ReadyChunk>();

	private readonly IReadOnlyList<string> _chunks;

	private readonly SpeechLanguage _language;

	private readonly SpeechDirective _directive;

	private readonly LocalTtsClient _client;

	private readonly ManualLogSource? _log;

	private readonly SemaphoreSlim _slots;

	private readonly CancellationTokenSource _cancellation;

	private readonly Task _producer;

	private int _produced;

	private int _failed;

	private int _consumed;

	private int _disposed;

	public SpeechPipeline(LocalTtsClient client, string serviceUrl, IReadOnlyList<string> chunks, SpeechLanguage language, SpeechDirective directive, int ahead = 1, ManualLogSource? log = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		_client = client ?? throw new ArgumentNullException("client");
		_chunks = chunks ?? throw new ArgumentNullException("chunks");
		_language = language;
		_directive = directive;
		_log = log;
		int slotCount = Math.Max(1, ahead);
		_slots = new SemaphoreSlim(slotCount, slotCount);
		_cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_log?.LogInfo("module=TTS; event=pipeline_started; chunks=" + _chunks.Count + "; ahead=" + slotCount + "; url_set=" + (string.IsNullOrEmpty(serviceUrl) ? "0" : "1"));
		_producer = Task.Run(RunAsync);
	}

	/// <summary>这一段是第几段（0 起）。</summary>
	public int TotalChunks => _chunks.Count;

	/// <summary>已经成功入队的段数。</summary>
	public int Produced => Volatile.Read(ref _produced);

	/// <summary>合成失败被跳过的段数。</summary>
	public int Failed => Volatile.Read(ref _failed);

	/// <summary>已经被主线程取走的段数。</summary>
	public int Consumed => Volatile.Read(ref _consumed);

	/// <summary>生产者处理完的段数（成功 + 失败）。</summary>
	public int Handled => Produced + Failed;

	/// <summary>生产者已经不再产出新段。</summary>
	public bool IsFinished => _producer.IsCompleted;

	/// <summary>取一段就绪音频；取到即占用的预取额度立刻还回去，让生产者继续往前跑。</summary>
	public bool TryTakeReady(out ReadyChunk chunk)
	{
		if (_ready.TryDequeue(out chunk!))
		{
			Interlocked.Increment(ref _consumed);
			ReleaseSlot();
			return true;
		}
		return false;
	}

	/// <summary>取消生产者并丢掉还没播的段。</summary>
	public void Cancel()
	{
		try
		{
			_cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		DrainReady();
	}

	private void DrainReady()
	{
		ReadyChunk ignored;
		while (_ready.TryDequeue(out ignored!))
		{
		}
	}

	private void ReleaseSlot()
	{
		try
		{
			_slots.Release();
		}
		catch (ObjectDisposedException)
		{
		}
		catch (SemaphoreFullException)
		{
		}
	}

	private async Task RunAsync()
	{
		CancellationToken token = _cancellation.Token;
		try
		{
			for (int i = 0; i < _chunks.Count; i++)
			{
				// 背压：没被取走就等着，最多领先已播段 ahead 段。
				await _slots.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				if (token.IsCancellationRequested)
				{
					return;
				}
				long synthMs = 0L;
				try
				{
					DateTimeOffset startedAt = DateTimeOffset.UtcNow;
					SpeechSynthesisRequest request = new SpeechSynthesisRequest(Guid.NewGuid(), _chunks[i], _language, _directive);
					PcmWaveData wave = await SpeechSplitSynthesizer.SynthesizeChunkAsync(_client, request, token).ConfigureAwait(continueOnCapturedContext: false);
					synthMs = (long)Math.Round((DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
					_ready.Enqueue(new ReadyChunk(i, _chunks[i], wave, synthMs));
					Interlocked.Increment(ref _produced);
					_log?.LogInfo("module=TTS; event=chunk_ready; index=" + (i + 1) + "; synth_ms=" + synthMs + "; audio_s=" + wave.DurationSeconds.ToString("0.00") + "; ahead=" + (_produced - _consumed));
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception error)
				{
					// 单段失败只跳段：占用的额度还回去，继续合成下一段。
					Interlocked.Increment(ref _failed);
					_log?.LogWarning("module=TTS; event=chunk_failed; index=" + (i + 1) + "; code=" + ((error is SpeechException speechException) ? speechException.Code : error.GetType().Name) + "; text_logged=false");
					ReleaseSlot();
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		catch (Exception error2)
		{
			_log?.LogWarning("module=TTS; event=pipeline_faulted; code=" + error2.GetType().Name + "; text_logged=false");
		}
		finally
		{
			int handled = _produced + _failed;
			_log?.LogInfo("module=TTS; event=pipeline_finished; chunks=" + _chunks.Count + "; produced=" + _produced + "; failed=" + _failed + "; consumed=" + _consumed + "; handled=" + handled);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		Cancel();
		try
		{
			// 等生产者真正退出再释放信号量，避免它在 WaitAsync 上撞到已释放的对象。
			_producer.Wait(TimeSpan.FromSeconds(2.0));
		}
		catch (Exception)
		{
		}
		DrainReady();
		_slots.Dispose();
		_cancellation.Dispose();
	}
}
