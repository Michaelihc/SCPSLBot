using System.Collections.Generic;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>
    /// Zones joined to a start zone through open zone links, as zone bitmasks. A link is a pair of
    /// zone bits that can be crossed in both directions (an elevator whose doors take calls, or a
    /// checkpoint passage).
    /// </summary>
    internal static class ZoneReachPolicy
    {
        public static int Reachable(int startBits, IReadOnlyList<(int A, int B)> openLinks)
        {
            var reached = startBits;
            bool grew;
            do
            {
                grew = false;
                foreach (var (a, b) in openLinks)
                {
                    var joined = (reached & a) != 0 ? b : (reached & b) != 0 ? a : 0;
                    if ((joined & ~reached) != 0)
                    {
                        reached |= joined;
                        grew = true;
                    }
                }
            }
            while (grew);

            return reached;
        }
    }
}
