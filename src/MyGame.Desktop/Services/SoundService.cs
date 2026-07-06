using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MyGame.Core.Profile;

namespace MyGame.Desktop.Services;

public enum SoundEffect
{
    Click,
    Select,
    PageTurn,
    Chime,
    Fanfare
}

/// <summary>
/// Synthesizes and plays high-quality atmospheric TTRPG interface sounds programmatically.
/// Supports Windows, macOS, and Linux without external dependencies.
/// </summary>
public static class SoundService
{
    [DllImport("winmm.dll", SetLastError = true, EntryPoint = "PlaySound", CharSet = CharSet.Auto)]
    private static extern bool PlaySound(byte[] pszSound, IntPtr hMod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    public static void Play(SoundEffect effect)
    {
        try
        {
            var settingsStore = ServiceHost.Resolve<SettingsStore>();
            var settings = settingsStore.Load();
            if (!settings.SoundEnabled || settings.MasterVolume <= 0) return;
            
            double volumeFactor = settings.MasterVolume / 100.0;
            
            byte[] wavBytes = effect switch
            {
                SoundEffect.Click => GenerateClick(volumeFactor),
                SoundEffect.Select => GenerateSelect(volumeFactor),
                SoundEffect.PageTurn => GeneratePageTurn(volumeFactor),
                SoundEffect.Chime => GenerateChime(volumeFactor),
                SoundEffect.Fanfare => GenerateFanfare(volumeFactor),
                _ => Array.Empty<byte>()
            };
            
            if (wavBytes.Length == 0) return;
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Play asynchronously from the byte array in memory using Win32 API.
                // This requires 0 extra dependencies or packages!
                PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
            }
            else
            {
                PlayFileWithShellCommand(wavBytes);
            }
        }
        catch {}
    }

    private static void PlayFileWithShellCommand(byte[] wavBytes)
    {
        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"pathstone_sound_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(tempFile, wavBytes);
            
            string cmd = "";
            string args = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                cmd = "afplay";
                args = $"\"{tempFile}\"";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                cmd = "aplay";
                args = $"\"{tempFile}\"";
            }
            
            if (!string.IsNullOrEmpty(cmd))
            {
                Task.Run(() =>
                {
                    try
                    {
                        using var proc = Process.Start(new ProcessStartInfo
                        {
                            FileName = cmd,
                            Arguments = args,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        proc?.WaitForExit();
                        File.Delete(tempFile);
                    }
                    catch {}
                });
            }
        }
        catch {}
    }

    private static byte[] CreateWavHeader(int sampleCount, int sampleRate = 22050)
    {
        var header = new byte[44];
        int totalSize = 36 + sampleCount * 2;
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BitConverter.GetBytes(totalSize).CopyTo(header, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BitConverter.GetBytes(16).CopyTo(header, 16); // Subchunk1Size
        BitConverter.GetBytes((short)1).CopyTo(header, 20); // PCM
        BitConverter.GetBytes((short)1).CopyTo(header, 22); // Mono
        BitConverter.GetBytes(sampleRate).CopyTo(header, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(header, 28); // ByteRate
        BitConverter.GetBytes((short)2).CopyTo(header, 32); // BlockAlign
        BitConverter.GetBytes((short)16).CopyTo(header, 34); // BitsPerSample
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BitConverter.GetBytes(sampleCount * 2).CopyTo(header, 40);
        return header;
    }

    private static byte[] ToWav(short[] samples, int sampleRate, double volumeFactor)
    {
        var header = CreateWavHeader(samples.Length, sampleRate);
        var wav = new byte[header.Length + samples.Length * 2];
        Buffer.BlockCopy(header, 0, wav, 0, header.Length);
        
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)(samples[i] * volumeFactor);
            wav[header.Length + i * 2] = (byte)(v & 0xff);
            wav[header.Length + i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return wav;
    }

    private static byte[] GenerateClick(double volumeFactor)
    {
        int sampleRate = 22050;
        double duration = 0.05;
        int sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double freq = 1000.0 - (t / duration) * 800.0;
            double amp = Math.Exp(-100.0 * t);
            samples[i] = (short)(Math.Sin(2.0 * Math.PI * freq * t) * 15000.0 * amp);
        }
        return ToWav(samples, sampleRate, volumeFactor);
    }

    private static byte[] GenerateSelect(double volumeFactor)
    {
        int sampleRate = 22050;
        double duration = 0.12;
        int sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double freq = 400.0 - (t / duration) * 250.0;
            double amp = Math.Exp(-30.0 * t);
            samples[i] = (short)(Math.Sin(2.0 * Math.PI * freq * t) * 18000.0 * amp);
        }
        return ToWav(samples, sampleRate, volumeFactor);
    }

    private static byte[] GeneratePageTurn(double volumeFactor)
    {
        int sampleRate = 22050;
        double duration = 0.25;
        int sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        var rand = new Random(42);
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double amp = Math.Sin(Math.PI * (t / duration)) * Math.Exp(-5.0 * t);
            double noise = (rand.NextDouble() * 2.0 - 1.0);
            samples[i] = (short)(noise * 8000.0 * amp);
        }
        return ToWav(samples, sampleRate, volumeFactor);
    }

    private static byte[] GenerateChime(double volumeFactor)
    {
        int sampleRate = 22050;
        double duration = 0.8;
        int sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        double[] freqs = { 523.25, 659.25, 784.00, 1046.50 };
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double amp = Math.Exp(-4.0 * t);
            double sampleVal = 0;
            for (int f = 0; f < freqs.Length; f++)
            {
                double tremolo = 1.0 + 0.15 * Math.Sin(2.0 * Math.PI * 6.0 * t);
                sampleVal += Math.Sin(2.0 * Math.PI * freqs[f] * t) * (1.0 / freqs.Length) * tremolo;
            }
            samples[i] = (short)(sampleVal * 20000.0 * amp);
        }
        return ToWav(samples, sampleRate, volumeFactor);
    }

    private static byte[] GenerateFanfare(double volumeFactor)
    {
        int sampleRate = 22050;
        double duration = 1.8;
        int sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double amp = Math.Exp(-2.0 * t);
            double freq = 261.63;
            
            if (t < 0.2) freq = 261.63;
            else if (t < 0.4) freq = 329.63;
            else if (t < 0.6) freq = 392.00;
            else if (t < 0.8) freq = 523.25;
            else
            {
                double sampleVal = Math.Sin(2.0 * Math.PI * 523.25 * t) +
                                   Math.Sin(2.0 * Math.PI * 659.25 * t) +
                                   Math.Sin(2.0 * Math.PI * 784.00 * t);
                samples[i] = (short)(sampleVal / 3.0 * 20000.0 * amp);
                continue;
            }
            
            samples[i] = (short)(Math.Sin(2.0 * Math.PI * freq * t) * 15000.0 * amp);
        }
        return ToWav(samples, sampleRate, volumeFactor);
    }
}
