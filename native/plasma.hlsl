cbuffer Frame : register(b0) { uint width; uint height; float time; uint mode; uint progress; }
StructuredBuffer<uint> source : register(t0);
RWTexture2D<unorm float> luma : register(u0);
RWTexture2D<unorm float2> chroma : register(u1);

uint rainbow(uint2 id)
{
    // Keep the same rounding points as the scalar/AVX2 HSV conversion.
    precise float phase = (asfloat(source[id.x]) + asfloat(source[width + id.y])) + time * 0.2f;
    precise float sector = (phase - trunc(phase)) * 6.0f;
    precise float foldedPhase = sector - 2.0f * trunc(sector * 0.5f);
    const float c = 0.95f * 0.85f;
    const float m = 0.95f - c;
    precise float middle = (c * (1.0f - abs(foldedPhase - 1.0f)) + m) * 255.0f;
    uint v = (uint)middle;
    const uint low = (uint)(m * 255.0f), high = (uint)((c + m) * 255.0f);
    switch ((uint)sector) {
        case 0: return high | (v << 8) | (low << 16);
        case 1: return v | (high << 8) | (low << 16);
        case 2: return low | (high << 8) | (v << 16);
        case 3: return low | (v << 8) | (high << 16);
        case 4: return v | (low << 8) | (high << 16);
        default: return high | (low << 8) | (v << 16);
    }
}

uint colorBar(uint2 id)
{
    if (id.y >= height - height / 16) return id.x < progress ? 0x80ff00 : 0x282828;
    const uint colors[7] = {0xc0c0c0, 0x00c0c0, 0xc0c000, 0x00c000, 0xc000c0, 0x0000c0, 0xc00000};
    return colors[min(id.x / (width / 7), 6u)];
}

// Each wave covers one scanline, so its vertical phase is uniform.
[numthreads(32, 1, 1)]
void main(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    // Traverse adjacent 32-pixel strips in eight-row bands, including the final partial band.
    uint band = group.y & ~7u;
    uint rows = min(8u, height - band);
    uint strip = (group.y - band) * ((width + 31) / 32) + group.x;
    uint2 id = uint2((strip / rows) * 32 + lane, band + strip % rows);
    float ny = float(id.y) / height;
    float cy = WaveReadLaneFirst(ny + 0.5 * cos(time * 0.333) - 0.5);
    float vertical = WaveReadLaneFirst(ny * cos(time * 0.333));
    if (id.x >= width || id.y >= height) return;
    if (mode != 0)
    {
        uint pixel;
        if (mode == 2) pixel = colorBar(id);
        else if (mode == 3) pixel = rainbow(id);
        else pixel = source[id.y * width + id.x];
        int3 rgb = int3(pixel & 255, (pixel >> 8) & 255, (pixel >> 16) & 255);
        int y = (3483 * rgb.r + 11718 * rgb.g + 1183 * rgb.b + 262144) >> 14;
        luma[id.xy] = clamp(y, 16, 235) / 255.0;
        if (((id.x | id.y) & 1) == 0)
        {
            int u = (-1877 * rgb.r - 6315 * rgb.g + 8192 * rgb.b + 2097152) >> 14;
            int v = (8192 * rgb.r - 7441 * rgb.g - 751 * rgb.b + 2097152) >> 14;
            chroma[id.xy / 2] = clamp(int2(u, v), 16, 240) / 255.0;
        }
        return;
    }
    float nx = float(id.x) / width;
    float cx = nx + 0.5 * sin(time * 0.2) - 0.5;
    float v = (sin(nx * 10 + time * 2)
        + sin(10 * (nx * sin(time * 0.5) + vertical) + time)
        + sin(sqrt(100 * (cx * cx + cy * cy) + 1) + time * 3)) * 0.33333;
    float3 rgb = float3(sin(v * 3.14159), cos(v * 3.14159), sin(v * 3.14159 + 2.09439)) * 0.5 + 0.5;
    luma[id.xy] = floor(clamp(16.5 + 219 * dot(rgb, float3(0.2126, 0.7152, 0.0722)), 16, 235)) / 255;
    if (((id.x | id.y) & 1) == 0)
    {
        float2 uv = float2(dot(rgb, float3(-0.11457, -0.38543, 0.5)), dot(rgb, float3(0.5, -0.45415, -0.04585)));
        chroma[id.xy / 2] = floor(clamp(128.5 + 224 * uv, 16, 240)) / 255;
    }
}
