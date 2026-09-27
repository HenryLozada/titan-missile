#version 300 es
precision highp float;
uniform vec2 uRes; uniform float uTime; uniform float uCells;
uniform float uW[5]; uniform vec4 uFx; uniform vec3 uMouse; uniform float uRot;
uniform float uShock; uniform vec3 uBg; uniform float uDark; uniform vec3 uPal[5];
out vec4 outColor;

// Face normals of the platonic solids (GDF, normalized)
const vec3 CUBE[3] = vec3[3](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1));
const vec3 OCTA[4] = vec3[4](vec3(.57735,.57735,.57735), vec3(-.57735,.57735,.57735), vec3(.57735,-.57735,.57735), vec3(.57735,.57735,-.57735));
const vec3 TETRA[4] = vec3[4](vec3(.57735,.57735,.57735), vec3(-.57735,-.57735,.57735), vec3(-.57735,.57735,-.57735), vec3(.57735,-.57735,-.57735));
const vec3 ICOX[6] = vec3[6](vec3(0.,.356822,.934172), vec3(0.,-.356822,.934172), vec3(.934172,0.,.356822), vec3(-.934172,0.,.356822), vec3(.356822,.934172,0.), vec3(-.356822,.934172,0.));
const vec3 DODE[6] = vec3[6](vec3(0.,.850651,.525731), vec3(0.,-.850651,.525731), vec3(.525731,0.,.850651), vec3(-.525731,0.,.850651), vec3(.850651,.525731,0.), vec3(-.850651,.525731,0.));

float W[5]; vec4 FX; vec3 MOUSE; float ROT; float T; float SEED;

void top2(float v, inout float a, inout float b){ if (v > a){ b = a; a = v; } else if (v > b){ b = v; } }
// each returns (distance, edge proximity: 0 on an edge)
vec2 fCube(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<3;i++) top2(abs(dot(p,CUBE[i])),a,b); return vec2(a-r, a-b); }
vec2 fOcta(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<4;i++) top2(abs(dot(p,OCTA[i])),a,b); return vec2(a-r, a-b); }
vec2 fTetra(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<4;i++) top2(dot(p,TETRA[i]),a,b); return vec2(a-r, a-b); }
vec2 fIcosa(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<4;i++) top2(abs(dot(p,OCTA[i])),a,b); for(int i=0;i<6;i++) top2(abs(dot(p,ICOX[i])),a,b); return vec2(a-r, a-b); }
vec2 fDode(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<6;i++) top2(abs(dot(p,DODE[i])),a,b); return vec2(a-r, a-b); }
float smin(float a, float b, float k){ float h = clamp(.5 + .5*(b-a)/k, 0., 1.); return mix(b, a, h) - k*h*(1.-h); }

// x: distance, y: edge proximity, z: spike-tip amount
vec3 map(vec3 pv){
  vec3 p = pv;
  float c = cos(ROT), s = sin(ROT);
  p.xz = mat2(c, -s, s, c)*p.xz;
  float ct = cos(.42), st = sin(.42);
  p.yz = mat2(ct, -st, st, ct)*p.yz;
  float scale = 1. - .14*FX.x - .22*FX.w;                    // contraction / violent gulp
  vec3 q = p/scale;
  float L = length(q); vec3 dir = q/max(L, 1e-4);

  float deform = W[0]*1. + W[1]*.5 + W[2]*.6 + W[3]*1.6 + W[4]*.06;
  float wob = (sin(3.1*q.x + 1.3*T + SEED) + sin(3.4*q.y - 1.1*T) + sin(2.8*q.z + .8*T + SEED))*.013*deform;
  wob += W[3]*.03*sin(9.*L - T*4.);                           // liberation: travelling surface wave
  float breathe = .02*sin(T*1.3)*(W[0] + W[3]);

  vec2 acc = vec2(0.);
  if (W[0] > .001) acc += W[0]*fDode(q, .64);
  if (W[1] > .001) acc += W[1]*fOcta(q, .56);
  if (W[2] > .001) acc += W[2]*fTetra(q, .40);
  if (W[3] > .001) acc += W[3]*fIcosa(q, .66);
  if (W[4] > .001) acc += W[4]*fCube(q, .55);
  float sol = W[0]*.45 + W[1]*.85 + W[2]*.8 + W[3]*.5 + W[4]*.97;
  float d = mix(L - (.80 + breathe), acc.x - .05, sol) - wob;

  float tip = 0.;
  if (FX.y > .001){                                           // ferrofluid spikes + tetra vertex tension
    float cs = 0.;
    for (int i=0;i<4;i++) cs = max(cs, abs(dot(dir, OCTA[i])));
    for (int i=0;i<6;i++) cs = max(cs, abs(dot(dir, ICOX[i])));
    for (int i=0;i<6;i++) cs = max(cs, abs(dot(dir, DODE[i])));
    float cv = 0.;
    for (int i=0;i<4;i++) cv = max(cv, dot(dir, -TETRA[i]));
    float cone = pow(smoothstep(.9, 1., cs), 2.);
    float vert = pow(smoothstep(.8, 1., cv), 3.);
    d -= FX.y*(.15*cone + .34*vert)*(.92 + .08*sin(T*9. + SEED));
    tip = FX.y*(cone*.6 + vert);
  }
  d *= scale;

  vec3 dv = normalize(pv);                                    // magnetic pull toward the cursor (view space)
  vec3 md = normalize(vec3(MOUSE.xy, .9));
  float a = max(dot(dv, md), 0.);
  d -= MOUSE.z*(.11*pow(a, 6.) + .08*pow(a, 48.));

  float drops = 1e3;
  for (int i=0;i<6;i++){
    float fi = float(i);
    float th = fi*1.047 + T*(.22 + .05*fi) + SEED;
    float orb = 1.28 + .12*sin(T*.4 + fi*1.9);
    orb = mix(orb, .15, clamp(FX.z*1.3 - fi*.06, 0., 1.));
    vec3 cc = vec3(cos(th)*orb, sin(fi*2.1 + T*.3)*.35*orb, sin(th)*orb);
    drops = min(drops, length(pv - cc) - (.08 + .03*sin(fi*3.7)));
  }
  return vec3(smin(d, drops, .22), acc.y, tip);
}

vec3 calcNormal(vec3 p){
  const vec2 k = vec2(1., -1.); float e = .0015;
  return normalize(k.xyy*map(p + k.xyy*e).x + k.yyx*map(p + k.yyx*e).x + k.yxy*map(p + k.yxy*e).x + k.xxx*map(p + k.xxx*e).x);
}

vec3 env(vec3 d){
  vec3 sky = mix(uBg*.8, mix(uBg, vec3(1.), .55), smoothstep(-.3, .9, d.y));
  float box1 = smoothstep(.35, .45, d.y)*(1. - smoothstep(.85, .95, d.y))*smoothstep(-.8, -.6, d.x)*(1. - smoothstep(-.25, -.05, d.x));
  float box2 = smoothstep(.75, .9, d.x)*(1. - smoothstep(.2, .5, abs(d.y)));
  return sky + vec3(box1*1.6 + box2*.9);
}

void main(){
  float cells = max(uCells, 1.);
  float cw = uRes.x/cells;
  float cell = floor(gl_FragCoord.x/cw);
  vec2 res = vec2(cw, uRes.y);
  vec2 fc = vec2(gl_FragCoord.x - cell*cw, gl_FragCoord.y);
  float R = min(res.x, res.y)*.5;
  vec2 uv = (fc - res*.5)/R;

  for (int i=0;i<5;i++) W[i] = uW[i];
  FX = uFx; MOUSE = uMouse; ROT = uRot; T = uTime; SEED = 0.;
  float shock = uShock;
  if (cells > 1.5){                                           // identity sheet: one state per cell
    int ci = int(cell);
    for (int i=0;i<5;i++) W[i] = (i == ci) ? 1. : 0.;
    SEED = cell*2.3; MOUSE = vec3(0.); shock = 0.;
    FX = ci == 1 ? vec4(.5, 0., .55, 0.) : ci == 2 ? vec4(0., 1., 1., 0.) : vec4(0.);
    ROT = uRot*(ci == 1 ? 4. : 1.) + cell*.9;
  }
  vec3 glow = W[0]*uPal[0] + W[1]*uPal[1] + W[2]*uPal[2] + W[3]*uPal[3] + W[4]*uPal[4];

  vec3 col = uBg;
  col *= 1. - .12*(1. - uDark)*exp(-pow(length(vec2(uv.x*1.1, (uv.y + .80)*5.)), 2.)*1.5);   // contact shadow
  if (shock > 0.){
    float ring = abs(length(uv) - (.5 + shock*.95));
    col = mix(col, glow, (1. - shock)*(1. - smoothstep(0., .016, ring)));
  }

  vec3 ro = vec3(0., 0., 3.6), rd = normalize(vec3(uv, -2.2));
  float b = dot(ro, rd), h = b*b - (dot(ro, ro) - 1.75*1.75);
  float minD = 1e3, t = 0.; bool hit = false; vec3 m = vec3(0.);
  if (h > 0.){
    t = max(-b - sqrt(h), 0.); float tmax = -b + sqrt(h);
    for (int i=0;i<72;i++){
      m = map(ro + rd*t);
      minD = min(minD, m.x);
      if (m.x < .0015){ hit = true; break; }
      t += m.x*.6;
      if (t > tmax) break;
    }
  }
  float pw = 2.4/R;                                            // ~1.5 px in world units
  float haloAmt = mix(.16, .5, uDark);
  if (!hit){
    col = mix(col, glow, haloAmt*exp(-max(minD, 0.)*9.)*(minD < 1e2 ? 1. : 0.));   // soft outer halo
    col = mix(col, vec3(.02) + glow*.25, (1. - smoothstep(0., pw, minD))*.85);       // silhouette AA
  } else {
    vec3 p = ro + rd*t, n = calcNormal(p), v = -rd, r = reflect(rd, n);
    float fres = pow(1. - max(dot(n, v), 0.), 4.);
    float rough = W[4];
    float refl = W[0]*.8 + W[1]*1. + W[2]*1.15 + W[3]*1.25 + W[4]*.35;
    vec3 e = mix(env(r), env(n)*.6, rough);
    vec3 base = vec3(.018, .018, .022) + W[4]*vec3(.07, .072, .08);                 // titanium in persistence
    col = base + e*(.035 + .9*fres)*refl;
    vec3 Ld = normalize(vec3(-.5, .7, .6));
    col += pow(max(dot(r, Ld), 0.), mix(180., 20., rough))*mix(1.6, .25, rough);
    col += W[1]*pow(max(sin(r.y*18. + T*9.), 0.), 24.)*.55;                          // platinum scan flashes
    float sol = W[0]*.45 + W[1]*.85 + W[2]*.8 + W[3]*.5 + W[4]*.97;
    float pulse = W[0]*(.6 + .4*sin(T*1.3)) + W[1]*(.7 + .3*sin(T*6.)) + W[2]*(.85 + .15*sin(T*14.))
                + W[3]*(.35 + .65*exp(-fract(T*.5)*3.)) + W[4];
    col += glow*exp(-m.y*38.)*sol*pulse*1.15;                                         // glowing edges
    float arc = step(.62, fract(sin(floor(T*26.)*12.9898 + dot(floor(p*14.), vec3(1., 57., 113.)))*43758.5453));
    col += glow*m.z*(.3 + 1.3*arc*W[2]);                                              // micro discharge arcs
    col += mix(vec3(1.), glow, .6)*fres*mix(.18, .6, uDark);                          // rim: never lost on dark UIs
  }
  outColor = vec4(col, 1.);
}
