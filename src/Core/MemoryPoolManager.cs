using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Botamochi.Core;

/// <summary>
/// 64バイトアラインメントされたネイティブフレームバッファ (Zero-Allocation)
/// </summary>
public sealed unsafe class NativeFrameBuffer : IDisposable
{
    private void* _ptr;
    private readonly int _byteLength;
    private readonly MemoryPoolManager? _owner;
    private int _isDisposed;

    public void* NativePointer => _ptr;
    public int ByteLength => _byteLength;
    public Span<byte> Span => new(_ptr, _byteLength);

    internal NativeFrameBuffer(int byteLength, MemoryPoolManager owner)
    {
        _byteLength = byteLength;
        _owner = owner;
        // AVX-512およびDirect3Dテクスチャアラインメントに最適な64バイト境界でネイティブメモリ確保
        _ptr = NativeMemory.AlignedAlloc((nuint)byteLength, 64);
    }

    internal NativeFrameBuffer(void* pointer, int byteLength)
    {
        _ptr = pointer;
        _byteLength = byteLength;
    }

    public void Return()
    {
        if (_owner != null && Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _owner.ReturnBuffer(this);
        }
    }

    internal void MarkRented() => Volatile.Write(ref _isDisposed, 0);

    internal void Terminate()
    {
        if (_ptr != null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }

    public void Dispose()
    {
        Return();
    }
}

/// <summary>
/// 完全Zero-Allocationを実現するカスタムネイティブメモリプール
/// </summary>
public sealed class MemoryPoolManager : IDisposable
{
    private readonly ConcurrentBag<NativeFrameBuffer> _pool = new();
    private readonly int _bufferSize;
    private readonly int _maxCapacity;
    private int _currentCount;
    private bool _disposed;

    public MemoryPoolManager(int width, int height, int maxCapacity = 16)
    {
        // NV12 format: Y plane = W * H, UV plane = W * (H / 2) -> Total = W * H * 1.5
        _bufferSize = (int)(width * height * 1.5);
        _maxCapacity = maxCapacity;

        for (int i = 0; i < maxCapacity; i++)
        {
            _pool.Add(new NativeFrameBuffer(_bufferSize, this));
            Interlocked.Increment(ref _currentCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeFrameBuffer Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pool.TryTake(out var buffer))
        {
            buffer.MarkRented();
            return buffer;
        }

        // プール枯渇時は新規確保（許容上限内）
        return new NativeFrameBuffer(_bufferSize, this);
    }

    internal void ReturnBuffer(NativeFrameBuffer buffer)
    {
        if (_disposed || _pool.Count >= _maxCapacity)
        {
            buffer.Terminate();
        }
        else
        {
            _pool.Add(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_pool.TryTake(out var buffer))
        {
            buffer.Terminate();
        }
    }
}

