using System.Numerics;
using Content.Client.Message;
using Content.Client.Resources;
using Content.Client.UserInterface.Controls;
using Content.Shared.Singularity.Components;
using Content.Shared.Access.Systems;
using Robust.Client.Animations;
using Robust.Client.AutoGenerated;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Noise;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Client.Player;

namespace Content.Client.ParticleAccelerator.UI;

[GenerateTypedNameReferences]
public sealed partial class ParticleAcceleratorControlMenu : FancyWindow
{
    [Dependency] private readonly IResourceCache _cache = default!;

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private readonly AccessReaderSystem _accessReader;

    private readonly FastNoiseLite _drawNoiseGenerator;

    private readonly Animation _alarmControlAnimation;

    private float _time;
    private int _lastDraw;
    private int _lastReceive;

    private bool _assembled;
    private bool _shouldContinueAnimating;
    private int _maxStrength = 3;

    public event Action<bool>? OnOverallState;
    public event Action? OnScan;
    public event Action<ParticleAcceleratorPowerState>? OnPowerState;

    private EntityUid _entity;

    public ParticleAcceleratorControlMenu()
    {
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);

        _accessReader = _entityManager.System<AccessReaderSystem>();
        _drawNoiseGenerator = new();
        _drawNoiseGenerator.SetFractalType(FastNoiseLite.FractalType.FBm);
        _drawNoiseGenerator.SetFrequency(0.5f);

        var panelTex = _cache.GetTexture("/Textures/Interface/Nano/button.svg.96dpi.png");

        MouseFilter = MouseFilterMode.Stop;

        _alarmControlAnimation = new Animation
        {
            Length = TimeSpan.FromSeconds(1),
            AnimationTracks =
            {
                new AnimationTrackControlProperty
                {
                    Property = nameof(Visible),
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(true, 0),
                        new AnimationTrackProperty.KeyFrame(false, 0.75f),
                    }
                }
            }
        };

        if (BackPanel.PanelOverride is StyleBoxTexture tex)
            tex.Texture = panelTex;

        StatusLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-status-label"));
        StatusStateLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-status-unknown"));
        PowerLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-power-label"));
        StrengthLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-strength-label"));
        BigAlarmLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-alarm-control-1"));
        BigAlarmLabelTwo.SetMarkup(Loc.GetString("particle-accelerator-control-menu-alarm-control-2"));
        DrawLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-draw"));

        StateSpinBox.IsValid = StrengthSpinBoxValid;
        StateSpinBox.InitDefaultButtons();
        StateSpinBox.ValueChanged += PowerStateChanged;
        StateSpinBox.LineEditDisabled = true;

        OffButton.OnPressed += _ =>
        {
            OnOverallState?.Invoke(false);
        };

        OnButton.OnPressed += _ =>
        {
            OnOverallState?.Invoke(true);
        };

        ScanButton.OnPressed += _ =>
        {
            OnScan?.Invoke();
        };

        AlarmControl.AnimationCompleted += _ =>
        {
            if (_shouldContinueAnimating)
            {
                AlarmControl.PlayAnimation(_alarmControlAnimation, "warningAnim");
            }
            else
            {
                AlarmControl.Visible = false;
            }
        };

        UpdateUI(false, false, false, false);
    }

    public void SetEntity(EntityUid uid)
    {
        _entity = uid;
    }

    private void PowerStateChanged(ValueChangedEventArgs e)
    {
        ParticleAcceleratorPowerState newState;
        switch (e.Value)
        {
            case 0:
                newState = ParticleAcceleratorPowerState.Standby;
                break;
            case 1:
                newState = ParticleAcceleratorPowerState.Level0;
                break;
            case 2:
                newState = ParticleAcceleratorPowerState.Level1;
                break;
            case 3:
                newState = ParticleAcceleratorPowerState.Level2;
                break;
            case 4:
                newState = ParticleAcceleratorPowerState.Level3;
                break;
            default:
                return;
        }

        StateSpinBox.SetButtonDisabled(true);
        OnPowerState?.Invoke(newState);
    }

    private bool StrengthSpinBoxValid(int n)
    {
        return n >= 0 && n <= _maxStrength;
    }

    protected override DragMode GetDragModeFor(Vector2 relativeMousePos)
    {
        return DragMode.Move;
    }

    public void DataUpdate(ParticleAcceleratorUIState uiState)
    {
        _assembled = uiState.Assembled;
        UpdateUI(uiState.Assembled,
            uiState.InterfaceBlock,
            uiState.Enabled,
            uiState.WirePowerBlock);
        StatusStateLabel.SetMarkup(Loc.GetString(uiState.Assembled
            ? "particle-accelerator-control-menu-status-operational"
            : "particle-accelerator-control-menu-status-incomplete"));
        UpdatePowerState(uiState.State, uiState.Enabled, uiState.Assembled, uiState.MaxLevel);
        UpdatePreview(uiState);
        _lastDraw = uiState.PowerDraw;
        _lastReceive = uiState.PowerReceive;
    }

    private void UpdatePowerState(ParticleAcceleratorPowerState state, bool enabled, bool assembled, ParticleAcceleratorPowerState maxState)
    {
        var value = state switch
        {
            ParticleAcceleratorPowerState.Standby => 0,
            ParticleAcceleratorPowerState.Level0 => 1,
            ParticleAcceleratorPowerState.Level1 => 2,
            ParticleAcceleratorPowerState.Level2 => 3,
            ParticleAcceleratorPowerState.Level3 => 4,
            _ => 0
        };

        StateSpinBox.OverrideValue(value);

        _maxStrength = maxState == ParticleAcceleratorPowerState.Level3 ? 4 : 3;
        if (_maxStrength > 3 && enabled && assembled)
        {
            _shouldContinueAnimating = true;
            if (!AlarmControl.HasRunningAnimation("warningAnim"))
                AlarmControl.PlayAnimation(_alarmControlAnimation, "warningAnim");
        }
        else
            _shouldContinueAnimating = false;
    }

    private void UpdateUI(bool assembled, bool blocked, bool enabled, bool powerBlock)
    {
        bool hasAccess = _player.LocalSession?.AttachedEntity is {} player
            && _accessReader.IsAllowed(player, _entity);

        OnButton.Pressed = enabled;
        OffButton.Pressed = !enabled;

        var cantUse = !assembled || blocked || powerBlock || !hasAccess;
        OnButton.Disabled = cantUse;
        OffButton.Disabled = cantUse;
        ScanButton.Disabled = blocked || !hasAccess;

        var cantChangeLevel = !assembled || blocked || !enabled || cantUse;
        StateSpinBox.SetButtonDisabled(cantChangeLevel);
    }

    private void UpdatePreview(ParticleAcceleratorUIState updateMessage)
    {
        EndCapTexture.SetPowerState(updateMessage, updateMessage.EndCapExists);
        ControlBoxTexture.SetPowerState(updateMessage, true);
        FuelChamberTexture.SetPowerState(updateMessage, updateMessage.FuelChamberExists);
        PowerBoxTexture.SetPowerState(updateMessage, updateMessage.PowerBoxExists);
        EmitterStarboardTexture.SetPowerState(updateMessage, updateMessage.EmitterStarboardExists);
        EmitterForeTexture.SetPowerState(updateMessage, updateMessage.EmitterForeExists);
        EmitterPortTexture.SetPowerState(updateMessage, updateMessage.EmitterPortExists);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (!_assembled)
        {
            DrawValueLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-draw-not-available"));
            return;
        }

        _time += args.DeltaSeconds;

        var watts = 0;
        if (_lastDraw != 0)
        {
            var val = _drawNoiseGenerator.GetNoise(_time, 0f);
            watts = (int) (_lastDraw + val * 5);
        }

        DrawValueLabel.SetMarkup(Loc.GetString("particle-accelerator-control-menu-draw-value",
            ("watts", $"{watts:##,##0}"),
            ("lastReceive", $"{_lastReceive:##,##0}")));
    }
}

public sealed class PASegmentControl : Control
{
    private readonly ShaderInstance _greyScaleShader;
    private readonly TextureRect _base;
    private readonly TextureRect _unlit;
    private RSI? _rsi;

    public string BaseState { get; set; } = "control_box";

    public PASegmentControl()
    {
        _greyScaleShader = IoCManager.Resolve<IPrototypeManager>().Index<ShaderPrototype>("Greyscale").Instance();

        AddChild(_base = new TextureRect());
        AddChild(_unlit = new TextureRect());
    }

    protected override void EnteredTree()
    {
        base.EnteredTree();
        _rsi = IoCManager.Resolve<IResourceCache>().GetResource<RSIResource>($"/Textures/Structures/Power/Generation/PA/{BaseState}.rsi").RSI;
        MinSize = _rsi.Size;
        _base.Texture = _rsi["completed"].Frame0;
    }

    public void SetPowerState(ParticleAcceleratorUIState state, bool exists)
    {
        _base.ShaderOverride = exists ? null : _greyScaleShader;
        _base.ModulateSelfOverride = exists ? null : new Color(127, 127, 127);

        if (!state.Enabled || !exists)
        {
            _unlit.Visible = false;
            return;
        }

        _unlit.Visible = true;

        var suffix = state.State switch
        {
            ParticleAcceleratorPowerState.Standby => "_unlitp",
            ParticleAcceleratorPowerState.Level0 => "_unlitp0",
            ParticleAcceleratorPowerState.Level1 => "_unlitp1",
            ParticleAcceleratorPowerState.Level2 => "_unlitp2",
            ParticleAcceleratorPowerState.Level3 => "_unlitp3",
            _ => ""
        };

        if (_rsi == null)
            return;

        if (!_rsi.TryGetState(BaseState + suffix, out var rState))
        {
            _unlit.Visible = false;
            return;
        }

        _unlit.Texture = rState.Frame0;
    }
}
