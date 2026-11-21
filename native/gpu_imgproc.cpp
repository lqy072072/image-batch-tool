// GPU accelerated image processing via D3D11 compute shaders
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <cstdint>
#include <cmath>
#include <cstring>
#include <vector>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")

// ---------- HLSL shaders ----------
// LUT apply: takes BGRA input texture, 3D LUT texture, constant params, writes BGRA output
static const char* LUT_CS = R"(
cbuffer Params : register(b0) {
    float4 inMin_Max;      // x=inMin, y=inMax, z=strength, w=chroma
    float4 wb_Exp;         // x=wbR, y=wbG, z=wbB, w=expOn(1/0)
    float4 logOn_dither;   // x=logOn, y,z,w unused
};
Texture2D<float4> srcTex : register(t0);
Texture3D<float4> lutTex : register(t1);
RWTexture2D<float4> dstTex : register(u0);

float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

[numthreads(16,16,1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint W, H;
    dstTex.GetDimensions(W, H);
    if (dtid.x >= W || dtid.y >= H) return;
    float4 px = srcTex[dtid.xy];
    // input is BGRA in memory, but DXGI uploads as R8G8B8A8 -> px.r=B, px.g=G, px.b=R, px.a=A
    float b = px.r, g = px.g, r = px.b;

    // WB
    r *= wb_Exp.x; g *= wb_Exp.y; b *= wb_Exp.z;
    r = min(r, 1.0); g = min(g, 1.0); b = min(b, 1.0);

    // log table applied via 1D? skip for now (C# passes pre-converted)
    // LUT input range normalize
    float range = inMin_Max.y - inMin_Max.x;
    float3 sr = saturate(float3(r, g, b) - inMin_Max.x) / range;
    // sample 3D LUT: .cube is R,G,B per vertex, stored as R,G,B,A
    // Texture3D is indexed (r,g,b) -> returns (R,G,B,A)
    float3 outC = lutTex.SampleLevel(sampler0, sr, 0).rgb;

    // mix with original
    float s = inMin_Max.z;
    outC = lerp(float3(r,g,b), outC, s);

    // chroma
    float ch = inMin_Max.w;
    float l = lum(outC);
    outC = l + (outC - l) * ch;
    outC = saturate(outC);

    // write back BGRA
    dstTex[dtid.xy] = float4(outC.bgr, px.a);
}
)";

// Color adjust: saturation, contrast, brightness, shadows, highlights, temp, tint, vignette
static const char* COLOR_CS = R"(
cbuffer Params : register(b0) {
    float4 sat_con_bri;   // x=sat(0-2), y=con, z=bri, w=unused
    float4 sh_hl;         // x=shadows, y=highlights, z=temp, w=tint
    float4 vig_WH;       // x=vignette, y=W, z=H, w=unused
};
Texture2D<float4> srcTex : register(t0);
RWTexture2D<float4> dstTex : register(u0);

float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

[numthreads(16,16,1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint W, H;
    dstTex.GetDimensions(W, H);
    if (dtid.x >= W || dtid.y >= H) return;
    float4 px = srcTex[dtid.xy];
    float b = px.r, g = px.g, r = px.b;
    float3 c = float3(r, g, b);

    // saturation
    c = lum(c) + (c - lum(c)) * sat_con_bri.x;
    // contrast
    c = (c - 0.5) * sat_con_bri.y + 0.5;
    // brightness
    c *= sat_con_bri.z;
    // temp
    c.r += sh_hl.z * 0.15;
    c.b -= sh_hl.z * 0.15;
    // tint
    c.g -= sh_hl.w * 0.1;
    // shadows/highlights (luma-based)
    float l = lum(c);
    float shMask = saturate(1.0 - l * 2.0);
    float hlMask = saturate((l - 0.5) * 2.0);
    c += (c - l) * (sh_hl.x * shMask - sh_hl.y * hlMask);
    // vignette
    if (vig_WH.x > 0.001) {
        float dx = (dtid.x - W/2.0) / (W/2.0);
        float dy = (dtid.y - H/2.0) / (H/2.0);
        float d = sqrt(dx*dx + dy*dy);
        float f = 1.0 - vig_WH.x * d * d * 0.8;
        c *= max(f, 0.0);
    }
    c = saturate(c);
    dstTex[dtid.xy] = float4(c.bgr, px.a);
}
)";

// ---------- D3D11 globals ----------
static ID3D11Device* g_pd3dDevice = nullptr;
static ID3D11DeviceContext* g_pd3dContext = nullptr;
static ID3D11ComputeShader* g_lutCS = nullptr;
static ID3D11ComputeShader* g_colorCS = nullptr;
static ID3D11Buffer* g_paramBuffer = nullptr;
static ID3D11SamplerState* g_sampler = nullptr;

static bool EnsureDevice() {
    if (g_pd3dDevice) return true;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        0, nullptr, 0, D3D11_SDK_VERSION, &g_pd3dDevice, nullptr, &g_pd3dContext);
    if (FAILED(hr)) {
        // try WARP (software fallback)
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
            0, nullptr, 0, D3D11_SDK_VERSION, &g_pd3dDevice, nullptr, &g_pd3dContext);
        if (FAILED(hr)) return false;
    }
    // sampler
    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    g_pd3dDevice->CreateSamplerState(&sd, &g_sampler);
    // param buffer
    D3D11_BUFFER_DESC bd = {};
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.ByteWidth = 64; // 4 float4 = 64 bytes
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    g_pd3dDevice->CreateBuffer(&bd, nullptr, &g_paramBuffer);
    // compile shaders
    ID3DBlob *lutBlob = nullptr, *err = nullptr;
    D3DCompile(LUT_CS, strlen(LUT_CS), nullptr, nullptr, nullptr,
        "CSMain", "cs_5_0", 0, 0, &lutBlob, &err);
    if (lutBlob) { g_pd3dDevice->CreateComputeShader(lutBlob->GetBufferPointer(), lutBlob->GetBufferSize(), nullptr, &g_lutCS); lutBlob->Release(); }
    if (err) err->Release();

    ID3DBlob* colBlob = nullptr;
    D3DCompile(COLOR_CS, strlen(COLOR_CS), nullptr, nullptr, nullptr,
        "CSMain", "cs_5_0", 0, 0, &colBlob, &err);
    if (colBlob) { g_pd3dDevice->CreateComputeShader(colBlob->GetBufferPointer(), colBlob->GetBufferSize(), nullptr, &g_colorCS); colBlob->Release(); }
    if (err) err->Release();
    return true;
}

extern "C" {

// GPU LUT apply. buf is BGRA (W*H*4 bytes). lut is R,G,B per vertex (size^3 * 3 floats).
// wbGain[3], logTable[256] not used on GPU (C# does log table upload as 1D texture if needed; here we skip for simplicity)
__declspec(dllexport) int __cdecl gpu_lut_apply(
    uint8_t* buf, int W, int H, int stride,
    const float* lut, int size,
    float inMin, float inMax, float strength, float chroma,
    const float* wbGain
)
{
    if (!EnsureDevice()) return 0;
    if (!g_lutCS || !g_paramBuffer || !buf || !lut || size < 2) return 0;
    if (W <= 0 || H <= 0 || stride < W * 4) return 0;

    // Create input texture (BGRA -> R8G8B8A8_UNORM)
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = W; td.Height = H; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    D3D11_SUBRESOURCE_DATA init = {};
    init.pSysMem = buf;
    init.SysMemPitch = stride;
    ID3D11Texture2D* srcTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&td, &init, &srcTex)) || !srcTex) return 0;
    ID3D11ShaderResourceView* srcSRV = nullptr;
    if (FAILED(g_pd3dDevice->CreateShaderResourceView(srcTex, nullptr, &srcSRV)) || !srcSRV) {
        srcTex->Release(); return 0;
    }

    // Create output texture (UAV)
    D3D11_TEXTURE2D_DESC od = td;
    od.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    ID3D11Texture2D* dstTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&od, nullptr, &dstTex)) || !dstTex) {
        srcSRV->Release(); srcTex->Release(); return 0;
    }
    ID3D11UnorderedAccessView* dstUAV = nullptr;
    if (FAILED(g_pd3dDevice->CreateUnorderedAccessView(dstTex, nullptr, &dstUAV)) || !dstUAV) {
        dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }

    // Create 3D LUT texture
    D3D11_TEXTURE3D_DESC t3d = {};
    t3d.Width = size; t3d.Height = size; t3d.Depth = size;
    t3d.MipLevels = 1;
    t3d.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
    t3d.Usage = D3D11_USAGE_DEFAULT;
    t3d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    // expand lut to RGBA
    std::vector<float> lutRGBA((size*size*size)*4, 0.0f);
    for (int i = 0; i < size*size*size; i++) {
        lutRGBA[i*4+0] = lut[i*3+0];
        lutRGBA[i*4+1] = lut[i*3+1];
        lutRGBA[i*4+2] = lut[i*3+2];
        lutRGBA[i*4+3] = 1.0f;
    }
    D3D11_SUBRESOURCE_DATA init3 = {};
    init3.pSysMem = lutRGBA.data();
    init3.SysMemPitch = size * 4 * sizeof(float);
    init3.SysMemSlicePitch = size * size * 4 * sizeof(float);
    ID3D11Texture3D* lutTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture3D(&t3d, &init3, &lutTex)) || !lutTex) {
        dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    ID3D11ShaderResourceView* lutSRV = nullptr;
    if (FAILED(g_pd3dDevice->CreateShaderResourceView(lutTex, nullptr, &lutSRV)) || !lutSRV) {
        lutTex->Release(); dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }

    // Update constants
    D3D11_MAPPED_SUBRESOURCE map;
    if (FAILED(g_pd3dContext->Map(g_paramBuffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &map)) || !map.pData) {
        lutSRV->Release(); lutTex->Release(); dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    float* p = (float*)map.pData;
    p[0]=inMin; p[1]=inMax; p[2]=strength; p[3]=chroma;
    p[4]=wbGain?wbGain[0]:1.0f; p[5]=wbGain?wbGain[1]:1.0f; p[6]=wbGain?wbGain[2]:1.0f; p[7]=1.0f;
    p[8]=1.0f; p[9]=0; p[10]=0; p[11]=0;
    p[12]=0; p[13]=0; p[14]=0; p[15]=0;
    g_pd3dContext->Unmap(g_paramBuffer, 0);

    // Dispatch
    g_pd3dContext->CSSetShader(g_lutCS, nullptr, 0);
    g_pd3dContext->CSSetShaderResources(0, 1, &srcSRV);
    g_pd3dContext->CSSetShaderResources(1, 1, &lutSRV);
    g_pd3dContext->CSSetUnorderedAccessViews(0, 1, &dstUAV, nullptr);
    g_pd3dContext->CSSetConstantBuffers(0, 1, &g_paramBuffer);
    ID3D11SamplerState* samps[1] = { g_sampler };
    g_pd3dContext->CSSetSamplers(0, 1, samps);
    g_pd3dContext->Dispatch((W+15)/16, (H+15)/16, 1);

    // Copy back
    D3D11_TEXTURE2D_DESC rd = td;
    rd.BindFlags = 0;
    rd.Usage = D3D11_USAGE_STAGING;
    rd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* readTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&rd, nullptr, &readTex)) || !readTex) {
        if (lutSRV) lutSRV->Release(); if (lutTex) lutTex->Release();
        dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    g_pd3dContext->CopyResource(readTex, dstTex);
    if (FAILED(g_pd3dContext->Map(readTex, 0, D3D11_MAP_READ, 0, &map)) || !map.pData) {
        readTex->Release();
        if (lutSRV) lutSRV->Release(); if (lutTex) lutTex->Release();
        dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    for (int y = 0; y < H; y++) {
        memcpy(buf + y*stride, (uint8_t*)map.pData + y*map.RowPitch, W*4);
    }
    g_pd3dContext->Unmap(readTex, 0);

    // cleanup
    readTex->Release(); dstUAV->Release(); dstTex->Release();
    if (lutSRV) lutSRV->Release(); if (lutTex) lutTex->Release();
    srcSRV->Release(); srcTex->Release();
    return 1;
}

// GPU color adjust
__declspec(dllexport) int __cdecl gpu_color_adjust(
    uint8_t* buf, int W, int H, int stride,
    float saturation, float contrast, float brightness,
    float shadows, float highlights, float temp, float tint, float vignette
)
{
    if (!EnsureDevice() || !g_colorCS || !g_paramBuffer || !buf) return 0;
    if (W <= 0 || H <= 0 || stride < W * 4) return 0;

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = W; td.Height = H; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA init = {};
    init.pSysMem = buf; init.SysMemPitch = stride;
    ID3D11Texture2D* srcTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&td, &init, &srcTex)) || !srcTex) return 0;
    ID3D11ShaderResourceView* srcSRV = nullptr;
    if (FAILED(g_pd3dDevice->CreateShaderResourceView(srcTex, nullptr, &srcSRV)) || !srcSRV) {
        srcTex->Release(); return 0;
    }

    D3D11_TEXTURE2D_DESC od = td;
    od.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    ID3D11Texture2D* dstTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&od, nullptr, &dstTex)) || !dstTex) {
        srcSRV->Release(); srcTex->Release(); return 0;
    }
    ID3D11UnorderedAccessView* dstUAV = nullptr;
    if (FAILED(g_pd3dDevice->CreateUnorderedAccessView(dstTex, nullptr, &dstUAV)) || !dstUAV) {
        dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }

    D3D11_MAPPED_SUBRESOURCE map;
    if (FAILED(g_pd3dContext->Map(g_paramBuffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &map)) || !map.pData) {
        dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    float* p = (float*)map.pData;
    p[0]=saturation; p[1]=contrast; p[2]=brightness; p[3]=0;
    p[4]=shadows; p[5]=highlights; p[6]=temp; p[7]=tint;
    p[8]=vignette; p[9]=(float)W; p[10]=(float)H; p[11]=0;
    p[12]=0; p[13]=0; p[14]=0; p[15]=0;
    g_pd3dContext->Unmap(g_paramBuffer, 0);

    g_pd3dContext->CSSetShader(g_colorCS, nullptr, 0);
    g_pd3dContext->CSSetShaderResources(0, 1, &srcSRV);
    g_pd3dContext->CSSetUnorderedAccessViews(0, 1, &dstUAV, nullptr);
    g_pd3dContext->CSSetConstantBuffers(0, 1, &g_paramBuffer);
    ID3D11SamplerState* samps[1] = { g_sampler };
    g_pd3dContext->CSSetSamplers(0, 1, samps);
    g_pd3dContext->Dispatch((W+15)/16, (H+15)/16, 1);

    D3D11_TEXTURE2D_DESC rd = td;
    rd.BindFlags = 0; rd.Usage = D3D11_USAGE_STAGING;
    rd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* readTex = nullptr;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&rd, nullptr, &readTex)) || !readTex) {
        dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    g_pd3dContext->CopyResource(readTex, dstTex);
    if (FAILED(g_pd3dContext->Map(readTex, 0, D3D11_MAP_READ, 0, &map)) || !map.pData) {
        readTex->Release(); dstUAV->Release(); dstTex->Release(); srcSRV->Release(); srcTex->Release(); return 0;
    }
    for (int y = 0; y < H; y++) {
        memcpy(buf + y*stride, (uint8_t*)map.pData + y*map.RowPitch, W*4);
    }
    g_pd3dContext->Unmap(readTex, 0);

    readTex->Release(); dstUAV->Release(); dstTex->Release();
    srcSRV->Release(); srcTex->Release();
    return 1;
}

// Horizontal blur pass
static const char* BLUR_H_CS = R"(
cbuffer Params : register(b0) {
    float4 params; // x=radius, y=W, z=H, w=unused
};
Texture2D<float4> srcTex : register(t0);
RWTexture2D<float4> dstTex : register(u0);
[numthreads(16,16,1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint W, H;
    dstTex.GetDimensions(W, H);
    if (dtid.x >= W || dtid.y >= H) return;
    int r = (int)params.x;
    float4 sum = 0; int cnt = 0;
    for (int dx = -r; dx <= r; dx++) {
        int x = dtid.x + dx;
        if (x >= 0 && x < W) { sum += srcTex[int2(x, dtid.y)]; cnt++; }
    }
    dstTex[dtid.xy] = sum / cnt;
}
)";

// Vertical blur pass (same as horizontal but transposed)
static const char* BLUR_V_CS = R"(
cbuffer Params : register(b0) {
    float4 params;
};
Texture2D<float4> srcTex : register(t0);
RWTexture2D<float4> dstTex : register(u0);
[numthreads(16,16,1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint W, H;
    dstTex.GetDimensions(W, H);
    if (dtid.x >= W || dtid.y >= H) return;
    int r = (int)params.x;
    float4 sum = 0; int cnt = 0;
    for (int dy = -r; dy <= r; dy++) {
        int y = dtid.y + dy;
        if (y >= 0 && y < H) { sum += srcTex[int2(dtid.x, y)]; cnt++; }
    }
    dstTex[dtid.xy] = sum / cnt;
}
)";

static ID3D11ComputeShader* g_blurHCS = nullptr;
static ID3D11ComputeShader* g_blurVCS = nullptr;

static void EnsureBlurShaders() {
    if (g_blurHCS) return;
    ID3DBlob* blob = nullptr; ID3DBlob* err = nullptr;
    D3DCompile(BLUR_H_CS, strlen(BLUR_H_CS), nullptr, nullptr, nullptr,
        "CSMain", "cs_5_0", 0, 0, &blob, &err);
    if (blob) { g_pd3dDevice->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &g_blurHCS); blob->Release(); }
    if (err) err->Release();
    blob = nullptr;
    D3DCompile(BLUR_V_CS, strlen(BLUR_V_CS), nullptr, nullptr, nullptr,
        "CSMain", "cs_5_0", 0, 0, &blob, &err);
    if (blob) { g_pd3dDevice->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &g_blurVCS); blob->Release(); }
    if (err) err->Release();
}

// GPU separable box blur (multi-pass). buf is BGRA.
__declspec(dllexport) int __cdecl gpu_blur(uint8_t* buf, int W, int H, int stride, int radius)
{
    if (!EnsureDevice() || !g_pd3dDevice || !g_pd3dContext || !g_sampler) return 0;
    if (!buf || W <= 0 || H <= 0 || stride < W * 4 || radius < 1) return 0;
    EnsureBlurShaders();
    if (!g_blurHCS || !g_blurVCS) return 0;

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = W; td.Height = H; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;

    D3D11_SUBRESOURCE_DATA init = {};
    init.pSysMem = buf; init.SysMemPitch = stride;
    ID3D11Texture2D* texA = nullptr;
    ID3D11Texture2D* texB = nullptr;
    ID3D11ShaderResourceView* srvA = nullptr;
    ID3D11UnorderedAccessView* uavA = nullptr;
    ID3D11ShaderResourceView* srvB = nullptr;
    ID3D11UnorderedAccessView* uavB = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11Texture2D* readTex = nullptr;
    D3D11_BUFFER_DESC bd = {};
    D3D11_TEXTURE2D_DESC rd = {};
    D3D11_MAPPED_SUBRESOURCE map;
    float* p = nullptr;
    bool ok = false;

    if (FAILED(g_pd3dDevice->CreateTexture2D(&td, &init, &texA)) || !texA) goto cleanup;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&td, nullptr, &texB)) || !texB) goto cleanup;
    if (FAILED(g_pd3dDevice->CreateShaderResourceView(texA, nullptr, &srvA)) || !srvA) goto cleanup;
    if (FAILED(g_pd3dDevice->CreateUnorderedAccessView(texA, nullptr, &uavA)) || !uavA) goto cleanup;
    if (FAILED(g_pd3dDevice->CreateShaderResourceView(texB, nullptr, &srvB)) || !srvB) goto cleanup;
    if (FAILED(g_pd3dDevice->CreateUnorderedAccessView(texB, nullptr, &uavB)) || !uavB) goto cleanup;

    bd.Usage = D3D11_USAGE_DYNAMIC; bd.ByteWidth = 16;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(g_pd3dDevice->CreateBuffer(&bd, nullptr, &cb)) || !cb) goto cleanup;

    if (FAILED(g_pd3dContext->Map(cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &map)) || !map.pData) goto cleanup;
    p = (float*)map.pData;
    p[0] = (float)radius; p[1] = (float)W; p[2] = (float)H; p[3] = 0;
    g_pd3dContext->Unmap(cb, 0);

    g_pd3dContext->CSSetShader(g_blurHCS, nullptr, 0);
    g_pd3dContext->CSSetShaderResources(0, 1, &srvA);
    g_pd3dContext->CSSetUnorderedAccessViews(0, 1, &uavB, nullptr);
    g_pd3dContext->CSSetConstantBuffers(0, 1, &cb);
    g_pd3dContext->Dispatch((W+15)/16, (H+15)/16, 1);

    g_pd3dContext->CSSetShader(g_blurVCS, nullptr, 0);
    g_pd3dContext->CSSetShaderResources(0, 1, &srvB);
    g_pd3dContext->CSSetUnorderedAccessViews(0, 1, &uavA, nullptr);
    g_pd3dContext->CSSetConstantBuffers(0, 1, &cb);
    g_pd3dContext->Dispatch((W+15)/16, (H+15)/16, 1);

    rd = td;
    rd.BindFlags = 0; rd.Usage = D3D11_USAGE_STAGING;
    rd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    if (FAILED(g_pd3dDevice->CreateTexture2D(&rd, nullptr, &readTex)) || !readTex) goto cleanup;
    g_pd3dContext->CopyResource(readTex, texA);
    if (FAILED(g_pd3dContext->Map(readTex, 0, D3D11_MAP_READ, 0, &map)) || !map.pData) goto cleanup;
    for (int y = 0; y < H; y++)
        memcpy(buf + y*stride, (uint8_t*)map.pData + y*map.RowPitch, W*4);
    g_pd3dContext->Unmap(readTex, 0);
    ok = true;

cleanup:
    if (readTex) readTex->Release();
    if (cb) cb->Release();
    if (srvA) srvA->Release(); if (uavA) uavA->Release(); if (texA) texA->Release();
    if (srvB) srvB->Release(); if (uavB) uavB->Release(); if (texB) texB->Release();
    return ok ? 1 : 0;
}

// Detect if GPU is available
__declspec(dllexport) int __cdecl gpu_available()
{
    return EnsureDevice() ? 1 : 0;
}

} // extern "C"
