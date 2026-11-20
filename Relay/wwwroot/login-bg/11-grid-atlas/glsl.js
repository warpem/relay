// 11 · Grid atlas — shaders. One full-screen pass per grid "scene"; the newest grid is composited over the
// previous one while it emerges. Everything is analytic and filtered by its own pixel footprint (scale-space
// LOD), so the zoom can run continuously over ~18 octaves without popping or aliasing.

export const VERT = `#version 300 es
void main() {
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

export const FRAG = `#version 300 es
precision highp float;
precision highp int;
out vec4 outColor;

uniform vec2 uRes;        // css px
uniform float uDpr;
uniform vec2 uA;          // screen anchor (css px) where the dive target sits
uniform vec2 uRot;        // cos, sin of view rotation
uniform float uS;         // px per micrometre
uniform vec2 uO;          // pan offset (um, mesh frame)
uniform ivec2 uSqIdx;     // target square index
uniform vec2 uSqOff;      // target - target square centre (um)
uniform vec2 uSqC;        // target square centre in grid coords (um)
uniform float uPm, uBw, uGridR, uMeshR;
uniform vec2 uFRot;       // film lattice rotation (cos, sin), mesh -> film
uniform float uPh, uHr;   // hole pitch, hole radius (um)
uniform int uHex;         // 1 = hexagonal hole lattice
uniform ivec2 uHoleIdx;   // target hole lattice index
uniform vec2 uHoleOff;    // target - target hole centre (um, mesh frame)
uniform uint uSeed;
uniform vec4 uIce;        // t0 (nm), gradient, blotch, per-square spread
uniform vec2 uIceDir;
uniform float uTargetT;
uniform vec4 uFail;       // p(broken), p(torn), p(cracked), contamination
uniform vec4 uSample;     // type, radius (nm), density, cell (nm)
uniform ivec2 uTCell;     // target particle cell
uniform vec2 uTP;         // target particle centre (nm, film frame, rel. target hole)
uniform float uMix;
uniform vec4 uFocus;      // card rect (css px)
uniform float uPartFade;  // particles fade while the next grid emerges
uniform float uReveal;    // this grid's disc opacity (emerging coin)
uniform float uDiss;      // this grid's whole-frame opacity
uniform float uCalm;
uniform vec3 uCover;      // screen disc fully covered by the next grid (css px), r <= 0 = none
uniform vec3 uPig;
uniform sampler2D uNoise;        // watercolour pigment for this grid's particles

// ---------------------------------------------------------------- hashing / noise
uint hu(uint x) { x ^= x >> 16u; x *= 0x7feb352du; x ^= x >> 15u; x *= 0x846ca68bu; x ^= x >> 16u; return x; }
uint hu2(ivec2 p, uint s) { return hu(uint(p.x) * 0x8da6b343u ^ hu(uint(p.y) * 0xd8163841u ^ s)); }
float hf(uint h) { return float(h >> 8u) / 16777216.0; }
float h21(ivec2 p, uint s) { return hf(hu2(p, s)); }
// value noise from a 256x256 random texture: one bilinear fetch with a smoothstep-warped coordinate
float vnoise(vec2 x, uint s) {
    // manual bilinear on exact texel values: hardware filtering has only ~8 bits of sub-texel precision,
    // which steps and shimmers once a texel spans hundreds of pixels at deep zoom
    x += vec2(float(s & 255u), float((s >> 8) & 255u));
    vec2 i = floor(x), f = x - i;
    f = f * f * (3.0 - 2.0 * f);
    uvec2 I = uvec2(ivec2(i)) & 255u, J = (I + 1u) & 255u;
    float a = texelFetch(uNoise, ivec2(I), 0).r, b = texelFetch(uNoise, ivec2(J.x, I.y), 0).r;
    float c = texelFetch(uNoise, ivec2(I.x, J.y), 0).r, d = texelFetch(uNoise, ivec2(J), 0).r;
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
// scale-invariant wash texture: three noise octaves anchored to the object, cross-faded by zoom level
float washNoise(vec2 d) {
    float k0 = log2(uS / 300.0);
    float kf = floor(k0), acc = 0.0, wsum = 0.0;
    for (int j = 0; j < 3; j++) {
        float k = kf + float(j);
        float t = (k - k0 + 1.0) / 3.0;            // 0..1 across the octave's life
        float w = 0.5 - 0.5 * cos(6.2831853 * t);
        uint s = hu(uint(int(k) + 4096) * 0x9e3779b9u);
        acc += w * (vnoise(d * exp2(k) + vec2(float(s & 255u), float((s >> 8) & 255u)), s) - 0.5);
        wsum += w;
    }
    return acc / max(wsum, 1e-3) * 2.0;
}
// 1-D box-filtered coverage of a band of half-width hw at distance d from its centre line
float band(float d, float hw, float px) {
    return clamp((min(d + 0.5 * px, hw) - max(d - 0.5 * px, -hw)) / px, 0.0, 1.0);
}
float inside(float sd, float px) { return clamp(0.5 - sd / px, 0.0, 1.0); }

float focusFalloff(vec2 q) {
    vec2 c = uFocus.xy + uFocus.zw * 0.5;
    vec2 e = (q - c) / (uFocus.zw * 0.5 + 230.0);
    float t = clamp((length(e) - 0.55) / 0.45, 0.0, 1.0);
    return 1.0 - t * t * (3.0 - 2.0 * t);
}

// ---------------------------------------------------------------- palettes
// light: watercolour washes on a lightbox; each tint multiplies the backlit paper
vec3 iceTint(float t) {
    vec3 c = vec3(1.0, 0.995, 0.985);
    c = mix(c, vec3(0.99, 0.95, 0.88), smoothstep(5.0, 40.0, t));      // thin: cream
    c = mix(c, vec3(0.975, 0.89, 0.74), smoothstep(35.0, 85.0, t));     // good: pale honey
    c = mix(c, vec3(0.95, 0.80, 0.64), smoothstep(80.0, 140.0, t));     // apricot
    c = mix(c, vec3(0.91, 0.72, 0.68), smoothstep(130.0, 210.0, t));    // rose
    c = mix(c, vec3(0.76, 0.64, 0.70), smoothstep(200.0, 320.0, t));    // mauve
    c = mix(c, vec3(0.58, 0.49, 0.57), smoothstep(300.0, 480.0, t));    // opaque: plum
    return c;
}
// dark: the honey-amber glow of a phosphor viewing screen; I = transmitted intensity
vec3 amber(float I) {
    I = clamp(I, 0.0, 1.2);
    vec3 c = vec3(0.050, 0.034, 0.024);
    c += vec3(0.70, 0.42, 0.16) * pow(I, 1.25);
    c += vec3(0.16, 0.14, 0.08) * pow(I, 4.0);
    return c;
}

// ---------------------------------------------------------------- particles
// Each particle gets a uniformly random 3-D orientation (Shoemake quaternion) and is drawn as a projection
// of a small 3-D model, so views really vary the way they do in vitreous ice.
// type 0: D7 barrel (chaperonin-like): 14 nm wide, 15 nm long; 7-fold rings with a central channel end-on,
//         four stacked bands side-on
// type 1: apoferritin-like shell: 12 nm outer, 8 nm cavity (octahedral, so every view is a ring)
// type 2: ribosome-like: a large and a small subunit built from lobes, asymmetric in every view
mat3 qrot(vec3 u) {
    float a = sqrt(1.0 - u.x), b = sqrt(u.x);
    float x = a * sin(6.2831853 * u.y), y = a * cos(6.2831853 * u.y), z = b * sin(6.2831853 * u.z), w = b * cos(6.2831853 * u.z);
    return mat3(1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + z * w), 2.0 * (x * z - y * w),
                2.0 * (x * y - z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + x * w),
                2.0 * (x * z + y * w), 2.0 * (y * z - x * w), 1.0 - 2.0 * (x * x + y * y));
}
// projected thickness of a ball (relative to its radius) and its signed distance
float ball(vec2 p, vec2 c, float r, inout float sd) {
    vec2 d = p - c;
    float q = dot(d, d);
    sd = min(sd, sqrt(q) - r);
    return sqrt(max(r * r - q, 0.0));
}
vec3 particle(vec2 p, int type, float R, mat3 M, float pxn) {
    float dens = 0.0, sd = 1e3;
    if (type == 0) {
        vec3 axis = M[2];
        float cb = abs(axis.z), sb = sqrt(max(1.0 - cb * cb, 0.0));
        vec2 ax = sb > 0.02 ? axis.xy / sb : vec2(1.0, 0.0);
        float Rr = R * 0.93, L = R * 2.0;
        float u = dot(p, ax), v = dot(p, vec2(-ax.y, ax.x));
        float h = 0.5 * L * sb;
        float a = max(Rr * cb, 0.6);
        float ue = u - clamp(u, -h, h);
        vec2 en = vec2(ue / a, v / Rr);
        float e = dot(en, en);
        dens = sqrt(max(1.0 - e, 0.0));
        sd = (sqrt(e) - 1.0) * mix(a, Rr, 0.6);
        // end-on: seven subunits around a central channel (in-plane phase from the rotation)
        vec2 m0 = M[0].xy;
        float rr = length(en);
        float ang = atan(en.y, en.x) - atan(m0.y, m0.x);
        float top = (1.0 - 0.72 * exp(-rr * rr / 0.08)) * (0.8 + 0.2 * cos(7.0 * ang) * smoothstep(0.25, 0.6, rr));
        // side-on: four stacked rings of seven
        float axial = clamp(u / max(h, 0.5), -1.0, 1.0);
        float side = 0.72 + 0.28 * cos(4.0 * 3.14159265 * axial - 3.14159265);
        dens *= mix(side, top, cb * cb);
    } else if (type == 1) {
        float Ro = R, Ri = R * 0.66;
        float r = length(p);
        dens = (sqrt(max(Ro * Ro - r * r, 0.0)) - sqrt(max(Ri * Ri - r * r, 0.0))) / sqrt(Ro * Ro - Ri * Ri);
        vec2 m0 = M[0].xy;
        dens = min(dens, 1.0) * (0.92 + 0.08 * cos(4.0 * (atan(p.y, p.x) - atan(m0.y, m0.x))));
        sd = r - Ro;
    } else {
        // lobes in the particle frame (units of R); large subunit up, small subunit below
        float t = 0.0;
        t += ball(p, (M * vec3(0.0, 0.16, 0.0)).xy * R, 0.60 * R, sd);
        t += ball(p, (M * vec3(0.34, 0.30, 0.06)).xy * R, 0.38 * R, sd);
        t += ball(p, (M * vec3(-0.32, 0.33, -0.08)).xy * R, 0.36 * R, sd);
        t += ball(p, (M * vec3(0.02, 0.28, 0.36)).xy * R, 0.32 * R, sd);
        t += 0.8 * ball(p, (M * vec3(0.05, -0.42, 0.10)).xy * R, 0.40 * R, sd);
        t += 0.8 * ball(p, (M * vec3(-0.30, -0.50, 0.0)).xy * R, 0.27 * R, sd);
        t += 0.8 * ball(p, (M * vec3(0.27, -0.50, -0.12)).xy * R, 0.25 * R, sd);
        dens = min(1.0, t / (0.75 * R));
    }
    // soften the silhouette to the pixel footprint
    dens *= smoothstep(0.0, 1.0, -sd / max(pxn, 0.3) + 0.5);
    // under-focus Fresnel fringe: a faint bright halo just outside the particle
    float fq = (sd - 1.2) / 1.1;
    float fr = exp(-fq * fq) * step(-0.2, sd);
    float rq = (sd + 0.9) / max(1.0, pxn);
    float ring = exp(-rq * rq);
    return vec3(dens, fr, ring);
}

void main() {
    vec2 q = vec2(gl_FragCoord.x, uRes.y * uDpr - gl_FragCoord.y) / uDpr;
    vec2 r = q - uA;
    vec2 d = uO + vec2(uRot.x * r.x + uRot.y * r.y, -uRot.y * r.x + uRot.x * r.y) / uS;   // um, rel. to target
    float px0 = 1.0 / uS;
    float calm = focusFalloff(q) * uCalm;
    float blur = 1.0 + 2.0 * calm;          // softer focus behind the card
    float px = px0 * blur;
    // early outs: transparent parts of an emerging grid, and parts of the old grid already covered by it
    vec2 wq = uSqC + uSqOff + d;
    float alpha = max(uDiss, uReveal * smoothstep(uGridR * 1.0, uGridR * 0.94, length(wq)));
    if (alpha < 0.002) discard;
    if (uCover.z > 0.0 && length(q - uCover.xy) < uCover.z) discard;
    float wn = washNoise(d);

    // ------------------------------------------------ copper mesh and rim
    vec2 u = uSqOff + d;
    vec2 rel = floor(u / uPm + 0.5);
    vec2 l = u - rel * uPm;
    ivec2 sq = uSqIdx + ivec2(rel);
    vec2 w = uSqC + u;
    float rw = length(w);
    float hw = 0.5 * uBw;
    // hand-made bars: widths wander a little along their length
    int bx = sq.x + (l.x > 0.0 ? 1 : 0), by = sq.y + (l.y > 0.0 ? 1 : 0);
    float wobX = (vnoise(vec2(w.y / (uBw * 3.5), float(bx) * 3.7), uSeed ^ 0x51u) - 0.5) * 0.07 * uBw;
    float wobY = (vnoise(vec2(w.x / (uBw * 3.5), float(by) * 3.7), uSeed ^ 0x52u) - 0.5) * 0.07 * uBw;
    float dX = 0.5 * uPm - abs(l.x), dY = 0.5 * uPm - abs(l.y);
    float cX = band(dX, hw + wobX, px), cY = band(dY, hw + wobY, px);
    float lodM = smoothstep(1.6, 4.0, uPm / px);
    float avgBar = 1.0 - pow(1.0 - uBw / uPm, 2.0);
    float cBar = mix(avgBar, max(cX, cY), lodM);
    float cDisc = inside(rw - uGridR, px);
    float cMesh = inside(rw - uMeshR, px);
    float copper = cDisc - cMesh + cMesh * cBar;

    // ------------------------------------------------ square: state and ice
    vec2 sqc = uSqC + rel * uPm;
    bool isT = all(equal(sq, uSqIdx));
    uint hs = hu2(sq, uSeed ^ 0x1234567u);
    float st = hf(hs), st2 = hf(hu(hs + 1u)), st3 = hf(hu(hs + 2u)), st4 = hf(hu(hs + 3u));
    float edgeF = smoothstep(0.6, 1.02, length(sqc) / uMeshR);
    float pB = uFail.x * (1.0 + 2.5 * edgeF);
    int state = 0;
    if (!isT) {
        if (st < pB) state = 1;
        else if (st < pB + uFail.y) state = 2;
        else if (st < pB + uFail.y + uFail.z) state = 3;
    }
    vec2 cn = sqc / uGridR;
    float field = uIce.x * max(0.04, 1.0 + uIce.y * dot(cn, uIceDir) + uIce.z * (vnoise(cn * 2.3 + 17.0, uSeed ^ 0xabcu) * 2.0 - 1.0));
    float base = field * mix(1.0 - uIce.w, 1.0 + uIce.w, st2);
    if (isT) base = uTargetT;
    float inner = 0.5 * (uPm - uBw);
    vec2 ln = l / inner;
    float ga = st3 * 6.2831853;
    float tSq = base * (1.0 + 0.28 * (st4 - 0.2) * dot(ln, vec2(cos(ga), sin(ga)))
                      + 0.18 * (vnoise(ln * 1.6 + vec2(st3, st4) * 40.0, uSeed ^ 0x99u) - 0.5));
    float dE = inner - max(abs(l.x), abs(l.y));            // distance from the bar edge into the square
    tSq += base * 0.9 * exp(-max(dE, 0.0) / (0.07 * uPm)); // meniscus: ice thickens towards the bars
    tSq = max(tSq, 0.0);

    // film missing (broken / torn squares) and cracks
    float missing = 0.0, rim = 0.0, crack = 0.0;
    if (state == 1) {
        float f = dE - uPm * (0.02 + 0.05 * vnoise(l / (0.2 * uPm) + st3 * 50.0, uSeed ^ 0x3u));
        missing = inside(-f, px);
        rim = band(abs(f), 0.35, px) * (1.0 - missing * 0.0);
    } else if (state == 2) {
        vec2 dir = vec2(cos(ga), sin(ga));
        float f = (dot(ln, dir) - 0.25 + 0.7 * (vnoise(ln * 2.2 + st4 * 30.0, uSeed ^ 0x4u) - 0.5)) * inner;
        missing = inside(-f, px);
        rim = band(abs(f), 0.4, px);
    }
    if (state == 3 || state == 2) {
        float sc = 0.16 * uPm;
        float n = vnoise(l / sc + st2 * 70.0, uSeed ^ 0x7u) - 0.5;
        float gate = smoothstep(0.42, 0.62, vnoise(l / (0.4 * uPm) + st4 * 20.0, uSeed ^ 0x8u));
        float dist = abs(n) * sc / 1.1;
        crack = band(dist, 0.16 + 0.22 * gate, px) * gate * (state == 3 ? 1.0 : 0.6);
    }

    // ------------------------------------------------ holes in the carbon film
    vec2 hv = uHoleOff + d;
    vec2 v = vec2(uFRot.x * hv.x + uFRot.y * hv.y, -uFRot.y * hv.x + uFRot.x * hv.y);
    vec2 hrel, hl;
    if (uHex == 1) {
        // hexagonal lattice: rows offset by half a pitch
        float rowH = uPh * 0.8660254;
        float ry = floor(v.y / rowH + 0.5);
        float sx = mod(ry, 2.0) * 0.5;
        float rx = floor(v.x / uPh + 0.5 - sx);
        vec2 c1 = vec2((rx + sx) * uPh, ry * rowH);
        // check the two neighbouring rows too
        hrel = vec2(rx, ry); hl = v - c1;
        for (int k = -1; k <= 1; k += 2) {
            float ry2 = ry + float(k), sx2 = mod(ry2, 2.0) * 0.5;
            float rx2 = floor(v.x / uPh + 0.5 - sx2);
            vec2 c2 = vec2((rx2 + sx2) * uPh, ry2 * rowH);
            if (length(v - c2) < length(hl)) { hrel = vec2(rx2, ry2); hl = v - c2; }
        }
    } else {
        hrel = floor(v / uPh + 0.5);
        hl = v - hrel * uPh;
    }
    ivec2 hid = uHoleIdx + ivec2(hrel);
    bool isTH = isT && all(equal(hrel, vec2(0.0)));
    float hrad = length(hl);
    uint hh = hu2(hid, uSeed ^ 0x2468u);
    float wob = uHr * 0.03 * (vnoise(hl / max(hrad, 1e-6) * 1.4 + vec2(hf(hh), hf(hu(hh))) * 60.0, uSeed ^ 0x5u) - 0.5);
    float sdH = hrad - uHr - wob;
    float lodH = smoothstep(1.8, 5.0, uPh / px);
    float fill = 3.14159265 * uHr * uHr / (uPh * uPh * (uHex == 1 ? 0.8660254 : 1.0));
    float hole = mix(fill, inside(sdH, px), lodH);
    float holeRim = band(abs(sdH + 1.5 * px), 1.2 * px, px) * lodH;      // pooled pigment at the hole edge
    float tHole = tSq * (0.72 + 0.85 * pow(clamp(hrad / uHr, 0.0, 1.0), 3.0)); // thinner centre, thicker edge
    tHole = mix(tHole, tSq, 1.0 - lodH);
    float tCarb = tSq * 0.85;

    // ------------------------------------------------ crystalline-ice contamination
    float contam = 0.0;
    if (!isT) {
        // small crystals on the film, single lookup per cell (size chosen so they never cross cells)
        float cs = uPm / 12.0;
        vec2 cl = floor((l + 0.5 * uPm) / cs);
        ivec2 cid = sq * 12 + ivec2(cl);
        uint hc = hu2(cid, uSeed ^ 0x777u);
        if (hf(hc) < 0.035 * uFail.w) {
            float rad = cs * (0.05 + 0.3 * pow(hf(hu(hc + 1u)), 2.2));
            vec2 pos = (cl + 0.5) * cs - 0.5 * uPm + (vec2(hf(hu(hc + 2u)), hf(hu(hc + 3u))) - 0.5) * (cs - 2.2 * rad);
            vec2 dp = l - pos;
            float an = atan(dp.y, dp.x);
            float sdc = length(dp) - rad * (0.9 + 0.1 * cos(6.0 * an + hf(hc) * 40.0));
            contam = max(contam, inside(sdc, px));
        }
        // the odd big frost chunk
        if (st3 < 0.03 * uFail.w) {
            float rad = uPm * (0.03 + 0.08 * st2);
            vec2 pos = (vec2(st4, st) - 0.5) * (uPm - uBw - 2.2 * rad);
            vec2 dp = l - pos;
            float sdc = length(dp) - rad * (0.8 + 0.4 * vnoise(dp / (rad * 0.5), uSeed ^ 0x6u));
            contam = max(contam, inside(sdc, px));
        }
    }
    // tiny crystals inside individual holes
    if (!isTH && hf(hu(hh + 7u)) < 0.06 * uFail.w) {
        float rad = uHr * (0.04 + 0.12 * hf(hu(hh + 8u)));
        vec2 pos = (vec2(hf(hu(hh + 9u)), hf(hu(hh + 10u))) - 0.5) * 1.2 * uHr;
        contam = max(contam, inside(length(hl - pos) - rad, px) * step(sdH, 0.0));
    }

    // ------------------------------------------------ particles in the ice
    float pd = 0.0, pf = 0.0, pr2 = 0.0;
    float rp = uSample.y, cN = uSample.w;
    float ppx = rp * uS * 0.001 / blur;
    float lodP = smoothstep(0.9, 2.6, ppx) * uPartFade;
    float meanFill = 0.16 * uSample.z * smoothstep(1.4 * rp, 2.6 * rp, tHole);
    float onFilm = cMesh * (1.0 - missing);
    if (lodP > 0.0 && onFilm > 0.0) {
        vec2 pn = hl * 1000.0;
        float pxn = px * 1000.0;
        vec2 cf = pn / cN;
        ivec2 c0 = ivec2(floor(cf - 0.5));      // the 2x2 cells nearest the pixel (particles never reach further)
        uint hseed = hu2(hid, uSeed ^ 0x9e37u);
        float hrNm = uHr * 1000.0, halfP = uPh * 500.0;
        vec2 clump = vec2(hf(hseed), hf(hu(hseed))) * 50.0;
        float rb = 1.45 * rp + 3.5 + 2.0 * pxn, rb2 = rb * rb;
        for (int j = 0; j <= 1; j++) for (int i = 0; i <= 1; i++) {
            ivec2 c = c0 + ivec2(i, j);
            uint a = hu2(c, hseed);
            vec2 pos = (vec2(c) + 0.5) * cN + (vec2(hf(hu(a + 14u)), hf(hu(a + 15u))) - 0.5) * (cN - 1.2 * rp);
            bool tgt = isTH && all(equal(c, uTCell));
            if (tgt) pos = uTP;
            float pr = length(pos) / max(hrNm, 1.0);
            float wgt = 1.0, pres;
            if (pr < 1.0) {
                // in the hole: particles need ice thicker than themselves, crowd a little towards the edge,
                // and gather in loose patches
                float tLoc = tSq * (0.72 + 0.85 * pow(pr, 3.0));
                pres = uSample.z * smoothstep(1.4 * rp, 2.6 * rp, tLoc) * (0.7 + 0.5 * pr);
                pres *= 0.4 + 1.2 * h21(c / 3 + ivec2(clump), hseed);
                if (pr > 1.0 - 0.6 * rp / hrNm) pres = 0.0;
            } else {
                // a few stick to the carbon, seen through it at lower contrast
                pres = uSample.z * 0.22;
                wgt = 0.55;
                float lim = uHex == 1 ? length(pos) : max(abs(pos.x), abs(pos.y));
                if (pr < 1.0 + 1.3 * rp / hrNm || lim > halfP - 1.3 * rp) pres = 0.0;
            }
            if (!tgt && hf(a) > pres) continue;
            vec2 dp = pn - pos;
            if (dot(dp, dp) > rb2) continue;       // outside this particle's reach (incl. fringe)
            vec3 ou = tgt ? vec3(0.0) : vec3(hf(hu(a + 11u)), hf(hu(a + 12u)), hf(hu(a + 13u)));
            vec3 e = particle(dp, int(uSample.x), rp, qrot(ou), pxn) * wgt;
            pd = max(pd, e.x);
            pf = max(pf, e.y);
            pr2 = max(pr2, e.z);
        }
    }
    float dens = mix(meanFill * 0.55, pd, lodP);
    float fringe = pf * lodP;
    float pring = pr2 * lodP;

    // ------------------------------------------------ composite: film coverage
    float film = cMesh * (1.0 - missing) * (1.0 - crack);
    float inHole = hole;

    // copper (gouache, lit from the upper left): a height field with flat-topped, rounded bars, so the
    // intersections merge into one piece of metal
    float lit = 0.0, streak = 0.5, sheen = 0.0, outl = 0.0;
    if (copper > 0.0) {
        vec2 Lm = vec2(uRot.x * -0.6 + uRot.y * -0.8, -uRot.y * -0.6 + uRot.x * -0.8);   // light dir in mesh frame
        // --- mesh bars: one etched sheet. Each bar is a flat-topped ridge with rounded edges; the two families
        // are joined by a smooth union, so a crossing is a single continuous surface with filleted corners and
        // every derived term (slope, streaks) blends with the same weight instead of picking one bar.
        float barLit = 0.0, barStreak = 0.5, barSheen = 0.0;
        if (cMesh > 0.0) {
            float crX = clamp((l.x - sign(l.x) * 0.5 * uPm) / (hw + wobX), -1.0, 1.0);
            float crY = clamp((l.y - sign(l.y) * 0.5 * uPm) / (hw + wobY), -1.0, 1.0);
            float eX = max(abs(crX) - 0.62, 0.0) / 0.38, eY = max(abs(crY) - 0.62, 0.0) / 0.38;
            float hX = cX * (1.0 - eX * eX), hY = cY * (1.0 - eY * eY);
            const float K = 0.35;
            float wX = clamp(0.5 + 0.5 * (hX - hY) / K, 0.0, 1.0);    // smooth-max weight
            wX = wX * wX * (3.0 - 2.0 * wX);
            vec2 g = vec2(-2.0 * eX * sign(crX) * wX, -2.0 * eY * sign(crY) * (1.0 - wX));
            barLit = clamp(dot(-g, Lm) * 0.5, -1.0, 1.0) * lodM;
            outl = max(band(abs(dX - hw - wobX), 0.6 * px0, px) * (1.0 - cY), band(abs(dY - hw - wobY), 0.6 * px0, px) * (1.0 - cX)) * lodM;
            // brushed-metal streaks along each bar, only once a bar is wide enough to show them
            float lodT = smoothstep(14.0, 50.0, uBw / px);
            if (lodT > 0.0) {
                float sx = vnoise(vec2(w.y / (uBw * 3.0), crX * 2.5 + float(bx) * 9.0), uSeed ^ 0x61u);
                float sy = vnoise(vec2(w.x / (uBw * 3.0), crY * 2.5 + float(by) * 9.0), uSeed ^ 0x62u);
                barStreak = mix(0.5, mix(sy, sx, wX), lodT);
            }
            // broad sheen: the flat tops catch a window's light in patches, like metal under a loupe
            barSheen = smoothstep(0.45, 0.85, vnoise(w / (uPm * 2.3) + 3.0, uSeed ^ 0x63u)) * lodM;
        }
        // --- rim: solid annulus of its own material, concentric brushing, broad sheen; rounded outer edge, and a
        // rounded inner edge only where it faces an opening (bars run flush into it)
        vec2 rn = w / max(rw, 1.0);
        float rimW = uGridR - uMeshR;
        float rimT = clamp((rw - uMeshR) / rimW, 0.0, 1.0);
        float inner = rimT < 0.1 ? (1.0 - rimT / 0.1) * (1.0 - cBar) : 0.0;
        float outer = rimT > 0.84 ? (rimT - 0.84) / 0.16 : 0.0;
        float rimLit = clamp(dot(rn, Lm) * (1.4 * inner - 1.6 * outer) * 0.6, -1.0, 1.0);
        float lodR = smoothstep(4.0, 30.0, rimW / px);
        float rimStreak = mix(0.5, vnoise(vec2(rimT * 16.0, 0.0) + rn * 1.7, uSeed ^ 0x64u), lodR);
        float rimSheen = smoothstep(0.4, 0.85, vnoise(rn * 2.2 + 11.0, uSeed ^ 0x65u)) * smoothstep(1.0, 6.0, rimW / px);
        lit = mix(rimLit, barLit, cMesh);
        streak = mix(rimStreak, barStreak, cMesh);
        sheen = mix(rimSheen, barSheen, cMesh);
        outl *= cMesh;
    }

    vec3 colL = vec3(0.0), colD = vec3(0.0);
    if (uMix < 0.999) {
        // light: watercolour on a lightbox
        vec3 paper = vec3(0.988, 0.968, 0.935);
        vec3 carbonL = vec3(0.885, 0.84, 0.79) * iceTint(tCarb) * (1.0 + 0.06 * wn);
        vec3 holeL = iceTint(tHole) * (1.0 + 0.03 * wn);
        holeL *= mix(vec3(1.0), uPig, clamp(clamp(dens, 0.0, 1.0) * 0.5 + pring * 0.22, 0.0, 1.0));
        holeL = mix(holeL, vec3(1.0, 0.99, 0.96), fringe * 0.3);
        holeL *= 1.0 - 0.07 * holeRim;
        vec3 filmL = mix(carbonL, holeL, inHole);
        filmL = mix(filmL, vec3(0.58, 0.53, 0.55) * (0.92 + 0.12 * wn), contam * 0.62);
        colL = paper * (1.0 + 0.012 * wn);
        colL = mix(colL, paper * filmL, film);
        colL = mix(colL, colL * vec3(0.78, 0.7, 0.66), rim * cMesh * 0.6);
        vec3 cuL = vec3(0.85, 0.62, 0.45);
        cuL = mix(cuL, vec3(0.95, 0.74, 0.52), sheen * 0.55);
        cuL = mix(cuL, vec3(1.0, 0.89, 0.72), smoothstep(0.05, 0.8, lit) * 0.65);
        cuL = mix(cuL, vec3(0.72, 0.47, 0.32), smoothstep(0.05, 0.8, -lit) * 0.45);
        cuL *= (1.0 + 0.05 * wn) * (0.94 + 0.12 * streak);
        cuL = mix(cuL, vec3(0.66, 0.42, 0.29), outl * 0.18);
        colL = mix(colL, cuL, copper);
    }
    if (uMix > 0.001) {
        // dark: transmitted intensity on an amber viewing screen
        float odHole = tHole / 230.0;
        float odCarb = 0.32 + tCarb / 230.0;
        float od = mix(odCarb, odHole, inHole);
        od += dens * 0.36;
        od = mix(od, 2.6, contam);
        float I = exp(-od) * (1.0 + 0.05 * wn);
        I += fringe * 0.07;
        float Ibg = mix(0.62, 0.86, cDisc);
        float Id = mix(Ibg, I, film);
        Id = mix(Id, Id * 0.45, rim * cMesh);
        float beam = 0.5 + 0.5 * exp(-dot(r, r) / pow(0.62 * length(uRes), 2.0));
        colD = amber(Id * beam);
        vec3 cuD = vec3(0.05, 0.034, 0.025) + vec3(0.20, 0.11, 0.05) * smoothstep(0.3, 1.0, lit) * 0.5 + vec3(0.03, 0.02, 0.01) * streak;
        colD = mix(colD, cuD * (1.0 + 0.1 * wn), copper);
    }
    vec3 col = mix(colL, colD, uMix);

    // calm behind the card
    vec3 calmTone = mix(vec3(0.95, 0.925, 0.89), vec3(0.11, 0.08, 0.06), uMix);
    col = mix(col, calmTone, 0.3 * calm);

    outColor = vec4(col, alpha);
}`;
