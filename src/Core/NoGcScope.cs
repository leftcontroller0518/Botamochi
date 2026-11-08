using System.Runtime;

namespace Botamochi.Core;

internal sealed class NoGcScope : IDisposable
{
    private readonly bool _active;
    public NoGcScope(int megabytes)
    {
        if (megabytes != 0)
        {
            _active = GC.TryStartNoGCRegion((long)megabytes << 20, disallowFullBlockingGC: true);
            if (!_active) Console.Error.WriteLine("No-GC budget unavailable.");
        }
    }

    public void Dispose()
    {
        if (!_active) return;
        try { GC.EndNoGCRegion(); }
        catch (InvalidOperationException) { Console.Error.WriteLine("No-GC region ended before rendering completed."); }
    }
}
