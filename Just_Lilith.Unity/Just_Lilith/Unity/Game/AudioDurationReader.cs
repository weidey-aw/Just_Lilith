using System;
using System.IO;
using System.Text;

namespace Just_Lilith.Unity.Game;

internal static class AudioDurationReader
{
	public static float Read(string path)
	{
		try
		{
			using FileStream fileStream = File.OpenRead(path);
			long length = fileStream.Length;
			int num = (int)Math.Min(length, 1048576L);
			byte[] array = new byte[num];
			int i;
			int num2;
			for (i = 0; i < num; i += num2)
			{
				num2 = fileStream.Read(array, i, num - i);
				if (num2 <= 0)
				{
					break;
				}
			}
			if (i < 16)
			{
				return 0f;
			}
			if (i >= 12 && array[0] == 82 && array[1] == 73 && array[2] == 70 && array[3] == 70 && array[8] == 87 && array[9] == 65 && array[10] == 86 && array[11] == 69)
			{
				return WavDuration(array, i);
			}
			if ((i >= 3 && array[0] == 73 && array[1] == 68 && array[2] == 51) || (array[0] == byte.MaxValue && (array[1] & 0xE0) == 224))
			{
				return Mp3Duration(array, i, length);
			}
			if (array[0] == 102 && array[1] == 76 && array[2] == 97 && array[3] == 67)
			{
				return FlacDuration(array, i);
			}
			if (i >= 12 && array[4] == 102 && array[5] == 116 && array[6] == 121 && array[7] == 112)
			{
				return Mp4Duration(path, length);
			}
		}
		catch
		{
		}
		return 0f;
	}

	private static float WavDuration(byte[] buf, int got)
	{
		int num = IndexOfBytes(buf, Encoding.ASCII.GetBytes("fmt "));
		int num2 = IndexOfBytes(buf, Encoding.ASCII.GetBytes("data"));
		if (num < 0 || num2 < 0 || num + 20 > got || num2 + 8 > got)
		{
			return 0f;
		}
		int num3 = BitConverter.ToInt32(buf, num + 16);
		uint num4 = BitConverter.ToUInt32(buf, num2 + 4);
		if (num3 > 0 && num4 != 0)
		{
			return (float)num4 / (float)num3;
		}
		return 0f;
	}

	private static float Mp3Duration(byte[] buf, int got, long len)
	{
		long num = 0L;
		if (got >= 10 && buf[0] == 73 && buf[1] == 68 && buf[2] == 51)
		{
			num = 10 + (((long)(buf[6] & 0x7F) << 21) | ((long)(buf[7] & 0x7F) << 14) | ((long)(buf[8] & 0x7F) << 7) | (buf[9] & 0x7F));
		}
		int i;
		for (i = (int)Math.Min(num, got - 4); i + 4 <= got && (buf[i] != byte.MaxValue || (buf[i + 1] & 0xE0) != 224); i++)
		{
		}
		if (i + 4 > got)
		{
			return 0f;
		}
		byte b = buf[i + 1];
		int num2 = buf[i + 2];
		int num3 = (b >> 3) & 3;
		int num4 = (b >> 1) & 3;
		bool flag = (b & 1) == 0;
		if (num3 == 1 || num4 == 0)
		{
			return 0f;
		}
		int num5 = (num2 >> 4) & 0xF;
		int num6 = (num2 >> 2) & 3;
		int num7 = (buf[i + 3] >> 6) & 3;
		int[,] array = new int[5, 16]
		{
			{
				0, 32, 64, 96, 128, 160, 192, 224, 256, 288,
				320, 352, 384, 416, 448, 0
			},
			{
				0, 32, 48, 56, 64, 80, 96, 112, 128, 160,
				192, 224, 256, 320, 384, 0
			},
			{
				0, 32, 40, 48, 56, 64, 80, 96, 112, 128,
				160, 192, 224, 256, 320, 0
			},
			{
				0, 32, 48, 56, 64, 80, 96, 112, 128, 160,
				192, 224, 256, 320, 384, 0
			},
			{
				0, 8, 16, 24, 32, 40, 48, 56, 64, 80,
				96, 112, 128, 144, 160, 0
			}
		};
		int num8;
		if (num3 == 3)
		{
			num8 = 3 - num4;
		}
		else
		{
			num8 = ((num4 == 1) ? 3 : 4);
		}
		int num9 = array[num8, num5] * 1000;
		int num10 = (num3 switch
		{
			2 => new int[4] { 22050, 24000, 16000, 0 }, 
			3 => new int[4] { 44100, 48000, 32000, 0 }, 
			_ => new int[4] { 11025, 12000, 8000, 0 }, 
		})[num6];
		if (num9 <= 0 || num10 <= 0)
		{
			return 0f;
		}
		int num11;
		if (num4 == 3)
		{
			num11 = 384;
		}
		else
		{
			num11 = ((num3 == 3) ? 1152 : 576);
		}
		int num12;
		if (num3 != 3)
		{
			num12 = ((num7 == 3) ? 9 : 17);
		}
		else
		{
			num12 = ((num7 == 3) ? 17 : 32);
		}
		int num13 = i + 4 + (flag ? 2 : 0) + num12;
		bool num14 = num13 + 12 <= got && buf[num13] == 88 && buf[num13 + 1] == 105 && buf[num13 + 2] == 110 && buf[num13 + 3] == 103;
		bool flag2 = num13 + 12 <= got && buf[num13] == 73 && buf[num13 + 1] == 110 && buf[num13 + 2] == 102 && buf[num13 + 3] == 111;
		if ((num14 | flag2) && (((buf[num13 + 4] << 24) | (buf[num13 + 5] << 16) | (buf[num13 + 6] << 8) | buf[num13 + 7]) & 1) != 0)
		{
			int num15 = (buf[num13 + 8] << 24) | (buf[num13 + 9] << 16) | (buf[num13 + 10] << 8) | buf[num13 + 11];
			if (num15 > 0)
			{
				return (float)num15 * (float)num11 / (float)num10;
			}
		}
		long num16 = len - num;
		if (num16 > 0)
		{
			return (float)((double)num16 * 8.0 / (double)num9);
		}
		return 0f;
	}

	private static float FlacDuration(byte[] buf, int got)
	{
		int num = 4;
		while (num + 4 <= got)
		{
			bool flag = (buf[num] & 0x80) != 0;
			int num2 = buf[num] & 0x7F;
			int num3 = (buf[num + 1] << 16) | (buf[num + 2] << 8) | buf[num + 3];
			num += 4;
			if (num2 == 0)
			{
				if (num + 18 > got)
				{
					return 0f;
				}
				long num4 = 0L;
				for (int i = 10; i < 18; i++)
				{
					num4 = (num4 << 8) | buf[num + i];
				}
				int num5 = (int)((num4 >> 44) & 0xFFFFF);
				long num6 = num4 & 0xFFFFFFFFFL;
				if (num5 > 0 && num6 > 0)
				{
					return (float)((double)num6 / (double)num5);
				}
				return 0f;
			}
			num += num3;
			if (flag)
			{
				break;
			}
		}
		return 0f;
	}

	private static float Mp4Duration(string path, long len)
	{
		using FileStream fileStream = File.OpenRead(path);
		long num2;
		for (long num = 0L; num + 8 <= len; num += num2)
		{
			fileStream.Seek(num, SeekOrigin.Begin);
			byte[] array = new byte[8];
			if (fileStream.Read(array, 0, 8) < 8)
			{
				return 0f;
			}
			num2 = (array[0] << 24) | (array[1] << 16) | (array[2] << 8) | array[3];
			string text = Encoding.ASCII.GetString(array, 4, 4);
			long start = num + 8;
			if (num2 == 1)
			{
				byte[] array2 = new byte[8];
				if (fileStream.Read(array2, 0, 8) < 8)
				{
					return 0f;
				}
				num2 = 0L;
				for (int i = 0; i < 8; i++)
				{
					num2 = (num2 << 8) | array2[i];
				}
				start = num + 16;
			}
			if (text == "moov")
			{
				return MvhdIn(fileStream, start, num + num2);
			}
			if (num2 <= 0)
			{
				return 0f;
			}
		}
		return 0f;
	}

	private static float MvhdIn(FileStream fs, long start, long end)
	{
		long num2;
		for (long num = start; num + 8 <= end; num += num2)
		{
			fs.Seek(num, SeekOrigin.Begin);
			byte[] array = new byte[8];
			if (fs.Read(array, 0, 8) < 8)
			{
				return 0f;
			}
			num2 = (array[0] << 24) | (array[1] << 16) | (array[2] << 8) | array[3];
			if (Encoding.ASCII.GetString(array, 4, 4) == "mvhd")
			{
				byte[] array2 = new byte[72];
				if (fs.Read(array2, 0, array2.Length) < 32)
				{
					return 0f;
				}
				long num3;
				long num4;
				if (array2[0] == 1)
				{
					num3 = (long)(((ulong)array2[20] << 24) | ((ulong)array2[21] << 16) | ((ulong)array2[22] << 8) | array2[23]);
					num4 = 0L;
					for (int i = 24; i < 32; i++)
					{
						num4 = (num4 << 8) | array2[i];
					}
				}
				else
				{
					num3 = (long)(((ulong)array2[12] << 24) | ((ulong)array2[13] << 16) | ((ulong)array2[14] << 8) | array2[15]);
					num4 = (long)(((ulong)array2[16] << 24) | ((ulong)array2[17] << 16) | ((ulong)array2[18] << 8) | array2[19]);
				}
				if (num3 > 0 && num4 > 0)
				{
					return (float)((double)num4 / (double)num3);
				}
				return 0f;
			}
			if (num2 <= 0)
			{
				return 0f;
			}
		}
		return 0f;
	}

	private static int IndexOfBytes(byte[] haystack, byte[] needle)
	{
		for (int i = 0; i + needle.Length <= haystack.Length; i++)
		{
			bool flag = true;
			for (int j = 0; j < needle.Length; j++)
			{
				if (haystack[i + j] != needle[j])
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				return i;
			}
		}
		return -1;
	}
}
