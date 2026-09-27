// La entidad: ferrofluid that morphs across the five platonic solids. Raymarched SDF, one pass.
// Port of design/entity/entity.frag (GLSL ES 3.00). SV_Position is top-left, so y is flipped once in PS.

cbuffer Params : register(b0)
{
    float2 uRes;   float uTime;  float uRot;
    float4 uFx;                                 // x charge · y boost · z absorb · w gulp
    float4 uW0;                                 // state weights 0..3
    float  uW4;    float uShock; float uDark; float uGlowAmt;
    float3 uMouse; float uAura;                 // xy: field direction, z: strength
    float3 uBg;    float _p0;
    float3 uGlow;  float _p1;
};

static const float3 CUBE[3]  = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
static const float3 OCTA[4]  = { float3(.57735,.57735,.57735), float3(-.57735,.57735,.57735), float3(.57735,-.57735,.57735), float3(.57735,.57735,-.57735) };
static const float3 TETRA[4] = { float3(.57735,.57735,.57735), float3(-.57735,-.57735,.57735), float3(-.57735,.57735,-.57735), float3(.57735,-.57735,-.57735) };
static const float3 ICOX[6]  = { float3(0,.356822,.934172), float3(0,-.356822,.934172), float3(.934172,0,.356822), float3(-.934172,0,.356822), float3(.356822,.934172,0), float3(-.356822,.934172,0) };
static const float3 DODE[6]  = { float3(0,.850651,.525731), float3(0,-.850651,.525731), float3(.525731,0,.850651), float3(-.525731,0,.850651), float3(.850651,.525731,0), float3(-.850651,.525731,0) };

static float W[5];

float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

void top2(float v, inout float a, inout float b) { if (v > a) { b = a; a = v; } else if (v > b) { b = v; } }
float2 fCube(float3 p, float r)  { float a = -1e3, b = -1e3; [unroll] for (int i = 0; i < 3; i++) top2(abs(dot(p, CUBE[i])), a, b); return float2(a - r, a - b); }
float2 fOcta(float3 p, float r)  { float a = -1e3, b = -1e3; [unroll] for (int i = 0; i < 4; i++) top2(abs(dot(p, OCTA[i])), a, b); return float2(a - r, a - b); }
float2 fTetra(float3 p, float r) { float a = -1e3, b = -1e3; [unroll] for (int i = 0; i < 4; i++) top2(dot(p, TETRA[i]), a, b); return float2(a - r, a - b); }
float2 fIcosa(float3 p, float r) { float a = -1e3, b = -1e3; [unroll] for (int i = 0; i < 4; i++) top2(abs(dot(p, OCTA[i])), a, b); [unroll] for (int j = 0; j < 6; j++) top2(abs(dot(p, ICOX[j])), a, b); return float2(a - r, a - b); }
float2 fDode(float3 p, float r)  { float a = -1e3, b = -1e3; [unroll] for (int i = 0; i < 6; i++) top2(abs(dot(p, DODE[i])), a, b); return float2(a - r, a - b); }
float smin(float a, float b, float k) { float h = saturate(.5 + .5 * (b - a) / k); return lerp(b, a, h) - k * h * (1 - h); }
float2 rot(float2 v, float c, float s) { return float2(c * v.x + s * v.y, -s * v.x + c * v.y); }

// x distance · y edge proximity · z vertex glow
float3 map(float3 pv)
{
    float T = uTime;
    float3 p = pv;
    p.xz = rot(p.xz, cos(uRot), sin(uRot));
    p.yz = rot(p.yz, cos(.5), sin(.5));
    float scale = 1 - .12 * uFx.x - .22 * uFx.w;
    float3 q = p / scale;
    float L = length(q); float3 dir = q / max(L, 1e-4);

    float2 acc = 0;
    [branch] if (W[0] > .001) acc += W[0] * fDode(q, .62);
    [branch] if (W[1] > .001) acc += W[1] * fOcta(q, .55);
    [branch] if (W[2] > .001) acc += W[2] * fTetra(q, .30);
    [branch] if (W[3] > .001) acc += W[3] * fIcosa(q, .64);
    [branch] if (W[4] > .001) acc += W[4] * fCube(q, .52);
    float mw = max(max(max(W[0], W[1]), max(W[2], W[3])), W[4]);
    float melt = saturate((1 - mw) * 2.2);
    float d = lerp(acc.x - .012, L - .74, .75 * melt);
    float ripple = W[0] * .004 + W[1] * .003 + W[2] * .002 + W[3] * .007 + W[4] * .0015 + melt * .02;
    d -= ripple * (sin(7 * q.x + 1.3 * T) + sin(7.6 * q.y - 1.1 * T) + sin(6.8 * q.z + .9 * T));
    d -= W[3] * .006 * sin(10 * L - T * 3.5);

    float tip = 0;
    [branch] if (uFx.y > .001)
    {
        float cv = 0;
        [unroll] for (int i = 0; i < 4; i++) cv = max(cv, dot(dir, -TETRA[i]));
        tip = uFx.y * pow(smoothstep(.985, 1, cv), 4);
    }
    d *= scale;

    float3 dv = normalize(pv);
    float3 md = normalize(float3(uMouse.xy, .9));
    float a = max(dot(dv, md), 0);
    d -= uMouse.z * (.08 * pow(a, 8) + .06 * pow(a, 60));

    float drops = 1e3;
    [loop] for (int k = 0; k < 16; k++)
    {
        float fi = k;
        float h1 = frac(sin(fi * 12.9898) * 43758.5453), h2 = frac(sin(fi * 78.233) * 12543.21);
        float sp = (.12 + .16 * h1) * ((k & 1) == 0 ? 1 : -1);
        float th = fi * 2.39996 + T * sp;
        float inc = (h2 - .5) * 1.4;
        float orb = .96 + .24 * h2 + .03 * sin(T * .5 + fi);
        orb = lerp(orb, .12, saturate(uFx.z * 1.3 - fi * .035));
        float3 cc = float3(cos(th) * orb, sin(th) * orb * sin(inc), sin(th) * orb * cos(inc));
        float3 tang = normalize(float3(-sin(th), cos(th) * sin(inc), cos(th) * cos(inc))) * sign(sp);
        float rad = .02 + .042 * h1 * h1;
        float head = length(pv - cc) - rad;
        float tail = length(pv - (cc - tang * rad * 2.4)) - rad * .42;
        drops = min(drops, smin(head, tail, rad * .9));
    }
    return float3(smin(d, drops, .16), acc.y + melt, tip);
}

float3 calcNormal(float3 p)
{
    const float2 k = float2(1, -1); const float e = .0012;
    return normalize(k.xyy * map(p + k.xyy * e).x + k.yyx * map(p + k.yyx * e).x +
                     k.yxy * map(p + k.yxy * e).x + k.xxx * map(p + k.xxx * e).x);
}

float hash3(float3 p) { p = frac(p * .1031); p += dot(p, p.zyx + 31.32); return frac((p.x + p.y) * p.z); }
float3 ferroDots(float3 p, float scale)
{
    float3 g = p * scale, id = floor(g), f = frac(g) - .5;
    float3 c = (float3(hash3(id), hash3(id + 7.1), hash3(id + 3.3)) - .5) * .4;
    float3 dv = f - c;
    float bump = 1 - smoothstep(0, .34, length(dv));
    return normalize(dv + 1e-4) * bump * bump;
}

float3 env(float3 d)
{
    float3 room = lerp(uBg * .72, lerp(uBg, 1, .5), smoothstep(-.4, .9, d.y));
    float box1 = smoothstep(.35, .45, d.y) * (1 - smoothstep(.85, .95, d.y)) * smoothstep(-.85, -.65, d.x) * (1 - smoothstep(-.3, -.1, d.x));
    float box2 = smoothstep(.72, .88, d.x) * (1 - smoothstep(.25, .55, abs(d.y)));
    return room + box1 * 1.5 + box2 * .8;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float T = uTime;
    W[0] = uW0.x; W[1] = uW0.y; W[2] = uW0.z; W[3] = uW0.w; W[4] = uW4;
    float2 fc = float2(pos.x, uRes.y - pos.y);
    float R = min(uRes.x, uRes.y) * .5;
    float2 uv = (fc - uRes * .5) / R;
    float3 glow = uGlow;

    float3 col = uBg;
    col *= 1 - .1 * (1 - uDark) * exp(-pow(length(float2(uv.x * 1.1, (uv.y + .82) * 5)), 2) * 1.5);
    if (uShock > 0)
    {
        float ring = abs(length(uv) - (.45 + uShock * .45));
        col = lerp(col, glow, .6 * (1 - uShock) * (1 - smoothstep(0, .012, ring)));
    }

    float3 ro = float3(0, 0, 3.6), rd = normalize(float3(uv, -2.45));
    float b = dot(ro, rd), h = b * b - (dot(ro, ro) - 1.75 * 1.75);
    float minD = 1e3, t = 0; bool hit = false; float3 m = 0;
    [branch] if (h > 0)
    {
        t = max(-b - sqrt(h), 0); float tmax = -b + sqrt(h);
        [loop] for (int i = 0; i < 80; i++)
        {
            m = map(ro + rd * t);
            minD = min(minD, m.x);
            if (m.x < .0012) { hit = true; break; }
            t += m.x * .55;
            if (t > tmax) break;
        }
    }
    float pw = 2.4 / R;
    if (!hit)
    {
        float cd = max(minD, 0);
        float3 cor = lerp(float3(1, .97, .88), glow, smoothstep(0, .12, cd));
        cor = lerp(cor, float3(.45, .09, .05), smoothstep(.12, .42, cd) * uAura * .75);
        float flick = .93 + .07 * sin(T * 3.1) * sin(T * 1.7);
        float k = exp(-cd * lerp(16, 7, uAura)) * (minD < 1e2 ? 1 : 0);
        col = lerp(col, cor, saturate(k * (.2 + .8 * uAura) * flick * lerp(.55, .9, uDark)));
        col = lerp(col, .015, (1 - smoothstep(0, pw, minD)) * .85);
    }
    else
    {
        float3 p = ro + rd * t, n = calcNormal(p), v = -rd;
        float liquid = W[0] * .5 + W[1] * .3 + W[2] * .2 + W[3] * 1 + W[4] * .25;
        n = normalize(n + .018 * liquid * float3(sin(p.y * 22 + T * 1.8), sin(p.z * 22 - T * 1.5), sin(p.x * 22 + T * 1.2)));
        n = normalize(n + .10 * ferroDots(p, 96));
        float3 r = reflect(rd, n);
        float fres = pow(1 - max(dot(n, v), 0), 4);
        col = float3(.012, .012, .015) + env(r) * (.045 + .88 * fres);
        float3 Ld = normalize(float3(-.5, .7, .6));
        col += pow(max(dot(r, Ld), 0), 220) * 1.4;
        col += pow(max(dot(r, Ld), 0), 900) * 1.2 * lerp(1, glow, .5);
        col += W[1] * pow(max(sin(r.y * 16 + T * 7), 0), 30) * .25;
        float pulse = W[0] * (.7 + .3 * sin(T * 1.3)) + W[1] * (.75 + .25 * sin(T * 5)) + W[2] * (.9 + .1 * sin(T * 9))
                    + W[3] * (.5 + .5 * exp(-frac(T * .5) * 3)) + W[4];
        float edge = exp(-m.y * 110);
        col += lerp(float3(1, .96, .86), glow, .45) * edge * pulse * .75 * uGlowAmt;
        col += lerp(float3(.5, .1, .05), glow, .6) * m.z * .9 * uGlowAmt;
        col += glow * uAura * .05 * pow(max(dot(n, v), 0), 3) * (.6 + .4 * sin(T * 2));
        col += lerp(1, glow, .4) * fres * lerp(.10, .5, uDark);
    }
    float border = max(abs(uv.x) * R / (uRes.x * .5), abs(uv.y) * R / (uRes.y * .5));
    col = lerp(col, uBg, smoothstep(.86, .99, border));
    return float4(col, 1);
}
