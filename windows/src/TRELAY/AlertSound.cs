using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;

namespace TRELAY;

internal static class AlertSound
{
	internal sealed class OutputDevice
	{
		public uint Id;

		public string Name;

		public override string ToString()
		{
			return Name;
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WaveOutCaps
	{
		public ushort Manufacturer;

		public ushort Product;

		public uint DriverVersion;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string Name;

		public uint Formats;

		public ushort Channels;

		public ushort Reserved;

		public uint Support;
	}

	private struct WaveFormat
	{
		public ushort FormatTag;

		public ushort Channels;

		public uint SamplesPerSecond;

		public uint AverageBytesPerSecond;

		public ushort BlockAlign;

		public ushort BitsPerSample;

		public ushort ExtraSize;
	}

	private struct WaveHeader
	{
		public IntPtr Data;

		public uint BufferLength;

		public uint BytesRecorded;

		public IntPtr User;

		public uint Flags;

		public uint Loops;

		public IntPtr Next;

		public IntPtr Reserved;
	}

	private const int SampleRate = 22050;

	private static readonly object Sync = new object();

	private static SoundPlayer current;

	private static byte[] alarm;

	private static byte[] chime;

	private static DateTime lastAlarmUtc = DateTime.MinValue;

	public static List<OutputDevice> GetOutputDevices()
	{
		List<OutputDevice> list = new List<OutputDevice>();
		uint num = waveOutGetNumDevs();
		for (uint num2 = 0u; num2 < num; num2++)
		{
			if (waveOutGetDevCaps(new UIntPtr(num2), out var caps, (uint)Marshal.SizeOf(typeof(WaveOutCaps))) == 0)
			{
				list.Add(new OutputDevice
				{
					Id = num2,
					Name = caps.Name
				});
			}
		}
		return list;
	}

	public static bool IsOutputAvailable(string outputDevice)
	{
		if (outputDevice == "default")
		{
			return true;
		}
		List<OutputDevice> outputDevices = GetOutputDevices();
		return ResolveOutput(outputDevices, outputDevice) != null;
	}

	private static OutputDevice ResolveOutput(List<OutputDevice> devices, string outputDevice)
	{
		if (string.IsNullOrEmpty(outputDevice) || outputDevice == "speakers")
		{
			return devices.Find((OutputDevice d) => IsSpeaker(d.Name));
		}
		return devices.Find((OutputDevice d) => string.Equals(d.Name, outputDevice, StringComparison.OrdinalIgnoreCase));
	}

	public static void Play(bool critical, string outputDevice = "speakers", bool force = false)
	{
		try
		{
			lock (Sync)
			{
				DateTime utcNow = DateTime.UtcNow;
				if (!force && utcNow - lastAlarmUtc < TimeSpan.FromSeconds(2.0))
				{
					return;
				}
				if (critical)
				{
					lastAlarmUtc = utcNow;
				}
				byte[] data = (critical ? (alarm ?? (alarm = BuildAlarm())) : (chime ?? (chime = BuildChime())));
				if (outputDevice == "default")
				{
					if (current != null)
					{
						try
						{
							current.Stop();
						}
						catch
						{
						}
						current.Dispose();
					}
					current = new SoundPlayer(new MemoryStream(data, writable: false));
					current.Play();
					return;
				}
				List<OutputDevice> outputDevices = GetOutputDevices();
				OutputDevice outputDevice2 = ResolveOutput(outputDevices, outputDevice);
				if (outputDevice2 != null)
				{
					uint deviceId = outputDevice2.Id;
					ThreadPool.QueueUserWorkItem(delegate
					{
						PlayOnDevice(data, deviceId);
					});
				}
			}
		}
		catch
		{
			if (!(outputDevice == "default"))
			{
				return;
			}
			try
			{
				if (critical)
				{
					SystemSounds.Hand.Play();
				}
				else
				{
					SystemSounds.Asterisk.Play();
				}
			}
			catch
			{
			}
		}
	}

	private static bool IsSpeaker(string name)
	{
		string text = (name ?? "").ToLowerInvariant();
		if (text.Contains("headphone") || text.Contains("headset") || text.Contains("наушник") || text.Contains("гарнитур"))
		{
			return false;
		}
		if (!text.Contains("speaker") && !text.Contains("динамик"))
		{
			return text.Contains("колонк");
		}
		return true;
	}

	private static void PlayOnDevice(byte[] wave, uint deviceId)
	{
		IntPtr handle = IntPtr.Zero;
		IntPtr intPtr = IntPtr.Zero;
		IntPtr intPtr2 = IntPtr.Zero;
		uint num = (uint)Marshal.SizeOf(typeof(WaveHeader));
		using AutoResetEvent autoResetEvent = new AutoResetEvent(initialState: false);
		try
		{
			WaveFormat format = new WaveFormat
			{
				FormatTag = 1,
				Channels = 1,
				SamplesPerSecond = 22050u,
				AverageBytesPerSecond = 44100u,
				BlockAlign = 2,
				BitsPerSample = 16
			};
			if (waveOutOpen(out handle, deviceId, ref format, autoResetEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 327680u) != 0)
			{
				return;
			}
			int num2 = wave.Length - 44;
			intPtr = Marshal.AllocHGlobal(num2);
			Marshal.Copy(wave, 44, intPtr, num2);
			intPtr2 = Marshal.AllocHGlobal((int)num);
			Marshal.StructureToPtr(new WaveHeader
			{
				Data = intPtr,
				BufferLength = (uint)num2
			}, intPtr2, fDeleteOld: false);
			if (waveOutPrepareHeader(handle, intPtr2, num) != 0)
			{
				return;
			}
			try
			{
				if (waveOutWrite(handle, intPtr2, num) == 0)
				{
					DateTime dateTime = DateTime.UtcNow.AddSeconds(8.0);
					while (DateTime.UtcNow < dateTime && (Marshal.PtrToStructure<WaveHeader>(intPtr2).Flags & 1) == 0)
					{
						autoResetEvent.WaitOne(100);
					}
				}
			}
			finally
			{
				waveOutReset(handle);
				waveOutUnprepareHeader(handle, intPtr2, num);
			}
		}
		catch
		{
		}
		finally
		{
			if (handle != IntPtr.Zero)
			{
				waveOutClose(handle);
			}
			if (intPtr2 != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(intPtr2);
			}
			if (intPtr != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}
	}

	[DllImport("winmm.dll")]
	private static extern uint waveOutGetNumDevs();

	[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
	private static extern uint waveOutGetDevCaps(UIntPtr deviceId, out WaveOutCaps caps, uint size);

	[DllImport("winmm.dll")]
	private static extern uint waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

	[DllImport("winmm.dll")]
	private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);

	[DllImport("winmm.dll")]
	private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);

	[DllImport("winmm.dll")]
	private static extern uint waveOutReset(IntPtr handle);

	[DllImport("winmm.dll")]
	private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);

	[DllImport("winmm.dll")]
	private static extern uint waveOutClose(IntPtr handle);

	private static byte[] BuildAlarm()
	{
		MemoryStream memoryStream = new MemoryStream();
		for (int i = 0; i < 3; i++)
		{
			Tone(memoryStream, 988.0, 150, 0.85, rich: true);
			Silence(memoryStream, 40);
			Tone(memoryStream, 1319.0, 150, 0.85, rich: true);
			Silence(memoryStream, 170);
		}
		return Wave(memoryStream.ToArray());
	}

	private static byte[] BuildChime()
	{
		MemoryStream memoryStream = new MemoryStream();
		Tone(memoryStream, 784.0, 120, 0.4, rich: false);
		Tone(memoryStream, 1175.0, 260, 0.4, rich: false);
		return Wave(memoryStream.ToArray());
	}

	private static void Tone(Stream output, double frequency, int milliseconds, double volume, bool rich)
	{
		int num = 22050 * milliseconds / 1000;
		int num2 = 176;
		for (int i = 0; i < num; i++)
		{
			double num3 = (double)i / 22050.0;
			double num4 = Math.Min(1.0, Math.Min((double)i / (double)num2, (double)(num - i) / (double)num2));
			if (!rich)
			{
				num4 *= Math.Exp(-3.0 * (double)i / (double)num);
			}
			double num5 = Math.Sin(Math.PI * 2.0 * frequency * num3);
			if (rich)
			{
				num5 = (num5 + 0.3 * Math.Sin(Math.PI * 2.0 * frequency * 3.0 * num3)) / 1.3;
			}
			short num6 = (short)Math.Max(-32768.0, Math.Min(32767.0, num5 * num4 * volume * 32767.0));
			output.WriteByte((byte)(num6 & 0xFF));
			output.WriteByte((byte)((num6 >> 8) & 0xFF));
		}
	}

	private static void Silence(Stream output, int milliseconds)
	{
		int num = 22050 * milliseconds / 1000 * 2;
		for (int i = 0; i < num; i++)
		{
			output.WriteByte(0);
		}
	}

	private static byte[] Wave(byte[] pcm)
	{
		using MemoryStream memoryStream = new MemoryStream();
		using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
		binaryWriter.Write(new char[4] { 'R', 'I', 'F', 'F' });
		binaryWriter.Write(36 + pcm.Length);
		binaryWriter.Write(new char[8] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
		binaryWriter.Write(16);
		binaryWriter.Write((short)1);
		binaryWriter.Write((short)1);
		binaryWriter.Write(22050);
		binaryWriter.Write(44100);
		binaryWriter.Write((short)2);
		binaryWriter.Write((short)16);
		binaryWriter.Write(new char[4] { 'd', 'a', 't', 'a' });
		binaryWriter.Write(pcm.Length);
		binaryWriter.Write(pcm);
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}
}
