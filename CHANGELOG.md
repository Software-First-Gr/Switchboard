# Changelog

All notable changes to Switchboard. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

For step-by-step upgrade instructions, see [Upgrading to 1.2](README.md#upgrading-to-12) in the README.

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

[1.2.1]: https://github.com/Software-First-Gr/Switchboard/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/Software-First-Gr/Switchboard/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Software-First-Gr/Switchboard/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/Software-First-Gr/Switchboard/releases/tag/v1.0.1
[1.0.0]: https://github.com/Software-First-Gr/Switchboard/commits/v1.0.1
