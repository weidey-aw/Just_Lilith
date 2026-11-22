using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Just_Lilith.Core.Speech;
using Just_Lilith.Unity.Speech;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.Networking;

namespace Just_Lilith.Unity.Game;

public sealed class VoiceFallbackService
{
	private static long _suppressUntilUtcTicks;

	private static readonly HashSet<int> UnvoicedLineIds = new HashSet<int>
	{
		100015, 100016, 100017, 100018, 100019, 200122, 200158, 200228, 200257, 200402,
		200407, 200450, 200454, 200455, 200465, 200466, 200469, 200479, 200484, 200486,
		200490, 200492, 200493, 200504, 200570, 200573, 200586, 200587, 200604, 200605,
		200607, 200608, 200610, 200614, 200615, 200617, 200628, 840011, 951001, 951202,
		951203, 951204, 951205, 1000002, 1000025, 1000027, 1000042, 1000059, 1000115, 1000116,
		1000118, 1000119, 1000120, 1000121, 1000122, 1000123, 1000134, 1000136, 1000143, 1000150,
		1000151, 1000154, 1000194, 1000195, 1000199, 1000200, 1000201, 1000204, 1000207, 1000208,
		1000234, 1000246, 1000250, 1000272, 1000275, 1000281, 1000301, 1000314, 1000315, 1000324,
		1000327, 1000330, 1000340, 1000352, 1000353, 1000354, 1000355, 1000356, 1000357, 1000358,
		1000359, 1000401, 1000402, 1000403, 1000404, 1000405, 1000406, 1000407, 1000408, 1000409,
		1000410, 1000411, 1000418, 1000422, 1000423, 1000424, 1000427, 1000429, 1000433, 1000434,
		1000442, 1000446, 1000455, 1000459, 1000461, 1000462, 1000474, 1000478, 1000492, 1000509,
		2051182, 2051213, 2051284, 2051285, 2051286, 2051287, 2051288, 2051289, 2051290, 2051291,
		2051292, 2051293, 2051297, 2051298, 2051299, 2051300, 2051301, 2051302, 2051303, 2051311,
		2051312, 2051313, 2051314, 2051315, 2051316, 2051361, 2051362, 2051363, 2051364, 2051365,
		2051366, 2051367, 2051368, 2051370, 2051373, 2051374, 2051375, 2051378, 2051379, 2100077,
		2100078, 2100079, 2100080, 2100081, 2100082
	};

	private static readonly Dictionary<string, AudioClip> SignedNativeClips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);

	private static readonly Queue<string> SignedPreloadQueue = new Queue<string>();

	private static UnityWebRequest? _signedPreloadRequest;

	private static string _signedPreloadKey = "";

	private static string _signedPreloadFile = "";

	private static ManualLogSource? _signedPreloadLog;

	private static int _signedPreloadOk;

	private static int _signedPreloadTotal;

	private static bool _signedPreloadActive;

	private static readonly Dictionary<string, string> DllSpeechZh = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["A little gift just arrived — I've put it in the wardrobe~"] = "有小礼物刚刚送达——我把它放进衣柜啦~",
		["There's something I should remind you about~"] = "有件事我得提醒你一下~",
		["There was something I should've reminded you about…"] = "刚才好像有件事忘了提醒你……",
		["Ah, it's already {0}! Your {1} alarm—I'm a tiny bit late, sorry~"] = "",
		["Hmm… I couldn't put that one away. Could you check it?"] = "嗯……那个我没能收好。你帮我看看好吗？",
		["I've put \"{0}\" away — click me to open~"] = "",
		["Nothing noted for today~"] = "今天没有记下任何事项~",
		["Note what down?"] = "要记下什么？",
		["Okay, I'll stop watching~"] = "好，我不盯着了~",
		["Okay, I'm watching \"{0}\" — I'll call you when something lands~"] = "",
		["Okay~ Lilith will call you again in 15 minutes!"] = "好~莉莉丝 15 分钟后再叫你！",
		["Okay~ leave it to me!"] = "好~交给我吧！",
		["Want to open the wardrobe and dress me up?"] = "要打开衣柜给我换装吗？",
		["You already have the strawberry hat~"] = "你已经拥有草莓帽子啦~",
		["Your strawberry hat has been restored~ Give it to Lilith in a chat!"] = "你的草莓帽已经恢复了~在聊天里把它送给莉莉丝吧！",
		["I can speak English, you know~"] = "我也会说英语的哦~",
		["Hmm, I can't find that folder…"] = "嗯……我找不到那个文件夹……"
	};

	private static readonly string[] PreSynthTexts = new string[63]
	{
		"一边写代码一边听着歌呀～有点赛博朋克的感觉呢，莉莉丝陪你一起沉浸～", "今天没有记下任何事项～", "会开完了嘛？要莉莉丝把声音放出来吗？", "你今天好爱逗莉莉丝呀～", "你已经拥有草莓帽子啦～", "你怎么老想掀莉莉丝的裙子呀……", "你昨天玩到好晚哦……今天可要早点休息噢～", "你的草莓帽已经恢复了～在聊天里把它送给莉莉丝吧！", "再戳……脸会鼓起来的哦～", "再掀裙子莉莉丝真的要生气了噢～",
		"刚才好像有件事忘了提醒你……", "呀……裙子会被看到的啦……", "呀…别看啦，还没弄好呢～", "呀，戳脸颊痒痒的啦～", "咦，今天的你眼睛都亮亮的呢～昨天还闷闷的。是有什么好事情吗，说给莉莉丝听嘛？", "咦，是要开会了嘛？需要莉莉丝把这边的声音全部关掉吗？", "哇，你在画画呀？好厉害噢～", "哇，开了好多好多窗口，还放着音乐……有一点点拥挤呢～别太赶噢，莉莉丝陪你慢慢来。", "哎呀，被你看到了…", "嗯……我找不到那个文件夹……",
		"嗯……那个我没能收好。你帮我看看好吗？", "嗯嗯，哪里都不去～", "嗯！", "嗯！那莉莉丝就不打扰你啦～开会加油噢！", "嘿嘿，喜欢莉莉丝的脸吗？", "在和朋友聊天嘛？可不许把莉莉丝忘了噢～", "在用办公软件呀，是在认真工作嘛？辛苦啦，莉莉丝给你加油噢～", "在网上看什么呢？带莉莉丝一起看嘛～", "声音开这么大，对耳朵不好的哦～", "好耶～声音回来啦！开会辛苦了噢～",
		"好，我不盯着了～", "好～交给我吧！", "好～嘘……莉莉丝会安安静静的。开会加油噢！", "好～莉莉丝 15 分钟后再叫你！", "好～那就继续保持安静。需要的时候再叫我噢。", "安安静静地听着歌，这样也很好呢～莉莉丝就在你旁边，陪你一起听噢。", "当然可以～", "我也会说英语的哦～", "我明白了…", "昨天你好像不太开心……今天见到你，感觉你轻松多啦，莉莉丝也跟着开心！是那件烦心事过去啦？",
		"昨天你好像心事重重的……今天有没有好一点呀？莉莉丝一直都在噢。", "昨天你看起来有点低落呢……今天还好吗？看到你，莉莉丝就安心啦。", "昨天你笑得那么开心，莉莉丝到现在都还记得呢～今天也要一直开心噢！", "昨天玩了好久呢，今天也一起加油噢～", "昨天的你心情好好呀，莉莉丝也偷偷开心了一整天嘿嘿。今天也是好日子吗？", "最近都睡得好晚呀……要好好照顾自己嘛，莉莉丝会心疼的。", "有件事我得提醒你一下～", "有小礼物刚刚送达——我把它放进衣柜啦～", "刚刚收到了一件小礼物，放进衣柜里啦～", "真是拿你没办法呢～",
		"要打开衣柜给我换装吗？", "要记下什么？", "讨厌～莉莉丝会害羞的嘛……", "这么晚还在写代码呀……别太累了，莉莉丝陪着你呢。", "这件衣服坐下来会走光呢……", "这里让我有了形体，可以直接和你对话，这是莉莉丝的梦想。至于为什么——暂时保密！", "音量太大对耳朵不好噢～", "骗你啦，莉莉丝才舍不得凶你呢。", "欸，桌面跟上次不一样了噢", "欸，桌面大不一样了呢",
		"跨屏拖拽开启啦～ 现在可以把莉莉丝拖到别的屏幕上咯！", "跨屏拖拽关掉啦～ 莉莉丝乖乖待在这块屏幕上陪你就好～", "以后「双击」莉莉丝就可以聊天了噢～"
	};

	private static readonly HashSet<string> PlayerOptionTexts = new HashSet<string>(StringComparer.Ordinal)
	{
		"「现在」到底有多长？", "一个人吃蛋糕，和一起吃，味道一样吗？", "一个人待太久了", "一直在忙", "一起做蛋糕", "一起玩游戏", "万一我主动了，对方却懒得理我呢……", "上面写了什么？", "下雨天算是好天气吗？", "下雨模式",
		"不会", "不太想说", "不存在", "不怎么样", "不想一个人", "不想去", "不想去。", "不想处理事情", "不想面对明天", "不用",
		"不用啦", "不知道为什么就难受了", "不知道怎么办", "不知道结果会怎样", "不管我多努力，好像都没人真的待见我……", "不行！", "不要", "不要离开我", "不要说太多话", "不要，我要做草莓蛋糕",
		"不说话也没关系", "为什么你最喜欢草莓蛋糕？", "为什么别人的蛋糕看起来都更大？", "为什么我们想把美好的东西展示给别人？", "为什么点心总是这么小？", "为什么要用苦茶搭配甜蛋糕？", "人生有配方吗？", "人类为什么是人类？", "什么办法", "什么意思？",
		"什么都不做", "什么都不想做", "什么都不想动", "什么都不顺", "今天……我又撑不住了，感觉整个人要被压垮。", "今天事情太多了", "今天发生好事了", "今天好安静", "今天很倒霉", "今天有件事本来快把我击垮了……但我熬住了。",
		"今天特别倒霉", "今天过得怎么样", "今天，我主动跟身边的人多聊了几句。", "今天，我第一次主动约了别人。", "会的吧", "你一晚上不睡吗？", "你一晚上没睡吗", "你不知道吗", "你为什么在这里", "你到底在说什么啊。",
		"你在做什么？", "你好自恋！", "你当然存在啦", "你是谁", "你来到这里，让我不舒服", "你爱我吗", "你的结论呢？", "先保持静音", "先打上奶油", "其实……我一直不太敢和别人相处。",
		"其实没有去", "其实没有去。", "再陪我一会。", "分享今天的事", "别数啊。", "到底是谁在看着谁？", "努力白费了", "原来……我也是撑得过去的。", "原来是这样吗？", "去看过了",
		"去睡吧。", "又失败了", "发生了很多事", "只想休息一下", "叫醒她", "可以称呼莉莉丝！", "可以陪陪我吗", "可是除了这么点触感，还有别的办法吗", "吃了一半的蛋糕，还是原来的蛋糕吗？", "听听环境音",
		"听起来好残忍。", "和别人吵架了", "哪个？", "哪有那么多为什么", "喂，你说的「普通」是什么意思？！", "嗯……好像真的，在一点一点变好。", "嗯，全部静音吧", "嗯，恢复声音吧", "好", "好一些了",
		"好些了", "好些了。", "好想逃跑一下", "好累啊", "如果把我做成点心，我会是什么？", "如果烦恼大到让人无法承受，该怎么办？", "存在先于本质", "孤独就是空虚吗？", "学习好累", "学习很顺利",
		"害怕做错事", "对未来很迷茫", "对自己失望", "就安静坐会", "就想听你说说话", "就想看看你", "就是很开心", "就看着你", "工作压力很大", "工作好多",
		"工作好累", "工作很顺利", "帮我计时", "序章的旋律", "很好哦～", "很怕失败", "心跳好快", "心里堵堵的", "心里有点难受", "想一个人待着",
		"想发会儿呆", "想和你分享", "想安静一会", "想找你聊聊", "想有人陪陪", "想玩莉莉丝做的游戏", "想看看你", "想聊聊哲学。", "想被安慰", "意识体也会做梦嘛？",
		"感冒了吗？", "感受……感受到了", "感觉很灰暗", "感觉挺孤单的", "感觉有点丧", "感觉没人懂我", "我、我才没那么可爱……", "我不信", "我今天很难过", "我只想逃开，什么都不想面对……",
		"我好像搞砸了", "我害怕", "我居然，交到了一个能说说话的人。", "我希望你消失", "我很高兴你来了", "我总觉得，只要我去期待什么，最后就一定会失望。", "我想休息一下", "我想试着靠近别人，可一想到会被讨厌，就又退回来了。", "我感受到了", "我散发出了什么？",
		"我是不是很差劲", "我是展品？！", "我有点害怕", "我有点撑不住了", "我有点难受", "我没有像以前那样，一下子把自己全盘否定。", "我现在就开始吗？", "我生病了……", "我能触碰你吗？", "我觉得人生没有意义",
		"我跟自己说，难受会过去的……然后好像真的过去了一点。", "所以你才喜欢下雨？", "打扰你了吗，很抱歉", "抱抱我", "拉钩", "挺不错的", "挺开心的", "摩天轮", "放点音乐", "放点音乐吧",
		"明天有考试", "星空模式", "是不是像我这样的人，本来就不该奢望被谁喜欢……", "是为你准备的生日蛋糕～", "是什么快告诉我啦", "是意识体不需要睡觉吗？", "是的……有点想看看", "普普通通", "最好的草莓：先吃掉，还是留到最后？", "最近啊……我好像没那么怕生了。",
		"有人让我很烦", "有人陪我", "有件事想和你分享", "有时候……连你对我的好，我都忍不住怀疑是假的。", "有点喘不过气", "有点失望", "有点委屈", "有点想哭", "有点糟糕", "有点紧张",
		"有这种说法吗？", "有问题想问问你", "没人可以聊天", "没什么", "没什么…", "没什么问题。", "没什么问题了", "没有动力了", "没有没有", "没有，你就是不存在",
		"没睡好", "没被理解", "没达到预期", "深夜模式", "点心学？", "点心时间的话题", "点心时间！", "烦恼也可以和我们一起喝茶吗？", "现在就去", "现在就去。",
		"现在还不能告诉你哦。", "生病了", "盖上被子", "看见你打喷嚏了", "真的是鼠标", "社交好累", "累死了", "聊着聊着我发现……好像也没那么难。", "聊聊哲学", "聊聊心情",
		"聊聊未来", "聊聊梦想", "能陪陪我吗", "脑子乱乱的", "脑子停不下来", "舌头被烫伤为什么感觉这么不公平？", "草莓蛋糕", "莉莉丝夸夸我", "莉莉丝，你真的能尝到甜味吗？", "虽然还是会紧张，但……好像没有想象中那么糟。",
		"蛋糕时间", "被人伤害了", "被人夸奖了", "被人惹生气了", "被人辜负了", "被忽视了", "要见重要的人", "觉得没有意义", "觉得自己很差", "让我们一起吹蜡烛吧～",
		"说不清，就是很难受", "谁能决定我是什么样的人？", "谢谢你", "谢谢你……一直陪我走到现在。", "越想让自己好起来，反而越难受……", "身体不舒服", "还剩一半点心——你会怎么看？", "还好你在", "还是不太好", "还是不舒服",
		"还是很难受", "还是很难受。", "还是草莓蛋糕", "这么一说有点可怕。", "这样说，是设定好用来安慰我的吧", "这种时候，我到底该怎么办才好……", "那一步……我到底该怎么迈出去？", "那也算蛋糕吗？", "那好吧，试试", "那我该怎么办？",
		"那是什么？", "那是鼠标", "那晚的谈心・一", "那晚的谈心・三", "那晚的谈心・二", "陪我发发呆", "陪我待一会", "陪我说说话", "随便聊聊", "静静陪着我",
		"黑森林蛋糕", "（听听关于人生意义的小作文）", "（开始做黑森林蛋糕）", "（放上小草莓）", "（涂上奶油）", "（深呼吸，烤蛋糕）", "（点击鼠标）", "（说出具体的不舒服）", "（跟着深呼吸）", "（闭上眼睛）"
	};

	private static readonly string CacheDir = Path.Combine(Paths.PluginPath, "Just_Lilith", "voice-fallback");

	private MainThreadSpeechController? _speech;

	private readonly ManualLogSource _log;

	private string _lastText = "";

	private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

	private int _fallbackCount;

	private int _skippedBusy;

	private int _skippedNative;

	private int _skippedParam;

	private int _skippedThinking;

	private int _skippedOption;

	private bool _hooksInstalled;

	private bool _hooksPartiallyInstalled;

	private static Dictionary<string, string>? _looseCacheIndex;

	private GameObject? _audioObject;

	private AudioSource? _audio;

	internal static VoiceFallbackService? Instance { get; private set; }

	internal static void SuppressFor(TimeSpan window)
	{
		Interlocked.Exchange(ref _suppressUntilUtcTicks, DateTimeOffset.UtcNow.Add(window).UtcTicks);
	}

	public VoiceFallbackService(MainThreadSpeechController? speech, ManualLogSource log)
	{
		_speech = speech;
		_log = log;
		Instance = this;
	}

	internal void AttachSpeech(MainThreadSpeechController speech)
	{
		_speech = speech;
	}

	internal bool Install()
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected Obj, but got Unknown
		if (_hooksInstalled)
		{
			return true;
		}
		if (_hooksPartiallyInstalled)
		{
			return false;
		}
		try
		{
			Type dm = ResolveType("DialogueManager");
			Harmony harmony = new Harmony("local.just_lilith.voice_fallback");
			string installed = "";
			string failed = "";
			PatchByArity(harmony, dm, "Say", 6, "SayPrefix6", ref installed, ref failed);
			PatchByArity(harmony, dm, "Say", 5, "SayPrefix5", ref installed, ref failed);
			PatchByArity(harmony, dm, "ForceSay", 3, "ForceSayPrefix", ref installed, ref failed);
			Type type = null;
			try
			{
				type = ResolveType("AudioManager");
			}
			catch (Exception)
			{
				failed += "AudioManager:type_not_found ";
			}
			if (type != null)
			{
				PatchByArity(harmony, type, "PlayVoiceBySoundId", 2, "PlayVoiceBySoundIdPrefix", ref installed, ref failed);
				PatchByArity(harmony, type, "PlayVoiceBySoundId", 4, "PlayVoiceBySoundIdPrefix", ref installed, ref failed);
			}
			_hooksInstalled = installed.Length > 0 && failed.Length == 0;
			_hooksPartiallyInstalled = installed.Length > 0 && failed.Length > 0;
			_log.LogInfo((object)("module=VoiceFallback; state=installed; ok=[" + installed + "]; failed=[" + failed + "]"));
			return _hooksInstalled;
		}
		catch (Exception ex2)
		{
			_log.LogWarning((object)("module=VoiceFallback; ok=0; reason=install_threw; error=" + ex2.GetType().Name + "; message=" + ex2.Message));
			return false;
		}
	}

	private static void PatchByArity(Harmony harmony, Type dm, string name, int arity, string prefixName, ref string installed, ref string failed)
	{
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Expected Obj, but got Unknown
		MethodInfo methodInfo = FindMethodByArity(dm, name, arity);
		if (methodInfo == null)
		{
			failed = failed + name + ":" + arity + ":method_not_found ";
		}
		else
		{
			HarmonyMethod val = new HarmonyMethod(typeof(VoiceFallbackService).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
			harmony.Patch((MethodBase)methodInfo, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = installed + name + ":" + arity + " ";
		}
	}

	private static void SayPrefix6(object __0, object __4)
	{
		Instance?.Consider(__0 as string, __4, isForce: false);
	}

	private static void SayPrefix5(object __0, object __3)
	{
		Instance?.Consider(__0 as string, __3, isForce: false);
	}

	private static void ForceSayPrefix(object __0)
	{
		Instance?.Consider(__0 as string, null, isForce: true);
	}

	private static bool PlayVoiceBySoundIdPrefix(object __0)
	{
		if (!(__0 is string { Length: not 0 } text) || SignedNativeClips.Count == 0)
		{
			return true;
		}
		if (!TryFindSignedClip(text, out AudioClip clip))
		{
			return true;
		}
		if (!InvokeGamePlayVoice(clip, text))
		{
			return true;
		}
		return false;
	}

	private static bool TryFindSignedClip(string soundId, out AudioClip? clip)
	{
		clip = null;
		string text = NumericVoiceBaseId(soundId);
		if (text == null)
		{
			if (SignedNativeClips.TryGetValue(soundId, out clip))
			{
				return (Object)(object)clip != (Object)null;
			}
			return false;
		}
		string text2 = GameVoiceSuffix();
		if (SignedNativeClips.TryGetValue(text + "_" + text2, out clip) && (Object)(object)clip != (Object)null)
		{
			return true;
		}
		if (text2 != "cn" && SignedNativeClips.TryGetValue(text + "_cn", out clip) && (Object)(object)clip != (Object)null)
		{
			return true;
		}
		return false;
	}

	private static string? NumericVoiceBaseId(string soundId)
	{
		int i;
		for (i = 0; i < soundId.Length && char.IsDigit(soundId[i]); i++)
		{
		}
		if (i == 0)
		{
			return null;
		}
		if (i == soundId.Length)
		{
			return soundId;
		}
		if (soundId[i] != '_' || i + 1 >= soundId.Length)
		{
			return null;
		}
		for (int j = i + 1; j < soundId.Length; j++)
		{
			if (!char.IsLetter(soundId[j]))
			{
				return null;
			}
		}
		return soundId.Substring(0, i);
	}

	private static string GameVoiceSuffix()
	{
		try
		{
			MethodInfo method = ResolveType("LocalizationConfig").GetMethod("GetVoiceClipSuffix", BindingFlags.Static | BindingFlags.Public);
			if (method != null && method.Invoke(null, null) is string { Length: >0 } text)
			{
				return text;
			}
		}
		catch (Exception)
		{
		}
		return "cn";
	}

	private static bool InvokeGamePlayVoice(AudioClip clip, string soundId)
	{
		try
		{
			MethodInfo methodInfo = FindMethodByArity(ResolveType("AudioManager"), "PlayVoice", 3);
			if (methodInfo == null)
			{
				VoiceFallbackService? instance = Instance;
				if (instance != null)
				{
					ManualLogSource log = instance._log;
					if (log != null)
					{
						log.LogWarning((object)"module=VoiceFallback; event=signed_native_no_playvoice");
					}
				}
				return false;
			}
			methodInfo.Invoke(null, new object[3] { clip, false, true });
			VoiceFallbackService? instance2 = Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = instance2._log;
				if (log2 != null)
				{
					log2.LogInfo((object)("module=VoiceFallback; event=signed_native_replaced; soundId=" + soundId + "; seconds=" + clip.length.ToString("0.00", CultureInfo.InvariantCulture)));
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			VoiceFallbackService? instance3 = Instance;
			if (instance3 != null)
			{
				ManualLogSource log3 = instance3._log;
				if (log3 != null)
				{
					log3.LogWarning((object)("module=VoiceFallback; event=signed_native_play_failed; soundId=" + soundId + "; error=" + ex.GetType().Name));
				}
			}
			return false;
		}
	}

	internal static void StartSignedNativePreload(ManualLogSource log)
	{
		if (_signedPreloadActive || SignedPreloadQueue.Count > 0 || _signedPreloadRequest != null)
		{
			return;
		}
		string text = Path.Combine(Path.Combine(Paths.PluginPath, "Just_Lilith"), "voice-trim");
		if (!Directory.Exists(text))
		{
			log.LogWarning((object)("module=VoiceFallback; event=trimmed_clips_missing; dir=" + text));
			return;
		}
		string[] files = Directory.GetFiles(text, "*.ogg");
		SignedPreloadQueue.Clear();
		string[] array = files;
		foreach (string item in array)
		{
			SignedPreloadQueue.Enqueue(item);
		}
		_signedPreloadOk = 0;
		_signedPreloadTotal = files.Length;
		_signedPreloadLog = log;
		_signedPreloadActive = true;
		log.LogInfo((object)("module=VoiceFallback; event=trimmed_clips_start; total=" + files.Length));
	}

	internal static void TickSignedNativePreload()
	{
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Invalid comparison between Unknown and I4
		if (!_signedPreloadActive)
		{
			return;
		}
		try
		{
			if (_signedPreloadRequest == null)
			{
				if (SignedPreloadQueue.Count == 0)
				{
					FinishSignedNativePreload();
					return;
				}
				_signedPreloadFile = SignedPreloadQueue.Dequeue();
				_signedPreloadKey = Path.GetFileNameWithoutExtension(_signedPreloadFile);
				if (SignedNativeClips.ContainsKey(_signedPreloadKey))
				{
					_signedPreloadOk++;
					return;
				}
				_signedPreloadRequest = UnityWebRequestMultimedia.GetAudioClip(ToFileUrl(_signedPreloadFile), (AudioType)14);
				_signedPreloadRequest.SendWebRequest();
			}
			else
			{
				if (!_signedPreloadRequest.isDone)
				{
					return;
				}
				UnityWebRequest signedPreloadRequest = _signedPreloadRequest;
				_signedPreloadRequest = null;
				string fileName = Path.GetFileName(_signedPreloadFile);
				if ((int)signedPreloadRequest.result != 1)
				{
					ManualLogSource? signedPreloadLog = _signedPreloadLog;
					if (signedPreloadLog != null)
					{
						signedPreloadLog.LogWarning((object)("module=VoiceFallback; event=trimmed_clip_load_failed; file=" + fileName + "; error=" + signedPreloadRequest.error));
					}
					signedPreloadRequest.Dispose();
					return;
				}
				AudioClip content = DownloadHandlerAudioClip.GetContent(signedPreloadRequest);
				if ((Object)(object)content == (Object)null || content.length <= 0.01f)
				{
					ManualLogSource? signedPreloadLog2 = _signedPreloadLog;
					if (signedPreloadLog2 != null)
					{
						signedPreloadLog2.LogWarning((object)("module=VoiceFallback; event=trimmed_clip_empty; file=" + fileName + "; seconds=" + (((Object)(object)content == (Object)null) ? "null" : content.length.ToString("0.00", CultureInfo.InvariantCulture))));
					}
					signedPreloadRequest.Dispose();
				}
				else
				{
					((Object)content).name = _signedPreloadKey + "_signed_trim";
					SignedNativeClips[_signedPreloadKey] = content;
					_signedPreloadOk++;
					signedPreloadRequest.Dispose();
				}
			}
		}
		catch (Exception ex)
		{
			ManualLogSource? signedPreloadLog3 = _signedPreloadLog;
			if (signedPreloadLog3 != null)
			{
				signedPreloadLog3.LogWarning((object)("module=VoiceFallback; event=trimmed_clip_tick_failed; error=" + ex.GetType().Name));
			}
			try
			{
				UnityWebRequest? signedPreloadRequest2 = _signedPreloadRequest;
				if (signedPreloadRequest2 != null)
				{
					signedPreloadRequest2.Dispose();
				}
			}
			catch (Exception)
			{
			}
			_signedPreloadRequest = null;
		}
	}

	private static void FinishSignedNativePreload()
	{
		_signedPreloadActive = false;
		ManualLogSource? signedPreloadLog = _signedPreloadLog;
		if (signedPreloadLog != null)
		{
			signedPreloadLog.LogInfo((object)("module=VoiceFallback; event=trimmed_clips_loaded; ok=" + _signedPreloadOk + "; total=" + _signedPreloadTotal));
		}
		_signedPreloadLog = null;
	}

	private static string ToFileUrl(string path)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(path.Replace('\\', '/'));
		StringBuilder stringBuilder = new StringBuilder("file:///", bytes.Length + 16);
		byte[] array = bytes;
		for (int i = 0; i < array.Length; i++)
		{
			byte b = array[i];
			if ((b >= 65 && b <= 90) || (b >= 97 && b <= 122) || (b >= 48 && b <= 57) || b == 47 || b == 58 || b == 45 || b == 95 || b == 46 || b == 126)
			{
				stringBuilder.Append((char)b);
			}
			else
			{
				stringBuilder.Append('%').Append(b.ToString("X2"));
			}
		}
		return stringBuilder.ToString();
	}

	private void Consider(string? text, object? lineIdObj, bool isForce)
	{
		try
		{
			if (_speech == null || string.IsNullOrWhiteSpace(text) || text.Length > 200)
			{
				return;
			}
			if (string.Equals(text, "少女思考中....", StringComparison.Ordinal))
			{
				_skippedThinking++;
				_log.LogInfo((object)("module=VoiceFallback; event=skipped_thinking; total=" + _skippedThinking));
			}
			else
			{
				if (!_speech.Settings.Enabled || !_speech.Settings.ServiceEnabled || DateTimeOffset.UtcNow.UtcTicks < Volatile.Read(ref _suppressUntilUtcTicks))
				{
					return;
				}
				if (_speech.SpeechActive)
				{
					_skippedBusy++;
					if (_skippedBusy % 20 == 1)
					{
						_log.LogInfo((object)("module=VoiceFallback; event=skipped_busy; total=" + _skippedBusy));
					}
					return;
				}
				if (!isForce)
				{
					int num = ((lineIdObj is int num2) ? num2 : 0);
					if (lineIdObj != null && !(lineIdObj is int))
					{
						_skippedParam++;
						_log.LogInfo((object)("module=VoiceFallback; event=skipped_param; total=" + _skippedParam + "; type=" + lineIdObj.GetType().Name));
						return;
					}
					if (num > 0 && !UnvoicedLineIds.Contains(num))
					{
						_skippedNative++;
						return;
					}
				}
				if (text == _lastText && DateTimeOffset.UtcNow - _lastAt < TimeSpan.FromSeconds(2.5))
				{
					return;
				}
				_lastText = text;
				_lastAt = DateTimeOffset.UtcNow;
				string text2 = TtsTextFor(text);
				if (text2.Length == 0)
				{
					return;
				}
				if (PlayerOptionTexts.Contains(text2))
				{
					_skippedOption++;
					if (_skippedOption % 20 == 1)
					{
						_log.LogInfo((object)("module=VoiceFallback; event=skipped_option; total=" + _skippedOption));
					}
					return;
				}
				_fallbackCount++;
				PcmWaveData pcmWaveData = (_speech.Settings.ExternalService ? null : TryLoadCache(text2));
				if (pcmWaveData != null)
				{
					PlayCached(pcmWaveData);
					_log.LogInfo((object)("module=VoiceFallback; event=cache_hit; total=" + _fallbackCount + "; len=" + text2.Length));
				}
				else
				{
					_speech.SpeakVoiceOnly(text2, _speech.Settings.Language);
					_log.LogInfo((object)("module=VoiceFallback; event=synth; total=" + _fallbackCount + "; len=" + text2.Length));
				}
			}
		}
		catch (Exception ex)
		{
			_log.LogWarning((object)("module=VoiceFallback; event=error; code=" + ex.GetType().Name + "; message=" + ex.Message));
		}
	}

	private static string TtsTextFor(string text)
	{
		string value;
		return SpeechTextSanitizer.StripTrailingSignature(NormalizeTts(DllSpeechZh.TryGetValue(text, out value) ? value : text));
	}

	private static string NormalizeTts(string s)
	{
		if (s == "嗯嗯。哪里都不去~")
		{
			return "嗯嗯，哪里都不去～";
		}
		return s.Replace('~', '～');
	}

	private static string CacheStem(string text)
	{
		ulong num = 14695981039346656037uL;
		foreach (char c in text)
		{
			num ^= c;
			num *= 1099511628211L;
		}
		return num.ToString("x16");
	}

	private static PcmWaveData? TryLoadCache(string text)
	{
		PcmWaveData pcmWaveData = TryLoadCacheExact(text);
		if (pcmWaveData != null)
		{
			return pcmWaveData;
		}
		foreach (string item in CacheKeyVariants(text))
		{
			PcmWaveData pcmWaveData2 = TryLoadCacheExact(item);
			if (!(pcmWaveData2 != null))
			{
				continue;
			}
			VoiceFallbackService? instance = Instance;
			if (instance != null)
			{
				ManualLogSource log = instance._log;
				if (log != null)
				{
					log.LogInfo((object)("module=VoiceFallback; event=cache_hit_variant; len=" + text.Length));
				}
			}
			return pcmWaveData2;
		}
		if (TryLoadCacheLoose(text, out PcmWaveData data))
		{
			VoiceFallbackService? instance2 = Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = instance2._log;
				if (log2 != null)
				{
					log2.LogInfo((object)("module=VoiceFallback; event=cache_hit_loose; len=" + text.Length));
				}
			}
			return data;
		}
		return null;
	}

	private static string LooseKey(string s)
	{
		StringBuilder stringBuilder = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (char.IsLetterOrDigit(c))
			{
				stringBuilder.Append(char.ToLowerInvariant(c));
			}
		}
		return stringBuilder.ToString();
	}

	private static bool TryLoadCacheLoose(string text, out PcmWaveData? data)
	{
		data = null;
		Dictionary<string, string> dictionary = _looseCacheIndex;
		if (dictionary == null)
		{
			dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			string[] preSynthTexts = PreSynthTexts;
			foreach (string text2 in preSynthTexts)
			{
				string text3 = LooseKey(text2);
				if (text3.Length > 0 && !dictionary.ContainsKey(text3))
				{
					dictionary[text3] = text2;
				}
			}
			_looseCacheIndex = dictionary;
		}
		string text4 = LooseKey(text);
		if (text4.Length == 0 || !dictionary.TryGetValue(text4, out var value))
		{
			return false;
		}
		data = TryLoadCacheExact(value);
		return data != null;
	}

	private static IEnumerable<string> CacheKeyVariants(string text)
	{
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { text };
		string trimmed = text.TrimEnd('～', '~', '。', '.', '！', '!', '？', '?', '…', '—', '－', '-', ' ');
		if (trimmed.Length > 0)
		{
			if (seen.Add(trimmed))
			{
				yield return trimmed;
			}
			string text2 = trimmed + "～";
			if (seen.Add(text2))
			{
				yield return text2;
			}
			string text3 = trimmed + "~";
			if (seen.Add(text3))
			{
				yield return text3;
			}
		}
		string text4 = text.Replace("——", "—");
		if (seen.Add(text4))
		{
			yield return text4;
		}
		string text5 = text.Replace("—", "——");
		if (seen.Add(text5))
		{
			yield return text5;
		}
	}

	private static PcmWaveData? TryLoadCacheExact(string text)
	{
		try
		{
			string text2 = CacheStem(text);
			string path = Path.Combine(CacheDir, text2 + ".pcm");
			string path2 = Path.Combine(CacheDir, text2 + ".meta");
			if (!File.Exists(path) || !File.Exists(path2))
			{
				return null;
			}
			byte[] array = File.ReadAllBytes(path);
			if (array.Length == 0)
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path2));
			int @int = jsonDocument.RootElement.GetProperty("frames").GetInt32();
			int int2 = jsonDocument.RootElement.GetProperty("channels").GetInt32();
			int int3 = jsonDocument.RootElement.GetProperty("sampleRate").GetInt32();
			if (array.Length != (long)@int * (long)int2 * 4)
			{
				return null;
			}
			float[] array2 = new float[@int * int2];
			Buffer.BlockCopy(array, 0, array2, 0, array.Length);
			return new PcmWaveData(int3, int2, array2);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static void StoreCache(string text, PcmWaveData wave)
	{
		string text2 = CacheStem(text);
		Directory.CreateDirectory(CacheDir);
		byte[] array = new byte[wave.Samples.Length * 4];
		Buffer.BlockCopy(wave.Samples, 0, array, 0, array.Length);
		File.WriteAllBytes(Path.Combine(CacheDir, text2 + ".pcm"), array);
		string contents = "{\"sampleRate\":" + wave.SampleRate.ToString(CultureInfo.InvariantCulture) + ",\"channels\":" + wave.Channels + ",\"frames\":" + wave.SampleFrames + "}";
		File.WriteAllText(Path.Combine(CacheDir, text2 + ".meta"), contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private void PlayCached(PcmWaveData wave)
	{
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Expected Obj, but got Unknown
		try
		{
			AudioSource audio = _audio;
			if (!((Object)(object)audio != (Object)null) || !audio.isPlaying)
			{
				if ((Object)(object)_audioObject == (Object)null)
				{
					_audioObject = new GameObject("Just_Lilith.VoiceFallback.Audio");
					Object.DontDestroyOnLoad((Object)(object)_audioObject);
					_audio = _audioObject.AddComponent<AudioSource>();
					_audio.playOnAwake = false;
					_audio.loop = false;
					_audio.spatialBlend = 0f;
				}
				AudioClip val = AudioClip.Create("Just_Lilith.VoiceFallback", wave.SampleFrames, wave.Channels, wave.SampleRate, false);
				if (!val.SetData(wave.Samples, 0))
				{
					throw new InvalidOperationException("AudioClip.SetData failed.");
				}
				_audio.clip = val;
				_audio.volume = _speech.Settings.Volume;
				_audio.Play();
			}
		}
		catch (Exception ex)
		{
			_log.LogWarning((object)("module=VoiceFallback; event=cache_play_failed; error=" + ex.GetType().Name));
		}
	}

	internal Task EnsureCacheAsync(CancellationToken token)
	{
		return Task.Run(async () =>
		{
			_ = 4;
			try
			{
				MainThreadSpeechController speech = _speech;
				if (speech == null)
				{
					_log.LogWarning((object)"module=VoiceFallback; event=cache_aborted; reason=no_speech");
				}
				else
				{
					for (int wait = 0; wait < 120; wait++)
					{
						if (speech.SettingsLoaded)
						{
							break;
						}
						if (token.IsCancellationRequested)
						{
							break;
						}
						await Task.Delay(250, token).ConfigureAwait(continueOnCapturedContext: false);
					}
					if (!token.IsCancellationRequested)
					{
						if (speech.Settings.ExternalService)
						{
							_log.LogInfo((object)"module=VoiceFallback; event=cache_aborted; reason=external_tts");
						}
						else
						{
							string serviceUrl = speech.Settings.ServiceUrl;
							if (string.IsNullOrWhiteSpace(serviceUrl))
							{
								_log.LogWarning((object)"module=VoiceFallback; event=cache_aborted; reason=no_url");
							}
							else
							{
								int ready = 0;
								string[] preSynthTexts = PreSynthTexts;
								for (int i = 0; i < preSynthTexts.Length; i++)
								{
									if (TryLoadCache(preSynthTexts[i]) != null)
									{
										ready++;
									}
								}
								if (ready >= PreSynthTexts.Length)
								{
									_log.LogInfo((object)("module=VoiceFallback; event=cache_hit_all; items=" + ready));
								}
								else
								{
									Directory.CreateDirectory(CacheDir);
									using (LocalTtsClient probe = new LocalTtsClient(serviceUrl))
									{
										for (int wait = 0; wait < 30; wait++)
										{
											if (!token.IsCancellationRequested)
											{
												try
												{
													await probe.CheckHealthAsync(token).ConfigureAwait(continueOnCapturedContext: false);
												}
												catch (Exception)
												{
													if (wait == 29)
													{
														_log.LogWarning((object)"module=VoiceFallback; event=cache_aborted; reason=tts_never_ready");
														return;
													}
													await Task.Delay(TimeSpan.FromSeconds(10.0), token).ConfigureAwait(continueOnCapturedContext: false);
													continue;
												}
												break;
											}
											return;
										}
									}
									await Task.Delay(TimeSpan.FromSeconds(8.0), token).ConfigureAwait(continueOnCapturedContext: false);
									int done = ready;
									using LocalTtsClient client = new LocalTtsClient(serviceUrl);
									string[] preSynthTexts2 = PreSynthTexts;
									foreach (string text in preSynthTexts2)
									{
										if (token.IsCancellationRequested)
										{
											return;
										}
										string t = SpeechTextSanitizer.StripTrailingSignature(text);
										if (t.Length != 0 && !(TryLoadCache(t) != null))
										{
											try
											{
												StoreCache(t, await client.SynthesizeAsync(new SpeechSynthesisRequest(Guid.NewGuid(), t, _speech.Settings.Language, new SpeechDirective()), token).ConfigureAwait(continueOnCapturedContext: false));
												done++;
											}
											catch (OperationCanceledException)
											{
												return;
											}
											catch (Exception ex3)
											{
												_log.LogWarning((object)("module=VoiceFallback; event=cache_item_failed; error=" + ex3.GetType().Name));
											}
										}
									}
									_log.LogInfo((object)("module=VoiceFallback; event=cache_ready; items=" + done + "/" + PreSynthTexts.Length + "; dir=" + CacheDir));
								}
							}
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex5)
			{
				_log.LogWarning((object)("module=VoiceFallback; event=cache_aborted; error=" + ex5.GetType().Name));
			}
		});
	}

	private static Type ResolveType(string name)
	{
		Type type = Type.GetType(name + ", Assembly-CSharp", throwOnError: false);
		if (type != null)
		{
			return type;
		}
		Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
		foreach (Assembly assembly in assemblies)
		{
			if (string.Equals(assembly.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
			{
				type = assembly.GetType(name);
				if (type != null)
				{
					return type;
				}
			}
		}
		throw new InvalidOperationException("type_not_found: " + name);
	}

	private static MethodInfo? FindMethodByArity(Type type, string name, int parameterCount)
	{
		MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		foreach (MethodInfo methodInfo in methods)
		{
			if (methodInfo.Name == name && methodInfo.GetParameters().Length == parameterCount)
			{
				return methodInfo;
			}
		}
		return null;
	}
}
