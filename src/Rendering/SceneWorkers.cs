using System.Numerics;

namespace Botamochi.Rendering;

// Work items and their completion counter are reused for every frame of this scene.
internal sealed class SceneWorkers : IDisposable
{
    private readonly int _width, _height;
    private readonly TestPattern _pattern;
    private readonly Strip[] _strips;
    private readonly CountdownEvent _done = new(0);
    private readonly Vector2[]? _polar;
    private readonly uint[]? _gridRow;
    private readonly Vector3[]? _plasma;
    private nint _pixels;
    private float _time;

    public SceneWorkers(int width, int height, TestPattern pattern, int sessions = 1)
    {
        _width = width;
        _height = height;
        _pattern = pattern;
        if (pattern == TestPattern.Plasma) _plasma = new Vector3[width];
        if (pattern == TestPattern.CyberGrid) _gridRow = ProceduralScene.CreateGridRow(width);
        // Bound the combined cache across encoder sessions; larger scenes compute geometry on demand.
        if (pattern == TestPattern.Cyber3D && (long)width * height * 8 <= (64L << 20) / sessions)
        {
            _polar = GC.AllocateUninitializedArray<Vector2>(checked(width * height));
            Parallel.For(0, height, y =>
            {
                float dy = y - height * 0.5f;
                for (int x = 0; x < width; x++)
                    _polar[y * width + x] = ProceduralScene.Polar(x - width * 0.5f, dy);
            });
        }
        int count = Math.Clamp(Environment.ProcessorCount / sessions, 1, Math.Max(1, height / 16));
        _strips = new Strip[count];
        for (int i = 0; i < count; i++)
            _strips[i] = new Strip(this, height * i / count, height * (i + 1) / count);
    }

    private sealed class Strip(SceneWorkers scene, int first, int end) : IThreadPoolWorkItem
    {
        public void Execute()
        {
            try { ProceduralScene.RenderRows(scene._pixels, scene._width, scene._height, first, end, scene._time, scene._pattern, scene._gridRow, scene._polar, scene._plasma); }
            finally { scene._done.Signal(); }
        }
    }

    public unsafe void Render(Span<byte> rgba, int frame, float time)
    {
        if (_pattern == TestPattern.ColorBars)
        {
            ProceduralScene.GenerateFrame(rgba, _width, _height, frame, time, _pattern);
            return;
        }
        if (_plasma != null)
        {
            float sinHalfTime = MathF.Sin(time / 2.0f);
            float offset = 0.5f * MathF.Sin(time / 5.0f);
            for (int x = 0; x < _width; x++)
                _plasma[x] = ProceduralScene.PlasmaColumn(x, _width, time, sinHalfTime, offset);
        }
        fixed (byte* pixels = rgba[..checked(_width * _height * 4)])
        {
            _pixels = (nint)pixels;
            _time = time;
            _done.Reset(_strips.Length);
            for (int i = 1; i < _strips.Length; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_strips[i], preferLocal: false);
            _strips[0].Execute();
            _done.Wait();
        }
    }

    public void Dispose() => _done.Dispose();
}
