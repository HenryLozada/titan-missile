#version 300 es
precision highp float;
uniform vec2 uRes; uniform float uTime; uniform float uUse; uniform float uStb; uniform float uN;
uniform float uPulse; uniform vec3 uBg; uniform vec3 uGold;
out vec4 outColor;
const float H = 1.62;
const float R = .40;
float T;
float hash3(vec3 p){ p = fract(p*.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y)*p.z); }
float smin(float a, float b, float k){ float h = clamp(.5 + .5*(b-a)/k, 0., 1.); return mix(b, a, h) - k*h*(1.-h); }
vec3 ferroDots(vec3 p, float scale){
  vec3 g = p*scale, id = floor(g), f = fract(g) - .5;
  vec3 c = (vec3(hash3(id), hash3(id + 7.1), hash3(id + 3.3)) - .5)*.4;
  vec3 dv = f - c;
  float bump = 1. - smoothstep(0., .34, length(dv));
  return normalize(dv + 1e-4)*bump*bump;
}
float twist(float y){ return y*(2.3*6.2831853/(2.*H)) + T*.55; }
float radiusAt(float y){ return R*(1. + .07*uPulse*sin(y*11. - T*18.)); }
// x: distance · y: material (0 strand, 1 in use, 2 standby, 3 free)
vec2 map(vec3 p){
  float a = twist(p.y), rr = radiusAt(p.y);
  vec2 c1 = rr*vec2(cos(a), sin(a));
  float strands = min(length(p.xz - c1), length(p.xz + c1)) - .078;
  strands = max(strands, abs(p.y) - H);
  float sp = 2.*H/uN;
  float best = 1e3, mat = 0.;
  float yi0 = floor((p.y + H)/sp);
  for (int k = -1; k <= 1; k++){
    float yi = clamp(yi0 + float(k), 0., uN - 1.);
    float yc = -H + (yi + .5)*sp;
    float ac = twist(yc), rc = radiusAt(yc);
    vec3 B = vec3(rc*cos(ac), yc, rc*sin(ac)), A = vec3(-B.x, yc, -B.z);
    vec3 pa = p - A, ba = B - A;
    float h = clamp(dot(pa, ba)/dot(ba, ba), 0., 1.);
    float dr = length(pa - ba*h) - .042;
    if (dr < best){ best = dr; mat = yi < uUse ? 1. : (yi < uUse + uStb ? 2. : 3.); }
  }
  float d = smin(strands, best, .035);
  return vec2(d, best < strands ? mat : 0.);
}
vec3 calcNormal(vec3 p){
  const vec2 k = vec2(1., -1.); float e = .0015;
  return normalize(k.xyy*map(p + k.xyy*e).x + k.yyx*map(p + k.yyx*e).x + k.yxy*map(p + k.yxy*e).x + k.xxx*map(p + k.xxx*e).x);
}
vec3 env(vec3 d){
  vec3 room = mix(uBg*.7, mix(uBg, vec3(1.), .5), smoothstep(-.4, .9, d.y));
  float box1 = smoothstep(.3, .42, d.y)*(1. - smoothstep(.85, .95, d.y))*smoothstep(-.9, -.6, d.x)*(1. - smoothstep(-.3, -.1, d.x));
  float box2 = smoothstep(.7, .88, d.x)*(1. - smoothstep(.2, .6, abs(d.y)));
  return room + vec3(box1*1.5 + box2*.8);
}
void main(){
  T = uTime;
  vec2 uv = (gl_FragCoord.xy - uRes*.5)/(uRes.y*.5);
  vec3 col = uBg;
  vec3 ro = vec3(0., 0., 4.), rd = normalize(vec3(uv, -2.35));
  float t = 1.5, minD = 1e3; bool hit = false; vec2 m = vec2(0.);
  for (int i = 0; i < 90; i++){
    m = map(ro + rd*t);
    minD = min(minD, m.x);
    if (m.x < .0012){ hit = true; break; }
    t += m.x*.8;
    if (t > 6.5) break;
  }
  float pw = 3./(uRes.y*.5);
  if (!hit){
    col = mix(col, vec3(.02), (1. - smoothstep(0., pw, minD))*.8);
  } else {
    vec3 p = ro + rd*t, n = calcNormal(p), v = -rd;
    n = normalize(n + .3*ferroDots(p, 70.));
    vec3 r = reflect(rd, n);
    float fres = pow(1. - max(dot(n, v), 0.), 4.);
    vec3 Ld = normalize(vec3(-.5, .7, .6));
    float spec = pow(max(dot(r, Ld), 0.), 220.)*1.3, glint = pow(max(dot(r, Ld), 0.), 900.)*2.;
    vec3 e = env(r);
    if (m.y < .5 || m.y < 1.5){                       // black ferro: strands + memory in use
      col = vec3(.012, .012, .014) + e*(.045 + .88*fres) + spec + glint*uGold;
    } else if (m.y < 2.5){                            // white ferro: standby cache
      col = vec3(.72, .72, .70)*(.35 + .65*max(dot(n, Ld), 0.)) + e*(.08 + .6*fres) + spec*.8;
    } else {                                          // yellow ferro: free memory
      col = uGold*(.28 + .72*max(dot(n, Ld), 0.)) + uGold*e*(.25 + .6*fres) + spec + glint;
    }
  }
  float border = max(abs(uv.x)/(uRes.x/uRes.y), abs(uv.y));
  col = mix(col, uBg, smoothstep(.9, 1., border));
  outColor = vec4(col, 1.);
}
