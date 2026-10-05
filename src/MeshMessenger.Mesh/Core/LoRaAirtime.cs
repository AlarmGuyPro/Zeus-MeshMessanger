// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>LoRa time-on-air (Semtech AN1200.13), used only to space our own transmissions.</summary>
public static class LoRaAirtime
{
    /// <param name="payloadBytes">Bytes on air, including protocol headers.</param>
    /// <param name="bandwidthHz">e.g. 250_000.</param>
    /// <param name="codingRate">Denominator of 4/x (5..8).</param>
    public static TimeSpan Estimate(int payloadBytes, int spreadingFactor, int bandwidthHz, int codingRate, int preambleSymbols = 16)
    {
        if (spreadingFactor is < 5 or > 12 || bandwidthHz <= 0 || codingRate is < 5 or > 8 || payloadBytes < 0)
        {
            return TimeSpan.FromSeconds(1);
        }
        var symbolMs = Math.Pow(2, spreadingFactor) / bandwidthHz * 1000.0;
        var lowDataRateOptimise = symbolMs > 16 ? 1 : 0;
        var cr = codingRate - 4;
        var numerator = 8.0 * payloadBytes - 4 * spreadingFactor + 28 + 16; // CRC on, explicit header
        var denominator = 4.0 * (spreadingFactor - 2 * lowDataRateOptimise);
        var payloadSymbols = 8 + Math.Max(Math.Ceiling(numerator / denominator) * (cr + 4), 0);
        var totalMs = (preambleSymbols + 4.25 + payloadSymbols) * symbolMs;
        return TimeSpan.FromMilliseconds(totalMs);
    }
}
