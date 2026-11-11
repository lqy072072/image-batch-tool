// native imgproc DLL - high performance image processing
#include <cstdint>
#include <cmath>
#include <cstring>
#include <cstdlib>
#include <omp.h>

extern "C" {

// Full LUT pipeline: WB (3 gains) + exposure table (256) + log table (256) + LUT + chroma
// All arrays are float[256] lookup tables; pass null to skip.
__declspec(dllexport) void __cdecl lut_apply(
    uint8_t* buf, int W, int H, int stride,
    const float* lut, int size,
    float inMin, float inMax,
    float strength, float chroma,
    const float* wbGain,      // 3 floats: R,G,B; pass null to skip
    const float* expTable,    // 256 floats; pass null to skip
    const float* logTable     // 256 floats; pass null to skip
)
{
    float range = inMax - inMin;
    if (range < 1e-6f) range = 1.0f;
    bool mix = strength < 0.999f;
    bool doChroma = fabsf(chroma - 1.0f) > 0.001f;
    bool doWb = wbGain != nullptr;
    bool doExp = expTable != nullptr;
    bool doLog = logTable != nullptr;
    float wbR = doWb ? wbGain[0] : 1.0f;
    float wbG = doWb ? wbGain[1] : 1.0f;
    float wbB = doWb ? wbGain[2] : 1.0f;

    #pragma omp parallel for schedule(static)
    for (int y = 0; y < H; y++) {
        int row = y * stride;
        for (int x = 0; x < W; x++) {
            int i = row + x * 4;
            uint8_t ob0 = buf[i], og0 = buf[i+1], or0 = buf[i+2];
            float b = ob0 / 255.0f;
            float g = og0 / 255.0f;
            float r = or0 / 255.0f;

            if (doWb) { r *= wbR; g *= wbG; b *= wbB; if (r>1)r=1; if (g>1)g=1; if (b>1)b=1; }
            if (doExp) {
                float lum = 0.299f*r + 0.587f*g + 0.114f*b;
                float newLum = expTable[(int)(lum*255.0f + 0.5f)];
                float ratio = lum > 0.001f ? newLum / lum : 1.0f;
                r *= ratio; g *= ratio; b *= ratio;
                if (r<0)r=0; else if (r>1)r=1;
                if (g<0)g=0; else if (g>1)g=1;
                if (b<0)b=0; else if (b>1)b=1;
            }
            if (doLog) {
                r = logTable[(int)(r*255.0f + 0.5f)];
                g = logTable[(int)(g*255.0f + 0.5f)];
                b = logTable[(int)(b*255.0f + 0.5f)];
            }

            // normalize to LUT input range
            float sr = (r - inMin) / range;
            float sg = (g - inMin) / range;
            float sb = (b - inMin) / range;
            if (sr < 0) sr = 0; if (sr > 1) sr = 1;
            if (sg < 0) sg = 0; if (sg > 1) sg = 1;
            if (sb < 0) sb = 0; if (sb > 1) sb = 1;

            // tetrahedral interpolation
            float pr = sr * (size - 1);
            float pg = sg * (size - 1);
            float pb = sb * (size - 1);
            int r0 = (int)pr, g0 = (int)pg, b0 = (int)pb;
            int r1 = r0 + 1 < size ? r0 + 1 : size - 1;
            int g1 = g0 + 1 < size ? g0 + 1 : size - 1;
            int b1 = b0 + 1 < size ? b0 + 1 : size - 1;
            float fr = pr - r0, fg = pg - g0, fb = pb - b0;

            auto idx = [&](int rr, int gg, int bb) { return ((bb*size+gg)*size+rr)*3; };
            float R000=lut[idx(r0,g0,b0)],   G000=lut[idx(r0,g0,b0)+1],   B000=lut[idx(r0,g0,b0)+2];
            float R100=lut[idx(r1,g0,b0)],   G100=lut[idx(r1,g0,b0)+1],   B100=lut[idx(r1,g0,b0)+2];
            float R010=lut[idx(r0,g1,b0)],   G010=lut[idx(r0,g1,b0)+1],   B010=lut[idx(r0,g1,b0)+2];
            float R001=lut[idx(r0,g0,b1)],   G001=lut[idx(r0,g0,b1)+1],   B001=lut[idx(r0,g0,b1)+2];
            float R110=lut[idx(r1,g1,b0)],   G110=lut[idx(r1,g1,b0)+1],   B110=lut[idx(r1,g1,b0)+2];
            float R101=lut[idx(r1,g0,b1)],   G101=lut[idx(r1,g0,b1)+1],   B101=lut[idx(r1,g0,b1)+2];
            float R011=lut[idx(r0,g1,b1)],   G011=lut[idx(r0,g1,b1)+1],   B011=lut[idx(r0,g1,b1)+2];
            float R111=lut[idx(r1,g1,b1)],   G111=lut[idx(r1,g1,b1)+1],   B111=lut[idx(r1,g1,b1)+2];

            float nr, ng, nb;
            if (fr >= fg && fg >= fb) {
                nr = R000 + fr*(R100-R000) + fg*(R110-R100) + fb*(R111-R110);
                ng = G000 + fr*(G100-G000) + fg*(G110-G100) + fb*(G111-G110);
                nb = B000 + fr*(B100-B000) + fg*(B110-B100) + fb*(B111-B110);
            } else if (fr >= fb && fb >= fg) {
                nr = R000 + fr*(R100-R000) + fb*(R101-R100) + fg*(R111-R101);
                ng = G000 + fr*(G100-G000) + fb*(G101-G100) + fg*(G111-G101);
                nb = B000 + fr*(B100-B000) + fb*(B101-B100) + fg*(B111-B101);
            } else if (fg >= fr && fr >= fb) {
                nr = R000 + fg*(R010-R000) + fr*(R110-R010) + fb*(R111-R110);
                ng = G000 + fg*(G010-G000) + fr*(G110-G010) + fb*(G111-G110);
                nb = B000 + fg*(B010-B000) + fr*(B110-B010) + fb*(B111-B110);
            } else if (fg >= fb && fb >= fr) {
                nr = R000 + fg*(R010-R000) + fb*(R011-R010) + fr*(R111-R011);
                ng = G000 + fg*(G010-G000) + fb*(G011-G010) + fr*(G111-G011);
                nb = B000 + fg*(B010-B000) + fb*(B011-B010) + fr*(B111-B011);
            } else if (fb >= fr && fr >= fg) {
                nr = R000 + fb*(R001-R000) + fr*(R101-R001) + fg*(R111-R101);
                ng = G000 + fb*(G001-G000) + fr*(G101-G001) + fg*(G111-G101);
                nb = B000 + fb*(B001-B000) + fr*(B101-B001) + fg*(B111-B101);
            } else {
                nr = R000 + fb*(R001-R000) + fg*(R011-R001) + fr*(R111-R011);
                ng = G000 + fb*(G001-G000) + fg*(G011-G001) + fr*(G111-G011);
                nb = B000 + fb*(B001-B000) + fg*(B011-B001) + fr*(B111-B011);
            }

            if (mix) {
                nr = nr*strength + or0/255.0f*(1-strength);
                ng = ng*strength + og0/255.0f*(1-strength);
                nb = nb*strength + ob0/255.0f*(1-strength);
            }
            if (doChroma) {
                float lum = 0.299f*nr + 0.587f*ng + 0.114f*nb;
                nr = lum + (nr-lum)*chroma;
                ng = lum + (ng-lum)*chroma;
                nb = lum + (nb-lum)*chroma;
            }
            if (nr < 0) nr = 0; if (nr > 1) nr = 1;
            if (ng < 0) ng = 0; if (ng > 1) ng = 1;
            if (nb < 0) nb = 0; if (nb > 1) nb = 1;

            buf[i]   = (uint8_t)(nb * 255.0f + 0.5f);
            buf[i+1] = (uint8_t)(ng * 255.0f + 0.5f);
            buf[i+2] = (uint8_t)(nr * 255.0f + 0.5f);
        }
    }
}

// 色彩调整：饱和度、对比度、亮度、暗部、高光、色温、色调、暗角
// 所有参数 0-200 范围，100 = 不变
__declspec(dllexport) void __cdecl color_adjust(
    uint8_t* buf, int W, int H, int stride,
    float saturation, float contrast, float brightness,
    float shadows, float highlights, float temp, float tint, float vignette
)
{
    #pragma omp parallel for schedule(static)
    for (int y = 0; y < H; y++) {
        int row = y * stride;
        for (int x = 0; x < W; x++) {
            int i = row + x*4;
            float b = buf[i]/255.0f, g = buf[i+1]/255.0f, r = buf[i+2]/255.0f;
            float lum = 0.299f*r + 0.587f*g + 0.114f*b;

            // 饱和度
            if (fabsf(saturation - 100.0f) > 0.5f) {
                float s = saturation / 100.0f;
                r = lum + (r - lum) * s;
                g = lum + (g - lum) * s;
                b = lum + (b - lum) * s;
            }
            // 对比度
            if (fabsf(contrast - 100.0f) > 0.5f) {
                float c = contrast / 100.0f;
                r = (r - 0.5f) * c + 0.5f;
                g = (g - 0.5f) * c + 0.5f;
                b = (b - 0.5f) * c + 0.5f;
            }
            // 亮度
            if (fabsf(brightness - 100.0f) > 0.5f) {
                r *= brightness / 100.0f;
                g *= brightness / 100.0f;
                b *= brightness / 100.0f;
            }
            // 暗部提亮/压暗
            if (fabsf(shadows - 100.0f) > 0.5f) {
                float sh = shadows / 100.0f;
                float w = 1.0f - lum; // 暗部权重
                r = r + (r - lum) * w * (sh - 1.0f);
                g = g + (g - lum) * w * (sh - 1.0f);
                b = b + (b - lum) * w * (sh - 1.0f);
            }
            // 高光压暗/提亮
            if (fabsf(highlights - 100.0f) > 0.5f) {
                float hl = highlights / 100.0f;
                float w = lum;
                r = r + (r - lum) * w * (hl - 1.0f);
                g = g + (g - lum) * w * (hl - 1.0f);
                b = b + (b - lum) * w * (hl - 1.0f);
            }
            // 色温（>100 暖，<100 冷）
            if (fabsf(temp - 100.0f) > 0.5f) {
                float t = (temp - 100.0f) / 100.0f;
                r += t * 0.15f;
                b -= t * 0.15f;
            }
            // 色调（>100 品红，<100 绿）
            if (fabsf(tint - 100.0f) > 0.5f) {
                float ti = (tint - 100.0f) / 100.0f;
                g -= ti * 0.1f;
            }
            // 暗角
            if (fabsf(vignette - 100.0f) > 0.5f) {
                float v = (vignette - 100.0f) / 100.0f;
                float dx = (x - W/2.0f) / (W/2.0f);
                float dy = (y - H/2.0f) / (H/2.0f);
                float d = sqrtf(dx*dx + dy*dy);
                float vig = 1.0f - v * d * d * 0.8f;
                if (vig < 0) vig = 0;
                r *= vig; g *= vig; b *= vig;
            }

            if (r<0)r=0; else if (r>1)r=1;
            if (g<0)g=0; else if (g>1)g=1;
            if (b<0)b=0; else if (b>1)b=1;
            buf[i]   = (uint8_t)(b*255.0f+0.5f);
            buf[i+1] = (uint8_t)(g*255.0f+0.5f);
            buf[i+2] = (uint8_t)(r*255.0f+0.5f);
        }
    }
}

// 锐化（Unsharp Mask 3x3）
__declspec(dllexport) void __cdecl sharpen(
    uint8_t* buf, int W, int H, int stride, float amount
)
{
    if (amount < 0.01f) return;
    uint8_t* tmp = (uint8_t*)malloc((size_t)H * stride);
    memcpy(tmp, buf, (size_t)H * stride);

    #pragma omp parallel for schedule(static)
    for (int y = 1; y < H-1; y++) {
        int row = y * stride;
        for (int x = 1; x < W-1; x++) {
            int i = row + x*4;
            double br=0, bg=0, bb=0;
            for (int dy=-1; dy<=1; dy++)
                for (int dx=-1; dx<=1; dx++) {
                    int j = (y+dy)*stride + (x+dx)*4;
                    bb += tmp[j]; bg += tmp[j+1]; br += tmp[j+2];
                }
            br/=9; bg/=9; bb/=9;
            double dr = tmp[i+2]-br, dg = tmp[i+1]-bg, db = tmp[i]-bb;
            buf[i+2] = (uint8_t)fmin(255, fmax(0, tmp[i+2] + dr*amount*1.5));
            buf[i+1] = (uint8_t)fmin(255, fmax(0, tmp[i+1] + dg*amount*1.5));
            buf[i]   = (uint8_t)fmin(255, fmax(0, tmp[i]   + db*amount*1.5));
        }
    }
    free(tmp);
}

} // extern "C"
