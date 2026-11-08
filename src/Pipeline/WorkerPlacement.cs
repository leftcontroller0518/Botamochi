using System.Runtime.InteropServices;

namespace Botamochi.Pipeline;

internal static unsafe partial class WorkerPlacement
{
    // SYSTEM_CPU_SET_INFORMATION is a sequence of variable-sized records.
    internal static void Apply(int worker, int? node, bool physical)
    {
        if (!node.HasValue && !physical) return;
        GetSystemCpuSetInformation(null, 0, out uint length, 0, 0);
        byte[] records = new byte[length];
        var ids = new List<uint>();
        var cores = new HashSet<(ushort, byte)>();
        fixed (byte* start = records)
        {
            if (GetSystemCpuSetInformation(start, length, out length, 0, 0) == 0) throw new System.ComponentModel.Win32Exception();
            for (uint offset = 0; offset < length;)
            {
                byte* p = start + offset;
                uint size = *(uint*)p;
                if (size < 20 || size > length - offset) throw new InvalidDataException("Invalid CPU set record");
                if (*(uint*)(p + 4) == 0 && (!node.HasValue || p[17] == node) &&
                    (p[19] & 1) == 0 && ((p[19] & 2) == 0 || (p[19] & 4) != 0) &&
                    (!physical || cores.Add((*(ushort*)(p + 12), p[15]))))
                    ids.Add(*(uint*)(p + 8));
                offset += size;
            }
        }
        if (ids.Count == 0) throw new ArgumentException("No available CPU set matches the requested topology.");
        uint id = ids[worker % ids.Count];
        if (SetThreadSelectedCpuSets(GetCurrentThread(), &id, 1) == 0) throw new System.ComponentModel.Win32Exception();
    }

    [LibraryImport("kernel32", SetLastError = true)] private static partial int GetSystemCpuSetInformation(
        byte* buffer, uint size, out uint length, nint process, uint flags);
    [LibraryImport("kernel32", SetLastError = true)] private static partial int SetThreadSelectedCpuSets(nint thread, uint* ids, uint count);
    [LibraryImport("kernel32")] private static partial nint GetCurrentThread();
}
