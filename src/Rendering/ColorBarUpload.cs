using System.Runtime.InteropServices;

namespace Botamochi.Rendering;

// Each direct encoder owns one persistent upload surface, reused after Encode returns.
internal sealed class ColorBarUpload
{
    private readonly int _width, _height, _topRows;
    private int _previousSplit;

    public ColorBarUpload(Span<byte> upload, int width, int height)
    {
        _width = width;
        _height = height;
        _topRows = height - height / 16;
        var pixels = MemoryMarshal.Cast<byte, uint>(upload);
        uint[] row = new uint[width];
        ProceduralScene.FillColorBarRow(row);
        for (int y = 0; y < _topRows; y++) row.AsSpan().CopyTo(pixels.Slice(y * width, width));
        pixels[(_topRows * width)..].Fill(ProceduralScene.ProgressBackground);
    }

    public void Render(Span<byte> upload, float time)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(upload);
        int split = ProceduralScene.ProgressSplit(_width, time);
        if (split == _previousSplit) return;
        int start = Math.Min(split, _previousSplit);
        int count = Math.Abs(split - _previousSplit);
        uint color = split > _previousSplit ? ProceduralScene.ProgressForeground : ProceduralScene.ProgressBackground;
        for (int y = _topRows; y < _height; y++) pixels.Slice(y * _width + start, count).Fill(color);
        _previousSplit = split;
    }
}
