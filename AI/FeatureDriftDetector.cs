namespace PaperTradingBot.AI;

/// <summary>
/// Tracks the EMA of winning-cycle feature vectors and measures how far the current
/// market features have drifted from that "winning regime."
///
/// Used by ClaudeAdvisorService to fire an unscheduled call when the market regime
/// appears to have shifted away from conditions where the current settings work well.
/// </summary>
public sealed class FeatureDriftDetector
{
    private readonly float[] _winningMean;
    private readonly int     _featureCount;
    private int              _winningCount;

    // EMA decay: alpha=0.15 ≈ 7-cycle half-life for the winning mean
    private const float Alpha   = 0.15f;
    private const int   MinWins = 5;    // need this many wins before drift is meaningful

    public FeatureDriftDetector(int featureCount)
    {
        _featureCount = featureCount;
        _winningMean  = new float[featureCount];
    }

    public int WinningCount => _winningCount;

    /// <summary>Update the EMA with a completed, winning-cycle feature vector.</summary>
    public void UpdateWinning(float[] features)
    {
        if (features.Length != _featureCount) return;
        if (_winningCount == 0)
            Array.Copy(features, _winningMean, _featureCount);
        else
            for (var i = 0; i < _featureCount; i++)
                _winningMean[i] = (1f - Alpha) * _winningMean[i] + Alpha * features[i];
        _winningCount++;
    }

    /// <summary>
    /// Cosine drift score in [0, 2]. 0 = identical to winning regime, ~0.25+ = meaningful shift.
    /// Returns 0 if fewer than MinWins winning cycles have been recorded.
    /// </summary>
    public float DriftScore(float[] features)
    {
        if (_winningCount < MinWins || features.Length != _featureCount) return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (var i = 0; i < _featureCount; i++)
        {
            dot   += features[i] * _winningMean[i];
            normA += features[i] * features[i];
            normB += _winningMean[i] * _winningMean[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : 1f - dot / denom;
    }

    public bool IsDrifting(float[] features, float threshold)
        => DriftScore(features) > threshold;
}
