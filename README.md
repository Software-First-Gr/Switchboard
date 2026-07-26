# Switchboard

[![NuGet](https://img.shields.io/nuget/v/SoftwareFirst.Switchboard.svg)](https://www.nuget.org/packages/SoftwareFirst.Switchboard)
[![CI](https://github.com/Software-First-Gr/Switchboard/actions/workflows/ci.yml/badge.svg)](https://github.com/Software-First-Gr/Switchboard/actions/workflows/ci.yml)

**A lightweight, MediatR-compatible mediator for .NET.**

Switchboard implements the request/response, notification, and pipeline-behavior surface of MediatR on top of `Microsoft.Extensions.DependencyInjection`, in a few hundred lines of code with a single dependency (`Microsoft.Extensions.DependencyInjection.Abstractions`). It was extracted from a production system that moved off MediatR when it became commercially licensed: swap your `using` directives, change one registration call, and your handlers, behaviors, and call sites compile unchanged.

## Install

```bash
dotnet add package SoftwareFirst.Switchboard
```

Targets `net10.0`. The package ID is prefixed, but the assembly and namespace are both plain `Switchboard` — you write `using Switchboard;`.

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
services.AddSwitchboard(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>());
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

Void requests run through the same pipeline with `TResponse == Unit`, so open-generic behaviors apply to them unchanged.

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

## Migrating from MediatR

1. Replace the `MediatR` package reference with `SoftwareFirst.Switchboard`.
2. Replace `using MediatR;` with `using Switchboard;`.
3. Replace `services.AddMediatR(...)` with `services.AddSwitchboard(...)` — the configuration methods (`RegisterServicesFromAssemblyContaining`, `RegisterServicesFromAssembly`, `AddOpenBehavior`) keep their names.

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

## Semantics worth knowing

- **Cancellation is never lost.** The `CancellationToken` passed to `Send` flows to every behavior and the handler, even when a behavior calls `next()` without arguments.
- **Handlers and behaviors are transient**; they are resolved from the scope the mediator was resolved from, so scoped dependencies work as expected.
- **Publishing to zero handlers** is a no-op, mirroring MediatR.
- Handler-type wrappers are cached statically per request type; the cache is stateless and thread-safe.

## License

[Apache 2.0](LICENSE)
