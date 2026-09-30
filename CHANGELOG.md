# Changelog

All notable changes to Switchboard. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

For step-by-step upgrade instructions, see [Upgrading to 1.3](README.md#upgrading-to-13) and [Upgrading to 1.2](README.md#upgrading-to-12) in the README.

## [1.3.0] — 2026-09-30

No API changes. The version is a minor one because when the handler is created, and where its failures surface, is observable.

### Changed

- **The handler is resolved inside the pipeline, as in MediatR.** It used to be resolved before the first behavior ran. Now the innermost behavior's `next()` resolves it, so:
  - behaviors run before the handler is constructed, and construction time is inside any timing behavior;
  - a request that a behavior rejects (validation) or answers (a cache hit) never constructs its handler or its dependencies;
  - a missing handler, or one whose dependency cannot be resolved, fails inside the pipeline, where a logging or metrics behavior can catch and count it;
  - a behavior that calls `next()` more than once (a retry) gets a newly resolved handler each time.
- **Allocations with behaviors:** each dispatch allocates 64 bytes less (88 for void requests) and 8 bytes more per behavior, so less than before up to 7 behaviors. With no behaviors, still nothing beyond the handler.

### Behavior notes

- **With an `async` behavior registered, a missing or unconstructible handler now surfaces through the returned `Task`** instead of being thrown synchronously from `Send`. With no behaviors, or only behaviors that are not `async` methods, it is still thrown by `Send` itself. Code that `await`s the call is unaffected.
- **A behavior that turns exceptions into results now also sees configuration errors.** If a behavior catches every exception and returns a failure result, a missing handler or unresolvable dependency becomes such a result instead of an exception. `ValidateSwitchboard()` still reports missing handlers at startup.
- **Anything a handler's constructor did is now done after the behaviors' "before" code**, not ahead of it. Code that relied on the old order, such as a behavior reading scoped state the handler's constructor set, must move that work into the behavior or the handler's `Handle`.

## [1.2.2] — 2026-09-13

### Fixed

- **A covariant `Send` no longer poisons the handler cache.** `IRequest<out TResponse>` is covariant, so `Send<object>(new GetOrder())` or sending through `IRequest<ISomeBase>` compiles. It used to look for `IRequestHandler<GetOrder, object>`, fail, and cache that wrapper under `GetOrder`, after which every correct `Send(new GetOrder())` in the process threw `InvalidCastException`. The declared handler now runs and its response is converted.
- **A behavior can hand the rest of the pipeline a different token.** The token passed to `next(...)` used to be ignored, so a timeout behavior passing a linked token had no effect. It is now honored by everything inside that behavior; `next()` with no token keeps the token the behavior received, so cancellation is still never lost.
- **`AddOpenBehavior` rejects a behavior with other than two type parameters** with a clear message, instead of letting the container fail later at build time. `ValidateSwitchboard` no longer throws `IndexOutOfRangeException` for such a behavior registered directly on the container.
- **A later `AddSwitchboard` call can add or replace the scope-per-dispatch callback.** It used to be silently dropped when an earlier call had already switched scope-per-dispatch on. The last callback configured wins; a bare `UseScopePerDispatch()` never removes one configured elsewhere.

### Changed

- **Fewer allocations per dispatch.** A `Send` or `Publish` with no behaviors now allocates nothing beyond the handler itself; with behaviors, each dispatch allocates about 100 bytes less than before, and a void handler that completes synchronously no longer allocates a `Task<Unit>`. The telemetry closure was being allocated on every dispatch even with no listener attached; it is now only paid for when tracing or metrics are on.

## [1.2.1] — 2026-09-13

### Fixed

- **Scope per dispatch no longer merges scopes you isolate on purpose.** In 1.2.0, while a dispatch was in flight, *any* nested `Send`/`Publish` reused its scope — including one made through a mediator resolved from a scope the handler had created itself to get its own `DbContext`. That silently put the isolated work back on the outer `DbContext`. Now only a mediator resolved from the in-flight scope (the `ISender`/`IPublisher` injected into a handler) joins it; any other mediator starts its own dispatch scope. Only affects apps that call `UseScopePerDispatch`.

## [1.2.0] — 2026-09-13

No breaking API changes: every 1.1 call site compiles and behaves the same, apart from the fix and the small behavior notes below.

### Added

- **Startup validation** — `services.ValidateSwitchboard()` reads the registrations (without building the container or constructing handlers) and throws a `SwitchboardValidationException` listing every problem:
  - requests in scanned assemblies with no handler,
  - requests with more than one handler (only the last one registered would ever run),
  - open behaviors that never run for void requests because they are constrained to `IRequest<TResponse>`.
  Each check can be switched off through `SwitchboardValidationOptions`.
- **OpenTelemetry tracing and metrics** through `ActivitySource` and `Meter` from the base class library, with no new dependency. The spans are `Send {Request}`, `Publish {Notification}` and `Handle {Notification}` (one per handler). The histograms are `switchboard.request.duration` and `switchboard.notification.duration`. Nothing is recorded until you subscribe to `SwitchboardTelemetry.ActivitySourceName` / `SwitchboardTelemetry.MeterName`.
- **Scope per dispatch** — `cfg.UseScopePerDispatch()` runs every top-level `Send` and `Publish` in its own DI scope, reusing it for nested dispatches. It has overloads that take a callback (`DispatchScope` exposes `Parent`, `ServiceProvider` and `Message`) for carrying ambient state such as the current user into the new scope. Built for Blazor Server, where the caller's scope is the whole circuit.
- Tests and documentation for **constrained open behaviors** (`where TRequest : ISomeMarker`).

### Fixed

- Calling `AddSwitchboard` more than once with the same assembly or behavior no longer registers handlers and behaviors twice. Before, a notification handler or behavior registered twice ran twice.

### Behavior notes

- **Duplicates are skipped, not appended.** If the same open behavior is registered directly (`services.AddTransient(typeof(IPipelineBehavior<,>), typeof(X<,>))`) *and* through `AddOpenBehavior(typeof(X<,>))`, it now runs once, at the position of the first registration. Previously it ran twice.
- **Exceptions surface through the returned `Task`** when tracing/metrics are being listened to or scope-per-dispatch is on. Before, an exception such as a missing handler could be thrown synchronously from `Send`. Code that `await`s the call is unaffected.

## [1.1.0] — 2026-08-09

### Added

- `net8.0` and `net9.0` targets alongside `net10.0`, each floored at the lowest `Microsoft.Extensions.DependencyInjection.Abstractions` patch of its major. No API changes.

## [1.0.1] — 2026-07-26

### Added

- `AddBehavior<TBehavior>()` / `AddBehavior(Type)` for closed pipeline behaviors, sharing one ordering with `AddOpenBehavior`.

## [1.0.0] — 2026-07-26

- Initial release: `IRequest`/`IRequest<T>`, `INotification`, `IPipelineBehavior<,>`, `ISender`/`IPublisher`/`IMediator`, untyped `Send(object)`/`Publish(object)`, and assembly scanning, all MediatR-compatible.

[1.3.0]: https://github.com/Software-First-Gr/Switchboard/compare/v1.2.2...v1.3.0
[1.2.2]: https://github.com/Software-First-Gr/Switchboard/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/Software-First-Gr/Switchboard/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/Software-First-Gr/Switchboard/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Software-First-Gr/Switchboard/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/Software-First-Gr/Switchboard/releases/tag/v1.0.1
[1.0.0]: https://github.com/Software-First-Gr/Switchboard/commits/v1.0.1
