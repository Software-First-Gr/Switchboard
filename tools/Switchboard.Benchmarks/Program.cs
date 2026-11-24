using System;
using System.Globalization;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using Switchboard.Benchmarks.Study;

namespace Switchboard.Benchmarks;

internal static class Program
{
    // dotnet run -- --filter '*'          BenchmarkDotNet suites
    // <exe> study --variants A=exe,...    runtime-async study driver (see tools/run-runtime-async-study.sh)
    // <exe> probe assembly|stack|cold     one measurement in this process's variant
    // <exe> probe load <conn> <concurrency> <warmupSeconds> <durationSeconds>
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length > 0 && args[0] == "study")
        {
            return await StudyDriver.Run(args[1..]);
        }

        if (args.Length > 1 && args[0] == "probe")
        {
            Variant.AssertAsBuilt();
            object result = args[1] switch
            {
                "assembly" => Probes.Assembly(),
                "stack" => await Probes.Stack(),
                "cold" => await Probes.Cold(),
                "load" => await PostgresLoad.Run(
                    args[2],
                    int.Parse(args[3], CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(int.Parse(args[4], CultureInfo.InvariantCulture)),
                    TimeSpan.FromSeconds(int.Parse(args[5], CultureInfo.InvariantCulture))),
                _ => throw new ArgumentException($"Unknown probe {args[1]}"),
            };
            Probes.Emit(result);
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
