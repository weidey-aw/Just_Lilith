using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace Just_Lilith.Unity.Game;

internal sealed class TalkActionDriver
{
	private readonly ManualLogSource _log;

	private MethodInfo? _playTalkExpression;

	private object? _dialogueManager;

	private MethodInfo? _stopTalkExpression;

	private object? _characterController;

	private MethodInfo? _setLilithSpeaking;

	private bool _resolved;

	private bool _resolveFailed;

	private DialogueNode? _activeNode;

	private float _replayAt;

	private float _activeUntil;

	private bool _speakingSignalOn;

	public TalkActionDriver(ManualLogSource log)
	{
		_log = log;
	}

	public void Tick(float unscaledTime)
	{
		if (!_resolved)
		{
			TryResolve();
		}
		else if (_activeNode != null)
		{
			if (unscaledTime >= _activeUntil)
			{
				Finish();
			}
			else if (unscaledTime >= _replayAt)
			{
				_replayAt = unscaledTime + 2.5f;
				InvokePlay(_activeNode);
			}
		}
	}

	public void OnSpeechStarted(string text, float durationSeconds)
	{
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Expected Obj, but got Unknown
		if (!_resolved)
		{
			TryResolve();
		}
		if (!_resolved || string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			DialogueNode val = new DialogueNode();
			val.text = text;
			_activeNode = val;
			_replayAt = Time.unscaledTime + 2.5f;
			_activeUntil = Time.unscaledTime + Math.Max(0.5f, durationSeconds);
			InvokePlay(val);
			SetSpeaking(speaking: true);
		}
		catch (Exception ex)
		{
			_log.LogWarning((object)("module=TalkAction; event=start_failed; code=" + ex.GetType().Name));
		}
	}

	public void Finish()
	{
		if (_activeNode == null && !_speakingSignalOn)
		{
			return;
		}
		_activeNode = null;
		try
		{
			if (_speakingSignalOn)
			{
				SetSpeaking(speaking: false);
			}
			if ((object)_stopTalkExpression != null && _characterController != null)
			{
				_stopTalkExpression.Invoke(_characterController, new object[1] { -1f });
			}
		}
		catch (Exception ex)
		{
			_log.LogWarning((object)("module=TalkAction; event=finish_failed; code=" + ex.GetType().Name));
		}
	}

	private void InvokePlay(DialogueNode node)
	{
		if ((object)_playTalkExpression != null && _dialogueManager != null)
		{
			_playTalkExpression.Invoke(_dialogueManager, new object[1] { node });
		}
	}

	private void SetSpeaking(bool speaking)
	{
		if ((object)_setLilithSpeaking != null)
		{
			_setLilithSpeaking.Invoke(null, new object[1] { speaking });
			_speakingSignalOn = speaking;
		}
	}

	private void TryResolve()
	{
		if (_resolveFailed)
		{
			return;
		}
		try
		{
			if ((object)_playTalkExpression == null && UnityRuntimeObjectLocator.TryFind("DialogueManager, Assembly-CSharp", out var located))
			{
				_dialogueManager = located.Instance;
				_playTalkExpression = FindMethodByArity(located.ReflectedType, "PlayTalkExpression", 1);
			}
			if ((object)_stopTalkExpression == null && UnityRuntimeObjectLocator.TryFind("CharacterController, Assembly-CSharp", out var located2))
			{
				_characterController = located2.Instance;
				_stopTalkExpression = FindMethodByArity(located2.ReflectedType, "StopTalkExpression", 1);
			}
			if ((object)_setLilithSpeaking == null)
			{
				Type type = GameTypeResolver.ResolveLoadedType("DialogueBubbleUI, Assembly-CSharp");
				if ((object)type != null)
				{
					_setLilithSpeaking = FindMethodByArity(type, "SetLilithSpeaking", 1);
				}
				else
				{
					_resolveFailed = true;
				}
			}
			_resolved = (object)_playTalkExpression != null && (object)_stopTalkExpression != null && (object)_setLilithSpeaking != null;
			if (_resolved)
			{
				_log.LogInfo((object)"module=TalkAction; event=resolved");
			}
		}
		catch (Exception ex)
		{
			_log.LogWarning((object)("module=TalkAction; event=resolve_failed; code=" + ex.GetType().Name));
		}
	}

	private static MethodInfo? FindMethodByArity(Type type, string name, int parameterCount)
	{
		try
		{
			MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (methodInfo.Name == name && methodInfo.GetParameters().Length == parameterCount)
				{
					return methodInfo;
				}
			}
		}
		catch (Exception)
		{
		}
		return null;
	}
}
