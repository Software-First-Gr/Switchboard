using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Switchboard.Benchmarks;

public sealed class RuntimeAsyncConfig : ManualConfig
{
    public RuntimeAsyncConfig()
    {
#if SWITCHBOARD_EXPECT_RUNTIME_ASYNC
        AddJob(Job.ShortRun
            .WithId("RuntimeAsync")
            .WithToolchain(InProcessEmitToolchain.From(new InProcessEmitSettings())));
#else
        AddJob(Job.ShortRun
            .WithId("StateMachines")
            .WithToolchain(InProcessEmitToolchain.From(new InProcessEmitSettings())));
#endif
    }
}