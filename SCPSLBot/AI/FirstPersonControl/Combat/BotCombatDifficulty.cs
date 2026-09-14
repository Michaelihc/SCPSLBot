namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    internal enum BotCombatDifficulty
    {
        Easy,
        Normal,
        Hard,
        Hardest,
    }

    /// <summary>
    /// Immutable combat tuning shared by every bot at a difficulty level.
    /// </summary>
    internal sealed class BotCombatDifficultySettings
    {
        private static readonly BotCombatDifficultySettings Easy = new(
            shotCooldownSeconds: 0.26f,
            aimAngleDegrees: 14f,
            strafeSpeed: 1.0f,
            minStrafeFlipSeconds: 1.0f,
            maxStrafeFlipSeconds: 1.6f,
            chaseAfterLostLosSeconds: 1.5f,
            scp096RageDurationSeconds: 30f);

        private static readonly BotCombatDifficultySettings Normal = new(
            shotCooldownSeconds: 0.14f,
            aimAngleDegrees: 10f,
            strafeSpeed: 1.35f,
            minStrafeFlipSeconds: 0.55f,
            maxStrafeFlipSeconds: 0.95f,
            chaseAfterLostLosSeconds: 3f,
            scp096RageDurationSeconds: 40f);

        private static readonly BotCombatDifficultySettings Hard = new(
            shotCooldownSeconds: 0.055f,
            aimAngleDegrees: 6f,
            strafeSpeed: 1.95f,
            minStrafeFlipSeconds: 0.18f,
            maxStrafeFlipSeconds: 0.32f,
            chaseAfterLostLosSeconds: 5f,
            scp096RageDurationSeconds: 50f);

        private static readonly BotCombatDifficultySettings Hardest = new(
            shotCooldownSeconds: 0.024f,
            aimAngleDegrees: 3.5f,
            strafeSpeed: 2.45f,
            minStrafeFlipSeconds: 0.07f,
            maxStrafeFlipSeconds: 0.14f,
            chaseAfterLostLosSeconds: 8f,
            scp096RageDurationSeconds: 60f);

        private BotCombatDifficultySettings(
            float shotCooldownSeconds,
            float aimAngleDegrees,
            float strafeSpeed,
            float minStrafeFlipSeconds,
            float maxStrafeFlipSeconds,
            float chaseAfterLostLosSeconds,
            float scp096RageDurationSeconds)
        {
            ShotCooldownSeconds = shotCooldownSeconds;
            AimAngleDegrees = aimAngleDegrees;
            StrafeSpeed = strafeSpeed;
            MinStrafeFlipSeconds = minStrafeFlipSeconds;
            MaxStrafeFlipSeconds = maxStrafeFlipSeconds;
            ChaseAfterLostLosSeconds = chaseAfterLostLosSeconds;
            Scp096RageDurationSeconds = scp096RageDurationSeconds;
        }

        public float ShotCooldownSeconds { get; }
        public float AimAngleDegrees { get; }
        public float StrafeSpeed { get; }
        public float MinStrafeFlipSeconds { get; }
        public float MaxStrafeFlipSeconds { get; }
        public float ChaseAfterLostLosSeconds { get; }
        public float Scp096RageDurationSeconds { get; }

        public static BotCombatDifficultySettings For(BotCombatDifficulty difficulty)
        {
            return difficulty switch
            {
                BotCombatDifficulty.Easy => Easy,
                BotCombatDifficulty.Hard => Hard,
                BotCombatDifficulty.Hardest => Hardest,
                _ => Normal,
            };
        }
    }
}
