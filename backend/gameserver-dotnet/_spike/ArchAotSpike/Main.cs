// Entry point. Prints checksums so a NativeAOT-published run can be diffed
// against the JIT run byte-for-byte, plus a directional-only timing/allocation
// comparison against the dictionary shape GameWorld uses today.
//
// TIMING CAVEAT (ADR-7): this host shares CPU with everything else running on
// it and its tick p99 has been observed to swing 3.3x. The numbers printed here
// are directional only and MUST NOT be quoted as a benchmark result.

using System.Diagnostics;
using System.Globalization;

namespace ArchAotSpike;

public static class SpikeMain
{
    public static int Main(string[] args)
    {
        bool timing = args.Contains("--timing");

        Console.WriteLine($"runtime      : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"nativeaot    : {!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
        Console.WriteLine($"dynamic-code : IsSupported={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} IsCompiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}");
        Console.WriteLine($"arch         : {typeof(Arch.Core.World).Assembly.GetName().Version}");
        Console.WriteLine();

#if USE_ARCH_SYSTEM
        Console.WriteLine("config       : Arch + Arch.System + Arch.System.SourceGenerator");
#else
        Console.WriteLine("config       : Arch only (no Arch.System, no source generator)");
#endif
        Console.WriteLine();

        var results = new List<RunResult>();
        int failures = 0;

        void Guarded(string label, Func<RunResult> f)
        {
            try
            {
                var r = f();
                results.Add(r);
                Console.WriteLine($"{r.Label,-16} {r.Digest}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"{label,-16} THREW {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"                 {ex.StackTrace?.Split('\n')[0].Trim()}");
            }
        }

        Guarded("baseline-dict", () => BaselineRun.Run("baseline-dict"));
        Guarded("arch-direct", () => ArchRun.Run("arch-direct", useCommandBuffer: false));
        Guarded("arch-chunk", () => ArchChunkRun.Run("arch-chunk"));
        Guarded("arch-cmdbuffer", () => ArchRun.Run("arch-cmdbuffer", useCommandBuffer: true));
#if USE_ARCH_SYSTEM
        Guarded("arch-sourcegen", () => ArchSystemRun.Run("arch-sourcegen"));
#endif

        Console.WriteLine();

        // All implementations must agree on the observable simulation result.
        var reference = results[0];
        bool allMatch = true;
        foreach (var r in results)
        {
            bool ok = r.FinalCount == reference.FinalCount
                   && r.Sum == reference.Sum
                   && r.Xor == reference.Xor
                   && r.Created == reference.Created
                   && r.Destroyed == reference.Destroyed
                   && r.Stuns == reference.Stuns
                   && r.Unstuns == reference.Unstuns;
            if (!ok)
            {
                allMatch = false;
                Console.WriteLine($"MISMATCH: {r.Label} differs from {reference.Label}");
            }
        }
        Console.WriteLine(allMatch ? "AGREEMENT: all completed implementations produced identical state"
                                   : "AGREEMENT: FAILED");
        if (failures > 0) Console.WriteLine($"CONFIGS FAILED: {failures}");

        if (timing)
        {
            Console.WriteLine();
            Console.WriteLine("--- directional only, NOT a benchmark (see ADR-7) ---");
            Measure("baseline-dict", () => BaselineRun.Run("b"));
            Measure("arch-direct", () => ArchRun.Run("a", useCommandBuffer: false));
            Measure("arch-chunk", () => ArchChunkRun.Run("k"));
        }

        return allMatch ? 0 : 1;
    }

    private static void Measure(string label, Func<RunResult> run)
    {
        for (int i = 0; i < 3; i++) run();   // warm up

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        const int Reps = 10;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Reps; i++) run();
        sw.Stop();

        long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        double msPerRun = sw.Elapsed.TotalMilliseconds / Reps;
        double usPerTick = msPerRun * 1000.0 / Sim.Ticks;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label,-16} {msPerRun,8:F2} ms/run  {usPerTick,7:F1} us/tick  alloc {alloc / Reps / 1024.0,9:F1} KiB/run  gc {(GC.CollectionCount(0) - g0)}/{(GC.CollectionCount(1) - g1)}/{(GC.CollectionCount(2) - g2)}"));
    }
}
