namespace RecompOne.Runtime.Diagnostics;

//frames per second of the game (VSync presents, TestScript.Tick) and of the host window (OnRender), averaged over
//about one second; shown in the FrameStats line and by the "fps" script action
public static class FrameRate
{
    private sealed class Counter
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _windowStart;
        private int _count;
        public volatile float Fps;

        public void Mark()
        {
            _count++;
            var now = _clock.Elapsed.TotalSeconds;
            if (now - _windowStart < 1.0) return;
            Fps = (float)(_count / (now - _windowStart));
            _count = 0;
            _windowStart = now;
        }
    }

    private static readonly Counter _game = new(), _host = new();

    public static float GameFps => _game.Fps;
    public static float HostFps => _host.Fps;

    public static void MarkGame() => _game.Mark();
    public static void MarkHost() => _host.Mark();
}
