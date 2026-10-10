using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The next footstep, sounded — a beacon on the point the walk is actually sending the
/// player to, not on the objective.</b>
///
/// <para><b>Why a second beacon is needed at all.</b> The mission beacon
/// (<c>MissionBeaconHooks</c>) already sounds toward the OBJECTIVE, and it keeps doing so
/// untouched (the owner's rule: "没有雷达让我怎么找" — the homing sound is never traded away).
/// But when no route reaches the objective, a sound pointing at it points at the one place the
/// player cannot get to, which is the complaint that produced the trial search: "只会给我目标
/// 所在地的声音，但我并不知道该怎么过去". What has to be sounded then is the STEP — the measured
/// spot sixteen metres away that is nearer — so the player can walk toward a sound that is
/// actually walkable.</para>
///
/// <para><b>Three things make it unmistakable next to the mission beacon.</b> It is a pure
/// two-note tone rather than a recorded cue, so it shares no timbre with any of the mod's
/// sound files; it repeats roughly three times slower, so the two never sit on the same beat;
/// and it answers "in front or behind" by the ORDER of its two notes (rising = ahead,
/// falling = behind) rather than by pitch, which keeps it readable even when the two cues
/// overlap.</para>
///
/// <para>Pan is continuous, straight off the bearing, exactly as <see cref="HomingCue"/> does
/// it, and the frame is the camera's — <see cref="FieldDirectionService"/> owns that maths and
/// nothing here recomputes it.</para>
///
/// <para><b>It never falls silent while the walk is running.</b> No distance silences it — not
/// even standing on the step itself, which used to (<see cref="AtStepM"/>). While a step is
/// being followed there is always a ping; the walk ending is the only thing that stops it, and
/// that is the owner's ask of 2026-10-05: <i>"我要雷达音一直在，除非我关掉右手扶强"</i>.</para>
/// </summary>
internal static class StepBeacon
{
    // G5 and B5. The radar owns two registers on purpose — A4/E5/A5 and C4/F4/C5 — so that its
    // two channels can never be confused with each other; a beacon built from notes NEITHER
    // register uses cannot be mistaken for either, or for the recorded cues.
    private const float NoteLow = 783.99f;    // G5
    private const float NoteHigh = 987.77f;   // B5

    private static readonly float[] Ahead = { NoteLow, NoteHigh };    // rising: walk forward
    private static readonly float[] Behind = { NoteHigh, NoteLow };   // falling: turn round

    private const float Volume = 0.45f;

    // Two notes at AudioService's 0.18 s per note, plus its release tail: how long one repeat
    // occupies, so the silence after it can be measured rather than guessed.
    private const int ToneMs = 400;

    // Silence BETWEEN repeats. Deliberately about three times the mission beacon's (100–500 ms):
    // two beacons on the same beat are one beacon, and which one is which is then unlearnable.
    private const int GapNearMs = 300;
    private const int GapFarMs = 1200;

    // The distance the cadence spans, in the caller's own terms: a step is a handful of metres
    // away, and anything past one probe is not a step any more.
    private const float TightAtM = 3f;
    private const float LooseFromM = 20f;

    /// <summary><b>How close the step may come before the beacon stops saying WHERE and only says
    /// YOU ARE THERE.</b> The owner's report, 2026-10-05: <i>"再把那个走到一米内雷达音停止的修了，
    /// 我要雷达音一直在，除非我关掉右手扶强"</i> — inside a metre the tone went silent, and a
    /// sound that stops when it matters most is the one thing this project may never do
    /// ("沉默是唯一不允许的失败").
    ///
    /// <para>Inside this the ping keeps sounding and keeps its cadence — it is NOT dropped — but
    /// the pan centres and the note order goes to <see cref="Tone"/> only: a bearing computed
    /// from a metre of lever arm is noise, and a rising pair heard while standing on the spot
    /// says "walk forward" about nowhere. What survives is the fact that the walk still has a
    /// step: the player hears the beacon, walks, and the walk hands over the next one.</para>
    ///
    /// <para>NO distance can silence this beacon. The only things that stop it are the walk
    /// ending (F4 again, a whole round, a stuck) and a new step being handed over — which is
    /// exactly what the owner asked for.</para></summary>
    private const float AtStepM = 1.5f;

    /// <summary>The one note an at-the-step ping plays. A single held note is unmistakably not
    /// the two-note "walk forward" or "turn round", so a player who hears it knows the step has
    /// been reached rather than merely being close.</summary>
    private static readonly float[] Tone = { NoteLow };

    private static long _nextTick;
    private static int _lastLoggedM = int.MinValue;

    /// <summary>Sound now, and keep sounding: used when the walk hands over a new step, so the
    /// first ping of a new direction is heard at once rather than after a gap.</summary>
    internal static void Reset() => _nextTick = 0;

    /// <summary>Sound the step at (<paramref name="sx"/>, <paramref name="sz"/>) as seen from
    /// (<paramref name="px"/>, <paramref name="pz"/>), if a repeat is due. Cheap to call every
    /// frame: the clock is checked before anything is read.</summary>
    internal static void Tick(float px, float pz, float sx, float sz)
    {
        long now = Environment.TickCount64;
        if (now < _nextTick) return;

        float dist = Dist(px, pz, sx, sz);
        // Standing on the step: keep sounding, and say so. This used to return in silence, which
        // is the report above — the beacon went quiet at the one moment the player is closest to
        // what it is pointing at. The cadence tightens instead, so "you are on it" is heard as
        // the fastest repeat this beacon makes.
        if (dist < AtStepM)
        {
            AudioService.PlayTone(Tone, 0f, Volume);
            _nextTick = now + ToneMs + GapNearMs;
            if (_lastLoggedM != 0)
            {
                _lastLoggedM = 0;
                REFrameworkNET.API.LogInfo(
                    "[SF6Access] Step beacon: at the step - sounding centred until the walk hands over the next one");
            }
            return;
        }

        var forward = FieldDirectionService.GetCameraForward();
        var bearing = FieldDirectionService.GetBearing(forward, sx - px, sz - pz);
        // No usable bearing (camera unreadable) is not a reason to go quiet: centred and rising
        // still beats silence — the pan is the part that is missing, not the step.
        float pan = bearing.Ok ? bearing.Right : 0f;
        float[] notes = bearing.Ok && bearing.Behind ? Behind : Ahead;

        AudioService.PlayTone(notes, pan, Volume);
        _nextTick = now + ToneMs + Gap(dist);

        int metres = (int)dist;
        if (metres == _lastLoggedM) return;
        _lastLoggedM = metres;
        REFrameworkNET.API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Step beacon {metres}m, pan {pan:0.00}, {(notes == Behind ? "behind" : "ahead")}"));
    }

    private static long Gap(float dist)
    {
        float span = LooseFromM - TightAtM;
        float t = span <= 0f ? 1f : Math.Clamp((dist - TightAtM) / span, 0f, 1f);
        return (long)(GapNearMs + t * (GapFarMs - GapNearMs));
    }

    private static float Dist(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
