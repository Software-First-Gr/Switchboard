# Switchboard

[![NuGet](https://img.shields.io/nuget/v/SoftwareFirst.Switchboard.svg)](https://www.nuget.org/packages/SoftwareFirst.Switchboard)
[![CI](https://github.com/Software-First-Gr/Switchboard/actions/workflows/ci.yml/badge.svg)](https://github.com/Software-First-Gr/Switchboard/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

**A lightweight, MediatR-compatible mediator for .NET — with the production safety nets MediatR never had.**

📖 Overview, migration guide and FAQ: **[softwarefirst.gr/switchboard](https://softwarefirst.gr/switchboard)**

> **New in 1.2:** startup validation, built-in OpenTelemetry, scope per dispatch for Blazor Server, and a fix for handlers registered twice. It's a drop-in upgrade from 1.1 — see **[Upgrading to 1.2](#upgrading-to-12)** and the [changelog](CHANGELOG.md).

Switchboard implements the request/response, notification, and pipeline-behavior surface of MediatR on top of `Microsoft.Extensions.DependencyInjection`, in under 500 lines of code with a single dependency (`Microsoft.Extensions.DependencyInjection.Abstractions`). It was extracted from a production system that moved off MediatR when it became commercially licensed: swap your `using` directives, change one registration call, and your handlers, behaviors, and call sites compile unchanged.

## Install

```bash
dotnet add package SoftwareFirst.Switchboard
```

Targets `net8.0`, `net9.0` and `net10.0`, so you can move off MediatR without moving frameworks first. The package ID is prefixed, but the assembly and namespace are both plain `Switchboard` — you write `using Switchboard;`.

## Beyond MediatR

Same API, plus four things every one of our production apps ended up needing — each opt-in, none adding a dependency.

| The problem in production | What Switchboard does |
| --- | --- |
| A request with no handler (or two) is only discovered when a user hits it. | **[Startup validation](#startup-validation)** — `ValidateSwitchboard()` lists every missing and duplicate handler before the app serves a request, without constructing a single handler. |
| A behavior constrained to `where TRequest : IRequest<TResponse>` — the shape most templates ship — **silently never runs for void commands**. Your exception logging and timing just aren't there. | **[The same validation](#the-void-request-trap)** names the behavior and every request it skips, and tells you the one-word fix. |
| Every app writes its own `PerformanceBehaviour` to get spans and timings. | **[OpenTelemetry built in](#opentelemetry)** — a span per `Send`, per `Publish` and per notification handler, plus duration histograms. One `AddSource` line to switch on; zero cost when nobody listens. |
| In Blazor Server the DI scope is the whole circuit, so one `DbContext` serves every click: *"A second operation was started on this context"*. | **[Scope per dispatch](#scope-per-dispatch-blazor-server)** — each command gets its own scope and unit of work; nested commands share it; the current user is carried across. |

It also fixes a trap common to hand-rolled registration: calling `AddSwitchboard` from two modules that scan the same assembly **no longer registers handlers twice** (which made every notification handler run twice).

## Why this one

Several MediatR alternatives exist now, and most compete on speed or feature count. Switchboard competes on being small and safe:

- **Under 500 lines of code**, across files you can read end to end in one sitting.
- **One dependency** — `Microsoft.Extensions.DependencyInjection.Abstractions`, floored at the lowest patch of each major so it never drags your other `Microsoft.Extensions.*` packages forward. Telemetry uses `ActivitySource` and `Meter` from the base class library.
- **No source generators, analyzers, or build-time magic.** Plain reflection over the DI container, the way MediatR does it.
- **A deliberately identical API surface** — the migration is a find-and-replace, not a rewrite.
- **Apache 2.0**, extracted from a production system that made this exact switch.

If you need streaming, parallel publish strategies, or maximum throughput, a source-generated alternative is the better fit — the [migration table](#migrating-from-mediatr) below says so explicitly.

## Quick start

Define a request and its handler:

```csharp
using Switchboard;

public sealed record GetOrder(int Id) : IRequest<OrderDto>;

public sealed class GetOrderHandler : IRequestHandler<GetOrder, OrderDto>
{
    public Task<OrderDto> Handle(GetOrder request, CancellationToken cancellationToken)
        => /* ... */;
}
```

Register the mediator and send:

```csharp
builder.Services.AddSwitchboard(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>());

builder.Services.ValidateSwitchboard(); // optional, recommended: fail at startup, not in production
var app = builder.Build();
```

```csharp
public sealed class OrderController(ISender sender) : ControllerBase
{
    [HttpGet("{id}")]
    public Task<OrderDto> Get(int id, CancellationToken ct) => sender.Send(new GetOrder(id), ct);
}
```

Void requests implement `IRequest` (no type argument) and are handled by `IRequestHandler<TRequest>`.

## Pipeline behaviors

Behaviors wrap every handler, outermost first in the order they are added:

```csharp
public sealed class LoggingBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IBaseRequest
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // before
        var response = await next(cancellationToken);
        // after
        return response;
    }
}
```

```csharp
services.AddSwitchboard(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>()
    .AddOpenBehavior(typeof(LoggingBehaviour<,>))      // runs outermost
    .AddOpenBehavior(typeof(ValidationBehaviour<,>))); // runs inside logging
```

A behavior that applies to one specific request/response pair goes in with `AddBehavior`:

```csharp
services.AddSwitchboard(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>()
    .AddOpenBehavior(typeof(LoggingBehaviour<,>))   // outermost
    .AddBehavior<AuditGetOrder>()                   // IPipelineBehavior<GetOrder, OrderDto>
    .AddOpenBehavior(typeof(ValidationBehaviour<,>))); // innermost
```

Open and closed behaviors share a single ordering, so the first one added is outermost regardless of which kind it is. Registering directly against the container still works too:
`services.AddTransient<IPipelineBehavior<GetOrder, OrderDto>, MyBehavior>()`.

Void requests run through the same pipeline with `TResponse == Unit`, so open-generic behaviors apply to them unchanged — as long as their constraints allow it (see below).

### Constrained behaviors

Generic constraints decide which requests a behavior applies to. The container skips the behavior for any request that doesn't satisfy them, so a marker interface is all it takes to target a subset:

```csharp
public interface IIdempotentCommand { Guid CommandId { get; } }

public sealed class IdempotencyBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IIdempotentCommand   // runs only for commands that opt in
{
    /* ... */
}
```

This is covered by tests for both typed and void requests, so you can rely on it.

### The void-request trap

This constraint looks harmless and is in most Clean Architecture templates:

```csharp
public class UnhandledExceptionBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>   // ⚠️ excludes every void request
```

A void request implements `IRequest`, not `IRequest<Unit>` (the same is true in MediatR 12+), so the container **silently skips this behavior for every void command** — no exception logging, no timing, no transaction, and nothing tells you. Constrain to the marker every request implements instead:

```csharp
    where TRequest : IBaseRequest          // ✅ typed and void requests alike
```

[`ValidateSwitchboard()`](#startup-validation) detects the trap and names every request it affects. Constraints that exclude requests on purpose, like `IIdempotentCommand` above, are left alone.

## Notifications

```csharp
public sealed record OrderPlaced(int OrderId) : INotification;

public sealed class SendReceipt : INotificationHandler<OrderPlaced> { /* ... */ }
public sealed class UpdateStats : INotificationHandler<OrderPlaced> { /* ... */ }
```

```csharp
await publisher.Publish(new OrderPlaced(42), cancellationToken);
```

Handlers run **sequentially, in registration order** — never in parallel — so they can safely share scoped state such as an EF Core `DbContext`.

## Startup validation

Call `ValidateSwitchboard()` after all registrations, just before building the provider:

```csharp
builder.Services.AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<GetOrderHandler>());
// ... everything else ...
builder.Services.ValidateSwitchboard();
var app = builder.Build();
```

It reads the service registrations only — it never builds the container or constructs a handler, so it is safe for handlers that need an HTTP request or a Blazor circuit. When something is wrong it throws a `SwitchboardValidationException` listing every problem at once:

```text
Switchboard registration is invalid:
 - CancelOrder has no handler. Add a class implementing IRequestHandler<CancelOrder>.
 - GetOrder has 2 handlers (GetOrderHandler, LegacyGetOrderHandler); only the last one registered would ever run.
 - UnhandledExceptionBehaviour<TRequest, TResponse> never runs for 26 void request(s) (SubmitFeedback, ArchiveOrder, ...) because it constrains TRequest to IRequest<TResponse>, which void requests do not implement. Constrain it to IBaseRequest to cover every request.
```

| Check | Covers | Option to turn it off |
| --- | --- | --- |
| Missing handlers | Every concrete request type in the scanned assemblies | `RequireHandlerForEveryRequest` |
| Duplicate handlers | Every handler registration, scanned or manual | `ForbidDuplicateHandlers` |
| Behaviors that skip void requests | Every open behavior, via `AddOpenBehavior` or registered directly | `ForbidBehaviorsThatSkipVoidRequests` |

```csharp
// e.g. a shared application assembly whose handlers live in several hosts
services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
```

It also works well as a one-line unit test over your real registrations.

## OpenTelemetry

Switchboard emits traces and metrics through `System.Diagnostics` — no package to add, and nothing is recorded until a listener subscribes. Switch them on in your OpenTelemetry setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(SwitchboardTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(SwitchboardTelemetry.MeterName));
```

**Spans**

| Span | When | Tags |
| --- | --- | --- |
| `Send GetOrder` | every `Send`, behaviors included | `switchboard.request` |
| `Publish OrderPlaced` | every `Publish` | `switchboard.notification` |
| `Handle OrderPlaced` | each notification handler, as a child of its `Publish` | `switchboard.notification`, `switchboard.handler` |

A failure sets the span status to `Error`, adds `error.type` and an `exception` event. Cancellations are tagged with `error.type` but are not marked as errors — a user navigating away is not a failed handler.

**Metrics**

| Instrument | Unit | Tags |
| --- | --- | --- |
| `switchboard.request.duration` | s | `switchboard.request`, `error.type` on failure |
| `switchboard.notification.duration` | s | `switchboard.notification`, `error.type` on failure |

Tag values are type names (`GetOrder`, `Envelope<Invoice>`), never request contents, so cardinality stays bounded and no personal data leaks into your telemetry.

## Scope per dispatch (Blazor Server)

By default a handler is resolved from the scope the mediator was resolved from — exactly like MediatR. In ASP.NET Core that is the HTTP request, which is what you want. In **Blazor Server** it is the whole circuit, so a single scoped `DbContext` serves every render and click on the connection. `DbContext` isn't thread-safe and Blazor interleaves async work, so this fails intermittently with *"A second operation was started on this context instance"*.

Turn on a scope per dispatch:

```csharp
services.AddSwitchboard(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>()
    .UseScopePerDispatch());
```

- Every top-level `Send` and `Publish` runs in its **own DI scope**, disposed when it completes — one `DbContext`, one unit of work per operation.
- A handler that sends or publishes again through its injected `ISender`/`IPublisher` **reuses the scope in flight**, so nested commands (and domain events published from `SaveChanges`) share one `DbContext` and one transaction.
- A scope you create yourself inside a handler stays yours: a mediator resolved from it gets its own dispatch scope, so deliberately isolated work is never folded back into the outer unit of work.
- Concurrent dispatches from the same circuit never share a scope.

Scoped state that lived in the caller's scope — typically the current user — has to be carried into the new one. The callback runs before the handler, with both scopes in hand:

```csharp
services.AddSwitchboard(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>()
    .UseScopePerDispatch(async (scope, cancellationToken) =>
    {
        var auth = await scope.Parent.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(auth.User);
    }));
```

`scope.Parent` is the caller's scope, `scope.ServiceProvider` the new one, and `scope.Message` the request or notification. A synchronous overload, `UseScopePerDispatch(scope => ...)`, is there too.

> Work that outlives the dispatch — `Task.Run` fire-and-forget started inside a handler — must not send through the mediator it inherited: the scope it would reuse is disposed when the outer dispatch completes. Create a scope of your own for background work.

## Upgrading to 1.2

1.2 is a drop-in upgrade from 1.1: no API was removed or changed, so bumping the package is enough to compile and run. The new features are opt-in. The steps below take about fifteen minutes and are worth doing, because the validation tends to find something real.

### 1. Bump the package

```bash
dotnet add package SoftwareFirst.Switchboard --version 1.2.2
```

With central package management, change the version in `Directory.Packages.props` instead.

### 2. Check the two behavior notes

Most applications are unaffected by either note, but they are the only differences you can observe:

| What changed | Who notices | What to do |
| --- | --- | --- |
| Registering the same handler or behavior twice (two `AddSwitchboard` calls, or `AddOpenBehavior` plus a direct `AddTransient` of the same type) now registers it **once**. | Apps where a notification handler or behavior was running twice without anyone meaning it to. That was a bug, and it's fixed. | Nothing, unless you relied on the double run. If a behavior was registered both ways, it now sits at the position of its **first** registration, so check the pipeline order. |
| Exceptions can surface through the returned `Task` instead of being thrown synchronously from `Send` (when telemetry is listened to or scope-per-dispatch is on). | Only code that calls `Send` without `await` inside a `try`. | `await` the call. |

### 3. Turn on startup validation

Add it after every registration, just before the provider is built:

```csharp
builder.Services.ValidateSwitchboard();
var app = builder.Build();
```

Also add a one-line test over your real registrations, so a problem fails the build before it fails a deploy:

```csharp
[Fact]
public void Switchboard_registrations_are_valid()
    => new ServiceCollection().AddApplication().ValidateSwitchboard();
```

If it throws, each message says what to fix:

| Message | Fix |
| --- | --- |
| `X has no handler` | Add the handler. If the request is handled in another host, turn the check off for this one: `ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false)`. |
| `X has 2 handlers (A, B)` | Delete the handler that shouldn't exist. Until now only the last one registered was running. |
| `SomeBehaviour<TRequest, TResponse> never runs for N void request(s)` | Change the constraint from `where TRequest : IRequest<TResponse>` to `where TRequest : IBaseRequest`. |

> **Before widening a constraint, read the behavior.** After the change it starts running for your void commands too. For logging, timing, exception handling and validation that is the point. For a transaction or caching behavior, make sure that's what you want. Any metrics the behavior records also gain a series per void command, so dashboards and alerts that filter by request name may start matching commands they never saw before.

### 4. Opt into telemetry (optional)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(SwitchboardTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(SwitchboardTelemetry.MeterName));
```

If you already have a `PerformanceBehaviour` that starts its own span, you'll see Switchboard's `Send X` span as its parent. Either is fine to keep. The `Publish` and `Handle` spans for notification handlers are usually the new information. See [OpenTelemetry](#opentelemetry) for the span and metric names.

### 5. Blazor Server: replace a hand-written scoping sender (optional)

If you wrote an `ISender` decorator that creates a scope per call to avoid sharing a `DbContext` across a circuit, 1.2 does the same thing for `Send` **and** `Publish`, and handles nested dispatches:

```csharp
// Before
services.AddScoped<ISender, ScopingSender>();

// After
services.AddSwitchboard(cfg => cfg.UseScopePerDispatch(async (scope, cancellationToken) =>
{
    var auth = await scope.Parent.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
    scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(auth.User);
}));
```

`AddSwitchboard` is safe to call again for this, so you can put it in the host or infrastructure layer without touching your application layer's registration: the last callback configured wins, and a bare `UseScopePerDispatch()` elsewhere never removes it. Port the decorator's tests too, especially for identity: the callback is where the user carries over. See [Scope per dispatch](#scope-per-dispatch-blazor-server).

## Migrating from MediatR

1. Replace the `MediatR` package reference with `SoftwareFirst.Switchboard`.
2. Replace `using MediatR;` with `using Switchboard;`.
3. Replace `services.AddMediatR(...)` with `services.AddSwitchboard(...)` — the configuration methods (`RegisterServicesFromAssemblyContaining`, `RegisterServicesFromAssembly`, `AddOpenBehavior`) keep their names.
4. Optionally add `services.ValidateSwitchboard()` before `Build()` — it tends to find something on the first run.

| MediatR feature | Switchboard |
| --- | --- |
| `IRequest`, `IRequest<T>`, `IRequestHandler<,>`, `IRequestHandler<>` | ✅ identical |
| `INotification`, `INotificationHandler<>` | ✅ identical |
| `IPipelineBehavior<,>` (first registered runs outermost) | ✅ identical |
| `ISender`, `IPublisher`, `IMediator`, `Unit` | ✅ identical |
| Untyped `Send(object)` / `Publish(object)` | ✅ identical |
| Assembly scanning for handlers | ✅ identical |
| Streaming (`IStreamRequest<>`) | ❌ not implemented |
| Request pre-/post-processors | ❌ use a pipeline behavior |
| Exception handlers/actions (`IRequestExceptionHandler`) | ❌ use a pipeline behavior |
| Custom publish strategies (parallel, etc.) | ❌ sequential only |
| Startup validation of handlers and behaviors | ➕ Switchboard only |
| Built-in OpenTelemetry traces and metrics | ➕ Switchboard only |
| Scope per dispatch for Blazor Server | ➕ Switchboard only |

## Semantics worth knowing

- **Cancellation is never lost.** The `CancellationToken` passed to `Send` flows to every behavior and the handler, even when a behavior calls `next()` without arguments. A behavior that passes a token of its own to `next` (a linked token with a timeout, say) hands it to everything inside it.
- **Covariant sends work.** `IRequest<out TResponse>` is covariant, so a `GetOrder : IRequest<OrderDto>` can be sent as `IRequest<object>` or through a base interface of `OrderDto`; the handler registered for `OrderDto` runs and its response is converted.
- **Handlers and behaviors are transient**; they are resolved from the scope the mediator was resolved from (or the per-dispatch scope, when enabled), so scoped dependencies work as expected.
- **`AddSwitchboard` is safe to call more than once.** A handler or behavior that is already registered is not added again, so modules that scan a shared assembly never make a handler run twice.
- **Publishing to zero handlers** is a no-op, mirroring MediatR.
- Handler-type wrappers are cached statically per request type; the cache is stateless and thread-safe.

## License

[Apache 2.0](LICENSE)
