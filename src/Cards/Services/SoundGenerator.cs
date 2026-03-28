using System.Text;

namespace Cards.Services;

/// <summary>
/// Generates minimal PCM WAV byte arrays for placeholder sound effects.
/// No audio files on disk — all sounds are synthesised at runtime.
/// Replace with real samples by swapping the byte[] returned from each method.
/// </summary>
public static class SoundGenerator
{
    private const int SampleRate = 22050;

    // ── Public sounds ─────────────────────────────────────────────────────────

    /// <summary>Three short clicks — card sliding onto the table.</summary>
    public static byte[] Deal()
    {
        var s1 = Sine(900,  0.040, 0.22);
        var s2 = Sine(1000, 0.035, 0.22);
        var s3 = Sine(1100, 0.030, 0.22);
        return ToWav(Concat(s1, Silence(0.04), s2, Silence(0.04), s3));
    }

    /// <summary>Quick high-to-low snap — card flipping face-up.</summary>
    public static byte[] Flip()
    {
        var sweep = SweepDown(1400, 700, 0.12, 0.25);
        return ToWav(sweep);
    }

    /// <summary>Rising C–E–G arpeggio — game won.</summary>
    public static byte[] Win()
    {
        var c = Sine(523.25, 0.12, 0.4);
        var e = Sine(659.25, 0.12, 0.4);
        var g = Sine(783.99, 0.20, 0.5);
        return ToWav(Concat(c, e, g));
    }

    /// <summary>Soft descending two-tone — game lost.</summary>
    public static byte[] Lose()
    {
        var hi = Sine(440, 0.10, 0.3);
        var lo = Sine(330, 0.10, 0.3);
        return ToWav(Concat(hi, Silence(0.03), lo));
    }

    // ── Synthesis helpers ─────────────────────────────────────────────────────

    /// <summary>Single sine-wave tone with a linear fade-out envelope.</summary>
    private static short[] Sine(double freqHz, double durationSec, double volume)
    {
        int n = (int)(SampleRate * durationSec);
        var buf = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t   = i / (double)SampleRate;
            double env = Math.Max(0, 1.0 - i / (double)n); // fade out
            buf[i] = (short)(Math.Sin(2 * Math.PI * freqHz * t) * 32767 * volume * env);
        }
        return buf;
    }

    /// <summary>Frequency sweep from <paramref name="startHz"/> to <paramref name="endHz"/>.</summary>
    private static short[] SweepDown(double startHz, double endHz, double durationSec, double volume)
    {
        int n = (int)(SampleRate * durationSec);
        var buf = new short[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double progress = i / (double)n;
            double freq = startHz + (endHz - startHz) * progress;
            double env  = Math.Max(0, 1.0 - progress);
            phase += 2 * Math.PI * freq / SampleRate;
            buf[i] = (short)(Math.Sin(phase) * 32767 * volume * env);
        }
        return buf;
    }

    private static short[] Silence(double durationSec)
        => new short[(int)(SampleRate * durationSec)];

    private static short[] Concat(params short[][] segments)
    {
        int total = segments.Sum(s => s.Length);
        var result = new short[total];
        int pos = 0;
        foreach (var seg in segments)
        {
            seg.CopyTo(result, pos);
            pos += seg.Length;
        }
        return result;
    }

    // ── WAV serialisation ─────────────────────────────────────────────────────

    private static byte[] ToWav(short[] samples)
    {
        int dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);   // chunk size
        bw.Write("WAVE"u8);

        // fmt  sub-chunk
        bw.Write("fmt "u8);
        bw.Write(16);               // sub-chunk size (PCM)
        bw.Write((short)1);         // PCM = 1
        bw.Write((short)1);         // mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);   // byte rate = SampleRate * channels * bitsPerSample/8
        bw.Write((short)2);         // block align = channels * bitsPerSample/8
        bw.Write((short)16);        // bits per sample

        // data sub-chunk
        bw.Write("data"u8);
        bw.Write(dataBytes);
        foreach (var s in samples) bw.Write(s);

        return ms.ToArray();
    }
}
