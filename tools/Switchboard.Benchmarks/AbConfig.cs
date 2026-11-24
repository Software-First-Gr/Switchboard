using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace Switchboard.Benchmarks;

/// <summary>
/// Process-isolated A/B/C comparison: each job rebuilds Switchboard and this project with its own MSBuild switches and runs
/// in a separate process (BenchmarkDotNet 0.16, the first version that knows net11.0).
/// </summary>
public sealed class AbConfig : ManualConfig
{
    public AbConfig()
    {
        var job = Job.Default
            .WithRuntime(CoreRuntime.Core11_0)
            .WithLaunchCount(1)
            .WithWarmupCount(6)
            .WithIterationCount(15);

        AddJob(job.WithId("StateMachines").AsBaseline()
            .WithArguments([new MsBuildArgument("/p:UseRuntimeAsync=false"), new MsBuildArgument("/p:AppRuntimeAsync=false")]));
        AddJob(job.WithId("LibraryRA")
            .WithArguments([new MsBuildArgument("/p:UseRuntimeAsync=true"), new MsBuildArgument("/p:AppRuntimeAsync=false")]));
        AddJob(job.WithId("AllRA")
            .WithArguments([new MsBuildArgument("/p:UseRuntimeAsync=true"), new MsBuildArgument("/p:AppRuntimeAsync=true")]));

        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.P95);
        SummaryStyle = BenchmarkDotNet.Reports.SummaryStyle.Default.WithRatioStyle(RatioStyle.Percentage);
        WithOptions(ConfigOptions.JoinSummary);
    }
}
