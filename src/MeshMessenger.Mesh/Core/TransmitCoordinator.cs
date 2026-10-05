// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>
/// One transmission at a time across every node: everything Mesh Messenger
/// causes to be transmitted goes through here, and the next transmission
/// waits until the previous one's estimated airtime has passed, so our
/// Meshtastic and MeshCore nodes never key together because of us.
/// (The nodes' own traffic — repeats, acks, telemetry — can't be
/// coordinated by software; see docs/ZEUS-REQUIREMENTS.md §1.)
/// </summary>
public sealed class TransmitCoordinator
{
    /// <summary>Upper bound on the wait after one transmission.</summary>
    public static readonly TimeSpan MaxSpacing = TimeSpan.FromSeconds(8);

    /// <summary>Gap added after the estimated airtime.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private DateTimeOffset _clearAt = DateTimeOffset.MinValue;

    public TransmitCoordinator(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>When the channel (as far as we know) is clear again.</summary>
    public DateTimeOffset ClearAt => _clearAt;

    public async Task<SendResult> RunAsync(Func<CancellationToken, Task<SendResult>> transmit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _clearAt - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait > MaxSpacing ? MaxSpacing : wait, _time, ct).ConfigureAwait(false);
            }
            var result = await transmit(ct).ConfigureAwait(false);
            if (result.Ok)
            {
                var spacing = result.Airtime + Guard;
                if (spacing > MaxSpacing) spacing = MaxSpacing;
                _clearAt = _time.GetUtcNow() + spacing;
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
