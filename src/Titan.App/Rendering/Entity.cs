using System.Numerics;
using System.Runtime.InteropServices;

namespace Titan.App.Rendering;

/// <summary>Constant buffer of Entity.hlsl (7 × 16 bytes). Field order must match the cbuffer.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EntityParams
{
    public Vector2 Res; public float Time; public float Rot;
    public Vector4 Fx;
    public Vector4 W0;
    public float W4; public float Shock; public float Dark; public float GlowAmt;
    public Vector3 Mouse; public float Aura;
    public Vector3 Bg; public float P0;
    public Vector3 Glow; public float P1;
}

/// <summary>Constant buffer of Helix.hlsl (4 × 16 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HelixParams
{
    public Vector2 Res; public float Time; public float Use;
    public float Stb; public float N; public float Pulse; public float P0;
    public Vector3 Bg; public float P1;
    public Vector3 Gold; public float P2;
}

public enum EntityState { Rest = 1, Analysis = 2, MaxBoost = 3, Release = 4, Persist = 5 }

/// <summary>What each state wants; the animator springs toward it.</summary>
public readonly record struct EntityTargets(float Charge, float Spike, float Absorb, float Rot, float Speed, float Aura, bool Calm);

/// <summary>
/// Motion of the entity: eased state weights (the solid melts between shapes), an underdamped spring for the
/// boost, a violent-gulp spring for purges, the magnetic pointer / right stick, and the Xbox triggers.
/// </summary>
public sealed class EntityAnimator
{
    public static readonly Vector3 Page = new(246 / 255f, 246 / 255f, 243 / 255f);
    public static readonly Vector3 FerroYellow = new(242 / 255f, 194 / 255f, 0);

    private readonly float[] _w = [1, 0, 0, 0, 0];
    private float _charge, _spike, _spikeV, _absorb, _gulp, _gulpV, _mx, _my, _mz, _rot, _shock, _t, _aura = .2f;
    private double _snapUntil;
    private double _lastDraw;

    public EntityState State { get; set; } = EntityState.Rest;
    public Vector3 Pointer { get; set; }              // x, y: direction in entity space, z: strength
    public float TriggerLeft { get; set; }             // LT attracts the droplets
    public float TriggerRight { get; set; }            // RT tenses the mass
    public bool StickActive { get; set; }

    public void Kick(float spikeVelocity) => _spikeV = spikeVelocity;
    public void Shock() => _shock = .001f;
    public void Gulp(float force, double nowMs) { _gulpV = force; _snapUntil = nowMs + 420; }

    /// <summary>Advances the simulation. Returns false when nothing needs drawing this frame (30 fps at rest).</summary>
    public bool Update(float dt, double nowMs, EntityTargets tg)
    {
        float Ease(float c, float x, float k) => c + (x - c) * (1 - MathF.Exp(-dt * k));
        float lt = TriggerLeft, rt = TriggerRight;
        float absorbTarget = Math.Max(tg.Absorb, lt);
        float chargeTarget = Math.Max(tg.Charge, rt * .55f);
        bool calm = tg.Calm && lt < .02f && rt < .02f && !StickActive;

        int target = (int)State - 1;
        for (int i = 0; i < 5; i++) _w[i] = Ease(_w[i], i == target ? 1 : 0, 3.2f);
        _charge = Ease(_charge, chargeTarget, 8);
        _aura = Ease(_aura, Math.Max(tg.Aura, rt), 3);
        bool snapping = nowMs < _snapUntil;
        _absorb = Ease(_absorb, snapping ? 1 : absorbTarget, snapping ? 22 : (absorbTarget > _absorb ? 6 : 1.6f));
        _spikeV += (110 * (tg.Spike - _spike) - 12 * _spikeV) * dt; _spike += _spikeV * dt;
        _gulpV += (-150 * _gulp - 9 * _gulpV) * dt; _gulp += _gulpV * dt;
        _mx = Ease(_mx, Pointer.X, 9); _my = Ease(_my, Pointer.Y, 9); _mz = Ease(_mz, Pointer.Z, 5);
        _rot += dt * tg.Rot;
        if (_shock > 0) { _shock += dt * 1.1f; if (_shock >= 1) _shock = 0; }
        _t += dt * tg.Speed;

        bool settling = MathF.Abs(_spikeV) + MathF.Abs(_gulpV) + MathF.Abs(_w[target] - 1) > .01f || _shock > 0;
        if (calm && !settling && nowMs - _lastDraw < 32) return false;
        _lastDraw = nowMs;
        return true;
    }

    public EntityParams Params(int width, int height) => new()
    {
        Res = new(width, height), Time = _t, Rot = _rot,
        Fx = new(_charge, MathF.Max(_spike, 0), _absorb, MathF.Max(_gulp, -.3f)),
        W0 = new(_w[0], _w[1], _w[2], _w[3]), W4 = _w[4],
        Shock = _shock, Dark = 0, GlowAmt = .9f,
        Mouse = new(_mx, _my, _mz), Aura = _aura,
        Bg = Page, Glow = FerroYellow,
    };
}
