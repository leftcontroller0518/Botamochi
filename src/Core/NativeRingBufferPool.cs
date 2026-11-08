using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Botamochi.Core;

/// <summary>
/// スロットバッファを表す ref 構造体 (Zero-Allocation)
/// </summary>
public readonly unsafe ref struct NativeBufferSlot
{
    public readonly void* Pointer;
    public readonly int Size;
    public readonly Span<byte> Span;

    public NativeBufferSlot(void* ptr, int size)
    {
        Pointer = ptr;
        Size = size;
        Span = new Span<byte>(ptr, size);
    }
}

/// <summary>
/// ロックフリー・64バイト境界循環ネイティブメモリプール (Zero-Allocation)
/// </summary>
public sealed unsafe class NativeRingBufferPool : IDisposable
{
    private void* _arena;
    private readonly NativeFrameBuffer[] _frames;
    private int _next;
    public int Capacity => _frames.Length;
    public int BufferSize { get; }

    public NativeRingBufferPool(int width, int height, int capacity = 8)
    {
        if (width <= 0 || height <= 0 || (width | height) % 2 != 0 || capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        BufferSize = checked(width * height + width * (height / 2));
        nuint stride = ((nuint)BufferSize + 127) & ~(nuint)127;
        _frames = new NativeFrameBuffer[capacity];
        _arena = NativeMemory.AlignedAlloc(checked(stride * (nuint)capacity), 128);
        try
        {
            for (int i = 0; i < capacity; i++)
                _frames[i] = new NativeFrameBuffer((byte*)_arena + stride * (nuint)i, BufferSize);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // One producer; size the ring for queue capacity + writer + producer.
    internal NativeFrameBuffer NextFrame()
    {
        ObjectDisposedException.ThrowIf(_arena == null, this);
        NativeFrameBuffer frame = _frames[_next];
        if (++_next == _frames.Length) _next = 0;
        return frame;
    }

    public NativeBufferSlot AcquireNext()
    {
        var frame = NextFrame();
        return new NativeBufferSlot(frame.NativePointer, frame.ByteLength);
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_arena);
        _arena = null;
    }
}
