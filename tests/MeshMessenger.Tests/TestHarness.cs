// SPDX-License-Identifier: GPL-3.0-or-later
// Runner and helpers adapted from the author's Zeus-PowerStation test suite.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;

namespace MeshMessenger.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    /// <summary>
    /// Lets this one test reach a simulated node on 127.0.0.x. The suite's default is the
    /// shipped default — loopback refused — so the refusal path is exercised rather than
    /// switched off for everything. A test that needs a fake node says so here.
    /// </summary>
    public bool Loopback { get; init; }
}

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message = "expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what ?? "value"}: expected <{expected}> but got <{actual}>");
    }

    public static void NotNull(object? value, string what = "value")
    {
        if (value is null) throw new AssertionException($"{what} was null");
    }

    public static void Contains(string needle, string? haystack, string what = "text")
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertionException($"{what} should contain \"{needle}\" but was \"{haystack}\"");
    }

    /// <summary>Polls until <paramref name="condition"/> holds (async code under test).</summary>
    public static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new AssertionException($"timed out waiting for: {what}");
    }
}

/// <summary>In-memory stand-in for the Zeus per-plugin settings store.</summary>
public sealed class MemorySettings : IPluginSettings
{
    public ConcurrentDictionary<string, object?> Values { get; } = new();

    /// <summary>How many times each key has been written, so write amplification is testable.</summary>
    public ConcurrentDictionary<string, int> Writes { get; } = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var v) ? (T?)v : default);

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Values[key] = value;
        Writes.AddOrUpdate(key, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class FakePluginContext : IPluginContext
{
    /// <summary>
    /// The real version from plugin.json when it can be found, so the preview — and the
    /// screenshots taken from it — show the version actually being built rather than a
    /// placeholder a reviewer would have to take on trust. Falls back for the test suite,
    /// which only needs the value to be consistent.
    /// </summary>
    public static readonly string ManifestVersion = ReadManifestVersion();

    private static string ReadManifestVersion()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var manifest = Path.Combine(dir.FullName, "src", "MeshMessenger", "plugin.json");
                if (File.Exists(manifest))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { } s) return s;
                }
                dir = dir.Parent;
            }
        }
        catch (Exception)
        {
            // The suite does not care which version it sees, only that it is consistent.
        }
        return "0.0.0-test";
    }

    public string PluginId => "io.github.alarmguypro.meshmessenger";
    public PluginManifest Manifest { get; } = new()
    {
        Id = "io.github.alarmguypro.meshmessenger",
        Name = "Mesh Messenger",
        Version = ManifestVersion,
        Sdk = new SdkRequirement { Abi = 1, MinVersion = "1.5.0" },
        Entrypoint = new EntryPoint { Assembly = "MeshMessenger.dll" },
    };
    public ILogger Logger { get; } = NullLogger.Instance;
    public string PluginRootPath => Path.GetTempPath();
    public PluginCapabilities GrantedCapabilities => PluginCapabilities.NetworkAccess | PluginCapabilities.PersistSettings;
    public IPluginSettings Settings { get; init; } = new MemorySettings();
    public IRadioStateReader? Radio => null;
    public IRadioController? RadioController => null;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        if (filter == "--preview")
        {
            var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5319;
            return await Preview.RunAsync(port);
        }
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
                .Select(m => (Name: $"{t.Name}.{m.Name}", Method: m)))
            .Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var failures = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, method) in tests)
        {
            var sw = Stopwatch.StartNew();
            // Simulated nodes listen on 127.0.0.x, so some tests need loopback allowed.
            // It is granted for that test only; everything else runs with the shipped
            // default, which refuses it. Tests run one at a time, so this is safe.
            MeshMessenger.Discovery.HostValidator.AllowLoopback =
                method.GetCustomAttribute<TestAttribute>()!.Loopback;
            try
            {
                var result = method.Invoke(null, null);
                if (result is Task task)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
                    if (finished != task) throw new AssertionException("timed out after 30 s");
                    await task;
                }
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failures++;
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertionException) Console.WriteLine(inner.StackTrace);
            }
            finally
            {
                MeshMessenger.Discovery.HostValidator.AllowLoopback = false;
            }
        }
        Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed in {total.ElapsedMilliseconds} ms");
        return failures;
    }
}
