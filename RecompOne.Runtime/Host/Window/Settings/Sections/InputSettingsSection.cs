using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class InputSettingsSection : ISettingsSection
{
    public string Id => "input";
    public string TitleKey => "settings.input";
    public int Order => 0;

    private static readonly (string Label, Func<KeyBindings, string> GetKey, Action<KeyBindings, string> SetKey,
        Func<GamepadBindings, int[]> GetPad, Action<GamepadBindings, int[]> SetPad)[] _rows =
        [
            ("Cross", b => b.Cross, (b, v) => b.Cross = v, p => p.Cross, (p, v) => p.Cross = v),
            ("Circle", b => b.Circle, (b, v) => b.Circle = v, p => p.Circle, (p, v) => p.Circle = v),
            ("Square", b => b.Square, (b, v) => b.Square = v, p => p.Square, (p, v) => p.Square = v),
            ("Triangle", b => b.Triangle, (b, v) => b.Triangle = v, p => p.Triangle, (p, v) => p.Triangle = v),
            ("L1", b => b.L1, (b, v) => b.L1 = v, p => p.L1, (p, v) => p.L1 = v),
            ("R1", b => b.R1, (b, v) => b.R1 = v, p => p.R1, (p, v) => p.R1 = v),
            ("L2", b => b.L2, (b, v) => b.L2 = v, p => p.L2, (p, v) => p.L2 = v),
            ("R2", b => b.R2, (b, v) => b.R2 = v, p => p.R2, (p, v) => p.R2 = v),
            ("L3", b => b.L3, (b, v) => b.L3 = v, p => p.L3, (p, v) => p.L3 = v),
            ("R3", b => b.R3, (b, v) => b.R3 = v, p => p.R3, (p, v) => p.R3 = v),
            ("Start", b => b.Start, (b, v) => b.Start = v, p => p.Start, (p, v) => p.Start = v),
            ("Select", b => b.Select, (b, v) => b.Select = v, p => p.Select, (p, v) => p.Select = v),
            ("Up", b => b.Up, (b, v) => b.Up = v, p => p.Up, (p, v) => p.Up = v),
            ("Down", b => b.Down, (b, v) => b.Down = v, p => p.Down, (p, v) => p.Down = v),
            ("Left", b => b.Left, (b, v) => b.Left = v, p => p.Left, (p, v) => p.Left = v),
            ("Right", b => b.Right, (b, v) => b.Right = v, p => p.Right, (p, v) => p.Right = v)
        ];

    private bool _gamepadMode;
    private int _padIndex;
    private int _remapRow = -1;
    private bool _remapAdd;

    public void Draw()
    {
        DrawGeneral();
        ImGui.Separator();
        ImGui.Spacing();
        DrawDeviceSelector();
        ImGui.Spacing();
        DrawPadSelector();
        ImGui.Spacing();
        DrawPadKind();
        ImGui.Spacing();

        if (_gamepadMode)
        {
            DrawDeviceCombo();
            ImGui.Spacing();
        }

        var slot = InputManager.SlotOfPlayer(_padIndex);
        ImGuiEx.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), slot >= 0
            ? Localization.T("settings.input.slot", Hardware.Controller.SlotName(slot))
            : Localization.T("settings.input.slot.none"));
        ImGui.Spacing();

        if (!_gamepadMode && _padIndex > 1)
        {
            ImGuiEx.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), Localization.T("settings.input.keyboard_players"));
            return;
        }

        if (_gamepadMode && !InputManager.IsPadConnected(_padIndex))
        {
            ImGuiEx.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), Localization.T("settings.input.no_gamepad"));
            ImGui.Spacing();
        }

        DrawBindings();

        if (_gamepadMode && ConfigManager.Game.KindFor(_padIndex) == PadKind.Analog)
        {
            ImGui.Spacing();
            DrawStickAxes();
        }

        ImGui.Spacing();
        if (ImGui.Button(Localization.T("settings.input.reset_defaults")))
        {
            if (!_gamepadMode)
            {
                if (_padIndex == 0) ConfigManager.Game.Keys = new KeyBindings();
                else ConfigManager.Game.Keys2 = KeyBindings.DefaultPlayer2();
            }
            else
            {
                ConfigManager.Game.ResetPad(_padIndex);
            }

            _remapRow = -1;
            ConfigManager.SaveGame();
        }
    }

    private static readonly (string Label, Func<GamepadBindings, int> Get, Action<GamepadBindings, int> Set)[]
        _axisRows =
        [
            ("L Stick X", p => p.LeftStickX, (p, v) => p.LeftStickX = v),
            ("L Stick Y", p => p.LeftStickY, (p, v) => p.LeftStickY = v),
            ("R Stick X", p => p.RightStickX, (p, v) => p.RightStickX = v),
            ("R Stick Y", p => p.RightStickY, (p, v) => p.RightStickY = v)
        ];

    private static readonly string[] AxisNames = ["Left X", "Left Y", "Right X", "Right Y", "Trigger L", "Trigger R"];

    private void DrawStickAxes()
    {
        var pad = ConfigManager.Game.PadFor(_padIndex);
        ImGuiEx.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), Localization.T("settings.input.sticks"));

        foreach (var (label, get, set) in _axisRows)
        {
            var cur = get(pad);
            var name = cur >= 0 && cur < AxisNames.Length ? AxisNames[cur] : $"axis {cur}";

            ImGui.SetNextItemWidth(160f);
            if (ImGui.BeginCombo(label, name))
            {
                for (var i = 0; i < AxisNames.Length; i++)
                    if (ImGui.Selectable(AxisNames[i], i == cur))
                    {
                        set(pad, i);
                        ConfigManager.SaveGame();
                    }

                ImGui.EndCombo();
            }
        }
    }

    private static readonly string[] PadKindKeys = ["settings.input.kind.digital", "settings.input.kind.analog"];

    private void DrawPadKind()
    {
        var cfg = ConfigManager.Game;
        var index = (int)cfg.KindFor(_padIndex);

        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo(Localization.T("settings.input.kind"), Localization.T(PadKindKeys[index])))
        {
            for (var i = 0; i < PadKindKeys.Length; i++)
                if (ImGui.Selectable(Localization.T(PadKindKeys[i]), i == index))
                {
                    cfg.SetKind(_padIndex, (PadKind)i);
                    ConfigManager.SaveGame();
                }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGuiEx.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f),
            Localization.T(index == 1 ? "settings.input.kind.analog.hint" : "settings.input.kind.digital.hint"));
    }

    private void DrawDeviceSelector()
    {
        if (ImGui.BeginTabBar("##input-device"))
        {
            if (ImGui.BeginTabItem(Localization.T("settings.input.keyboard")))
            {
                if (_gamepadMode) _remapRow = -1;
                _gamepadMode = false;
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Localization.T("settings.input.gamepad")))
            {
                if (!_gamepadMode) _remapRow = -1;
                _gamepadMode = true;
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawPadSelector()
    {
        if (ImGui.BeginTabBar("##input-pad"))
        {
            if (ImGui.BeginTabItem(Localization.T("settings.input.pad", 1)))
            {
                if (_padIndex != 0) _remapRow = -1;
                _padIndex = 0;
                ImGui.EndTabItem();
            }

            for (var p = 1; p < GameConfig.MaxPlayers; p++)
                if (ImGui.BeginTabItem(Localization.T("settings.input.pad", p + 1)))
                {
                    if (_padIndex != p) _remapRow = -1;
                    _padIndex = p;
                    ImGui.EndTabItem();
                }

            ImGui.EndTabBar();
        }
    }

    private void DrawDeviceCombo()
    {
        var devices = InputManager.Devices;
        var current = ConfigManager.Game.DeviceFor(_padIndex);

        var preview = Localization.T("settings.input.device.auto");
        foreach (var d in devices)
            if (d.Id == current)
            {
                preview = d.Name;
                break;
            }

        if (!string.IsNullOrEmpty(current) && preview == Localization.T("settings.input.device.auto"))
            preview = Localization.T("settings.input.device.missing");

        ImGui.TextUnformatted(Localization.T("settings.input.device"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-70);

        if (ImGui.BeginCombo("##input-device-pick", preview))
        {
            if (ImGui.Selectable(Localization.T("settings.input.device.auto"), string.IsNullOrEmpty(current)))
                SetDevice("");

            foreach (var d in devices)
                if (ImGui.Selectable(d.Name, d.Id == current))
                    SetDevice(d.Id);

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(Localization.T("settings.input.device.refresh")))
            InputManager.RefreshDevices();

        //a pad remapper (DualSenseX, DS4Windows, Steam Input) can show one pad twice, a real one and a virtual Xbox
        //pad; ignoring one of them stops a single pad from being two players
        if (InputManager.IsPadConnected(_padIndex) && ImGui.Button(Localization.T("settings.input.ignore_device")))
        {
            var name = InputManager.PlayerDeviceName(_padIndex);
            if (name.Length > 0 && !ConfigManager.Game.IgnoredPads.Contains(name))
            {
                ConfigManager.Game.IgnoredPads.Add(name);
                ConfigManager.SaveGame();
                InputManager.RefreshDevices();
            }
        }
    }

    private static readonly string[] MultitapKeys =
        ["settings.input.multitap.auto", "settings.input.multitap.on", "settings.input.multitap.off"];

    private void DrawGeneral()
    {
        var cfg = ConfigManager.Game;
        var mt = (int)cfg.Multitap;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo(Localization.T("settings.input.multitap"), Localization.T(MultitapKeys[mt])))
        {
            for (var i = 0; i < MultitapKeys.Length; i++)
                if (ImGui.Selectable(Localization.T(MultitapKeys[i]), i == mt))
                {
                    cfg.Multitap = (MultitapMode)i;
                    ConfigManager.SaveGame();
                }

            ImGui.EndCombo();
        }

        var dz = cfg.StickDeadzone;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderFloat(Localization.T("settings.input.deadzone"), ref dz, 0f, 0.6f, "%.2f"))
        {
            cfg.StickDeadzone = dz;
            ConfigManager.SaveGame();
        }

        if (cfg.IgnoredPads.Count == 0) return;
        ImGuiEx.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), Localization.T("settings.input.ignored"));
        foreach (var name in cfg.IgnoredPads.ToList())
        {
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            if (!ImGui.SmallButton($"{Localization.T("settings.input.unignore")}##{name}")) continue;
            cfg.IgnoredPads.Remove(name);
            ConfigManager.SaveGame();
            InputManager.RefreshDevices();
        }
    }

    private void SetDevice(string id)
    {
        ConfigManager.Game.SetDevice(_padIndex, id);
        ConfigManager.SaveGame();
        InputManager.RefreshDevices();
    }

    private void DrawBindings()
    {
        if (!ImGui.BeginTable("##bindings", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn(Localization.T("settings.input.button"), ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn(Localization.T(_gamepadMode ? "settings.input.gamepad" : "settings.input.keyboard"),
            ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var keys = _padIndex == 0 ? ConfigManager.Game.Keys : ConfigManager.Game.Keys2;
        var pad = ConfigManager.Game.PadFor(_padIndex);

        for (var i = 0; i < _rows.Length; i++)
        {
            var (label, getKey, setKey, getPad, setPad) = _rows[i];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            var awaiting = _remapRow == i;

            if (_gamepadMode)
            {
                var bindings = getPad(pad);
                var text = awaiting
                    ? Localization.T(_remapAdd ? "settings.input.press_button_add" : "settings.input.press_button")
                    : bindings.Length == 0
                        ? Localization.T("settings.input.unbound")
                        : string.Join(" | ", bindings.Select(PadLabel));

                var plusW = ImGui.GetFrameHeight();
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                if (ImGui.Button($"{text}##p{i}", new Vector2(-plusW - spacing, 0)))
                {
                    _remapRow = i;
                    _remapAdd = false;
                }

                ImGui.SameLine();
                if (ImGui.Button($"+##add{i}", new Vector2(plusW, 0)))
                {
                    _remapRow = i;
                    _remapAdd = true;
                }

                if (awaiting)
                {
                    var p = InputManager.GetFirstPressedPadButton(_padIndex);
                    if (p.HasValue)
                    {
                        if (_remapAdd)
                        {
                            if (!bindings.Contains(p.Value)) setPad(pad, [.. bindings, p.Value]);
                        }
                        else
                        {
                            setPad(pad, [p.Value]);
                        }

                        _remapRow = -1;
                        ConfigManager.SaveGame();
                    }
                }
            }
            else
            {
                var key = getKey(keys);
                var text = awaiting
                    ? Localization.T("settings.input.press_key")
                    : $"{(key.Length == 0 ? Localization.T("settings.input.unbound") : key)}##k{i}";
                if (ImGui.Button(text, new Vector2(-1, 0))) _remapRow = i;
                if (awaiting)
                {
                    var p = GetPressedKey();
                    if (p != null)
                    {
                        setKey(keys, p);
                        _remapRow = -1;
                        ConfigManager.SaveGame();
                    }
                }
            }
        }

        ImGui.EndTable();
    }

    private static string? GetPressedKey()
    {
        foreach (var k in Enum.GetValues<Key>())
        {
            if (k is Key.Unknown or Key.Menu) continue;
            if (InputManager.IsKeyDown(k)) return k.ToString();
        }

        return null;
    }

    private static string PadLabel(int b)
    {
        return b switch
        {
            0 => "Cross (A)",
            1 => "Circle (B)",
            2 => "Square (X)",
            3 => "Triangle (Y)",
            4 => "Select (Back)",
            5 => "Guide",
            6 => "Start",
            7 => "L3 (LStick)",
            8 => "R3 (RStick)",
            9 => "L1 (LBumper)",
            10 => "R1 (RBumper)",
            11 => "D-Up",
            12 => "D-Down",
            13 => "D-Left",
            14 => "D-Right",
            100 => "L2 (LTrigger)",
            101 => "R2 (RTrigger)",
            102 => "LStick Left",
            103 => "LStick Right",
            104 => "LStick Up",
            105 => "LStick Down",
            106 => "RStick Left",
            107 => "RStick Right",
            108 => "RStick Up",
            109 => "RStick Down",
            _ => $"Btn {b}"
        };
    }
}