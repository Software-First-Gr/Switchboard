using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Switchboard;

namespace Switchboard.Benchmarks;

/// <summary>
/// Which async lowering this process actually runs, read from the loaded code rather than trusted from the build:
/// the compiler marks the methods it rewrites into a state machine with <see cref="AsyncStateMachineAttribute"/>.
/// </summary>
internal static class Variant
{
    public static bool LibraryRuntimeAsync { get; } =
        !typeof(Mediator).GetMethod("DispatchInNewScope", BindingFlags.Instance | BindingFlags.NonPublic)!
            .IsDefined(typeof(AsyncStateMachineAttribute));

    public static bool AppRuntimeAsync { get; } =
        !typeof(Variant).GetMethod(nameof(Probe), BindingFlags.Static | BindingFlags.NonPublic)!
            .IsDefined(typeof(AsyncStateMachineAttribute));

    public static string Name => (LibraryRuntimeAsync, AppRuntimeAsync) switch
    {
        (false, false) => "StateMachines",
        (true, false) => "LibraryRA",
        (true, true) => "AllRA",
        (false, true) => "AppRA",
    };

    /// <summary>Throws when the loaded code does not match what the build was asked for.</summary>
    public static void AssertAsBuilt()
    {
        var expectedLibrary = Expected("ExpectLibraryRuntimeAsync");
        var expectedApp = Expected("ExpectAppRuntimeAsync");

        if (expectedLibrary != LibraryRuntimeAsync || expectedApp != AppRuntimeAsync)
        {
            throw new InvalidOperationException(
                $"Built for library={expectedLibrary}/app={expectedApp} runtime async, " +
                $"but the loaded code is library={LibraryRuntimeAsync}/app={AppRuntimeAsync}.");
        }
    }

    private static bool Expected(string key)
        => bool.Parse(typeof(Variant).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    private static async Task Probe() => await Task.Yield();
}
