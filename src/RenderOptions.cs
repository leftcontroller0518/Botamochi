using System.Globalization;
using Botamochi.Rendering;

namespace Botamochi;

internal sealed class RenderOptions
{
    internal int Width { get; private set; } = 1920;
    internal int Height { get; private set; } = 1080;
    internal int Fps { get; private set; } = 60;
    internal float Duration { get; private set; } = 5f;
    internal string Output { get; private set; } = "render_output.mp4";
    internal TestPattern Pattern { get; private set; } = TestPattern.Plasma;
    internal bool Pace { get; private set; }
    internal bool Direct { get; private set; }
    internal bool Lossless { get; private set; }
    internal bool NamedPipe { get; private set; }
    internal bool PhysicalCores { get; private set; }
    internal bool RealtimePriority { get; private set; }
    internal bool ShowHelp { get; private set; }
    internal int Sessions { get; private set; } = 1;
    internal int PipeBytes { get; private set; }
    internal int GcBudgetMb { get; private set; }
    internal int? NumaNode { get; private set; }
    internal int[] Gpus { get; private set; } = [0];

    internal int FrameCount => checked((int)(Duration * Fps));

    internal static RenderOptions Parse(string[] args)
    {
        var options = new RenderOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            switch (arg)
            {
                case "--help" or "-?": options.ShowHelp = true; break;
                case "--direct": options.Direct = true; break;
                case "--lossless": options.Lossless = true; break;
                case "--named-pipe": options.NamedPipe = true; break;
                case "--physical-cores": options.PhysicalCores = true; break;
                case "--realtime-priority": options.RealtimePriority = true; break;
                case "--pace" or "--realtime": options.Pace = true; break;
                case "--numa": options.NumaNode = ReadInt(args, ref i, 0, 255, arg); break;
                case "--sessions": options.Sessions = ReadInt(args, ref i, 1, 64, arg); break;
                case "--pipe-bytes": options.PipeBytes = ReadInt(args, ref i, 4096, 64 << 20, arg); break;
                case "--no-gc-mb": options.GcBudgetMb = ReadInt(args, ref i, 1, 4096, arg); break;
                case "--fps": options.Fps = ReadInt(args, ref i, 1, 240, arg); break;
                case "--width" or "-w": options.Width = ReadInt(args, ref i, 128, 7680, arg); break;
                case "--height" or "-h": options.Height = ReadInt(args, ref i, 128, 4320, arg); break;
                case "--duration" or "-d": options.Duration = ReadFloat(args, ref i, 0.5f, 3600f, arg); break;
                case "--output" or "-o": options.Output = ReadValue(args, ref i, arg); break;
                case "--gpus": options.Gpus = ParseGpus(ReadValue(args, ref i, arg)); break;
                case "--pattern" or "-p": options.Pattern = ParsePattern(ReadValue(args, ref i, arg)); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}. Use --help for usage.");
            }
        }

        if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
            throw new ArgumentException("NV12 output requires even width and height.");
        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length) throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, int min, int max, string option)
    {
        string text = ReadValue(args, ref index, option);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new ArgumentException($"{option} expects an integer from {min} to {max}.");
        return value;
    }

    private static float ReadFloat(string[] args, ref int index, float min, float max, string option)
    {
        string text = ReadValue(args, ref index, option);
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            throw new ArgumentException($"{option} expects a number from {min} to {max}.");
        return Math.Clamp(value, min, max);
    }

    private static int[] ParseGpus(string value)
    {
        string[] values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0) throw new ArgumentException("--gpus requires at least one adapter index.");
        var gpus = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!int.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out gpus[i]) || gpus[i] < 0)
                throw new ArgumentException($"Invalid GPU adapter index: {values[i]}.");
        }
        return gpus;
    }

    private static TestPattern ParsePattern(string value) => value.ToLowerInvariant() switch
    {
        "plasma" => TestPattern.Plasma,
        "cyber3d" or "3d" or "complex" => TestPattern.Cyber3D,
        "grid" or "cyber" => TestPattern.CyberGrid,
        "rainbow" or "gradient" => TestPattern.RainbowGradient,
        "bars" or "colorbars" => TestPattern.ColorBars,
        _ => throw new ArgumentException($"Unknown pattern: {value}.")
    };

    internal const string Help = """
Botamochi C# video renderer

Usage: Botamochi [options]
  -p, --pattern <plasma|cyber3d|grid|rainbow|bars>
  -w, --width <128..7680>       Output width, even number
  -h, --height <128..4320>      Output height, even number
      --fps <1..240>            Frames per second
  -d, --duration <0.5..3600>    Duration in seconds
  -o, --output <path>           MP4 output path
      --direct                  D3D12 shared-surface NVENC path
      --lossless                Lossless Direct NVENC encoding
      --sessions <1..64>        Parallel NVENC sessions
      --gpus <0,1,...>          GPU adapter indices
      --pace                    Render at realtime frame rate
      --named-pipe              Use a named pipe for FFmpeg
      --pipe-bytes <bytes>      Pipe buffer size
      --no-gc-mb <1..4096>      No-GC region budget
      --numa <0..255>           NUMA node for workers
      --physical-cores          Pin workers to distinct physical cores
      --realtime-priority       Request realtime process priority
  -?, --help                    Show this help
""";
}
