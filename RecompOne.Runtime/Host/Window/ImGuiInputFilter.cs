using Silk.NET.Input;

namespace RecompOne.Runtime.Host.Window;

//Silk.NET's ImGuiController subscribes to the keyboard itself and throws NotImplementedException from its key
//callback (ImGuiController.TranslateInputKeyToImGuiKey) for keys it has no ImGui mapping for: Key.Unknown, World1,
//World2, F25. GLFW reports Key.Unknown for keys it cannot name, media keys and keys sent by virtual devices (a pad
//remapper that emulates a keyboard), so pressing one threw out of the window's event pump. The controller gets this
//view of the input instead, which leaves those keys out; ImGui has nothing to do with them anyway.
internal sealed class ImGuiInputFilter : IInputContext
{
    private static readonly HashSet<Key> Unmapped = [Key.Unknown, Key.World1, Key.World2, Key.F25];

    private readonly IInputContext _inner;
    private readonly IReadOnlyList<IKeyboard> _keyboards;

    public ImGuiInputFilter(IInputContext inner)
    {
        _inner = inner;
        _keyboards = inner.Keyboards.Select(k => (IKeyboard)new FilteredKeyboard(k)).ToList();
    }

    public nint Handle => _inner.Handle;
    public IReadOnlyList<IGamepad> Gamepads => _inner.Gamepads;
    public IReadOnlyList<IJoystick> Joysticks => _inner.Joysticks;
    public IReadOnlyList<IKeyboard> Keyboards => _keyboards;
    public IReadOnlyList<IMouse> Mice => _inner.Mice;
    public IReadOnlyList<IInputDevice> OtherDevices => _inner.OtherDevices;

    public event Action<IInputDevice, bool>? ConnectionChanged
    {
        add => _inner.ConnectionChanged += value;
        remove => _inner.ConnectionChanged -= value;
    }

    //the window owns the real context
    public void Dispose()
    {
    }

    private sealed class FilteredKeyboard : IKeyboard
    {
        private readonly IKeyboard _inner;
        private readonly Dictionary<Delegate, Action<IKeyboard, Key, int>> _down = [];
        private readonly Dictionary<Delegate, Action<IKeyboard, Key, int>> _up = [];

        public FilteredKeyboard(IKeyboard inner) => _inner = inner;

        public string Name => _inner.Name;
        public int Index => _inner.Index;
        public bool IsConnected => _inner.IsConnected;
        public IReadOnlyList<Key> SupportedKeys => _inner.SupportedKeys.Where(k => !Unmapped.Contains(k)).ToList();

        public string ClipboardText
        {
            get => _inner.ClipboardText;
            set => _inner.ClipboardText = value;
        }

        public bool IsKeyPressed(Key key) => !Unmapped.Contains(key) && _inner.IsKeyPressed(key);
        public bool IsScancodePressed(int scancode) => _inner.IsScancodePressed(scancode);
        public void BeginInput() => _inner.BeginInput();
        public void EndInput() => _inner.EndInput();

        public event Action<IKeyboard, Key, int>? KeyDown
        {
            add => _inner.KeyDown += Wrap(value, _down);
            remove => _inner.KeyDown -= Unwrap(value, _down);
        }

        public event Action<IKeyboard, Key, int>? KeyUp
        {
            add => _inner.KeyUp += Wrap(value, _up);
            remove => _inner.KeyUp -= Unwrap(value, _up);
        }

        public event Action<IKeyboard, char>? KeyChar
        {
            add => _inner.KeyChar += value;
            remove => _inner.KeyChar -= value;
        }

        private Action<IKeyboard, Key, int>? Wrap(Action<IKeyboard, Key, int>? handler,
            Dictionary<Delegate, Action<IKeyboard, Key, int>> map)
        {
            if (handler == null) return null;
            Action<IKeyboard, Key, int> w = (_, key, code) =>
            {
                if (!Unmapped.Contains(key)) handler(this, key, code);
            };
            map[handler] = w;
            return w;
        }

        private Action<IKeyboard, Key, int>? Unwrap(Action<IKeyboard, Key, int>? handler,
            Dictionary<Delegate, Action<IKeyboard, Key, int>> map)
        {
            if (handler == null || !map.Remove(handler, out var w)) return null;
            return w;
        }
    }
}
