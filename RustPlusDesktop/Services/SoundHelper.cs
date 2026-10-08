using System;
using System.IO;
using System.Media;

namespace RustPlusDesk.Services
{
    /// <summary>
    /// Provides gentle, low-latency audio feedback with reduced volume (approx half of Windows system sounds).
    /// </summary>
    public static class SoundHelper
    {
        private static SoundPlayer? _cachedPlayer;
        private static readonly object _syncLock = new();

        public static void PlaySoftFeedback()
        {
            try
            {
                if (_cachedPlayer == null)
                {
                    lock (_syncLock)
                    {
                        _cachedPlayer ??= BuildSoftTonePlayer(frequency: 720.0, durationMs: 40, volume: 0.18f);
                    }
                }
                _cachedPlayer.Play();
            }
            catch
            {
                try { Console.Beep(800, 30); } catch { }
            }
        }

        private static SoundPlayer BuildSoftTonePlayer(double frequency, int durationMs, float volume)
        {
            int sampleRate = 44100;
            int numSamples = (int)(sampleRate * (durationMs / 1000.0));
            short[] samples = new short[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;
                // Smooth exponential decay envelope to avoid clicking
                double progress = (double)i / numSamples;
                double envelope = Math.Pow(1.0 - progress, 2.0);
                double sine = Math.Sin(2.0 * Math.PI * frequency * t);
                short sample = (short)(sine * envelope * volume * short.MaxValue);
                samples[i] = sample;
            }

            var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            // RIFF header
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + numSamples * 2);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16); // subchunk1 size (16 for PCM)
            writer.Write((short)1); // PCM
            writer.Write((short)1); // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2); // byte rate
            writer.Write((short)2); // block align
            writer.Write((short)16); // bits per sample
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(numSamples * 2);
            foreach (var s in samples)
            {
                writer.Write(s);
            }
            writer.Flush();
            ms.Position = 0;

            var player = new SoundPlayer(ms);
            player.Load();
            return player;
        }
    }
}
