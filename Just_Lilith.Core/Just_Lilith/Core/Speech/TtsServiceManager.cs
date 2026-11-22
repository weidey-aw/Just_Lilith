using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Just_Lilith.Core.Speech;

public sealed class TtsServiceManager : ITtsServiceManager, IDisposable
{
	private enum Health
	{
		Missing,
		Loading,
		Ready,
		Failed,
		Unknown
	}

	private sealed class ServiceFailure : Exception
	{
		public ServiceFailure(string message)
			: base(message)
		{
		}
	}

	private readonly object gate = new object();

	private readonly TtsServiceOptions options;

	private readonly Queue<string> logs = new Queue<string>();

	private TtsServiceState state = new TtsServiceState(TtsServicePhase.Off, "TTS 服务已关闭");

	private CancellationTokenSource? runCancellation;

	private ITtsManagedProcess? owned;

	private Task worker = Task.CompletedTask;

	private long generation;

	private bool enabled;

	private bool stopped;

	private string endpoint = "";

	private bool externalMode;

	public TtsServiceState State
	{
		get
		{
			lock (gate)
			{
				return state;
			}
		}
	}

	public IReadOnlyList<string> RecentLog
	{
		get
		{
			lock (gate)
			{
				return logs.ToArray();
			}
		}
	}

	public Task Completion
	{
		get
		{
			lock (gate)
			{
				return worker;
			}
		}
	}

	public TtsServiceManager(TtsServiceOptions options)
	{
		this.options = options ?? throw new ArgumentNullException("options");
		bool flag = options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout > TimeSpan.FromMinutes(10.0) || options.HealthPollInterval < TimeSpan.FromMilliseconds(10.0) || options.HealthPollInterval > TimeSpan.FromSeconds(30.0) || options.HealthRequestTimeout < TimeSpan.FromMilliseconds(50.0) || options.HealthRequestTimeout > TimeSpan.FromSeconds(30.0);
		if (!flag)
		{
			int logCapacity = options.LogCapacity;
			flag = ((logCapacity < 1 || logCapacity > 256) ? true : false);
		}
		if (flag)
		{
			throw new ArgumentException("Invalid TTS lifecycle limits.", "options");
		}
	}

	public void SetEnabled(bool value, string serviceUrl)
	{
		SetEnabled(value, serviceUrl, allowExternal: false);
	}

	public void SetEnabled(bool value, string serviceUrl, bool allowExternal)
	{
		string url = "";
		bool flag = false;
		if (value)
		{
			try
			{
				url = NormalizeEndpoint(serviceUrl);
			}
			catch (ArgumentException)
			{
				flag = true;
			}
		}
		CancellationTokenSource cancellationTokenSource;
		ITtsManagedProcess ttsManagedProcess;
		Task previous;
		lock (gate)
		{
			if (stopped || (value == enabled && (!value || endpoint == url) && (!value || externalMode == allowExternal) && (!value || state.Phase != TtsServicePhase.Failed)))
			{
				return;
			}
			enabled = value;
			endpoint = url;
			externalMode = allowExternal;
			long revision = ++generation;
			cancellationTokenSource = runCancellation;
			ttsManagedProcess = owned;
			runCancellation = null;
			previous = worker;
			if (value && !flag)
			{
				CancellationTokenSource cancellation = new CancellationTokenSource();
				runCancellation = cancellation;
				state = ((owned != null) ? new TtsServiceState(TtsServicePhase.Stopping, "正在等待先前 TTS 服务退出", Owned: true) : new TtsServiceState(TtsServicePhase.Starting, "TTS 服务启动中"));
				worker = Task.Run(async () =>
				{
					try
					{
						await previous.ConfigureAwait(continueOnCapturedContext: false);
					}
					catch
					{
					}
					lock (gate)
					{
						if (owned != null)
						{
							if (generation == revision)
							{
								state = new TtsServiceState(TtsServicePhase.Stopping, "先前 TTS 服务尚未退出", Owned: true);
							}
							return;
						}
					}
					if (!cancellation.IsCancellationRequested)
					{
						await RunAsync(revision, url, allowExternal, cancellation.Token).ConfigureAwait(continueOnCapturedContext: false);
					}
				});
			}
			else
			{
				state = ((flag && previous.IsCompleted) ? new TtsServiceState(TtsServicePhase.Failed, "TTS 服务地址须为本机 127.0.0.1 的有效端口") : new TtsServiceState((!previous.IsCompleted) ? TtsServicePhase.Stopping : TtsServicePhase.Off, previous.IsCompleted ? "TTS 服务已关闭" : "TTS 服务关闭中", owned != null));
				worker = FinishStopAsync(previous, revision, flag);
			}
		}
		ttsManagedProcess?.RequestStop();
		cancellationTokenSource?.Cancel();
		if (cancellationTokenSource != null)
		{
			DisposeCancellationAfterAsync(cancellationTokenSource, previous);
		}
	}

	private static async Task DisposeCancellationAfterAsync(CancellationTokenSource cancellation, Task previous)
	{
		try
		{
			await previous.ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
		}
		cancellation.Dispose();
	}

	private async Task FinishStopAsync(Task previous, long revision, bool failed)
	{
		try
		{
			await previous.ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
		}
		lock (gate)
		{
			if (generation == revision)
			{
				ref TtsServiceState reference = ref state;
				TtsServiceState ttsServiceState;
				if (owned != null)
				{
					ttsServiceState = new TtsServiceState(TtsServicePhase.Stopping, "TTS 服务退出尚未确认，请查看诊断日志", Owned: true);
				}
				else
				{
					ttsServiceState = (failed ? new TtsServiceState(TtsServicePhase.Failed, "TTS 服务地址须为本机 127.0.0.1 的有效端口") : new TtsServiceState(TtsServicePhase.Off, "TTS 服务已关闭"));
				}
				reference = ttsServiceState;
			}
		}
	}

	public void RequestStop()
	{
		ITtsManagedProcess ttsManagedProcess;
		CancellationTokenSource cancellationTokenSource;
		Task previous;
		lock (gate)
		{
			if (stopped)
			{
				return;
			}
			stopped = true;
			enabled = false;
			generation++;
			ttsManagedProcess = owned;
			cancellationTokenSource = runCancellation;
			runCancellation = null;
			state = new TtsServiceState((!worker.IsCompleted) ? TtsServicePhase.Stopping : TtsServicePhase.Off, worker.IsCompleted ? "TTS 服务已关闭" : "TTS 服务关闭中", owned != null);
			previous = worker;
			worker = FinishStopAsync(previous, generation, failed: false);
		}
		ttsManagedProcess?.RequestStop();
		cancellationTokenSource?.Cancel();
		if (cancellationTokenSource != null)
		{
			DisposeCancellationAfterAsync(cancellationTokenSource, previous);
		}
	}

	public void Dispose()
	{
		RequestStop();
	}

	private bool IsCurrent(long revision, CancellationToken cancellation)
	{
		lock (gate)
		{
			return generation == revision && enabled && !stopped && !cancellation.IsCancellationRequested;
		}
	}

	private void Publish(long revision, TtsServicePhase phase, string message, bool isOwned = false)
	{
		lock (gate)
		{
			if (generation == revision && enabled && !stopped)
			{
				state = new TtsServiceState(phase, message, isOwned);
			}
		}
	}

	private void AppendLog(string value, string instance)
	{
		value = TtsServiceManagerDiagnostics.SanitizeText(value.Replace(instance, "[instance]", StringComparison.Ordinal));
		value = new string(value.Where((char c) => !char.IsControl(c) || c == '\t').Take(512).ToArray());
		lock (gate)
		{
			while (logs.Count >= options.LogCapacity)
			{
				logs.Dequeue();
			}
			logs.Enqueue(value);
		}
	}

	private async Task RunAsync(long revision, string url, bool allowExternal, CancellationToken cancellation)
	{
		ITtsManagedProcess child = null;
		TtsServiceManagerDiagnostics diagnostics = null;
		string token = Guid.NewGuid().ToString("N");
		string failure = null;
		bool exitConfirmed = true;
		try
		{
			_ = 2;
			try
			{
				string runtime = Path.GetFullPath(options.RuntimeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				TtsServiceManagerProcess.AssertPlainPath(runtime);
				diagnostics = new TtsServiceManagerDiagnostics(runtime);
				Publish(revision, TtsServicePhase.Starting, allowExternal ? "外部 TTS 连接中" : "TTS 服务启动中");
				Log("lifecycle: attempt_started; generation=" + revision + "; endpoint=" + url + "; external=" + allowExternal);
				string project = ProjectIdentity(runtime);
				using SocketsHttpHandler handler = new SocketsHttpHandler
				{
					UseProxy = false,
					AllowAutoRedirect = false,
					ConnectTimeout = options.HealthRequestTimeout
				};
				using HttpClient http = new HttpClient(handler)
				{
					Timeout = Timeout.InfiniteTimeSpan
				};
				Uri uri = new Uri(url);
				Stopwatch startupClock = Stopwatch.StartNew();
				Health health = await ProbeAsync(http, uri, project, null, allowExternal, cancellation).ConfigureAwait(continueOnCapturedContext: false);
				Log("lifecycle: initial_health=" + health);
				if (!IsCurrent(revision, cancellation))
				{
					return;
				}
				switch (health)
				{
				case Health.Failed:
				case Health.Unknown:
					if (allowExternal)
					{
						throw new ServiceFailure((health == Health.Unknown) ? "外部 TTS 健康检查格式不正确：/health 需返回 JSON，status 为 ready/loading/error" : "外部 TTS 返回错误状态（status=error）");
					}
					throw new ServiceFailure((health == Health.Unknown) ? "端口已占用或服务身份未确认" : "TTS 服务模型加载失败");
				case Health.Loading:
				case Health.Ready:
					if (!allowExternal)
					{
						throw new ServiceFailure("检测到外部 TTS 实例；请先停止手动服务再开启，本插件未接管");
					}
					break;
				}
				if (health != Health.Missing)
				{
					goto IL_044b;
				}
				if (allowExternal)
				{
					throw new ServiceFailure("外部 TTS 未启动：请先在 " + url + " 启动你的 TTS 服务");
				}
				child = options.ProcessFactory?.Invoke(runtime, uri.Port, token, Log) ?? new TtsServiceManagerProcess(runtime, uri.Port, token, Log);
				lock (gate)
				{
					if (generation != revision || stopped || !enabled || cancellation.IsCancellationRequested)
					{
						return;
					}
					owned = child;
					goto IL_0428;
				}
				IL_044b:
				bool ready = false;
				int missed = 0;
				while (IsCurrent(revision, cancellation))
				{
					if (child?.HasExited ?? false)
					{
						int exitCode = child.ExitCode;
						string text = exitCode.ToString();
						uint num = (uint)exitCode;
						Log("lifecycle: unexpected_exit; exit_code=" + text + "; exit_hex=0x" + num.ToString("X8"));
						string[] array = new string[5]
						{
							"TTS 服务进程已退出（",
							exitCode.ToString(),
							" / 0x",
							null,
							null
						};
						num = (uint)exitCode;
						array[3] = num.ToString("X8");
						array[4] = "）";
						throw new ServiceFailure(string.Concat(array));
					}
					Health health2 = await ProbeAsync(http, uri, project, (child == null) ? null : token, allowExternal, cancellation).ConfigureAwait(continueOnCapturedContext: false);
					if (!IsCurrent(revision, cancellation))
					{
						return;
					}
					switch (health2)
					{
					case Health.Ready:
						missed = 0;
						if (!ready)
						{
							ready = true;
							Log("lifecycle: ready; owned=" + (child != null));
							Publish(revision, TtsServicePhase.Ready, allowExternal ? "外部 TTS 已就绪" : "TTS 服务已就绪", child != null);
						}
						break;
					case Health.Failed:
					case Health.Unknown:
						if (allowExternal)
						{
							throw new ServiceFailure((health2 == Health.Unknown) ? "外部 TTS 健康检查格式不正确：/health 需返回 JSON，status 为 ready/loading/error" : "外部 TTS 返回错误状态（status=error）");
						}
						throw new ServiceFailure((health2 == Health.Unknown) ? "TTS 服务身份或端口冲突" : "TTS 服务模型加载失败");
					default:
						if (ready)
						{
							int num2 = missed + 1;
							missed = num2;
							if (num2 >= 3)
							{
								throw new ServiceFailure(allowExternal ? "外部 TTS 连接已中断（服务可能已退出）" : "TTS 服务连接已中断");
							}
						}
						break;
					}
					if (!ready && startupClock.Elapsed >= options.StartupTimeout)
					{
						throw new ServiceFailure(allowExternal ? "外部 TTS 启动超时（/health 一直不是 ready）" : "TTS 服务启动超时");
					}
					await Task.Delay(options.HealthPollInterval, cancellation).ConfigureAwait(continueOnCapturedContext: false);
				}
				goto end_IL_009e;
				IL_0428:
				child.Start();
				Publish(revision, TtsServicePhase.Starting, "TTS 服务启动中", isOwned: true);
				goto IL_044b;
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
			{
				Log("lifecycle: stop_requested");
				goto end_IL_009e;
			}
			catch (ServiceFailure serviceFailure)
			{
				failure = serviceFailure.Message;
				Log("lifecycle: failure_detected; " + serviceFailure.Message);
				goto end_IL_009e;
			}
			catch (Exception ex2)
			{
				Log("lifecycle: managed_failure; type=" + ex2.GetType().Name + "; hresult=0x" + ((uint)ex2.HResult).ToString("X8") + ((ex2 is Win32Exception ex3) ? ("; win32=" + ex3.NativeErrorCode) : ""));
				failure = "TTS 服务启动或运行失败，请检查本地运行环境";
				goto end_IL_009e;
			}
			end_IL_009e:;
		}
		finally
		{
			if (failure != null && child != null)
			{
				Publish(revision, TtsServicePhase.Stopping, "TTS 服务失败，正在完成进程清理", isOwned: true);
			}
			if (child != null)
			{
				child.RequestStop();
				try
				{
					await child.WaitForExitAsync(TimeSpan.FromSeconds(5.0)).ConfigureAwait(continueOnCapturedContext: false);
					_ = child.HasExited;
					Stopwatch startupClock = Stopwatch.StartNew();
					int missed = new Uri(url).Port;
					while (HasLocalListener(missed) && startupClock.Elapsed < TimeSpan.FromSeconds(1.0))
					{
						await Task.Delay(20).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				catch (Exception ex4)
				{
					Log("lifecycle: cleanup_error=" + ex4.GetType().Name);
				}
				while (!child.HasExited)
				{
					Publish(revision, TtsServicePhase.Stopping, "TTS 服务退出尚未确认，请查看诊断日志", isOwned: true);
					await Task.Delay(100).ConfigureAwait(continueOnCapturedContext: false);
				}
				exitConfirmed = true;
				bool ready = false;
				try
				{
					await child.DrainLogsAsync(TimeSpan.FromSeconds(3.0)).ConfigureAwait(continueOnCapturedContext: false);
					ready = true;
				}
				catch (Exception ex5)
				{
					Log("lifecycle: drain_error=" + ex5.GetType().Name);
				}
				int exitCode2 = child.ExitCode;
				string[] array2 = new string[8]
				{
					"lifecycle: child_retired; exit_confirmed=",
					exitConfirmed.ToString(),
					"; exit_code=",
					exitCode2.ToString(),
					"; exit_hex=0x",
					null,
					null,
					null
				};
				uint num3 = (uint)exitCode2;
				array2[5] = num3.ToString("X8");
				array2[6] = "; pipe_drain_completed=";
				array2[7] = ready.ToString();
				Log(string.Concat(array2));
				if (exitConfirmed)
				{
					child.Dispose();
					lock (gate)
					{
						if (owned == child)
						{
							owned = null;
						}
					}
				}
				else
				{
					failure = "TTS 服务退出尚未确认";
				}
			}
			Log("lifecycle: attempt_finished; failed=" + (failure != null) + "; exit_confirmed=" + exitConfirmed);
			bool flag = diagnostics?.WriteFailed ?? true;
			diagnostics?.Dispose();
			if (failure != null)
			{
				Publish(revision, exitConfirmed ? TtsServicePhase.Failed : TtsServicePhase.Stopping, failure + "；" + (flag ? "诊断日志写入失败，请检查 tts/logs 目录" : "详情见 tts/logs/managed-service.log"), !exitConfirmed);
			}
		}
		void Log(string text2)
		{
			text2 = TtsServiceManagerDiagnostics.SanitizeText(text2.Replace(token, "[instance]", StringComparison.Ordinal));
			AppendLog(text2, token);
			diagnostics?.Write(text2, child?.ProcessId);
		}
	}

	private async Task<Health> ProbeAsync(HttpClient http, Uri endpointUri, string project, string? instance, bool allowExternal, CancellationToken cancellation)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(options.HealthRequestTimeout);
		try
		{
			if (!HasLocalListener(endpointUri.Port))
			{
				return Health.Missing;
			}
			using HttpResponseMessage response = await http.GetAsync(new Uri(endpointUri, "/health"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 8192)
			{
				return Health.Unknown;
			}
			using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			byte[] bytes = new byte[8193];
			int count = 0;
			int num = 0;
			while (true)
			{
				bool flag = count < bytes.Length;
				if (flag)
				{
					flag = (num = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token).ConfigureAwait(continueOnCapturedContext: false)) > 0;
				}
				if (!flag)
				{
					break;
				}
				count += num;
			}
			if (count > 8192)
			{
				return Health.Unknown;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(bytes.AsMemory(0, count));
			JsonElement rootElement = jsonDocument.RootElement;
			if (Text(rootElement, "service") != "Just_Lilith.TTS" || Text(rootElement, "project_id") != project || Text(rootElement, "model_version") != "v4" || !Flag(rootElement, "gpu_only") || (instance != null && Text(rootElement, "instance_token") != instance))
			{
				return allowExternal ? ExternalHealth(rootElement) : Health.Unknown;
			}
			bool flag2;
			switch (Text(rootElement, "status"))
			{
			case "error":
				return Health.Failed;
			case "ready":
				return (Text(rootElement, "device") == "cuda" && Flag(rootElement, "model_loaded")) ? Health.Ready : Health.Failed;
			case "starting":
			case "loading":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			return flag2 ? Health.Loading : Health.Unknown;
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2) when (((ex2 is HttpRequestException || ex2 is IOException || ex2 is JsonException || ex2 is OperationCanceledException || ex2 is SocketException) ? 1 : 0) != 0)
		{
			return HasLocalListener(endpointUri.Port) ? Health.Unknown : Health.Missing;
		}
	}

	private static bool HasLocalListener(int port)
	{
		return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any((IPEndPoint listener) => listener.Port == port && (listener.Address.Equals(IPAddress.Loopback) || listener.Address.Equals(IPAddress.Any) || listener.Address.Equals(IPAddress.IPv6Any)));
	}

	private static Health ExternalHealth(JsonElement root)
	{
		switch (Text(root, "status"))
		{
		case "ready":
			return Health.Ready;
		case "starting":
		case "loading":
			return Health.Loading;
		case "error":
			return Health.Failed;
		default:
			return Health.Unknown;
		}
	}

	private static string Text(JsonElement root, string name)
	{
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return "";
		}
		return value.GetString() ?? "";
	}

	private static bool Flag(JsonElement root, string name)
	{
		if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
		{
			return value.ValueKind == JsonValueKind.True;
		}
		return false;
	}

	internal static string ProjectIdentity(string runtime)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(runtime).TrimEnd('\\', '/').Replace('\\', '/')
			.ToLowerInvariant()))).ToLowerInvariant();
	}

	private static string NormalizeEndpoint(string value)
	{
		Uri result = null;
		bool flag = string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out result) || result.Scheme != "http" || result.UserInfo.Length > 0 || result.Query.Length > 0 || result.Fragment.Length > 0 || result.AbsolutePath != "/";
		if (!flag)
		{
			int port = result.Port;
			flag = ((port < 1024 || port > 65535) ? true : false);
		}
		if (flag || (result.Host != "127.0.0.1" && !result.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
		{
			throw new ArgumentException("Expected the local IPv4 TTS endpoint.");
		}
		return "http://127.0.0.1:" + result.Port;
	}
}
