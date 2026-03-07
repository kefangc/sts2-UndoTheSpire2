using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace UndoTheSpire2;

public partial class UndoHud : Control
{
    private readonly UndoHudButton _undoButton;
    private readonly UndoHudButton _redoButton;
    private NCombatUi? _combatUi;

    public UndoHud()
    {
        Name = "UndoHud";
        MouseFilter = MouseFilterEnum.Ignore;
        ProcessMode = Node.ProcessModeEnum.Inherit;
        ZIndex = 100;
        SetAnchorsPreset(LayoutPreset.TopLeft);

        _undoButton = CreateButton("UndoButton", true);
        _redoButton = CreateButton("RedoButton", false);

        _undoButton.Position = Vector2.Zero;
        _redoButton.Position = new Vector2(68f, 0f);

        _undoButton.Activated += OnUndoPressed;
        _redoButton.Activated += OnRedoPressed;

        AddChild(_undoButton);
        AddChild(_redoButton);
    }

    public void Bind(NCombatUi combatUi)
    {
        _combatUi = combatUi;
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(130f, 48f);
        Size = CustomMinimumSize;
    }

    public override void _Process(double delta)
    {
        if (_combatUi == null || !GodotObject.IsInstanceValid(_combatUi))
        {
            Visible = false;
            return;
        }

        UndoController controller = MainFile.Controller;
        Visible = controller.ShouldShowHud(_combatUi) && !controller.IsRestoring;
        if (!Visible)
            return;

        Vector2 energyPosition = _combatUi.EnergyCounterContainer.Position;
        Position = energyPosition + new Vector2(6f, -Size.Y - 12f);
        _undoButton.IsEnabled = controller.CanUndoNow(_combatUi);
        _redoButton.IsEnabled = controller.CanRedoNow(_combatUi);
        _undoButton.HoverTitle = ModLocalization.Get("hud.undo_hover_title", "Undo (Ctrl+Z)");
        _redoButton.HoverTitle = ModLocalization.Get("hud.redo_hover_title", "Redo (Ctrl+Y)");
        _undoButton.HoverDescription = controller.HasUndo
            ? controller.UndoLabel
            : ModLocalization.Get("hud.undo_hover_empty", "No action to undo.");
        _redoButton.HoverDescription = controller.HasRedo
            ? controller.RedoLabel
            : ModLocalization.Get("hud.redo_hover_empty", "No action to redo.");
    }

    private void OnUndoPressed()
    {
        if (_combatUi != null)
            MainFile.Controller.TryHandleUndoRequest(_combatUi, "hud");
    }

    private void OnRedoPressed()
    {
        if (_combatUi != null)
            MainFile.Controller.TryHandleRedoRequest(_combatUi, "hud");
    }

    private static UndoHudButton CreateButton(string name, bool isUndo)
    {
        return new UndoHudButton(isUndo)
        {
            Name = name,
            HoverAlignment = isUndo ? HoverTipAlignment.Left : HoverTipAlignment.Right
        };
    }
}


