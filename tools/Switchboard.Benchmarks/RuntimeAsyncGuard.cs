using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Switchboard;

namespace Switchboard.Benchmarks;

internal static class RuntimeAsyncGuard
{
    public static void AssertExpectedMode()
    {
#if SWITCHBOARD_EXPECT_RUNTIME_ASYNC
        const bool expectedStateMachine = false;
#else
        const bool expectedStateMachine = true;
#endif

        AssertStateMachine(typeof(Mediator).GetMethod(
            "DispatchInNewScope", BindingFlags.Instance | BindingFlags.NonPublic)!);
        AssertStateMachine(typeof(Mediator).GetMethod(
            "SendCovariant", BindingFlags.Instance | BindingFlags.NonPublic)!);
        AssertStateMachine(typeof(SwitchboardTelemetry).GetMethod(
            "Observe", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!);

        var notificationWrapper = typeof(Mediator).Assembly.GetType("Switchboard.NotificationHandlerWrapperImpl`1")!;
        AssertStateMachine(notificationWrapper.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!);

        void AssertStateMachine(MethodInfo method)
        {
            var actual = method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false);
            if (actual != expectedStateMachine)
            {
                throw new InvalidOperationException(
                    $"{method.DeclaringType?.FullName}.{method.Name} has AsyncStateMachineAttribute={actual}, " +
                    $"but this benchmark job expects {expectedStateMachine}. Check its UseRuntimeAsync build argument.");
            }
        }
    }
}