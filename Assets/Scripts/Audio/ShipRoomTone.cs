using System.Text;
using UnityEngine;

/// <summary>
/// The ship's persistent room tone for the local listener (owner only; added with the player's ambience). Layers, each a seamless loop:
///  - the quiet global ship bed (the AudioBank 'atmos' clip), louder in the machinery rooms (the space's ambience level);
///  - an air-handling layer for corridors and habitation, and optional machinery and duct layers, each weighted per space.
/// Layers never restart at a zone boundary: they keep their place and only their volume blends (over roomToneTransition); a layer at
/// zero is paused, not stopped, and resumes where it was. Each loop plays only the clip's measured seamless region (the bed fades to
/// silence at its very end) and crossfades at the seam with two voices. All flat and dry (the Ambience category bypasses the reverb).
/// While spectating the tone continues lower; after escaping it fades out. Everything is a child of the player, so it goes with it.
/// </summary>
public class ShipRoomTone : MonoBehaviour
{
    // Two voices on one clip: play the loop region, and shortly before its end start the other voice at the region's start and crossfade.
    sealed class CrossfadeLooper
    {
        readonly AudioSource a, b;
        readonly float start, end, crossfade;
        bool aIsCurrent = true, fading, paused;
        float fadeT;

        public string Name { get; }
        public float Volume { get; private set; }
        public GameObject Host { get; }

        public CrossfadeLooper(Transform parent, string name, AudioClip clip, Vector2 loop, float crossfadeSeconds)
        {
            Name = name;
            var go = Host = new GameObject($"Room Tone {name}");
            go.transform.SetParent(parent, false);
            a = Voice(go, clip);
            b = Voice(go, clip);
            start = loop.y > loop.x + 1f ? Mathf.Clamp(loop.x, 0f, clip.length) : 0f;
            end = loop.y > loop.x + 1f ? Mathf.Clamp(loop.y, start + 1f, clip.length) : clip.length;
            crossfade = Mathf.Clamp(crossfadeSeconds, 0.1f, (end - start) * 0.4f);
            a.time = start + (end - start - crossfade) * Random.value * 0.8f; // somewhere inside the loop, so a session does not always open on the same bar
            a.Play();
            a.Pause();
            paused = true;
        }

        static AudioSource Voice(GameObject go, AudioClip clip)
        {
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = false; // the looper decides where it loops
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.volume = 0f;
            AudioRouting.Configure(s, AudioCategory.Ambience); // dry
            return s;
        }

        public void Tick(float dt, float volume)
        {
            Volume = volume;
            var cur = aIsCurrent ? a : b;
            var other = aIsCurrent ? b : a;
            if (volume <= 0.0005f)
            {
                // Silent: hold the place instead of stopping, so it resumes seamlessly.
                if (!paused) { cur.Pause(); if (fading) other.Pause(); paused = true; }
                return;
            }
            if (paused) { cur.UnPause(); if (fading) other.UnPause(); paused = false; }
            else if (!cur.isPlaying && !fading) { cur.time = start; cur.Play(); } // ran off the clip's end (a hitch past the seam)

            if (!fading && cur.time >= end - crossfade)
            {
                other.time = start;
                other.volume = 0f;
                other.Play();
                fading = true;
                fadeT = 0f;
            }
            float cat = AudioRouting.Volume(AudioCategory.Ambience);
            if (fading)
            {
                fadeT += dt / crossfade;
                AudioRules.Crossfade(fadeT, out float incoming, out float outgoing);
                other.volume = volume * cat * incoming;
                cur.volume = volume * cat * outgoing;
                if (fadeT >= 1f)
                {
                    cur.Stop();
                    aIsCurrent = !aIsCurrent;
                    fading = false;
                }
            }
            else cur.volume = volume * cat;
        }
    }

    AudioBank bank;
    PlayerLife life;
    CrossfadeLooper bed, air, machinery, duct;
    float bedLevel, airLevel, machineryLevel, ductLevel, lifeLevel = 1f;

    public void Begin()
    {
        bank = AudioBank.Get();
        life = GetComponent<PlayerLife>();
        if (bank == null) { enabled = false; return; }
        float xf = bank.roomToneLoopCrossfade;
        if (bank.atmos != null) bed = new CrossfadeLooper(transform, "Bed", bank.atmos, bank.atmosLoop, xf);
        if (bank.airTone != null) air = new CrossfadeLooper(transform, "Air", bank.airTone, bank.airToneLoop, xf);
        if (bank.machineryTone != null) machinery = new CrossfadeLooper(transform, "Machinery", bank.machineryTone, bank.machineryToneLoop, xf);
        if (bank.ductTone != null) duct = new CrossfadeLooper(transform, "Duct", bank.ductTone, bank.ductToneLoop, xf);
    }

    void OnDestroy()
    {
        if (bed != null) Destroy(bed.Host);
        if (air != null) Destroy(air.Host);
        if (machinery != null) Destroy(machinery.Host);
        if (duct != null) Destroy(duct.Host);
    }

    void Update()
    {
        if (bank == null) return;
        float dt = Time.deltaTime;
        var profile = bank.Profile(ShipAcoustics.CurrentSpace);
        var (airW, machineryW, ductW) = profile != null ? profile.ToneLevels : (0f, 0f, 0f);
        bool escaped = life != null && life.Status == PlayerStatus.Escaped;
        bool dead = life != null && life.Status == PlayerStatus.Dead;
        float wantLife = escaped ? 0f : dead ? bank.spectatorRoomTone : 1f;
        float k = bank.roomToneTransition;
        lifeLevel = AcousticRules.Approach(lifeLevel, wantLife, dt, k);
        bedLevel = AcousticRules.Approach(bedLevel, bank.atmosVolume * ShipAcoustics.AmbientLevel, dt, k);
        airLevel = AcousticRules.Approach(airLevel, bank.airToneVolume * airW, dt, k);
        machineryLevel = AcousticRules.Approach(machineryLevel, bank.machineryToneVolume * machineryW, dt, k);
        ductLevel = AcousticRules.Approach(ductLevel, bank.ductToneVolume * ductW, dt, k);
        bed?.Tick(dt, bedLevel * lifeLevel);
        air?.Tick(dt, airLevel * lifeLevel);
        machinery?.Tick(dt, machineryLevel * lifeLevel);
        duct?.Tick(dt, ductLevel * lifeLevel);
    }

    /// <summary>One line for the F4 diagnostics.</summary>
    public void Describe(StringBuilder sb)
    {
        sb.Append("room tone: ");
        Layer(sb, bed);
        Layer(sb, air);
        Layer(sb, machinery);
        Layer(sb, duct);
        sb.Append('\n');
    }

    static void Layer(StringBuilder sb, CrossfadeLooper l)
    {
        if (l == null) return;
        sb.Append(l.Name).Append(' ').Append(l.Volume.ToString("0.000")).Append("  ");
    }
}
