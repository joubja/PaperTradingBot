namespace PaperTradingBot.Config;

public sealed class OptimizerOptions
{
    public bool   Enabled             { get; set; } = true;

    /// <summary>UCB1 exploration constant. Higher = explores more arms before exploiting best.</summary>
    public float  ExplorationC        { get; set; } = 0.5f;

    /// <summary>Cosine drift score (0–2) above which Claude is triggered for a regime-shift call.</summary>
    public float  DriftThreshold      { get; set; } = 0.25f;

    public string BanditPersistPath   { get; set; } = "data/bandit_state.json";
    public string SettingsPersistPath { get; set; } = "data/live_settings.json";
}
