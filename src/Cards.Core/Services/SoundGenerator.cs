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

    /// <summary>A riffle: a run of tiny card clicks, quick and uneven, as a pack is shuffled.</summary>
    public static byte[] Shuffle()
    {
        var rng = new Random(11);
        var parts = new List<short[]>();
        for (int i = 0; i < 22; i++)
        {
            parts.Add(Noise(0.006 + rng.NextDouble() * 0.004, 0.22, rng, smooth: 2));
            parts.Add(Silence(0.012 + rng.NextDouble() * 0.018));
        }
        return ToWav(Concat([.. parts]));
    }

    /// <summary>A card laid down: a short soft snap with a low knock under it.</summary>
    public static byte[] Play()
    {
        var rng   = new Random(3);
        var snap  = Noise(0.035, 0.32, rng, smooth: 3);
        var knock = Sine(170, 0.035, 0.25);
        return ToWav(Mix(snap, knock));
    }

    /// <summary>A card drawn: a slide that swells and fades.</summary>
    public static byte[] Draw()
        => ToWav(Swell(Noise(0.11, 0.22, new Random(5), smooth: 6)));

    /// <summary>A trick gathered in: two quick swishes.</summary>
    public static byte[] Gather()
    {
        var rng = new Random(7);
        return ToWav(Concat(Swell(Noise(0.07, 0.2, rng, smooth: 5)), Silence(0.03),
                            Swell(Noise(0.07, 0.17, rng, smooth: 5))));
    }

    /// <summary>Points scored: a light rising pair of notes.</summary>
    public static byte[] Score()
        => ToWav(Concat(Sine(659.25, 0.07, 0.22), Sine(880, 0.11, 0.24)));

    /// <summary>Your turn: one soft bell, so it is heard without being startling.</summary>
    public static byte[] YourTurn()
        => ToWav(Mix(Sine(880, 0.28, 0.16), Sine(1760, 0.18, 0.05)));

    // ── Synthesis helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// White noise, softened by averaging <paramref name="smooth"/> samples (a crude low
    /// pass — more is duller), fading out as it plays. Seeded, so a sound is the same
    /// every time.
    /// </summary>
    private static short[] Noise(double durationSec, double volume, Random rng, int smooth)
    {
        int n = (int)(SampleRate * durationSec);
        var raw = new double[n];
        for (int i = 0; i < n; i++) raw[i] = rng.NextDouble() * 2 - 1;

        var buf = new short[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0; int k = 0;
            for (int j = Math.Max(0, i - smooth + 1); j <= i; j++) { sum += raw[j]; k++; }
            double env = Math.Pow(Math.Max(0, 1.0 - i / (double)n), 2);
            buf[i] = (short)(sum / k * 32767 * volume * env);
        }
        return buf;
    }

    /// <summary>Shapes a sound to rise to its middle and fall away, like a slide.</summary>
    private static short[] Swell(short[] sound)
    {
        var buf = new short[sound.Length];
        for (int i = 0; i < sound.Length; i++)
            buf[i] = (short)Math.Clamp(sound[i] * Math.Sin(Math.PI * i / Math.Max(1, sound.Length - 1)) * 1.6, short.MinValue, short.MaxValue);
        return buf;
    }

    /// <summary>Two sounds at once, the length of the longer.</summary>
    private static short[] Mix(short[] a, short[] b)
    {
        var buf = new short[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = (short)Math.Clamp((i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0), short.MinValue, short.MaxValue);
        return buf;
    }

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
