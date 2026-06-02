namespace PaperTradingBot.AI;

/// <summary>
/// Converts a MarketSnapshot into a normalized float[] suitable for NN input.
/// All features are scaled to approximately [-1, 1] or [0, 1].
/// Feature layout is fixed — changing it invalidates persisted NN weights.
/// </summary>
public static class MarketFeatureExtractor
{
    public const int FeatureCount = 12;

    public static float[] ToVector(MarketSnapshot s)
    {
        var f = new float[FeatureCount];

        f[0]  = Clamp01((float)s.Rsi1m / 100f);
        f[1]  = Math.Clamp((float)s.AtrPct / 0.001f, 0f, 3f);             // 0.1% ATR → 1.0
        f[2]  = (float)Math.Tanh((double)s.MacdHistogram * 10_000.0);     // small values → [-1,1]
        f[3]  = Clamp01((float)s.EmaSpreadPct / 1.0f);                    // 1% spread → 1.0
        f[4]  = (float)Math.Tanh((double)s.PriceMomentum5 / 0.02);       // ±2% maps to ≈ ±1
        f[5]  = Clamp01((float)s.CashPct);
        f[6]  = Clamp01((float)s.EthPositionPct);
        f[7]  = (float)(s.Timestamp.Hour / 24.0);                         // time of day
        f[8]  = Clamp01(s.BarsSinceLastTrade / 100f);
        f[9]  = Clamp01((float)s.CycleSuccessRate);
        f[10] = s.AboveTrendEma  ? 1f : 0f;
        f[11] = s.CyclingEnabled ? 1f : 0f;

        return f;
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
}
