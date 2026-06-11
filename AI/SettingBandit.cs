using System.Text;

namespace PaperTradingBot.AI;

/// <summary>
/// UCB1 bandit managing a discrete set of candidate values for a single strategy setting.
///
/// Flow per cycle:
///   1. Cycle completes → caller calls RecordReward(reward) for the currently-active arm.
///   2. Caller calls SelectNext() → UCB picks the arm to try next; CurrentValue updates.
///   3. Caller applies CurrentValue to LiveSettingsService.
///
/// Unvisited arms are always tried first (initial exploration) before UCB scoring kicks in.
/// </summary>
public sealed class SettingBandit
{
    private readonly decimal[] _arms;
    private readonly float[]   _totalReward;
    private readonly int[]     _pulls;
    private readonly float     _explorationC;

    private int _activeArm;
    private int _totalPulls;

    public SettingBandit(string name, decimal[] arms, float explorationC = 0.5f)
    {
        Name          = name;
        _arms         = arms;
        _totalReward  = new float[arms.Length];
        _pulls        = new int[arms.Length];
        _explorationC = explorationC;
        _activeArm    = arms.Length / 2;  // start at middle arm (nearest to default)
    }

    public string  Name         { get; }
    public decimal CurrentValue => _arms[_activeArm];
    public int     ActiveArm    => _activeArm;
    public int     ArmCount     => _arms.Length;
    public int     TotalPulls   => _totalPulls;

    public decimal  GetArm(int i)        => _arms[i];
    public int      GetPulls(int i)      => _pulls[i];
    public float    GetMeanReward(int i) => _pulls[i] > 0 ? _totalReward[i] / _pulls[i] : 0f;

    /// <summary>Record reward for the currently-active arm.</summary>
    public void RecordReward(float reward)
    {
        _pulls[_activeArm]++;
        _totalReward[_activeArm] += reward;
        _totalPulls++;
    }

    /// <summary>
    /// UCB1 arm selection. Always tries unvisited arms first.
    /// Returns the selected value.
    /// </summary>
    public decimal SelectNext()
    {
        // Phase 1: explore every arm once before UCB scoring
        for (var i = 0; i < _arms.Length; i++)
        {
            if (_pulls[i] == 0)
            {
                _activeArm = i;
                return _arms[i];
            }
        }

        // Phase 2: UCB1
        var best    = float.MinValue;
        var bestArm = _activeArm;
        for (var i = 0; i < _arms.Length; i++)
        {
            var mean        = _totalReward[i] / _pulls[i];
            var exploration = _explorationC * MathF.Sqrt(MathF.Log(_totalPulls + 1f) / _pulls[i]);
            var score       = mean + exploration;
            if (score > best) { best = score; bestArm = i; }
        }

        _activeArm = bestArm;
        return _arms[_activeArm];
    }

    /// <summary>
    /// Step the active arm one index lower WITHOUT recording a pull. Used by drought
    /// recovery: zero-reward pulls would dilute the arm means and destroy the UCB signal,
    /// so a stalled bandit just walks down toward easier thresholds until a real cycle
    /// completes and normal RecordReward/SelectNext flow resumes.
    /// Returns true if the arm changed (false when already at the lowest arm).
    /// </summary>
    public bool StepDown()
    {
        if (_activeArm == 0) return false;
        _activeArm--;
        return true;
    }

    /// <summary>
    /// Compact description for logging: "[33:-0.1/2, 40:+0.2/5*, 47:0.0/1]"
    /// Asterisk marks the active arm.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < _arms.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var mean = _pulls[i] > 0 ? _totalReward[i] / _pulls[i] : 0f;
            sb.Append($"{_arms[i]}:{mean:+0.00;-0.00}/{_pulls[i]}");
            if (i == _activeArm) sb.Append('*');
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    public BanditState ToState()
        => new(Name, _activeArm, _totalPulls, (int[])_pulls.Clone(), (float[])_totalReward.Clone());

    public void ApplyState(BanditState s)
    {
        if (s.Pulls.Length != _arms.Length || s.TotalReward.Length != _arms.Length) return;
        _activeArm  = Math.Clamp(s.ActiveArm, 0, _arms.Length - 1);
        _totalPulls = s.TotalPulls;
        Array.Copy(s.Pulls,       _pulls,       _arms.Length);
        Array.Copy(s.TotalReward, _totalReward, _arms.Length);
    }
}

public sealed record BanditState(
    string  Name,
    int     ActiveArm,
    int     TotalPulls,
    int[]   Pulls,
    float[] TotalReward);

public sealed record BanditArmStatus(
    string  Name,
    decimal CurrentValue,
    int     ActiveArmIndex,
    int     TotalPulls,
    (decimal Value, int Pulls, float MeanReward)[] Arms);
