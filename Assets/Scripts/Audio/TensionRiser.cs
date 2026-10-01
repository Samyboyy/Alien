using UnityEngine;

/// <summary>
/// The tension riser, played as the original recording (forward, normal speed, no reversing or pitch tricks) by cutting between
/// places in it and blending the cuts.
///
/// The clip is a build, a peak and a fall. How near the creature is (a level 0..1) says how loud the clip should be right now, and
/// the clip's measured loudness curve says where that is: on the way up it is a point on the build, on the way down a point on the
/// fall. Two voices share the clip. One plays forward through a short window around the wanted place; when the wanted place moves
/// away (creature closing in fast, or backing off) or the playhead runs out of window (at the peak, say), a second voice starts at
/// the right place and the two crossfade with an equal-power curve, so a cut is a blend rather than a click. At the peak it keeps
/// looping the same stretch this way; leaving plays the clip's own fall; close again cuts back onto the build.
/// </summary>
public sealed class TensionRiser
{
    readonly AudioBank bank;
    readonly AudioSource[] voice = new AudioSource[2];
    readonly GameObject host;
    int cur;
    float xf = 1f;      // crossfade progress, 1 = settled on `cur`
    float master;       // overall fade in/out
    float anchor, cooldown;
    bool rising = true;

    public TensionRiser(AudioBank bank, GameObject parent)
    {
        this.bank = bank;
        host = new GameObject("Tension Riser");
        host.transform.SetParent(parent.transform, false);
        for (int i = 0; i < 2; i++)
        {
            var s = host.AddComponent<AudioSource>();
            s.clip = bank.tensionRiser;
            s.loop = false;
            s.playOnAwake = false;
            s.spatialBlend = 0f; // in your head, not in the world
            s.volume = 0f;
            AudioRouting.Configure(s, AudioCategory.Internal); // dry: never in the room reverb
            voice[i] = s;
        }
    }

    public void Dispose()
    {
        if (host != null) Object.Destroy(host);
    }

    /// <summary>Call every frame with the smoothed nearness of the creature (0 = far/none, 1 = right here).</summary>
    public void Tick(float level, float dt)
    {
        var clip = bank.tensionRiser;
        if (clip == null) return;
        cooldown -= dt;

        if (level < 0.02f)
        {
            master = Mathf.MoveTowards(master, 0f, dt / 0.8f); // fade the whole thing out
            if (master <= 0f) { StopAll(); return; }
        }
        else master = Mathf.MoveTowards(master, 1f, dt / 0.5f);

        if (level >= 0.02f)
        {
            // Which side of the clip we are on: on the build while it gets nearer, on the fall while it leaves.
            if (level > anchor + 0.02f) { rising = true; anchor = level; }
            else if (level < anchor - 0.02f) { rising = false; anchor = level; }
            float wanted = AudioRules.TimeForLevel(bank.riserEnvelope, bank.riserEnvelopeStep, level, rising, bank.riserPeakSeconds);
            Steer(clip, wanted);
        }
        ApplyVolumes();
    }

    void Steer(AudioClip clip, float wanted)
    {
        var v = voice[cur];
        if (!v.isPlaying && xf >= 1f)
        {
            Begin(cur, Mathf.Max(0f, wanted - bank.riserWindow * 0.5f)); // start (or restart) at the right place; `master` fades it in
            return;
        }
        if (xf < 1f || cooldown > 0f) return;
        bool outOfWindow = AudioRules.CutTarget(v.time, wanted, bank.riserWindow, bank.riserTolerance, out float to);
        bool endOfClip = v.time > clip.length - 0.4f;
        if (!outOfWindow && !endOfClip) return;

        // Cut: a second voice starts at the new place and the two blend.
        cur = 1 - cur;
        Begin(cur, endOfClip ? Mathf.Max(0f, wanted - bank.riserWindow * 0.5f) : to);
        xf = 0f;
        cooldown = bank.riserCrossfade * 0.8f;
    }

    void Begin(int i, float seconds)
    {
        var s = voice[i];
        s.Stop();
        s.timeSamples = Mathf.Clamp((int)(seconds * s.clip.frequency), 0, s.clip.samples - 1);
        s.pitch = 1f;
        s.Play();
    }

    void ApplyVolumes()
    {
        float v = bank.riserVolume * master * AudioRouting.Volume(AudioCategory.Internal);
        if (xf < 1f)
        {
            xf = Mathf.Min(1f, xf + Time.deltaTime / Mathf.Max(0.05f, bank.riserCrossfade));
            AudioRules.Crossfade(xf, out float incoming, out float outgoing);
            voice[cur].volume = v * incoming;
            voice[1 - cur].volume = v * outgoing;
            if (xf >= 1f) voice[1 - cur].Stop();
        }
        else
        {
            voice[cur].volume = v;
            voice[1 - cur].volume = 0f;
        }
    }

    void StopAll()
    {
        foreach (var s in voice) { s.Stop(); s.volume = 0f; }
        xf = 1f;
        anchor = 0f;
        rising = true;
    }
}
