namespace DuelArena;

public sealed class DuelConfig
{
    public bool Enabled { get; set; } = true;
    public float SurfaceHeightOffset { get; set; } = 180f;
    public float InvitationSeconds { get; set; } = 30f;
    public float LobbySeconds { get; set; } = 120f;
    public float MatchSeconds { get; set; } = 180f;
    // King-of-the-hill: the winner defends against individual FIFO challengers.
    public bool KeepWinnerInArena { get; set; } = false;
}
