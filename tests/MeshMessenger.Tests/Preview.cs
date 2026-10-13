// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Core;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace MeshMessenger.Tests;

/// <summary>
/// Serves the real panel against the real backend with simulated nodes behind it, so the
/// panel can be looked at and photographed without hardware.
///
/// <c>dotnet run --project tests/MeshMessenger.Tests -- --preview [port]</c>
///
/// It exists because the screenshot set the catalog requires — dark, light, narrow, 200%,
/// focus, loading, empty, error, disconnected, unavailable — is twenty-odd images that have
/// to be retaken every release. Taking them by hand does not survive contact with reality,
/// and rebuilding this harness from scratch each time is worse, so it lives here.
/// Its companion is scripts/screenshots.mjs.
/// </summary>
public static class Preview
{
    public static async Task<int> RunAsync(int port)
    {
        // The fakes listen on loopback, which the shipped validator refuses.
        MeshMessenger.Discovery.HostValidator.AllowLoopback = true;

        // A node in the state most are in: saving everything it hears, recycling on.
        await using var meshcore = new FakeMeshCoreNode
        {
            AutoAddConfig = MeshCoreProtocol.AutoAddOverwriteOldest,
            ManualAddContacts = 0,
        };
        await using var meshtastic = new FakeMeshtasticNode();
        // Messages the node was holding before Zeus ever connected: they arrive in the first
        // drain marked "queued while away", which is the one path a live node exercises on
        // every start and a preview that only sends traffic afterwards never does.
        await meshcore.HearChannelAsync(0, "W0QQ Base", "held while Zeus was away");
        await meshcore.HearChannelAsync(0, "KE0XYZ Mobile", "also held");

        var settings = new MemorySettings();
        var plugin = new MeshMessengerPlugin();
        await plugin.InitializeAsync(new FakePluginContext { Settings = settings }, default);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        const string id = "io.github.alarmguypro.meshmessenger";
        plugin.MapEndpoints(app.MapGroup($"/api/plugins/{id}"));

        // Forced states for the awkward screenshots, switched on by the Playwright script
        // through these endpoints rather than by waiting for the real thing to happen.
        var slow = false;
        var broken = false;
        app.MapPost("/preview/slow/{on:bool}", (bool on) => { slow = on; return Results.Ok(); });
        app.MapPost("/preview/broken/{on:bool}", (bool on) => { broken = on; return Results.Ok(); });
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                if (broken) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsync("preview: forced failure"); return; }
                if (slow) await Task.Delay(4000);
            }
            await next();
        });

        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var dist = Path.Combine(repo, "web", "dist");
        var vendor = Path.Combine(repo, "tests", "preview", "node_modules");
        app.MapGet("/meshmessenger.js", () =>
        {
            var file = Path.Combine(dist, "meshmessenger.js");
            return File.Exists(file)
                ? Results.Text(File.ReadAllText(file), "text/javascript")
                : Results.NotFound($"Build the panel first: npm run build in web/ (looked in {dist})");
        });
        // React comes from tests/preview/node_modules, not from a CDN: the container has no
        // route to one, and a local copy is what the catalog's own build does anyway.
        app.MapGet("/vendor/{*path}", (string path) =>
        {
            var file = Path.GetFullPath(Path.Combine(vendor, path));
            if (!file.StartsWith(vendor, StringComparison.Ordinal) || !File.Exists(file))
            {
                return Results.NotFound($"Run: npm install in tests/preview (looked for {path})");
            }
            return Results.Text(File.ReadAllText(file), "text/javascript");
        });
        // The bundle imports "react" and "react/jsx-runtime", as it does inside Zeus. These
        // two shims point those at the UMD globals; jsx is rebuilt from createElement,
        // because the UMD build does not carry the runtime.
        app.MapGet("/shim/react.js", () => Results.Text(ReactShim, "text/javascript"));
        app.MapGet("/shim/jsx-runtime.js", () => Results.Text(JsxShim, "text/javascript"));
        app.MapGet("/", () => Results.Text(Page(id), "text/html"));

        await app.StartAsync();
        Console.WriteLine($"Preview on http://127.0.0.1:{port}/");
        Console.WriteLine($"  MeshCore fake on 127.0.0.1:{meshcore.Port}, Meshtastic fake on 127.0.0.1:{meshtastic.Port}");

        // Two nodes, some traffic, one node pointed at an address nothing answers on.
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/plugins/{id}/") };
        await http.PostAsJsonAsync("nodes", new { network = "meshcore", host = $"127.0.0.1:{meshcore.Port}", name = "KQ4WLR Zeus" });
        await http.PostAsJsonAsync("nodes", new { network = "meshtastic", host = $"127.0.0.1:{meshtastic.Port}", name = "KQ4WLR Meshtastic" });
        // Wait until both are actually connected: traffic sent before the connector attaches
        // goes nowhere, and a preview that silently has no contacts is worse than no preview.
        for (var i = 0; i < 60; i++)
        {
            var status = await http.GetFromJsonAsync<System.Text.Json.Nodes.JsonNode>("status");
            var connected = status?["nodes"]?.AsArray()
                .Count(n => n?["state"]?.GetValue<string>() == "connected") ?? 0;
            if (connected >= 2) break;
            await Task.Delay(250);
        }
        await meshcore.HearChannelAsync(0, "W0QQ Base", "morning all, nets at 0900");
        await meshcore.HearChannelAsync(1, "KE0XYZ Mobile", "anyone on #Test?");
        await meshtastic.HearTextAsync(0x77e0a913, 0xFFFFFFFF, 0, "repeater is back up");
        // A node with auto-add on, as a node on a busy mesh usually is: it saves repeaters
        // it hears without being asked, which is what the Nodes tab now explains.
        await meshcore.AdvertiseSavedAsync("Tower Repeater");
        await meshcore.AdvertiseSavedAsync("Hilltop Repeater");
        await meshcore.HearUnsavedAdvertAsync("KQ4WLR Wio L1");
        Console.WriteLine("Ready. Ctrl-C to stop.");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        await stop.Task;
        await plugin.ShutdownAsync(default);
        await app.StopAsync();
        return 0;
    }


    private const string ReactShim = """
        const R = window.React;
        export default R;
        export const {
          Children, Component, Fragment, Profiler, PureComponent, StrictMode, Suspense,
          cloneElement, createContext, createElement, createRef, forwardRef, isValidElement,
          lazy, memo, startTransition, useCallback, useContext, useDebugValue, useDeferredValue,
          useEffect, useId, useImperativeHandle, useInsertionEffect, useLayoutEffect, useMemo,
          useReducer, useRef, useState, useSyncExternalStore, useTransition, version
        } = R;
        """;

    private const string JsxShim = """
        const R = window.React;
        export const Fragment = R.Fragment;
        export function jsx(type, props, key) {
          const { children, ...rest } = props ?? {};
          if (key !== undefined) rest.key = key;
          return children === undefined ? R.createElement(type, rest) : R.createElement(type, rest, children);
        }
        export const jsxs = jsx;
        export const jsxDEV = jsx;
        """;

    /// <summary>
    /// A page that stands in for Zeus: it supplies React and the two host calls the panel
    /// uses, and nothing else. Deliberately plain, so what is photographed is the panel.
    /// </summary>
    private static string Page(string pluginId) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Mesh Messenger preview</title>
          <style>
            :root { color-scheme: dark; }
            html, body { margin: 0; height: 100%; background: #111418; color: #e6e9ee;
              font: 13px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
            html[data-theme="light"] { color-scheme: light; }
            html[data-theme="light"], html[data-theme="light"] body { background: #f6f7f9; color: #1a1d23; }
            #host { height: 100%; display: flex; flex-direction: column; }
          </style>
          <script type="importmap">
            { "imports": { "react": "/shim/react.js", "react/jsx-runtime": "/shim/jsx-runtime.js" } }
          </script>
          <script src="/vendor/react/umd/react.production.min.js"></script>
          <script src="/vendor/react-dom/umd/react-dom.production.min.js"></script>
        </head>
        <body>
          <div id="host"></div>
          <script type="module">
            const base = "/api/plugins/{{pluginId}}";
            const api = {
              registerPanel: ({ component }) => {
                ReactDOM.createRoot(document.getElementById("host"))
                  .render(React.createElement(component));
              },
              callBackend: (method, path, body) =>
                fetch(base + path, {
                  method,
                  headers: body === undefined ? {} : { "content-type": "application/json" },
                  body: body === undefined ? undefined : JSON.stringify(body),
                }),
            };
            const mod = await import("/meshmessenger.js");
            mod.default(api);
            window.__previewReady = true;
          </script>
        </body>
        </html>
        """;
}
