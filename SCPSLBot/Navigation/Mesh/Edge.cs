namespace SCPSLBot.Navigation.Mesh
{
    internal struct Edge
    {
        public Vertex From;
        public Vertex To;

        public Edge(Vertex from, Vertex to)
        {
            From = from;
            To = to;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is Edge edge && (From, To).Equals((edge.From, edge.To));
        }

        public override readonly int GetHashCode()
        {
            return (From, To).GetHashCode();
        }

        public static bool operator ==(Edge left, Edge right)
        {
            return (left.From, left.To) == (right.From, right.To);
        }

        public static bool operator !=(Edge left, Edge right)
        {
            return !(left == right);
        }

        public override readonly string ToString()
        {
            return (From, To).ToString();
        }
    }
}
