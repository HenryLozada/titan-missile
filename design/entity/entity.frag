#version 300 es
precision highp float;
uniform vec2 uRes; uniform float uTime; uniform float uCells;
uniform float uW[5]; uniform vec4 uFx; uniform vec3 uMouse; uniform float uRot;
uniform float uShock; uniform vec3 uBg; uniform float uDark; uniform vec3 uPal[5]; uniform float uGlowAmt;
out vec4 outColor;

// Face normals (GDF). Dodecahedron: 6 axes -> 12 pentagonal faces. Icosahedron: 10 axes -> 20 triangles.
const vec3 CUBE[3] = vec3[3](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1));
const vec3 OCTA[4] = vec3[4](vec3(.57735,.57735,.57735), vec3(-.57735,.57735,.57735), vec3(.57735,-.57735,.57735), vec3(.57735,.57735,-.57735));
const vec3 TETRA[4] = vec3[4](vec3(.57735,.57735,.57735), vec3(-.57735,-.57735,.57735), vec3(-.57735,.57735,-.57735), vec3(.57735,-.57735,-.57735));
const vec3 ICOX[6] = vec3[6](vec3(0.,.356822,.934172), vec3(0.,-.356822,.934172), vec3(.934172,0.,.356822), vec3(-.934172,0.,.356822), vec3(.356822,.934172,0.), vec3(-.356822,.934172,0.));
const vec3 DODE[6] = vec3[6](vec3(0.,.850651,.525731), vec3(0.,-.850651,.525731), vec3(.525731,0.,.850651), vec3(-.525731,0.,.850651), vec3(.850651,.525731,0.), vec3(-.850651,.525731,0.));

float W[5]; vec4 FX; vec3 MOUSE; float ROT; float T; float SEED;

void top2(float v, inout float a, inout float b){ if (v > a){ b = a; a = v; } else if (v > b){ b = v; } }
// (distance, edge proximity: 0 exactly on an edge)
vec2 fCube(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<3;i++) top2(abs(dot(p,CUBE[i])),a,b); return vec2(a-r, a-b); }
vec2 fOcta(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<4;i++) top2(abs(dot(p,OCTA[i])),a,b); return vec2(a-r, a-b); }
vec2 fTetra(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<4;i++) top2(dot(p,TETRA[i]),a,b); return vec2(a-r, a-b); }
vec2 fIcosa(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<4;i++) top2(abs(dot(p,OCTA[i])),a,b); for(int i=0;i<6;i++) top2(abs(dot(p,ICOX[i])),a,b); return vec2(a-r, a-b); }
vec2 fDode(vec3 p, float r){ float a=-1e3,b=-1e3; for(int i=0;i<6;i++) top2(abs(dot(p,DODE[i])),a,b); return vec2(a-r, a-b); }
float smin(float a, float b, float k){ float h = clamp(.5 + .5*(b-a)/k, 0., 1.); return mix(b, a, h) - k*h*(1.-h); }

// x distance · y edge proximity · z vertex glow (tetra needles)
vec3 map(vec3 pv){
  vec3 p = pv;
  float c = cos(ROT), s = sin(ROT);
  p.xz = mat2(c, -s, s, c)*p.xz;
  float ct = cos(.5), st = sin(.5);
  p.yz = mat2(ct, -st, st, ct)*p.yz;
  float scale = 1. - .12*FX.x - .22*FX.w;                     // contraction / violent gulp
  vec3 q = p/scale;
  float L = length(q); vec3 dir = q/max(L, 1e-4);

  vec2 acc = vec2(0.);
  if (W[0] > .001) acc += W[0]*fDode(q, .62);
  if (W[1] > .001) acc += W[1]*fOcta(q, .55);
  if (W[2] > .001) acc += W[2]*fTetra(q, .30);
  if (W[3] > .001) acc += W[3]*fIcosa(q, .64);
  if (W[4] > .001) acc += W[4]*fCube(q, .52);
  // Solid most of the time; melts to liquid only mid-transition between two solids.
  float mw = max(max(max(W[0], W[1]), max(W[2], W[3])), W[4]);
  float melt = clamp((1. - mw)*2.2, 0., 1.);
  float d = mix(acc.x - .012, L - .74, .75*melt);
  // surface life: a tiny ripple, never enough to hide the faces
  float ripple = W[0]*.004 + W[1]*.003 + W[2]*.002 + W[3]*.007 + W[4]*.0015 + melt*.02;
  d -= ripple*(sin(7.*q.x + 1.3*T + SEED) + sin(7.6*q.y - 1.1*T) + sin(6.8*q.z + .9*T));
  d -= W[3]*.006*sin(10.*L - T*3.5);                            // liberation: slow travelling wave

  float tip = 0.;
  if (FX.y > .001){                                            // MAX BOOST: a true tetrahedron; only the vertex points light up
    float cv = 0.;
    for (int i=0;i<4;i++) cv = max(cv, dot(dir, -TETRA[i]));
    tip = FX.y*pow(smoothstep(.97, 1., cv), 3.);
  }
  d *= scale;

  vec3 dv = normalize(pv);                                     // magnetic pull toward the cursor (view space)
  vec3 md = normalize(vec3(MOUSE.xy, .9));
  float a = max(dot(dv, md), 0.);
  d -= MOUSE.z*(.08*pow(a, 8.) + .06*pow(a, 60.));

  float drops = 1e3;
  for (int i=0;i<6;i++){
    float fi = float(i);
    float th = fi*1.047 + T*(.2 + .05*fi) + SEED;
    float orb = 1.1 + .05*sin(T*.4 + fi*1.9);
    orb = mix(orb, .15, clamp(FX.z*1.3 - fi*.06, 0., 1.));
    vec3 cc = vec3(cos(th)*orb, sin(fi*2.1 + T*.3)*.3*orb, sin(th)*orb);
    drops = min(drops, length(pv - cc) - (.06 + .025*sin(fi*3.7)));
  }
  return vec3(smin(d, drops, .2), acc.y + melt, tip);
}

vec3 calcNormal(vec3 p){
  const vec2 k = vec2(1., -1.); float e = .0012;
  return normalize(k.xyy*map(p + k.xyy*e).x + k.yyx*map(p + k.yyx*e).x + k.yxy*map(p + k.yxy*e).x + k.xxx*map(p + k.xxx*e).x);
}

float hash3(vec3 p){ p = fract(p*.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y)*p.z); }
vec3 ferroDots(vec3 p, float scale){
  vec3 g = p*scale, id = floor(g), f = fract(g) - .5;
  vec3 c = (vec3(hash3(id), hash3(id + 7.1), hash3(id + 3.3)) - .5)*.4;
  vec3 dv = f - c;
  float bump = 1. - smoothstep(0., .34, length(dv));
  return normalize(dv + 1e-4)*bump*bump;
}

vec3 env(vec3 d){                                              // clinical studio: pale room, two long softboxes
  vec3 room = mix(uBg*.72, mix(uBg, vec3(1.), .5), smoothstep(-.4, .9, d.y));
  float box1 = smoothstep(.35, .45, d.y)*(1. - smoothstep(.85, .95, d.y))*smoothstep(-.85, -.65, d.x)*(1. - smoothstep(-.3, -.1, d.x));
  float box2 = smoothstep(.72, .88, d.x)*(1. - smoothstep(.25, .55, abs(d.y)));
  return room + vec3(box1*1.5 + box2*.8);
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
  if (cells > 1.5){                                            // identity sheet: one state per cell
    int ci = int(cell);
    for (int i=0;i<5;i++) W[i] = (i == ci) ? 1. : 0.;
    SEED = cell*2.3; MOUSE = vec3(0.); shock = 0.;
    FX = ci == 1 ? vec4(.4, 0., .5, 0.) : ci == 2 ? vec4(0., 1., 1., 0.) : vec4(0.);
    ROT = uRot*(ci == 1 ? 3. : 1.) + cell*.9;
  }
  vec3 glow = W[0]*uPal[0] + W[1]*uPal[1] + W[2]*uPal[2] + W[3]*uPal[3] + W[4]*uPal[4];

  vec3 col = uBg;
  col *= 1. - .1*(1. - uDark)*exp(-pow(length(vec2(uv.x*1.1, (uv.y + .82)*5.)), 2.)*1.5);   // contact shadow
  if (shock > 0.){
    float ring = abs(length(uv) - (.45 + shock*.45));
    col = mix(col, glow, .6*(1. - shock)*(1. - smoothstep(0., .012, ring)));
  }

  vec3 ro = vec3(0., 0., 3.6), rd = normalize(vec3(uv, -2.45));
  float b = dot(ro, rd), h = b*b - (dot(ro, ro) - 1.75*1.75);
  float minD = 1e3, t = 0.; bool hit = false; vec3 m = vec3(0.);
  if (h > 0.){
    t = max(-b - sqrt(h), 0.); float tmax = -b + sqrt(h);
    for (int i=0;i<80;i++){
      m = map(ro + rd*t);
      minD = min(minD, m.x);
      if (m.x < .0012){ hit = true; break; }
      t += m.x*.55;
      if (t > tmax) break;
    }
  }
  float pw = 2.4/R;
  if (!hit){
    col = mix(col, glow, mix(.10, .38, uDark)*uGlowAmt*exp(-max(minD, 0.)*10.)*(minD < 1e2 ? 1. : 0.));   // faint halo
    col = mix(col, vec3(.015), (1. - smoothstep(0., pw, minD))*.85);                                      // silhouette AA
  } else {
    vec3 p = ro + rd*t, n = calcNormal(p), v = -rd;
    float liquid = W[0]*.5 + W[1]*.3 + W[2]*.2 + W[3]*1. + W[4]*.25;          // reflective liquid skin
    n = normalize(n + .018*liquid*vec3(sin(p.y*22. + T*1.8), sin(p.z*22. - T*1.5), sin(p.x*22. + T*1.2)));
    n = normalize(n + .22*ferroDots(p, 64.));                                              // ferro grain
    vec3 r = reflect(rd, n);
    float fres = pow(1. - max(dot(n, v), 0.), 4.);
    col = vec3(.012, .012, .015) + env(r)*(.045 + .88*fres);
    vec3 Ld = normalize(vec3(-.5, .7, .6));
    col += pow(max(dot(r, Ld), 0.), 220.)*1.4;
    col += pow(max(dot(r, Ld), 0.), 900.)*2.2*glow;                                        // grain glints
    col += W[1]*pow(max(sin(r.y*16. + T*7.), 0.), 30.)*.25;                                // faint platinum scan
    float pulse = W[0]*(.7 + .3*sin(T*1.3)) + W[1]*(.75 + .25*sin(T*5.)) + W[2]*(.9 + .1*sin(T*9.))
                + W[3]*(.5 + .5*exp(-fract(T*.5)*3.)) + W[4];
    float edge = exp(-m.y*45.);
    col += glow*edge*pulse*.85*uGlowAmt;                                                   // light lives ONLY on edges
    col += glow*m.z*1.1*uGlowAmt;                                                          // …and on the boost vertices
    col += mix(vec3(1.), glow, .5)*fres*mix(.10, .55, uDark);                              // rim: visible on dark UIs
  }
  float border = max(abs(uv.x)*R/(res.x*.5), abs(uv.y)*R/(res.y*.5));
  col = mix(col, uBg, smoothstep(.86, .99, border));                                      // fade into the page, no cuts
  outColor = vec4(col, 1.);
}
