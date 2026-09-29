namespace RecompOne.Runtime.Input;

//finds a controller that is the same physical pad as another one. Pad remappers (DualSenseX, DS4Windows, Steam Input)
//expose a PlayStation pad as a virtual Xbox pad; when the real pad stays visible too, SDL opens both and one pad
//drives two players. Every button change of a mirror shows up on its twin within a few polls, and a real second pad
//does not do that. Each press of either pad of a pair is judged "seen on both" or "only on one"; the later pad is
//reported as the mirror when the pair's last History judged presses hold no press seen on only one of them and at
//least MatchesNeeded presses of at least DistinctNeeded different buttons. Needing different buttons keeps players
//who happen to hit the same button together (everyone confirming at once) from looking like one pad; judging only the
//recent presses lets a mirror be found even when one press went missing (while the remapper started, for example).
public sealed class MirrorDetector
{
    public const int Window = 3;
    public const int MatchesNeeded = 5;
    public const int DistinctNeeded = 2;
    public const int History = 12;

    private readonly int _players;
    private readonly uint[] _last;
    private sealed class Change(long poll, int player, uint mask)
    {
        public readonly long Poll = poll;
        public readonly int Player = player;
        public readonly uint Mask = mask;
        public bool Judged;
    }

    private readonly List<Change> _changes = [];
    //per pair (a < b): the last judged presses, mask when seen on both, 0 when seen on only one
    private readonly Queue<uint>[,] _recent;
    private long _poll;

    public MirrorDetector(int players)
    {
        _players = players;
        _last = new uint[players];
        _recent = new Queue<uint>[players, players];
        for (var a = 0; a < players; a++)
        for (var b = 0; b < players; b++)
            _recent[a, b] = new Queue<uint>();
    }

    public void Reset()
    {
        Array.Clear(_last);
        _changes.Clear();
        foreach (var q in _recent) q.Clear();
        _poll = 0;
    }

    //masks[p] = buttons held on player p's pad this poll, open[p] = player p has a pad. Returns the player whose pad
    //mirrors an earlier player's pad, or -1.
    public int Observe(uint[] masks, bool[] open)
    {
        _poll++;
        for (var p = 0; p < _players; p++)
        {
            if (!open[p] || masks[p] == _last[p]) continue;
            _last[p] = masks[p];
            if (masks[p] != 0) _changes.Add(new Change(_poll, p, masks[p]));
        }

        //a press is judged once the window after it has passed; a press seen on both pads is judged once for the pair
        foreach (var c in _changes)
        {
            if (c.Judged || _poll - c.Poll < Window) continue;
            c.Judged = true;
            for (var q = 0; q < _players; q++)
            {
                if (q == c.Player || !open[q]) continue;
                var twin = _changes.FirstOrDefault(o => o.Player == q && o.Mask == c.Mask && Math.Abs(o.Poll - c.Poll) <= Window);
                if (twin != null)
                {
                    if (twin.Judged && twin != c && twin.Poll < c.Poll) continue; //already counted from the twin's side
                    twin.Judged = true;
                }

                var queue = _recent[Math.Min(c.Player, q), Math.Max(c.Player, q)];
                queue.Enqueue(twin != null ? c.Mask : 0);
                while (queue.Count > History) queue.Dequeue();
            }
        }

        _changes.RemoveAll(c => _poll - c.Poll >= Window * 2);

        for (var a = 0; a < _players; a++)
        for (var b = a + 1; b < _players; b++)
        {
            if (!open[a] || !open[b]) continue;
            var q = _recent[a, b];
            if (q.Count < MatchesNeeded || q.Contains(0u)) continue;
            if (q.Distinct().Count() >= DistinctNeeded) return b;
        }

        return -1;
    }
}
