using System;
using Microsoft.Extensions.DependencyInjection;
using MyGame.Core.AI.Prompts;
using MyGame.Core.Common;
using MyGame.Core.Profile;
using MyGame.Core.Saves;

namespace MyGame.Desktop.Services;

/// <summary>
/// Static facade over the Microsoft.Extensions.DependencyInjection
/// container. Built once on app startup in
/// <see cref="App.OnFrameworkInitializationCompleted"/>; the rest of
/// the app resolves Core-layer services via <see cref="Resolve{T}"/>.
///
/// <para>
/// All Core services are registered as singletons — they're either
/// stateless (PromptLoader), wrap a single on-disk file
/// (ProfileStore, SettingsStore), or own a process-lifetime directory
/// (SaveManager, CharacterSheetStore). The shared
/// <see cref="EventBus"/> is also a singleton so any code path can
/// subscribe to engine events.
/// </para>
///
/// <para>
/// ViewModels are NOT registered here — they're constructed on demand
/// by <see cref="ViewModels.MainViewModel"/> with the services they
/// need. This keeps the ViewModel tree lightweight and lets the
/// navigation root decide when to spawn each screen.
/// </para>
/// </summary>
public static class ServiceHost
{
    private static ServiceProvider? _provider;
    private static readonly object _buildLock = new();

    /// <summary>
    /// Build the DI container if not already built. Idempotent —
    /// subsequent calls are no-ops. Thread-safe.
    /// </summary>
    public static void Initialize()
    {
        if (_provider is not null) return;
        lock (_buildLock)
        {
            if (_provider is not null) return;

            var services = new ServiceCollection();

            // ─── Core.Common ─────────────────────────────────────────────
            // One shared event bus for the whole app. Any code path can
            // publish/subscribe (engine events, MP events, etc.).
            services.AddSingleton<EventBus>();

            // ─── Core.Profile ────────────────────────────────────────────
            services.AddSingleton<ProfileStore>();
            services.AddSingleton<SettingsStore>();

            // ─── Core.Saves ──────────────────────────────────────────────
            services.AddSingleton<SaveManager>();
            services.AddSingleton<CharacterSheetStore>();

            // ─── Core.AI.Prompts ─────────────────────────────────────────
            // The prompt loader reads embedded .md resources — a single
            // instance caches them all.
            services.AddSingleton<PromptLoader>();

            // ─── Issue #59: Mod loading ──────────────────────────────────
            // Load .pathstone-pack files from {profileDir}/mods/ into a
            // shared ContentRegistry. The registry is used by DefaultWorld
            // + WorldBuilder + all GM tools.
            var profileDir = MyGame.Core.Profile.ProfileStore.DefaultProfileDirectory;
            var modsDir = System.IO.Path.Combine(profileDir, "mods");
            var modRegistry = MyGame.Core.World.Content.ContentRegistry.LoadDefault();
            var loadedMods = MyGame.Core.Tooling.ModLoader.LoadAll(modsDir, modRegistry);
            if (loadedMods.Count > 0)
            {
                System.Diagnostics.Trace.WriteLine($"[ServiceHost] Loaded {loadedMods.Count} mod(s): {string.Join(", ", loadedMods)}");
            }
            services.AddSingleton(modRegistry);

            _provider = services.BuildServiceProvider();
        }
    }

    /// <summary>
    /// Resolve a registered service. Throws if
    /// <see cref="Initialize"/> hasn't been called yet, or if the
    /// service isn't registered.
    /// </summary>
    public static T Resolve<T>() where T : notnull
    {
        if (_provider is null)
            throw new InvalidOperationException(
                "ServiceHost has not been initialized. Call ServiceHost.Initialize() at app startup.");
        return _provider.GetRequiredService<T>();
    }

    /// <summary>
    /// Get the underlying provider (advanced scenarios — e.g. spawning
    /// scopes). Most callers should use <see cref="Resolve{T}"/>.
    /// </summary>
    public static IServiceProvider Provider
    {
        get
        {
            if (_provider is null)
                throw new InvalidOperationException(
                    "ServiceHost has not been initialized. Call ServiceHost.Initialize() at app startup.");
            return _provider;
        }
    }
}
