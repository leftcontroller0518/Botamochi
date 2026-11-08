using System.Runtime.InteropServices;
using Botamochi.Rendering;

namespace Botamochi.Encoder;

internal sealed unsafe partial class DirectNvenc : IDisposable
{
    private nint _encoder;
    private readonly bool _pipelined;
    internal int SurfaceCount => _pipelined ? 4 : 1;
    private readonly int _length;
    internal Span<byte> Upload => _length == 0 ? default : new(rgbaUpload(_encoder), _length);

    internal DirectNvenc(int width, int height, int fps, int gpu, TestPattern pattern, bool lossless, bool pipeline)
    {
        int mode = pattern switch
        {
            TestPattern.Plasma => 0, TestPattern.ColorBars => 2, TestPattern.RainbowGradient => 3, _ => 1
        };
        if (pipeline)
        {
            try { _encoder = createPipelinedEncoder(width, height, fps, gpu, mode, lossless ? 1 : 0); }
            catch (EntryPointNotFoundException) { } // Older synchronous native library.
            _pipelined = _encoder != 0;
        }
        if (_encoder == 0) _encoder = createEncoder(width, height, fps, gpu, mode, lossless ? 1 : 0);
        if (_encoder == 0) throw Error();
        _length = rgbaUpload(_encoder) == null ? 0 : checked(width * height * 4);
    }

    internal ReadOnlySpan<byte> Encode(float time)
    {
        if (encodeFrame(_encoder, time, out byte* bytes, out int length) != 0) throw Error();
        return new ReadOnlySpan<byte>(bytes, length);
    }

    internal ReadOnlySpan<byte> Drain()
    {
        if (!_pipelined) return default;
        if (drainEncoder(_encoder, out byte* bytes, out int length) != 0) throw Error();
        return new ReadOnlySpan<byte>(bytes, length);
    }

    public void Dispose()
    {
        if (_encoder != 0) destroyEncoder(_encoder);
        _encoder = 0;
    }

    private static Exception Error() => new InvalidOperationException(Marshal.PtrToStringAnsi(encoderError()));
    [LibraryImport("BotamochiNvenc")] private static partial nint createEncoder(int width, int height, int fps, int gpu, int rgba, int lossless);
    [LibraryImport("BotamochiNvenc")] private static partial nint createPipelinedEncoder(int width, int height, int fps, int gpu, int mode, int lossless);
    [LibraryImport("BotamochiNvenc")] private static partial int drainEncoder(nint encoder, out byte* data, out int size);
    [LibraryImport("BotamochiNvenc")] private static partial byte* rgbaUpload(nint encoder);
    [LibraryImport("BotamochiNvenc")] private static partial int encodeFrame(nint encoder, float time, out byte* data, out int size);
    [LibraryImport("BotamochiNvenc")] private static partial nint encoderError();
    [LibraryImport("BotamochiNvenc")] private static partial void destroyEncoder(nint encoder);
}
