// Shaders for the snow globe (GLSL ES 3.00, raw WebGL2 on p5's context).
const HEAD = `#version 300 es
precision highp float;
`;

const NOISE = `
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float vnoise3(vec3 x) {
    vec3 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x), mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x), mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}
float vnoise2(vec2 x) {
    vec2 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x), mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
}
float fbm2(vec2 x) { return 0.55 * vnoise2(x) + 0.3 * vnoise2(x * 2.07 + 5.3) + 0.15 * vnoise2(x * 4.3 + 1.7); }
`;

const FOCUS = `
uniform vec4 uFocus;   // card cx, cy, half w, half h (css px)
float focusF(vec2 p, float pad) {
    vec2 d = max(abs(p - uFocus.xy) - uFocus.zw, 0.0);
    float l = length(d);
    return 1.0 - smoothstep(0.0, 1.0, clamp(l / pad, 0.0, 1.0));
}
`;

// ------------------------------------------------------------------------------------------- meshes
export const MESH_VS = HEAD + `
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNor;
layout(location = 2) in vec3 aAux;     // part, occlusion, shade
layout(location = 3) in vec4 iA;       // x, y (css px), depth z, px per nm
layout(location = 4) in vec4 iQ;       // orientation quaternion
layout(location = 5) in vec4 iC1;      // colour A, alpha
layout(location = 6) in vec4 iC2;      // colour B, seed
uniform vec2 uRes;
uniform float uShell;                  // fuzz shell inflation (nm)
out vec3 vN; out vec3 vObj; out vec3 vAux; out vec4 vC1; out vec4 vC2; out vec2 vScr; out float vZ;
vec3 qrot(vec4 q, vec3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
void main() {
    vec3 p = aPos + aNor * uShell;
    vec3 r = qrot(iQ, p);
    vN = qrot(iQ, aNor);
    vObj = aPos + iC2.a * 17.0; vAux = aAux; vC1 = iC1; vC2 = iC2; vZ = iA.z;
    vec2 scr = iA.xy + vec2(r.x, -r.y) * iA.w;
    vScr = scr;
    float depth = 0.45 - 0.3 * (iA.z + r.z * iA.w / 2400.0);
    gl_Position = vec4(scr.x / uRes.x * 2.0 - 1.0, 1.0 - scr.y / uRes.y * 2.0, depth, 1.0);
}
`;

export const MESH_FS = HEAD + NOISE + FOCUS + `
in vec3 vN; in vec3 vObj; in vec3 vAux; in vec4 vC1; in vec4 vC2; in vec2 vScr; in float vZ;
uniform vec3 uL;          // key light direction (view space, towards the light)
uniform vec3 uKey;        // key light colour
uniform vec3 uSkyA, uGndA;// hemisphere ambient
uniform vec3 uSheen;      // velvet rim of the felt
uniform vec3 uGlow;       // translucent glow (lamplit wax / frosted glass)
uniform float uGlowK;
uniform vec3 uFog; uniform float uFogK;   // haze of the water towards depth
uniform vec3 uVeil;       // calm colour around the card
uniform float uVeilK;
uniform float uShellA;    // 0 = core surface, >0 = fuzz shell opacity
uniform float uFibre;     // fibre texture scale
uniform float uLift;      // pastel lift of the felt colours
uniform vec3 uRimCol;     // morning sun behind the figurines: luminous translucent rims
uniform vec2 uRimDir;     // screen direction towards the sun (y up)
uniform float uRimK;
out vec4 o;
void main() {
    vec3 n = normalize(vN);
    float part = vAux.x, ao = vAux.y, shade = vAux.z;
    vec3 alb = mix(vC1.rgb, vC2.rgb, smoothstep(0.35, 0.65, part));
    alb *= 1.0 + shade * 0.18;
    alb = mix(alb, vec3(1.0), uLift);
    float f2 = vnoise3(vObj * uFibre);   // fibres (the broad mottling is baked into the shade attribute)
    alb *= 0.96 + 0.08 * f2;
    float ndl = dot(n, uL);
    float wrap = clamp((ndl + 0.5) / 1.5, 0.0, 1.0);
    float nz = clamp(n.z, 0.0, 1.0);
    float rim = pow(1.0 - nz, 2.0);
    vec3 amb = mix(uGndA, uSkyA, n.y * 0.5 + 0.5);
    float occ = mix(0.45, 1.0, ao);
    vec3 col = alb * (amb * occ + uKey * wrap * mix(0.7, 1.0, ao));
    col += uSheen * alb * rim * (0.3 + 0.7 * wrap);
    // light through the figurine: thin edges and the lamp-facing side glow
    col += uGlow * alb * uGlowK * (0.25 + 0.75 * rim) * (0.35 + 0.65 * wrap) * occ;
    // backlight: the sun behind the water lights the felt through its thin edges
    vec2 nxy = n.xy / max(length(n.xy), 1e-4);
    float bl = pow(1.0 - nz, 1.7) * (0.35 + 0.65 * clamp(dot(nxy, uRimDir) * 0.5 + 0.5, 0.0, 1.0));
    col += uRimCol * mix(vec3(1.0), alb, 0.4) * bl * uRimK * occ;
    float a = vC1.a;
    if (uShellA > 0.0) {
        float fib = vnoise3(vObj * uFibre * 1.7);
        a *= smoothstep(0.52, 0.82, fib) * uShellA * (0.35 + 0.65 * rim);
        col += uSheen * alb * 0.25 + uRimCol * uRimK * 0.25 * rim;
        if (a < 0.004) discard;
    }
    float haze = uFogK * pow(clamp(1.0 - vZ * 0.85, 0.0, 1.0), 1.4);
    col = mix(col, uFog, haze);
    col = mix(col, uVeil, uVeilK * focusF(vScr, 190.0));
    o = vec4(col * a, a);
}
`;

// ------------------------------------------------------------------------------------------- full-screen passes
export const FS_VS = HEAD + `
out vec2 vUv;
void main() {
    vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
    vUv = p * 0.5 + 0.5;
    gl_Position = vec4(p, 0.0, 1.0);
}
`;

export const BG_FS = HEAD + NOISE + FOCUS + `
in vec2 vUv;
uniform vec2 uRes;
uniform float uMix;
uniform vec3 uTop, uMidC, uBot;
uniform vec3 uSunCol; uniform vec3 uSun;          // x, y, radius (css px)
uniform vec3 uLampCol; uniform vec3 uLamp;        // x, y, radius
uniform vec4 uIce;                                 // old film offset xy, new film offset xy
uniform float uIceK;
uniform vec4 uFront;                               // dir xy, position along dir, active
uniform vec4 uShaft;                               // angle, phase a, phase b, strength
uniform vec4 uMoteA, uMoteB;                       // layer offsets (px)
uniform vec3 uMoteCol; uniform float uMoteK; uniform float uMoteGlow;
uniform vec4 uCaus;                                // two drifting caustic layer offsets
uniform float uCausK;
out vec4 o;
float ridge(vec2 p) { return 1.0 - abs(vnoise2(p) * 2.0 - 1.0); }
float mote(vec2 px, float cell, vec2 off, float seed, float rmax) {
    vec2 q = (px + off) / cell, c = mod(floor(q), 64.0), f = fract(q);
    float h = hash12(c + seed);
    if (h > 0.55) return 0.0;
    vec2 pos = vec2(hash12(c + seed + 7.1), hash12(c + seed + 3.7)) * 0.7 + 0.15;
    float r = mix(0.6, rmax, hash12(c + seed + 9.3)) / cell;
    float d = length(f - pos) / r;
    return exp(-d * d) * (0.5 + 0.5 * hash12(c + seed + 1.9));
}
void main() {
    vec2 px = vec2(vUv.x, 1.0 - vUv.y) * uRes;
    float y = px.y / uRes.y;
    vec3 col = mix(uTop, uMidC, smoothstep(0.0, 0.6, y));
    col = mix(col, uBot, smoothstep(0.45, 1.05, y));
    // halfway through a theme change the water passes through a rosy winter dusk
    float dusk = 4.0 * uMix * (1.0 - uMix);
    col = mix(col, mix(vec3(0.66, 0.55, 0.62), vec3(0.86, 0.6, 0.5), smoothstep(0.2, 1.0, y)), dusk * 0.42);
    // window light (day) and lamp below the globe (night)
    float ds = length(px - uSun.xy) / uSun.z;
    col += uSunCol / (1.0 + ds * ds * 2.5);
    vec2 dl = (px - uLamp.xy) / vec2(uLamp.z * 1.5, uLamp.z);
    float lg = exp(-dot(dl, dl) * 1.3);
    col += uLampCol * (lg * 0.9 + 0.25 / (1.0 + dot(dl, dl) * 3.0));
    // soft shafts of light through the water
    vec2 sd = vec2(cos(uShaft.x), sin(uShaft.x));
    float s = dot(px, vec2(-sd.y, sd.x));
    float sh = (0.5 + 0.5 * sin(s / 150.0 + uShaft.y)) * (0.5 + 0.5 * sin(s / 263.0 + uShaft.z));
    sh *= smoothstep(1.4, 0.0, ds * 0.55) * uShaft.w;
    col += vec3(1.0, 0.95, 0.82) * sh * sh * 0.09;
    // morning sun rippling through the water: soft caustic light, strongest towards the sun, quiet at the card
    if (uCausK > 0.001) {
        vec2 cp = px / 230.0;
        float c1 = ridge(cp + uCaus.xy);
        float c2 = ridge(cp * 1.37 + uCaus.zw + c1 * 0.55);
        float c = pow(c1 * c2, 6.0);
        float cm = uCausK * (0.35 + 0.65 / (1.0 + ds * ds * 1.2)) * (1.0 - 0.92 * focusF(px, 200.0));
        col += vec3(1.0, 0.9, 0.66) * c * cm;
    }
    // uneven film: ice thickness varies; a plunge brings a new film behind the front
    vec2 u = px / 520.0;
    float side = uFront.w > 0.5 ? smoothstep(-120.0, 120.0, dot(px, uFront.xy) - uFront.z) : 1.0;
    float thick = side > 0.999 ? fbm2(u + uIce.xy) : side < 0.001 ? fbm2(u + uIce.zw) : mix(fbm2(u + uIce.zw), fbm2(u + uIce.xy), side);
    col *= 1.0 - uIceK * (thick - 0.5);
    // drifting motes: snow by day, warm plankton glow by night
    float m = mote(px, 74.0, uMoteA.xy, 3.0, 1.9) + mote(px, 118.0, uMoteA.zw, 11.0, 2.6) * 0.8 + mote(px, 53.0, uMoteB.xy, 23.0, 1.3) * 0.6;
    m *= uMoteK * (1.0 - 0.85 * focusF(px, 120.0));
    col = mix(col, uMoteCol, clamp(m * (1.0 - uMoteGlow), 0.0, 1.0)) + uMoteCol * m * uMoteGlow;
    col += (hash12(px + fract(uMoteB.z)) - 0.5) / 255.0;
    o = vec4(col, 1.0);
}
`;

export const OVER_FS = HEAD + NOISE + FOCUS + `
in vec2 vUv;
uniform vec2 uRes;
uniform float uMix;
uniform vec4 uFront;           // dir xy, position, active
uniform float uFrontK;         // sheen strength
uniform vec3 uVeil; uniform float uVeilK;
uniform vec4 uArc;             // globe glint: angle centre, half-span, alpha, unused
uniform vec3 uArcCol;
out vec4 o;
vec4 over(vec4 dst, vec3 c, float a) { return vec4(c * a + dst.rgb * (1.0 - a), a + dst.a * (1.0 - a)); }
void main() {
    vec2 px = vec2(vUv.x, 1.0 - vUv.y) * uRes;
    vec4 acc = vec4(0.0);
    if (uVeilK > 0.0) acc = over(acc, uVeil, uVeilK * focusF(px, 170.0));
    // a thin glint along the globe's rim
    if (uArc.z > 0.002) {
        vec2 c = uRes * 0.5, r = uRes * vec2(0.62, 0.78);
        vec2 q = (px - c) / r;
        float rad = length(q), ang = atan(q.y, q.x);
        float da = abs(atan(sin(ang - uArc.x), cos(ang - uArc.x)));
        float g = exp(-pow((rad - 0.93) * 34.0, 2.0)) * smoothstep(uArc.y, 0.0, da);
        acc = over(acc, uArcCol, g * uArc.z);
    }
    // a plunge: a glassy sheen sweeps across as the fresh film vitrifies
    if (uFront.w > 0.5) {
        float s = dot(px, uFront.xy) - uFront.z;
        float band = exp(-s * s / (2.0 * 150.0 * 150.0));
        float trail = smoothstep(0.0, -60.0, s) * exp(s / 520.0);
        vec3 film = 0.5 + 0.5 * cos(6.2832 * (s / 380.0 + vec3(0.0, 0.33, 0.67)));
        vec3 c = mix(mix(vec3(1.0), film, 0.35), vec3(1.0, 0.86, 0.6), uMix);
        float a = uFrontK * (band * 0.7 + trail * 0.25) * (1.0 - 0.6 * focusF(px, 160.0));
        acc = over(acc, c, a);
    }
    o = acc;
}
`;

export const BLUR_FS = HEAD + `
in vec2 vUv;
uniform sampler2D uTex;
uniform vec2 uDir;     // one texel along the blur direction
out vec4 o;
void main() {
    vec4 s = texture(uTex, vUv) * 0.2504;
    s += (texture(uTex, vUv + uDir) + texture(uTex, vUv - uDir)) * 0.2061;
    s += (texture(uTex, vUv + 2.0 * uDir) + texture(uTex, vUv - 2.0 * uDir)) * 0.1147;
    s += (texture(uTex, vUv + 3.0 * uDir) + texture(uTex, vUv - 3.0 * uDir)) * 0.0431;
    s += (texture(uTex, vUv + 4.0 * uDir) + texture(uTex, vUv - 4.0 * uDir)) * 0.0110;
    o = s / 0.9002;
}
`;

// composite only the screen tiles a layer touches: one instanced quad per tile
export const TILE_VS = HEAD + `
layout(location = 0) in vec2 iTile;    // tile origin (css px)
uniform vec2 uRes;
uniform float uTileSize;
out vec2 vUv;
void main() {
    int v = gl_VertexID;
    vec2 c = vec2((v == 1 || v == 2 || v == 4) ? 1.0 : 0.0, (v == 2 || v == 4 || v == 5) ? 1.0 : 0.0);
    vec2 px = min(iTile + c * uTileSize, uRes);
    vUv = vec2(px.x / uRes.x, 1.0 - px.y / uRes.y);
    gl_Position = vec4(vUv * 2.0 - 1.0, 0.0, 1.0);
}
`;

// bicubic B-spline upsampling of a premultiplied layer (smooth even at 1/16 resolution)
export const COMP_FS = HEAD + `
in vec2 vUv;
uniform sampler2D uTex;
uniform float uCubic;
uniform vec3 uGain;
out vec4 o;
vec4 cubic(float v) { vec4 n = vec4(1.0, 2.0, 3.0, 4.0) - v; vec4 s = n * n * n; float x = s.x, y = s.y - 4.0 * s.x, z = s.z - 4.0 * s.y + 6.0 * s.x; return vec4(x, y, z, 6.0 - x - y - z) / 6.0; }
vec4 bicubic(sampler2D t, vec2 uv) {
    vec2 ts = vec2(textureSize(t, 0));
    uv = uv * ts - 0.5;
    vec2 f = fract(uv); uv -= f;
    vec4 xc = cubic(f.x), yc = cubic(f.y);
    vec4 c = uv.xxyy + vec2(-0.5, 1.5).xyxy;
    vec4 s = vec4(xc.xz + xc.yw, yc.xz + yc.yw);
    vec4 off = (c + vec4(xc.yw, yc.yw) / s) / ts.xxyy;
    vec4 s0 = texture(t, off.xz), s1 = texture(t, off.yz), s2 = texture(t, off.xw), s3 = texture(t, off.yw);
    float sx = s.x / (s.x + s.y), sy = s.z / (s.z + s.w);
    return mix(mix(s3, s2, sx), mix(s1, s0, sx), sy);
}
void main() { o = (uCubic > 0.5 ? bicubic(uTex, vUv) : texture(uTex, vUv)) * vec4(uGain, 1.0); }
`;
