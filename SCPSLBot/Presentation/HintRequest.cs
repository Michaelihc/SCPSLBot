namespace SCPSLBot.Presentation
{
    internal readonly struct HintRequest
    {
        public HintRequest(string tagId, string message, float duration, float x, float y, int textSize)
        {
            TagId = tagId;
            Message = message;
            Duration = duration;
            X = x;
            Y = y;
            TextSize = textSize;
        }

        public string TagId { get; }

        public string Message { get; }

        public float Duration { get; }

        public float X { get; }

        public float Y { get; }

        public int TextSize { get; }
    }
}
