using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace UndoTheSpire2;

internal sealed partial class UndoModSettingsPanel : PanelContainer
{
    private readonly Label _titleLabel;
    private readonly CheckBox _choiceUndoToggle;
    private readonly Label _choiceUndoDescription;
    private readonly CheckBox _unifiedEffectToggle;
    private readonly Label _unifiedEffectDescription;
    private readonly CheckBox _hud2Toggle;
    private readonly Label _hud2Description;
    private NModInfoContainer? _container;
    private bool _isRefreshing;

    public UndoModSettingsPanel()
    {
        Name = nameof(UndoModSettingsPanel);
        MouseFilter = MouseFilterEnum.Pass;
        ProcessMode = Node.ProcessModeEnum.Inherit;
        ZAsRelative = false;
        ZIndex = 10;
        SetAnchorsPreset(LayoutPreset.TopLeft);

        StyleBoxFlat panel = new()
        {
            BgColor = new Color(0.07f, 0.12f, 0.16f, 0.92f),
            BorderColor = new Color(0.33f, 0.54f, 0.66f, 0.9f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusBottomLeft = 8,
            ContentMarginLeft = 14,
            ContentMarginTop = 14,
            ContentMarginRight = 14,
            ContentMarginBottom = 14
        };
        AddThemeStyleboxOverride("panel", panel);

        VBoxContainer layout = new()
        {
            Name = "Layout",
            CustomMinimumSize = new Vector2(340f, 220f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        AddChild(layout);

        _titleLabel = CreateTitleLabel();
        _choiceUndoToggle = CreateToggle();
        _choiceUndoDescription = CreateDescriptionLabel();
        _unifiedEffectToggle = CreateToggle();
        _unifiedEffectDescription = CreateDescriptionLabel();
        _hud2Toggle = CreateToggle();
        _hud2Description = CreateDescriptionLabel();

        layout.AddChild(_titleLabel);
        layout.AddChild(_choiceUndoToggle);
        layout.AddChild(_choiceUndoDescription);
        layout.AddChild(_unifiedEffectToggle);
        layout.AddChild(_unifiedEffectDescription);
        layout.AddChild(_hud2Toggle);
        layout.AddChild(_hud2Description);

        _choiceUndoToggle.Toggled += OnChoiceUndoToggled;
        _unifiedEffectToggle.Toggled += OnUnifiedEffectToggled;
        _hud2Toggle.Toggled += OnHud2Toggled;
        RefreshTexts();
        RefreshFromSettings();
    }

    public void Bind(NModInfoContainer container)
    {
        _container = container;
        Visible = true;
        RefreshTexts();
        RefreshFromSettings();
    }

    public override void _Ready()
    {
        UndoModSettings.Changed += OnSettingsChanged;
        TreeExiting += OnTreeExiting;
    }

    public override void _Process(double delta)
    {
        if (_container == null || !GodotObject.IsInstanceValid(_container))
        {
            Visible = false;
            return;
        }

        MegaRichTextLabel? title = _container.GetNodeOrNull<MegaRichTextLabel>("ModTitle");
        MegaRichTextLabel? description = _container.GetNodeOrNull<MegaRichTextLabel>("ModDescription");
        if (title == null || description == null)
            return;

        Visible = true;
        float titleBottom = title.Position.Y + title.Size.Y;
        float descriptionBottom = description.Position.Y + Mathf.Min(description.GetContentHeight(), 180);
        float panelY = Mathf.Clamp(descriptionBottom + 22f, titleBottom + 92f, Mathf.Max(titleBottom + 92f, _container.Size.Y - 248f));
        Position = new Vector2(description.Position.X, panelY);
        Size = new Vector2(Mathf.Max(340f, _container.Size.X - 12f), 220f);
    }

    private void OnTreeExiting()
    {
        UndoModSettings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        RefreshFromSettings();
    }

    private void OnChoiceUndoToggled(bool toggled)
    {
        if (_isRefreshing)
            return;

        UndoModSettings.SetEnableChoiceUndo(toggled);
    }

    private void OnUnifiedEffectToggled(bool toggled)
    {
        if (_isRefreshing)
            return;

        UndoModSettings.SetEnableUnifiedEffectMode(toggled);
    }

    private void OnHud2Toggled(bool toggled)
    {
        if (_isRefreshing)
            return;

        UndoModSettings.SetEnableHud2(toggled);
    }

    private void RefreshTexts()
    {
        _titleLabel.Text = ModLocalization.Get("settings.panel_title", "Undo Settings");
        _choiceUndoToggle.Text = ModLocalization.Get("settings.choice_undo_title", "Enable Choice Undo");
        _choiceUndoDescription.Text = ModLocalization.Get(
            "settings.choice_undo_description",
            "Return to the choice screen after undoing a card or potion that asks you to choose.");
        _unifiedEffectToggle.Text = ModLocalization.Get("settings.unified_effect_title", "Enable Unified Effect Mode");
        _unifiedEffectDescription.Text = ModLocalization.Get(
            "settings.unified_effect_description",
            "Prefer the generic instant-restore effect synthesis path instead of conservative fallbacks.");
        _hud2Toggle.Text = ModLocalization.Get("settings.hud2_title", "Enable HUD2");
        _hud2Description.Text = ModLocalization.Get(
            "settings.hud2_description",
            "Use an alternate HUD style inspired by Slay the Spire 2's dark stone-and-brass combat UI.");
    }

    private void RefreshFromSettings()
    {
        _isRefreshing = true;
        _choiceUndoToggle.ButtonPressed = UndoModSettings.EnableChoiceUndo;
        _unifiedEffectToggle.ButtonPressed = UndoModSettings.EnableUnifiedEffectMode;
        _hud2Toggle.ButtonPressed = UndoModSettings.EnableHud2;
        _unifiedEffectToggle.Disabled = !_choiceUndoToggle.ButtonPressed;
        _isRefreshing = false;
    }

    private static Label CreateTitleLabel()
    {
        return new Label
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Modulate = new Color(0.95f, 0.84f, 0.42f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
    }

    private static CheckBox CreateToggle()
    {
        return new CheckBox
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.All
        };
    }

    private static Label CreateDescriptionLabel()
    {
        return new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.82f, 0.88f, 0.91f, 0.95f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(320f, 0f)
        };
    }
}
