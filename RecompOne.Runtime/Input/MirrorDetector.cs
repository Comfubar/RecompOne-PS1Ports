namespace RecompOne.Runtime.Input;

//finds a controller that is the same physical pad as another one. Pad remappers (DualSenseX, DS4Windows, Steam Input)
//expose a PlayStation pad as a virtual Xbox pad; when the real pad stays visible too, SDL opens both and one pad
//drives two players. Every button change of a mirror shows up on its twin within a few polls, and a real second pad
//does not do that: after MatchesNeeded matching presses of at least DistinctNeeded different buttons, and no press
//that only one of them saw, the later player is reported as the mirror. Needing different buttons keeps players who
//happen to hit the same button together (everyone confirming at once) from looking like one pad.
public sealed class MirrorDetector
{
    public const int Window = 3;
    public const int MatchesNeeded = 5;
    public const int DistinctNeeded = 2;

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
    private readonly int[,] _matches;
    private readonly int[,] _misses;
    private readonly HashSet<uint>[,] _matchedMasks;
    private long _poll;

    public MirrorDetector(int players)
    {
        _players = players;
        _last = new uint[players];
        _matches = new int[players, players];
        _misses = new int[players, players];
        _matchedMasks = new HashSet<uint>[players, players];
        for (var a = 0; a < players; a++)
        for (var b = 0; b < players; b++)
            _matchedMasks[a, b] = [];
    }

    public void Reset()
    {
        Array.Clear(_last);
        _changes.Clear();
        Array.Clear(_matches);
        Array.Clear(_misses);
        foreach (var set in _matchedMasks) set.Clear();
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

        //a press is judged once the window after it has passed
        foreach (var c in _changes)
        {
            if (c.Judged || _poll - c.Poll < Window) continue;
            c.Judged = true;
            for (var q = 0; q < _players; q++)
            {
                if (q == c.Player || !open[q]) continue;
                var seen = _changes.Any(o => o.Player == q && o.Mask == c.Mask && Math.Abs(o.Poll - c.Poll) <= Window);
                if (seen)
                {
                    _matches[c.Player, q]++;
                    _matchedMasks[Math.Min(c.Player, q), Math.Max(c.Player, q)].Add(c.Mask);
                }
                else _misses[c.Player, q]++;
            }
        }

        _changes.RemoveAll(c => _poll - c.Poll >= Window * 2);

        for (var a = 0; a < _players; a++)
        for (var b = a + 1; b < _players; b++)
        {
            if (!open[a] || !open[b]) continue;
            var matched = Math.Min(_matches[a, b], _matches[b, a]);
            if (matched >= MatchesNeeded && _matchedMasks[a, b].Count >= DistinctNeeded &&
                _misses[a, b] == 0 && _misses[b, a] == 0) return b;
        }

        return -1;
    }
}
