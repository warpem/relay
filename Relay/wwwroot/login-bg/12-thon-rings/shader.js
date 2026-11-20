// Thon rings — fragment shader. The water surface is a sum of drop ripples whose radial profile is a
// real CTF: h = -a·E(k)·sin(χ(k) + φA), χ = π·λ·Δf(θ)·k² − (π/2)·Cs·λ³·k⁴, with astigmatism
// Δf(θ) = Δf + Δfa·cos 2(θ − θa) and a B-factor envelope E = exp(−B·k²/4). Each ripple maps screen
// radius to spatial frequency with k = s·r, and s shrinks as the ripple spreads, so the rings travel outward.
// Height, slope and curvature are evaluated analytically per drop (one sin/cos/exp each).
export const N_DROPS = 16;
export const N_LEAVES = 8;
export const N_PADS = 5;
export const N_FLIES = 6;

export const VERT = `#version 300 es
precision highp float;
in vec3 aPosition;
in vec2 aTexCoord;
void main() { gl_Position = vec4(aTexCoord * 2.0 - 1.0, 0.0, 1.0); }
`;

export const FRAG = `#version 300 es
precision highp float;
out vec4 outColor;

#define ND ${N_DROPS}
#define NL ${N_LEAVES}
#define NP ${N_PADS}
#define NF ${N_FLIES}
#define PHIA 0.0700573   // asin(0.07): 7% amplitude contrast

uniform vec2 uSize;
uniform float uDensity;
uniform float uMix;
uniform vec4 uFocus;          // cx, cy, half w, half h
uniform float uS;
uniform vec4 uDA[ND];         // x, y, R (front radius px), curvature amplitude (1/px)
uniform vec4 uDB[ND];         // s (Å⁻¹ per px), mean defocus (Å), Δfa·cos2θa, Δfa·sin2θa
uniform vec4 uDC[ND];         // π·λ, (π/2)·Cs·λ³, B (Å²), regulariser g² (|∇χ|² near the first ring)
uniform vec4 uLA[NL];         // x, y, angle, half length
uniform vec4 uLB[NL];         // half width, kind (0 leaf, 1 petal), tilt x, tilt y
uniform vec4 uLC[NL];         // rgb, opacity
uniform vec4 uLD[NL];         // rib/vein rgb, curl
uniform vec4 uPA[NP];         // lily pad: x, y, radius, notch angle
uniform vec4 uPB[NP];         // tilt x, tilt y, opacity, hue (0 fresh green … 1 old, bronzed)
uniform vec4 uFA;             // water lily: x, y, petal length, openness (0 bud … 1 open)
uniform vec4 uFB;             // rotation, opacity, rise, tint
// day
uniform vec2 uSunSpot;        // where the sun's mirror image sits (px)
uniform float uSunI;          // sun (clouds dim it)
uniform vec2 uShadowOff;      // floating-leaf shadow offset on the basin floor (px)
uniform float uGlaze;         // celadon pooling amount drift
uniform vec2 uGlazeOff;
// night
uniform vec3 uMoon;           // x, y, radius
uniform float uMoonI;
uniform float uMoonPhase;     // terminator orientation
uniform float uMoonK;         // 1 = full, 0 = half
uniform vec2 uLantern;        // reflected lantern window position (px)
uniform float uLanternI;
uniform vec3 uLanternCol;
uniform vec2 uCloudOff;
uniform float uCloudAmt;
uniform float uSeedF;
uniform vec3 uEye;            // eye above the basin (px; z = height)
uniform vec3 uSunL;           // unit vector toward the sun
uniform vec3 uMoonL;          // unit vector toward the moon
uniform vec3 uLampP;          // lantern window position (px, z above water)
uniform vec2 uLantern2;       // a second, smaller paper lantern (mirror-image position)
uniform vec3 uLamp2P;
uniform float uLantern2I;
uniform vec4 uFF[NF];         // fireflies: x, y, intensity, 0

float specl(vec3 n, vec3 V, vec3 l, float e) { return pow(max(dot(n, normalize(V + l)), 0.0), e); }

float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 hash22(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
  float a = hash12(i), b = hash12(i + vec2(1, 0)), c = hash12(i + vec2(0, 1)), d = hash12(i + vec2(1, 1));
  return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
float fbm(vec2 p) { return vnoise(p) * 0.55 + vnoise(p * 2.03 + 7.1) * 0.3 + vnoise(p * 4.1 + 3.7) * 0.15; }

// crackle glaze: thin lines along Voronoi cell borders
float crackle(vec2 x) {
  vec2 n = floor(x), f = fract(x);
  float F1 = 8.0, F2 = 8.0;
  for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
    vec2 g = vec2(float(i), float(j));
    vec2 r = g + hash22(n + g) * 0.9 + 0.05 - f;
    float d = dot(r, r);
    if (d < F1) { F2 = F1; F1 = d; } else if (d < F2) F2 = d;
  }
  return 1.0 - smoothstep(0.0, 0.022, sqrt(F2) - sqrt(F1));
}

float rectDist(vec2 px) {
  vec2 d = max(abs(px - uFocus.xy) - uFocus.zw, 0.0);
  return length(d);
}

// sum of all ripples: height H (px), slope G, curvature (Laplacian) L
void waves(vec2 p, out float H, out vec2 G, out float L) {
  H = 0.0; G = vec2(0.0); L = 0.0;
  for (int i = 0; i < ND; i++) {
    vec4 A = uDA[i];
    vec2 u = p - A.xy;
    float r2 = dot(u, u);
    if (r2 >= A.z * A.z) continue;
    vec4 B = uDB[i], C = uDC[i];
    float s2 = B.x * B.x;
    float k2 = s2 * r2;                                                // k² (Å⁻²)
    float q = B.y * r2 + B.z * (u.x * u.x - u.y * u.y) + B.w * 2.0 * u.x * u.y;  // Δf(θ)·r²
    float chi = C.x * s2 * q - C.y * k2 * k2;
    vec2 gq = 2.0 * B.y * u + 2.0 * vec2(B.z * u.x + B.w * u.y, -B.z * u.y + B.w * u.x);
    vec2 gchi = C.x * s2 * gq - 4.0 * C.y * s2 * k2 * u;
    float env = exp(-0.25 * C.z * k2);
    float x = sqrt(r2) / A.z;
    float win = 1.0 - smoothstep(0.72, 1.0, x);
    float g2 = dot(gchi, gchi);
    float per = 6.2831853 * inversesqrt(max(g2, 1e-10)) * uDensity;  // local ring period, device px
    float aa = smoothstep(2.4, 4.5, per);
    // the surface's curvature carries the CTF itself, so the caustics on the floor are Thon rings
    float cv = A.w * env * win * aa;
    float ph = chi + PHIA;
    float sn = sin(ph);
    float inv = 1.0 / (g2 + C.w);
    H += -cv * sn * inv;
    G += -cv * cos(ph) * inv * gchi;
    L += cv * sn * g2 * inv;
  }
}

// floating leaf / petal: signed distance (px) and local coords
float leafSdf(vec2 p, int i, out vec2 q) {
  vec4 A = uLA[i], B = uLB[i];
  vec2 d = p - A.xy;
  float ca = cos(A.z), sa = sin(A.z);
  q = vec2(ca * d.x + sa * d.y, -sa * d.x + ca * d.y);
  float Lh = A.w, W = B.x;
  float curl = uLD[i].w;
  q.y -= curl * (q.x * q.x / max(Lh, 1.0) - 0.3 * Lh);    // a slightly bowed outline
  float u = clamp(q.x / Lh, -1.0, 1.0);
  float w, dd;
  if (B.y < 0.5) {
    // leaf: pointed tip (u = 1), rounded base, widest just behind the middle
    float t = 1.0 - u * u;
    w = W * pow(max(t, 0.0), 0.62) * (1.0 - 0.28 * u);
    dd = max(abs(q.y) - w, abs(q.x) - Lh);
    // short stem at the base
    vec2 sq = q - vec2(-Lh * 1.12, 0.0);
    float st = max(abs(sq.y) - 0.9, abs(sq.x) - Lh * 0.16);
    dd = min(dd, st);
  } else {
    // cherry petal: narrow claw at the base, broad rounded blade, notch at the tip
    float t = (u + 1.0) * 0.5;
    w = W * sqrt(max(1.0 - u * u, 0.0)) * mix(0.42, 1.08, smoothstep(0.0, 0.8, t));
    dd = max(abs(q.y) - w, abs(q.x) - Lh);
    float notch = Lh * 0.26 - length((q - vec2(Lh * 1.02, 0.0)) * vec2(1.0, 1.25));
    dd = max(dd, notch);
  }
  return dd * 0.85;
}

float padSdf(vec2 p, int i, out vec2 q, out float ang) {
  vec4 A = uPA[i];
  vec2 d = p - A.xy;
  float ca = cos(A.w), sa = sin(A.w);
  q = vec2(ca * d.x + sa * d.y, -sa * d.x + ca * d.y);
  ang = atan(q.y, q.x);
  float R = A.z * (1.0 + 0.018 * sin(ang * 3.0 + A.w * 5.0) + 0.01 * sin(ang * 7.0 + 1.3 + A.x));
  float dc = length(q) - R;
  float w = 0.085;
  float dn = (abs(q.y) - q.x * tan(w)) * cos(w);       // the slit runs from the rim to the centre
  return max(dc, -dn);
}

// top view of a water lily: two whorls of pointed petals around a golden centre
vec4 lily(vec2 p, float dayK) {
  if (uFB.y <= 0.0) return vec4(0.0);
  vec2 d = p - uFA.xy;
  float Lp = uFA.z, op = uFA.w;
  if (dot(d, d) > Lp * Lp * 1.7) return vec4(0.0);
  float ca = cos(uFB.x), sa = sin(uFB.x);
  d = vec2(ca * d.x + sa * d.y, -sa * d.x + ca * d.y);
  vec3 col = vec3(0.0); float a = 0.0;
  for (int layer = 0; layer < 2; layer++) {
    float fl = float(layer);
    float len = Lp * mix(1.0, 0.72, fl) * mix(0.34, 1.0, op);   // closed petals stand up: short in plan
    float wid = Lp * mix(0.30, 0.27, fl) * mix(0.75, 1.0, op);
    float off = fl * 0.3927;
    for (int k = 0; k < 8; k++) {
      float th = float(k) * 0.7854 + off;
      vec2 dir = vec2(cos(th), sin(th));
      vec2 lq = vec2(dot(d, dir), dot(d, vec2(-dir.y, dir.x)));
      float u = (lq.x - len * 0.5) / (len * 0.5);
      float ww = wid * sqrt(max(1.0 - u * u, 0.0)) * (1.0 - 0.35 * u);
      float sd = max(abs(lq.y) - ww, abs(u) - 1.0);
      float cov = 1.0 - smoothstep(-0.6, 0.6, sd);
      if (cov > 0.0) {
        float tip = smoothstep(-0.2, 1.0, u);
        vec3 pc = mix(vec3(0.99, 0.95, 0.93), vec3(0.95, 0.70, 0.78), tip * (0.55 + 0.45 * uFB.w));
        pc *= mix(0.86, 1.0, smoothstep(0.0, 1.5, abs(lq.y) / max(ww, 0.5) * 3.0)) * mix(1.0, 0.94, fl);
        pc *= 1.0 - 0.1 * (1.0 - fl) * (1.0 - op);
        col = mix(col, pc, cov); a = max(a, cov);
      }
    }
  }
  // golden stamens in the middle (hidden when the flower is shut)
  float rc = length(d);
  float cen = (1.0 - smoothstep(Lp * 0.13 - 0.8, Lp * 0.13 + 0.8, rc)) * smoothstep(0.35, 0.9, op);
  col = mix(col, vec3(0.97, 0.78, 0.30) * (0.9 + 0.1 * vnoise(d * 1.3)), cen);
  a = max(a, cen);
  return vec4(col, a * uFB.y);
}

void main() {
  vec2 px = vec2(gl_FragCoord.x, uSize.y * uDensity - gl_FragCoord.y) / uDensity;
  float s = uS;
  float rd = rectDist(px);
  // the patch of water behind the card lies still (as if sheltered by a lily pad below the card)
  float calm = mix(0.05, 1.0, smoothstep(10.0 * s, 230.0 * s, rd));
  float calmL = smoothstep(0.0, 160.0 * s, rd);

  float H; vec2 G; float L;
  waves(px, H, G, L);
  H *= calm; G *= calm; L *= calm;
  float sl2 = dot(G, G);
  vec3 n = normalize(vec3(-G, 1.0));

  // floating leaves (composited later) + their shadows on the floor
  float shadow = 0.0;
  vec4 leafCol = vec4(0.0);
  vec2 lq;
  for (int i = 0; i < NL; i++) {
    if (uLC[i].a <= 0.0) continue;
    vec4 A = uLA[i];
    float reach = A.w * 1.6 + 30.0 * s;
    vec2 dl = px - A.xy;
    if (dot(dl, dl) > (reach + 40.0 * s) * (reach + 40.0 * s)) continue;
    // shadow on the basin floor, displaced by the sun angle and softened by depth
    float dsh = leafSdf(px - uShadowOff, i, lq);
    shadow = max(shadow, (1.0 - smoothstep(-6.0 * s, 7.0 * s, dsh)) * uLC[i].a);
    float d = leafSdf(px, i, lq);
    float cov = (1.0 - smoothstep(-0.7, 0.7, d)) * uLC[i].a;
    if (cov > 0.0) {
      vec4 Bv = uLB[i];
      float u = lq.x / A.w;
      vec3 c = uLC[i].rgb;
      vec3 rib = uLD[i].rgb;
      if (Bv.y < 0.5) {
        // midrib and a few side veins
        float mid = 1.0 - smoothstep(0.35, 1.1, abs(lq.y)) ;
        mid *= step(-1.25, u) * step(u, 0.92);
        float vv = abs(fract((lq.x - abs(lq.y) * 1.3) / (A.w * 0.32)) - 0.5);
        float vein = (1.0 - smoothstep(0.03, 0.09, vv)) * smoothstep(1.5, 3.0, abs(lq.y)) * 0.45;
        c = mix(c, rib, clamp(mid + vein, 0.0, 1.0) * 0.7);
        // a hint of edge darkening
        c *= 1.0 - 0.18 * smoothstep(-4.0 * s, 0.0, d);
      } else {
        // petal: deeper pink at the claw, translucent pale blade, faint veins
        c = mix(rib, c, smoothstep(-0.9, 0.1, u));
        float vv = abs(fract(atan(lq.y, lq.x + A.w * 1.1) * 7.0) - 0.5);
        c *= 1.0 - 0.05 * (1.0 - smoothstep(0.05, 0.2, vv));
      }
      // rocking on the rings: the floating thing tilts with the local slope
      float rock = dot(Bv.zw, vec2(-0.55, -0.65)) * 3.2;
      c *= 1.0 + clamp(rock, -0.22, 0.22);
      leafCol.rgb = mix(leafCol.rgb, c, cov);
      leafCol.a = max(leafCol.a, cov);
    }
  }

  // lily pads: albedo, local tilt, and their shadows
  vec4 padCol = vec4(0.0);
  vec2 padTilt = vec2(0.0);
  float padRim = 0.0;
  for (int i = 0; i < NP; i++) {
    if (uPB[i].z <= 0.0) continue;
    vec4 A = uPA[i];
    vec2 dl = px - A.xy;
    float rr = A.z + 40.0 * s;
    if (dot(dl, dl) > rr * rr) continue;
    vec2 q; float ang;
    float dsh = padSdf(px - uShadowOff * 0.8, i, q, ang);
    shadow = max(shadow, (1.0 - smoothstep(-8.0 * s, 9.0 * s, dsh)) * uPB[i].z);
    float d = padSdf(px, i, q, ang);
    float cov = (1.0 - smoothstep(-0.7, 0.7, d)) * uPB[i].z;
    if (cov > 0.0) {
      float r = length(q) / A.z;
      vec3 c = mix(vec3(0.50, 0.60, 0.40), vec3(0.62, 0.56, 0.36), uPB[i].w);
      // soft mottling and a few darker freckles, like a real pad
      c *= 0.92 + 0.12 * fbm(q / (22.0 * s) + A.xy * 0.01);
      c = mix(c, c * vec3(0.86, 0.82, 0.76), smoothstep(0.6, 0.85, vnoise(q / (11.0 * s) + 31.0)) * 0.22);
      // radiating veins
      float vv = abs(fract(ang * 3.5014 + 0.5 * r) - 0.5);
      c = mix(c, c * vec3(1.12, 1.1, 1.0), (1.0 - smoothstep(0.02, 0.07, vv)) * smoothstep(0.08, 0.3, r) * 0.6);
      // a slightly upturned, paler rim and a darker heart where the stem joins
      float rim = smoothstep(-3.5 * s, -0.5 * s, d);
      c = mix(c, vec3(0.70, 0.46, 0.34), rim * 0.55);                 // the reddish underside shows at the turned-up edge
      c *= 1.0 - 0.10 * smoothstep(-14.0 * s, -3.0 * s, d) * (1.0 - rim);
      c *= 1.0 - 0.18 * (1.0 - smoothstep(0.0, 0.07, r));
      padCol.rgb = mix(padCol.rgb, c, cov);
      padCol.a = max(padCol.a, cov);
      padTilt = mix(padTilt, uPB[i].xy, cov);
      padRim = max(padRim, rim * cov);
    }
  }
  vec4 flw = lily(px, 1.0);

  vec3 colL = vec3(0.0), colD = vec3(0.0);

  // ---------------- the basin floor (shared by day and night) ----------------
  vec2 fq = px + G * (60.0 * s);     // refraction: the floor shifts under the slopes
  float n1 = fbm((fq + uGlazeOff) / (520.0 * s));
  float n2 = vnoise(fq / (60.0 * s) + 11.0);
  vec3 cream = vec3(0.97, 0.927, 0.852);
  vec3 celadon = vec3(0.94, 0.925, 0.87);
  vec3 fl = mix(cream, celadon, smoothstep(0.4, 0.85, n1) * uGlaze * 0.7);
  fl *= 0.99 + 0.02 * n2;
  // tea-stained crackle in the glaze, faint and fine
  float cr = crackle(fq / (52.0 * s));
  fl = mix(fl, fl * vec3(0.90, 0.86, 0.80), 0.24 * cr * (0.5 + 0.5 * calmL));
  // the bowl's walls rise toward the edges: gentle shading
  vec2 cc = (px / uSize - 0.5) * vec2(1.0, uSize.y / uSize.x);
  fl *= 1.0 - 0.10 * smoothstep(0.25, 0.75, length(cc));
  // caustics: surface curvature focuses light on the floor
  float C = 1.0 / max(0.38, 1.0 + 60.0 * s * L);
  C = min(C, 2.2);
  vec3 V = normalize(uEye - vec3(px, 0.0));

  if (uMix < 0.999) {
    // ---------------- noon: a pale glazed basin seen through shallow water ----------------
    float sun = uSunI;
    vec3 sunCol = vec3(1.0, 0.93, 0.80);
    vec3 lit = fl * (0.70 + 0.30 * sun * C) * mix(vec3(1.0), vec3(0.82, 0.84, 0.86), shadow * 0.75 * sun);
    lit += vec3(0.045, 0.03, 0.0) * max(C - 1.0, 0.0) * sun;          // caustic crests are warm
    lit *= vec3(0.985, 0.99, 0.98);                                     // a breath of water colour
    // reflection of the sky: a soft bright sheen, and the sun's mirror image broken by the rings
    vec2 rq = px + G * (700.0 * s);
    float F = 0.025 + 0.6 * sl2;
    vec3 sky = vec3(0.97, 0.975, 0.98);
    float glint = specl(n, V, uSunL, 60000.0) * 0.16 + specl(n, V, uSunL, 6000.0) * 0.06 + specl(n, V, uSunL, 500.0) * 0.035;
    vec3 c = mix(lit, sky, F) + sunCol * glint * sun * mix(0.4, 1.0, calmL);
    // lily pads: waxy, with a soft sheen on the side tipped toward the sun
    float pt = dot(padTilt, vec2(-0.55, -0.65)) * 3.0;
    vec3 pcD = padCol.rgb * (0.86 + 0.14 * sun) * (1.0 + clamp(pt, -0.2, 0.2));
    vec3 pn = normalize(vec3(-padTilt * 2.0, 1.0));
    pcD += vec3(1.0, 0.97, 0.9) * specl(pn, V, uSunL, 40.0) * 0.12 * sun;
    c = mix(c, pcD, padCol.a);
    c = mix(c, flw.rgb * (0.93 + 0.07 * sun), flw.a);
    // leaves on top
    vec3 lc = leafCol.rgb * (0.9 + 0.1 * sun);
    c = mix(c, lc, leafCol.a);
    colL = c;
  }

  if (uMix > 0.001) {
    // ---------------- night: the same basin by lantern light, the moon a small guest ----------------
    vec2 rq = px + G * (1500.0 * s);
    vec2 uv = rq / uSize;
    vec3 sky = mix(vec3(0.050, 0.050, 0.058), vec3(0.030, 0.030, 0.036), smoothstep(0.0, 1.0, uv.y * 0.8 + uv.x * 0.25));
    // thin clouds drifting over (dim the moon, catch a little of its light)
    float cl = fbm(rq / (380.0 * s) + uCloudOff);
    float cloud = smoothstep(0.38, 0.9, cl) * uCloudAmt;
    // moon: a small soft reflection
    vec2 md = rq - uMoon.xy;
    float dm = length(md);
    float mr = uMoon.z;
    float disc = 1.0 - smoothstep(mr - 1.8, mr + 1.8, dm);
    vec2 mq = md / mr;
    float ct = cos(uMoonPhase), st2 = sin(uMoonPhase);
    vec2 mr2 = vec2(ct * mq.x + st2 * mq.y, -st2 * mq.x + ct * mq.y);
    float term = sqrt(max(1.0 - mr2.y * mr2.y, 0.0)) * uMoonK;
    float litSide = smoothstep(-0.1, 0.1, mr2.x + term);
    disc *= mix(0.12, 1.0, litSide);
    float mare = 1.0 - 0.2 * smoothstep(0.4, 0.75, vnoise(md / (mr * 0.42) + 3.0 + uSeedF * 50.0));
    float halo = exp(-dm / (mr * 2.2)) * 0.06 + exp(-dm / (320.0 * s)) * 0.025;
    float mI = uMoonI * (1.0 - 0.6 * cloud);
    sky += vec3(0.80, 0.78, 0.70) * disc * mare * mI * 0.55;
    sky += vec3(0.40, 0.42, 0.46) * halo * mI;
    sky += vec3(0.06, 0.06, 0.065) * cloud * (0.4 + 1.6 * exp(-dm / (mr * 12.0))) * uMoonI;
    // stars
    vec2 sc = rq / (46.0 * s);
    vec2 ci = floor(sc);
    float hs = hash12(ci + uSeedF * 91.0);
    if (hs > 0.96) {
      vec2 sp = hash22(ci + 7.0) * 0.8 + 0.1;
      float sdd = length(fract(sc) - sp) * 46.0;
      float tw = 0.55 + 0.45 * hash12(ci + 3.0);
      sky += vec3(0.85, 0.82, 0.76) * (1.0 - smoothstep(0.0, 1.5, sdd)) * tw * (1.0 - cloud) * 0.35;
    }
    // the lanterns beside the basin: warm windows and glow, reflected
    vec2 ld = (rq - uLantern) / s;
    float lw = length(max(abs(ld) - vec2(4.0, 6.0), 0.0)) - 3.0;
    float ll = length(ld);
    sky += uLanternCol * ((1.0 - smoothstep(-1.5, 2.5, lw)) * 0.8 + exp(-ll / 22.0) * 0.4 + exp(-ll / 120.0) * 0.14 + exp(-ll / 420.0) * 0.07) * uLanternI;
    vec2 ld2 = (rq - uLantern2) / s;
    float ll2 = length(ld2 * vec2(1.0, 0.85));
    sky += uLanternCol * ((1.0 - smoothstep(5.0, 8.0, ll2)) * 0.6 + exp(-ll2 / 18.0) * 0.35 + exp(-ll2 / 100.0) * 0.12 + exp(-ll2 / 360.0) * 0.06) * uLantern2I;
    // the floor: warm charcoal by moonlight, with big lantern pools full of caustic rings
    float fld = length((px - uLantern) / s), fld2 = length((px - uLantern2) / s);
    float pool = 1.1 * exp(-pow(fld / 430.0, 1.6)) * uLanternI;
    float pool2 = 1.0 * exp(-pow(fld2 / 360.0, 1.6)) * uLantern2I;
    // fireflies: tiny warm lights low over the water, each with its own little pool and reflection
    float ffPool = 0.0; vec3 ffGlow = vec3(0.0);
    vec3 ffCol = vec3(1.0, 0.82, 0.42);
    for (int i = 0; i < NF; i++) {
      vec4 F4 = uFF[i];
      if (F4.z <= 0.0) continue;
      vec2 fd = (px - F4.xy) / s;
      float fr2 = dot(fd, fd);
      ffPool += F4.z * exp(-sqrt(fr2) / 110.0);
      ffGlow += ffCol * F4.z * (exp(-fr2 / 8.0) * 0.9 + exp(-sqrt(fr2) / 20.0) * 0.16);
      vec2 rd = (rq - F4.xy - vec2(0.0, 9.0 * s)) / s;            // its reflection sits a little below it
      float rr2 = dot(rd, rd);
      ffGlow += ffCol * F4.z * exp(-rr2 / 10.0) * 0.35;
    }
    vec3 moonLit = vec3(0.084, 0.094, 0.090) * (0.8 + 0.2 * uMoonI) * (0.45 + 0.55 * C) * (1.0 - 0.15 * cloud);
    float warm = pool + pool2 + ffPool * 0.35;
    vec3 lampLit = uLanternCol * 0.28 * warm * (0.2 + 0.8 * C);
    vec3 floorN = fl * (moonLit * (1.0 - 0.5 * shadow) + lampLit * (1.0 - 0.7 * shadow));
    float F = 0.35 + 2.5 * sl2;
    vec3 Ll = normalize(uLampP - vec3(px, 0.0)), Ll2 = normalize(uLamp2P - vec3(px, 0.0));
    float mg = specl(n, V, uMoonL, 20000.0) * 0.22 + specl(n, V, uMoonL, 2000.0) * 0.035;
    float lg = (specl(n, V, Ll, 3000.0) * 0.45 + specl(n, V, Ll, 300.0) * 0.12 + specl(n, V, Ll, 30.0) * 0.06) * uLanternI
             + (specl(n, V, Ll2, 3000.0) * 0.4 + specl(n, V, Ll2, 300.0) * 0.1 + specl(n, V, Ll2, 30.0) * 0.05) * uLantern2I;
    vec3 c = sky * F + floorN + vec3(0.75, 0.74, 0.70) * mg * uMoonI * (1.0 - 0.5 * cloud) + uLanternCol * lg;
    // floating things are lit from the side by lanterns and fireflies
    float lampOn = exp(-fld / 520.0) * uLanternI + exp(-fld2 / 460.0) * uLantern2I * 0.85 + ffPool * 0.8;
    vec3 lc = leafCol.rgb;
    vec3 ln = lc * (0.07 + 0.06 * uMoonI) + uLanternCol * lc * 0.6 * lampOn;
    vec3 pn = normalize(vec3(-padTilt * 2.0, 1.0));
    vec3 padNight = mix(padCol.rgb, vec3(dot(padCol.rgb, vec3(0.3, 0.5, 0.2))), 0.45);
    vec3 pcN = padNight * (0.08 + 0.05 * uMoonI) + uLanternCol * padCol.rgb * 0.65 * lampOn
      + vec3(0.5, 0.5, 0.5) * specl(pn, V, uMoonL, 60.0) * 0.03 * uMoonI
      + uLanternCol * (specl(pn, V, Ll, 25.0) * uLanternI + specl(pn, V, Ll2, 25.0) * uLantern2I) * 0.08
      + uLanternCol * padRim * 0.12 * lampOn;
    c = mix(c, pcN, padCol.a);
    // a shut lily glimmers pale in the dark
    c = mix(c, flw.rgb * (0.12 + 0.08 * uMoonI) + uLanternCol * flw.rgb * 0.6 * lampOn, flw.a);
    c = mix(c, ln, leafCol.a);
    c += ffGlow;
    colD = c;
  }

  vec3 col;
  if (uMix <= 0.001) col = colL;
  else if (uMix >= 0.999) col = colD;
  else {
    col = exp(mix(log(max(colL, 1e-3)), log(max(colD, 1e-3)), uMix));
    col *= 1.0 + sin(3.14159 * uMix) * vec3(0.24, 0.03, 0.02);       // the rosy hour of dusk
  }
  float dn = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
  col += dn / 255.0;
  outColor = vec4(col, 1.0);
}
`;
