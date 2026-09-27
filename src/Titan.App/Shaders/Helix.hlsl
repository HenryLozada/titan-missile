// RAM meter: a vertical ferrofluid double helix. 24 rungs ≈ 1 GB each; bottom → top: in use (black),
// standby (white), free (ferro yellow). Droplets are born on the strands, detach and thin away.

cbuffer Params : register(b0)
{
    float2 uRes;  float uTime; float uUse;
    float  uStb;  float uN;    float uPulse; float _p0;
    float3 uBg;   float _p1;
    float3 uGold; float _p2;
};

static const float H = 1.62;
static const float R = .40;

float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float hash3(float3 p) { p = frac(p * .1031); p += dot(p, p.zyx + 31.32); return frac((p.x + p.y) * p.z); }
float smin(float a, float b, float k) { float h = saturate(.5 + .5 * (b - a) / k); return lerp(b, a, h) - k * h * (1 - h); }
float hs(float n) { return frac(sin(n * 127.1 + 311.7) * 43758.5453); }
float3 ferroDots(float3 p, float scale)
{
    float3 g = p * scale, id = floor(g), f = frac(g) - .5;
    float3 c = (float3(hash3(id), hash3(id + 7.1), hash3(id + 3.3)) - .5) * .4;
    float3 dv = f - c;
    float bump = 1 - smoothstep(0, .34, length(dv));
    return normalize(dv + 1e-4) * bump * bump;
}
float twist(float y) { return y * (2.3 * 6.2831853 / (2 * H)) + uTime * .55; }
float radiusAt(float y) { return R * (1 + .07 * uPulse * sin(y * 11 - uTime * 18)); }

// x distance · y material (0 strand, 1 in use, 2 standby, 3 free)
float2 map(float3 p)
{
    float T = uTime;
    float a = twist(p.y), rr = radiusAt(p.y);
    float2 c1 = rr * float2(cos(a), sin(a));
    float strands = min(length(p.xz - c1), length(p.xz + c1)) - .078;
    strands = max(strands, abs(p.y) - H);

    float sp = 2 * H / uN;
    float best = 1e3, mat = 0;
    float yi0 = floor((p.y + H) / sp);
    [unroll] for (int k = -1; k <= 1; k++)
    {
        float yi = clamp(yi0 + k, 0, uN - 1);
        float yc = -H + (yi + .5) * sp;
        float ac = twist(yc), rc = radiusAt(yc);
        float3 B = float3(rc * cos(ac), yc, rc * sin(ac)), A = float3(-B.x, yc, -B.z);
        float3 pa = p - A, ba = B - A;
        float h = saturate(dot(pa, ba) / dot(ba, ba));
        float dr = length(pa - ba * h) - .042;
        if (dr < best) { best = dr; mat = yi < uUse ? 1 : (yi < uUse + uStb ? 2 : 3); }
    }

    // droplets are individuals: every life cycle re-rolls birth point, side, drift, size and whether it shows
    float drops = 1e3;
    [loop] for (int i = 0; i < 18; i++)
    {
        float fi = i;
        float speed = .02 + .045 * hs(fi * 1.7 + .3);
        float cyc = T * speed + hs(fi * 3.1 + .7);
        float life = frac(cyc);
        float seed = fi * 17.13 + floor(cyc) * 7.31;
        if (hs(seed + .5) < .3) continue;
        float y0 = -H * .92 + 1.84 * H * hs(seed + 1.1);
        float side = hs(seed + 2.2) < .5 ? 0 : 3.14159265;
        float a0 = twist(y0) + side + (hs(seed + 3.3) - .5) * 1.1;
        float ang = a0 + (hs(seed + 4.4) - .5) * 1.6 * life;
        float dist = R + .015 + life * (.08 + .24 * hs(seed + 5.5));
        float rise = life * (.05 + .4 * hs(seed + 6.6)) * (hs(seed + 7.7) < .2 ? -1 : 1);
        float3 outv = float3(cos(ang), 0, sin(ang));
        float3 c = float3(outv.x * dist, y0 + rise, outv.z * dist);
        float big = hs(seed + 8.8);
        float size = .01 + .05 * big * big * big;
        float rad = size * smoothstep(0, .14, life) * (1 - smoothstep(.45 + .4 * hs(seed + 9.9), 1, life));
        float tail = length(p - (c - outv * rad * 1.7 - float3(0, rad * .9 * sign(rise), 0))) - rad * .4;
        drops = min(drops, smin(length(p - c) - rad, tail, rad * .8 + .001));
    }
    strands = smin(strands, drops, .05);
    float d = smin(strands, best, .035);
    return float2(d, best < strands ? mat : 0);
}

float3 calcNormal(float3 p)
{
    const float2 k = float2(1, -1); const float e = .0015;
    return normalize(k.xyy * map(p + k.xyy * e).x + k.yyx * map(p + k.yyx * e).x +
                     k.yxy * map(p + k.yxy * e).x + k.xxx * map(p + k.xxx * e).x);
}

float3 env(float3 d)
{
    float3 room = lerp(uBg * .7, lerp(uBg, 1, .5), smoothstep(-.4, .9, d.y));
    float box1 = smoothstep(.3, .42, d.y) * (1 - smoothstep(.85, .95, d.y)) * smoothstep(-.9, -.6, d.x) * (1 - smoothstep(-.3, -.1, d.x));
    float box2 = smoothstep(.7, .88, d.x) * (1 - smoothstep(.2, .6, abs(d.y)));
    return room + box1 * 1.5 + box2 * .8;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 fc = float2(pos.x, uRes.y - pos.y);
    float2 uv = (fc - uRes * .5) / (uRes.y * .5);
    float3 col = uBg;
    float3 ro = float3(0, 0, 4), rd = normalize(float3(uv, -2.05));
    float t = 1.5, minD = 1e3; bool hit = false; float2 m = 0;
    [loop] for (int i = 0; i < 90; i++)
    {
        m = map(ro + rd * t);
        minD = min(minD, m.x);
        if (m.x < .0012) { hit = true; break; }
        t += m.x * .8;
        if (t > 6.5) break;
    }
    float pw = 3 / (uRes.y * .5);
    if (!hit)
    {
        col = lerp(col, .02, (1 - smoothstep(0, pw, minD)) * .8);
    }
    else
    {
        float3 p = ro + rd * t, n = calcNormal(p), v = -rd;
        n = normalize(n + .3 * ferroDots(p, 70));
        float3 r = reflect(rd, n);
        float fres = pow(1 - max(dot(n, v), 0), 4);
        float3 Ld = normalize(float3(-.5, .7, .6));
        float spec = pow(max(dot(r, Ld), 0), 220) * 1.3, glint = pow(max(dot(r, Ld), 0), 900) * 2;
        float3 e = env(r);
        if (m.y < 1.5)
            col = float3(.012, .012, .014) + e * (.045 + .88 * fres) + spec + glint * uGold;
        else if (m.y < 2.5)
            col = float3(.72, .72, .70) * (.35 + .65 * max(dot(n, Ld), 0)) + e * (.08 + .6 * fres) + spec * .8;
        else
            col = uGold * (.28 + .72 * max(dot(n, Ld), 0)) + uGold * e * (.25 + .6 * fres) + spec + glint;
    }
    float border = max(abs(uv.x) / (uRes.x / uRes.y), abs(uv.y));
    col = lerp(col, uBg, smoothstep(.9, 1, border));
    return float4(col, 1);
}
