cbuffer Frame : register(b0) { uint width; uint height; }
StructuredBuffer<uint> rgba : register(t0);
RWStructuredBuffer<uint> nv12 : register(u0);

uint3 convert(uint pixel)
{
    int r = pixel & 255, g = (pixel >> 8) & 255, b = (pixel >> 16) & 255;
    return uint3(clamp((3483*r + 11718*g + 1183*b + 262144) >> 14, 16, 235),
                 clamp((-1877*r - 6315*g + 8192*b + 2097152) >> 14, 16, 240),
                 clamp((8192*r - 7441*g - 751*b + 2097152) >> 14, 16, 240));
}

[numthreads(32, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint row = id.y * width;
    uint3 a = convert(rgba[row + min(id.x, width - 1)]);
    uint lane = WaveGetLaneIndex() & ~3u;
    uint y1 = WaveReadLaneAt(a.x, lane + 1);
    uint3 next = WaveReadLaneAt(a, lane + 2);
    uint y3 = WaveReadLaneAt(a.x, lane + 3);
    bool contiguous = WaveActiveAllTrue((id.x & 31) == (WaveReadLaneFirst(id.x & 31) + WaveGetLaneIndex()));
    if ((id.x & 3) != 0 || id.x >= width) return;
    if (!contiguous)
    {
        y1 = convert(rgba[row + id.x + 1]).x;
        next = convert(rgba[row + id.x + 2]);
        y3 = convert(rgba[row + id.x + 3]).x;
    }
    nv12[(row + id.x) / 4] = a.x | (y1 << 8) | (next.x << 16) | (y3 << 24);
    if ((id.y & 1) == 0)
        nv12[(width * height + (id.y / 2) * width + id.x) / 4] =
            a.y | (a.z << 8) | (next.y << 16) | (next.z << 24);
}
