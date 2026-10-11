namespace DuelArena;

public sealed class DuelConfig
{
    public bool Enabled { get; set; } = true;
    public float SurfaceHeightOffset { get; set; } = 180f;
    public float InvitationSeconds { get; set; } = 30f;
    public float LobbySeconds { get; set; } = 120f;
    public float MatchSeconds { get; set; } = 180f;
    // A retained winner yields to the next agreed pair; queued pairs stay intact.
    public bool KeepWinnerInArena { get; set; } = false;
}
