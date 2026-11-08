using Botamochi.Simd;

namespace Botamochi.Rendering;

internal sealed class ColorBarsNv12Renderer
{
    private readonly int _width;
    private readonly int _height;
    private readonly uint[] _rgba;
    private readonly byte[] _bars;
    private readonly byte[] _progress;

    public ColorBarsNv12Renderer(int width, int height)
    {
        _width = width;
        _height = height;
        _rgba = new uint[2 * width];
        _bars = new byte[3 * width];
        _progress = new byte[3 * width];
        ProceduralScene.FillColorBarRow(_rgba.AsSpan(0, width));
        ConvertRow(_bars);
    }

    private unsafe void ConvertRow(byte[] packed)
    {
        _rgba.AsSpan(0, _width).CopyTo(_rgba.AsSpan(_width));
        fixed (uint* rgba = _rgba)
        fixed (byte* dst = packed)
            Avx512Converter.ConvertRgbaToNv12Vectorized((byte*)rgba, dst, dst + 2 * _width, _width, 2);
    }

    public void Render(float time, Span<byte> destination)
    {
        ProceduralScene.FillProgressRow(_rgba.AsSpan(0, _width), time);
        ConvertRow(_progress);
        int topRows = _height - _height / 16;
        int pixels = _width * _height;
        FillPlane(destination[..pixels], topRows, 0);
        // Each chroma pair samples the upper-left pixel of its 2x2 block.
        FillPlane(destination[pixels..], (topRows + 1) / 2, 2 * _width);
    }

    private void FillPlane(Span<byte> plane, int barRows, int sourceOffset)
    {
        int split = barRows * _width;
        _bars.AsSpan(sourceOffset, _width).CopyTo(plane);
        ProceduralScene.RepeatRow(plane[..split], _width);
        if (split == plane.Length) return;
        _progress.AsSpan(sourceOffset, _width).CopyTo(plane[split..]);
        ProceduralScene.RepeatRow(plane[split..], _width);
    }
}
