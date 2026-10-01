// Pure generator for the noisemaker's TEMPORARY stand-in sounds (no Unity types; Editor/Tests/NoisemakerTests.cs). Deterministic: the same kind
// and sample rate always give the same samples. Soft, mid-pitched and normalised to a stated peak, so none is piercing; each is meant to be
// replaced by a real recording through the AudioBank fields.

public enum NoisemakerSound : byte { Pickup, Throw, Impact, Pulse, Spent }

public static class NoisemakerSynth
{
    public const int SoundCount = 5;

    /// <summary>The peak level (0..1) each kind is normalised to: the pulse is the one that must carry, the pickup is a quiet click.</summary>
    public static float Peak(NoisemakerSound kind) => kind switch
    {
        NoisemakerSound.Pickup => 0.4f,
        NoisemakerSound.Throw => 0.5f,
        NoisemakerSound.Impact => 0.85f,
        NoisemakerSound.Pulse => 0.8f,
        _ => 0.6f,
    };

    public static float Seconds(NoisemakerSound kind) => kind switch
    {
        NoisemakerSound.Pickup => 0.07f,
        NoisemakerSound.Throw => 0.09f,
        NoisemakerSound.Impact => 0.24f,
        NoisemakerSound.Pulse => 0.3f,
        _ => 0.5f,
    };

    public static float[] Generate(NoisemakerSound kind, int rate)
    {
        int n = System.Math.Max(1, (int)(Seconds(kind) * rate));
        var data = new float[n];
        uint noise = 12345u + (uint)kind * 7919u; // a fixed little noise source: no System.Random, so it is the same on every machine
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate;
            noise = noise * 1664525u + 1013904223u;
            double white = (noise >> 8) / (double)(1 << 24) * 2.0 - 1.0;
            double attack = System.Math.Min(1.0, t / 0.003);
            double v;
            switch (kind)
            {
                case NoisemakerSound.Pickup: // a small mechanical tick and a soft low thump
                    v = attack * (System.Math.Exp(-t * 90) * (0.6 * white + 0.7 * Sine(2100, t)) + System.Math.Exp(-t * 45) * 0.8 * Sine(160, t));
                    break;
                case NoisemakerSound.Throw: // a short plastic click, a little rattle
                    v = attack * (System.Math.Exp(-t * 70) * (0.5 * white + 0.8 * Sine(1250, t)) + System.Math.Exp(-t * 30) * 0.3 * Sine(520, t));
                    break;
                case NoisemakerSound.Impact: // a dull metallic clank: a noise burst and ringing partials that die quickly
                    v = attack * (System.Math.Exp(-t * 60) * 0.7 * white
                        + System.Math.Exp(-t * 20) * (0.7 * Sine(430, t) + 0.5 * Sine(1180, t) + 0.3 * Sine(2310, t)));
                    break;
                case NoisemakerSound.Pulse: // two short mid-pitched electronic notes (about 660 then 880 Hz): clear, not shrill
                {
                    bool second = t >= 0.14;
                    double local = second ? t - 0.14 : t;
                    double f = second ? 880 : 660;
                    v = System.Math.Min(1.0, local / 0.004) * System.Math.Exp(-local * 11) * (Sine(f, t) + 0.25 * Sine(2 * f, t) + 0.08 * Sine(3 * f, t));
                    break;
                }
                default: // shutdown: a falling tone
                {
                    double f = 520 - 260 * (t / 0.5);
                    v = attack * System.Math.Exp(-t * 6) * (Sine(f, t) + 0.2 * Sine(2 * f, t));
                    break;
                }
            }
            data[i] = (float)v;
        }
        // Soft fade at both ends so nothing clicks, then normalise to the stated peak.
        int fade = System.Math.Min(n / 4, (int)(rate * 0.004));
        float max = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i < fade) data[i] *= i / (float)fade;
            if (n - 1 - i < fade) data[i] *= (n - 1 - i) / (float)fade;
            max = System.Math.Max(max, System.Math.Abs(data[i]));
        }
        float scale = max > 1e-6f ? Peak(kind) / max : 0f;
        for (int i = 0; i < n; i++) data[i] *= scale;
        return data;
    }

    static double Sine(double hz, double t) => System.Math.Sin(2.0 * System.Math.PI * hz * t);
}
