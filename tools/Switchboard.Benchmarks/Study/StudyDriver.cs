using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Testcontainers.PostgreSql;

namespace Switchboard.Benchmarks.Study;

/// <summary>
/// Runs the probes of every variant in separate, interleaved processes (A B C A B C ...) so drift in machine state hits all
/// variants alike, starts Postgres in a container for the load test, and prints medians as Markdown.
/// </summary>
internal static class StudyDriver
{
    public static async Task<int> Run(string[] args)
    {
        var options = Options.Parse(args);
        var variants = options.Variants;
        var results = new JsonObject { ["machine"] = Environment.MachineName.Length > 0 ? RuntimeLabel() : "" };

        Console.WriteLine($"Variants: {string.Join(", ", variants.Select(v => v.Name))}");

        // 1. Assembly shape and stack traces: deterministic, one run each.
        var assembly = new JsonObject();
        var stacks = new JsonObject();
        foreach (var variant in variants)
        {
            assembly[variant.Name] = Child(variant, "probe", "assembly");
            stacks[variant.Name] = Child(variant, "probe", "stack");
        }

        results["assembly"] = assembly;
        results["stack"] = stacks;

        // 2. Cold start: many fresh processes, interleaved.
        var cold = variants.ToDictionary(v => v.Name, _ => new JsonArray());
        for (var i = 0; i < options.ColdRuns; i++)
        {
            foreach (var variant in variants)
            {
                cold[variant.Name].Add(Child(variant, "probe", "cold"));
            }
        }

        results["cold"] = new JsonObject(cold.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

        // 3. Postgres load, interleaved.
        if (options.LoadRuns > 0)
        {
            await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
                .WithCommand("-c", "max_connections=300", "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "shared_buffers=256MB")
                .Build();
            Console.WriteLine("Starting Postgres (Testcontainers) ...");
            await postgres.StartAsync();
            var connection = postgres.GetConnectionString();
            await PostgresLoad.Seed(connection);

            var load = variants.ToDictionary(v => v.Name, _ => new JsonArray());
            for (var i = 0; i < options.LoadRuns; i++)
            {
                foreach (var variant in variants)
                {
                    Console.WriteLine($"Load run {i + 1}/{options.LoadRuns}: {variant.Name}");
                    load[variant.Name].Add(Child(variant, "probe", "load", connection,
                        options.Concurrency.ToString(CultureInfo.InvariantCulture),
                        options.WarmupSeconds.ToString(CultureInfo.InvariantCulture),
                        options.DurationSeconds.ToString(CultureInfo.InvariantCulture)));
                }
            }

            results["load"] = new JsonObject(load.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        }

        File.WriteAllText(options.Output, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine(Report(results, variants.Select(v => v.Name).ToArray()));
        Console.WriteLine($"Raw results: {options.Output}");
        return 0;
    }

    private static string RuntimeLabel()
        => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} cores";

    private static JsonNode Child(VariantExe variant, params string[] args)
    {
        var start = new ProcessStartInfo(variant.Path) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var line = stdout.Split('\n').LastOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        if (process.ExitCode != 0 || line is null)
        {
            throw new InvalidOperationException($"{variant.Name} {string.Join(' ', args)} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
        }

        return JsonNode.Parse(line["RESULT ".Length..])!;
    }

    private static string Report(JsonObject results, string[] names)
    {
        var sb = new StringBuilder();
        var baseline = names[0];

        sb.AppendLine("### Assembly").AppendLine();
        sb.AppendLine("| Variant | Switchboard.dll bytes | Types | Async state-machine types |").AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var name in names)
        {
            var a = results["assembly"]![name]!;
            sb.AppendLine($"| {name} | {a["bytes"]} | {a["types"]} | {a["asyncStateMachineTypes"]} |");
        }

        sb.AppendLine().AppendLine("### Stack traces seen by a handler behind three async behaviors").AppendLine();
        sb.AppendLine("| Variant | Live frames before first await | Live frames after resuming | Exception frames |").AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var name in names)
        {
            var s = results["stack"]![name]!;
            sb.AppendLine($"| {name} | {s["BeforeAwait"]!.AsArray().Count} | {s["AfterAwait"]!.AsArray().Count} | {s["Exception"]!.AsArray().Count} |");
        }

        foreach (var name in names)
        {
            sb.AppendLine().AppendLine($"<details><summary>{name}: live stack after resuming</summary>").AppendLine().AppendLine("```");
            foreach (var frame in results["stack"]![name]!["AfterAwait"]!.AsArray())
            {
                sb.AppendLine(frame!.GetValue<string>());
            }

            sb.AppendLine("```").AppendLine().AppendLine("</details>");
        }

        sb.AppendLine().AppendLine("### Cold start (median of fresh processes)").AppendLine();
        sb.AppendLine("| Variant | First Send+Publish+Stream ms | Methods JIT-compiled | IL bytes JIT-compiled | JIT time ms | Second Send µs |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var name in names)
        {
            var runs = results["cold"]![name]!.AsArray();
            sb.AppendLine($"| {name} | {Median(runs, "firstDispatchesMs"):F2} | {Median(runs, "jitMethods"):F0} | {Median(runs, "jitIlBytes"):F0} | {Median(runs, "jitTimeMs"):F2} | {Median(runs, "secondSendUs"):F1} |");
        }

        if (results["load"] is JsonObject load)
        {
            sb.AppendLine().AppendLine("### Postgres load (median of runs)").AppendLine();
            sb.AppendLine("| Variant | Requests/s | p50 µs | p99 µs | Client CPU µs/request | Bytes allocated/request | Gen0 GCs | GC pause ms |")
                .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            var baseCpu = Median(load[baseline]!.AsArray(), "cpuUsPerOp");
            var baseBytes = Median(load[baseline]!.AsArray(), "bytesPerOp");
            foreach (var name in names)
            {
                var runs = load[name]!.AsArray();
                var cpu = Median(runs, "cpuUsPerOp");
                var bytes = Median(runs, "bytesPerOp");
                sb.AppendLine($"| {name} | {Median(runs, "opsPerSecond"):F0} | {Median(runs, "p50Us"):F0} | {Median(runs, "p99Us"):F0} | " +
                              $"{cpu:F2} ({Delta(cpu, baseCpu)}) | {bytes:F0} ({Delta(bytes, baseBytes)}) | {Median(runs, "gen0"):F0} | {Median(runs, "gcPauseMs"):F1} |");
            }
        }

        return sb.ToString();
    }

    private static string Delta(double value, double baseline)
        => baseline == 0 ? "–" : $"{(value - baseline) / baseline * 100:+0.0;-0.0}%";

    private static double Median(JsonArray runs, string key)
    {
        var values = runs.Select(r => r![key]!.GetValue<double>()).Order().ToArray();
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    private sealed record VariantExe(string Name, string Path);

    private sealed record Options(
        VariantExe[] Variants, int ColdRuns, int LoadRuns, int Concurrency, int WarmupSeconds, int DurationSeconds, string Output)
    {
        public static Options Parse(string[] args)
        {
            string Get(string name, string fallback)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }

            var variants = Get("--variants", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Split('=', 2)).Select(p => new VariantExe(p[0], p[1])).ToArray();
            if (variants.Length == 0)
            {
                throw new ArgumentException("--variants Name=path/to/exe,... is required");
            }

            return new Options(variants,
                int.Parse(Get("--cold", "30"), CultureInfo.InvariantCulture),
                int.Parse(Get("--load-runs", "3"), CultureInfo.InvariantCulture),
                int.Parse(Get("--concurrency", "32"), CultureInfo.InvariantCulture),
                int.Parse(Get("--warmup", "5"), CultureInfo.InvariantCulture),
                int.Parse(Get("--duration", "20"), CultureInfo.InvariantCulture),
                Get("--out", "study-results.json"));
        }
    }
}
