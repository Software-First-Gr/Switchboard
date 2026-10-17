using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.IntegrationTests;

/// <summary>
/// Stands in for Blazor's <c>AuthenticationStateProvider</c>: scoped, so it lives in the circuit's scope and knows
/// who is signed in on that connection. A fresh dispatch scope gets a fresh, anonymous instance.
/// </summary>
public sealed class CircuitAuthenticationState
{
    public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity());

    public Task<ClaimsPrincipal> GetAuthenticationStateAsync() => Task.FromResult(User);
}

/// <summary>The application's scoped "current user", which handlers read. Empty until something sets it.</summary>
public sealed class CurrentUser
{
    public string? Name { get; private set; }

    public void Set(ClaimsPrincipal user) => Name = user.Identity?.Name;
}

/// <summary>Records which DbContext instance each handler ran with, by a label the test chooses.</summary>
public sealed class ContextLog
{
    private readonly ConcurrentDictionary<string, AppDbContext> _seen = new();

    public void Record(string label, AppDbContext db) => _seen[label] = db;

    public AppDbContext this[string label] => _seen[label];
}

/// <summary>
/// One application: a root service provider over a named, shared-cache in-memory SQLite database, kept alive by a
/// connection held open for the host's lifetime. Every DbContext opens its own connection to it, as it would to a
/// real server.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    private TestHost(SqliteConnection keepAlive, ServiceProvider services)
    {
        _keepAlive = keepAlive;
        Services = services;
    }

    public ServiceProvider Services { get; }

    public CommandGate Gate => Services.GetRequiredService<CommandGate>();

    public ContextLog Contexts => Services.GetRequiredService<ContextLog>();

    /// <summary>Builds the host; <paramref name="configure"/> adds the scope-per-dispatch setting under test, if any.</summary>
    public static async Task<TestHost> StartAsync(Action<SwitchboardConfiguration>? configure = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"switchboard-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        // An in-memory database lives as long as at least one connection to it is open.
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var services = new ServiceCollection()
            .AddSingleton<CommandGate>()
            .AddSingleton<GatingInterceptor>()
            .AddSingleton<ContextLog>()
            .AddScoped<CircuitAuthenticationState>()
            .AddScoped<CurrentUser>()
            .AddDbContext<AppDbContext>((sp, options) => options
                .UseSqlite(connectionString)
                .AddInterceptors(sp.GetRequiredService<GatingInterceptor>()))
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<TestHost>();
                configure?.Invoke(cfg);
            })
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var host = new TestHost(keepAlive, services);

        await host.WithFreshContextAsync(db => db.Database.EnsureCreatedAsync());

        return host;
    }

    /// <summary>
    /// Opens a Blazor Server circuit: one DI scope that lives as long as the user's connection. Components inject
    /// <see cref="ISender"/> from it, so that is where the circuit's sender is resolved.
    /// </summary>
    public Circuit OpenCircuit(string? userName = null)
    {
        var scope = Services.CreateAsyncScope();

        if (userName is not null)
        {
            // Signed in on this circuit, and known to the circuit scope's own CurrentUser.
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, userName) }, authenticationType: "Test"));
            scope.ServiceProvider.GetRequiredService<CircuitAuthenticationState>().User = user;
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(user);
        }

        return new Circuit(scope);
    }

    /// <summary>Runs <paramref name="work"/> against a DbContext of its own, outside any circuit: what is really in the database.</summary>
    public async Task<T> WithFreshContextAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        // Lets a command still held by a failed test finish, so disposal never waits on it.
        Gate.Release();
        await Services.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}

/// <summary>A Blazor Server circuit: the long-lived scope, and the <see cref="ISender"/> a component would inject.</summary>
public sealed class Circuit : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;

    internal Circuit(AsyncServiceScope scope)
    {
        _scope = scope;
        Sender = scope.ServiceProvider.GetRequiredService<ISender>();
    }

    public ISender Sender { get; }

    /// <summary>The circuit scope's own DbContext: the one every dispatch shares when scope-per-dispatch is off.</summary>
    public AppDbContext DbContext => _scope.ServiceProvider.GetRequiredService<AppDbContext>();

    /// <summary>The circuit scope's own <see cref="IntegrationTests.CurrentUser"/>.</summary>
    public CurrentUser CurrentUser => _scope.ServiceProvider.GetRequiredService<CurrentUser>();

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}

internal static class Timeouts
{
    /// <summary>Bounds an await that would otherwise hang a broken test; never what a test asserts.</summary>
    public static Task<T> Guarded<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30));
}
