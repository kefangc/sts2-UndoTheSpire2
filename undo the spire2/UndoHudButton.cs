using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace UndoTheSpire2;

public partial class UndoHudButton : Control
{
    private static readonly PropertyInfo? HoverTipTitleProperty = typeof(HoverTip).GetProperty("Title", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? HoverTipDescriptionProperty = typeof(HoverTip).GetProperty("Description", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? HoverTipIdProperty = typeof(HoverTip).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly bool _isUndo;
    private bool _hovered;
    private bool _pressed;
    private bool _enabled = true;
    private string _hoverTitle = string.Empty;
    private string _hoverDescription = string.Empty;
    private HoverTipAlignment _hoverAlignment;

    public UndoHudButton(bool isUndo)
    {
        _isUndo = isUndo;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = new Vector2(62f, 48f);
        Size = CustomMinimumSize;
        _hoverAlignment = isUndo ? HoverTipAlignment.Left : HoverTipAlignment.Right;
        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;
    }

    public event Action? Activated;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            MouseDefaultCursorShape = value ? CursorShape.PointingHand : CursorShape.Arrow;
            QueueRedraw();
        }
    }

    public HoverTipAlignment HoverAlignment
    {
        get => _hoverAlignment;
        set => _hoverAlignment = value;
    }

    public string HoverTitle
    {
        get => _hoverTitle;
        set
        {
            if (_hoverTitle == value)
                return;

            _hoverTitle = value;
            RefreshHoverTip();
        }
    }

    public string HoverDescription
    {
        get => _hoverDescription;
        set
        {
            if (_hoverDescription == value)
                return;

            _hoverDescription = value;
            RefreshHoverTip();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (!_enabled)
            return;

        if (@event is not InputEventMouseButton mouseEvent || mouseEvent.ButtonIndex != MouseButton.Left)
            return;

        if (mouseEvent.Pressed)
        {
            _pressed = true;
            QueueRedraw();
            AcceptEvent();
            return;
        }

        bool shouldActivate = _pressed && _hovered;
        _pressed = false;
        QueueRedraw();
        AcceptEvent();
        if (shouldActivate)
            Activated?.Invoke();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
        {
            _pressed = false;
            QueueRedraw();
        }
        else if (what == NotificationExitTree)
        {
            NHoverTipSet.Remove(this);
        }
    }

    public override void _Draw()
    {
        Rect2 rect = new(Vector2.Zero, Size);
        DrawShadow(rect);
        DrawFrame(rect);
        DrawSurface(rect.Grow(-3f));
        DrawTrim(rect.Grow(-5f));
        DrawGlyph(rect.Grow(-5f));
    }

    private void DrawShadow(Rect2 rect)
    {
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, _enabled ? 0.26f : 0.16f),
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10
        }, new Rect2(rect.Position + new Vector2(0f, 2f), rect.Size));
    }

    private void DrawFrame(Rect2 rect)
    {
        Color border = _enabled
            ? (_pressed ? new Color(0.57f, 0.44f, 0.19f) : (_hovered ? new Color(0.84f, 0.66f, 0.28f) : new Color(0.70f, 0.53f, 0.23f)))
            : new Color(0.34f, 0.30f, 0.25f);
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = border,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10
        }, rect);
    }

    private void DrawSurface(Rect2 rect)
    {
        Color fill = _enabled
            ? (_pressed ? new Color(0.10f, 0.15f, 0.18f) : (_hovered ? new Color(0.11f, 0.19f, 0.23f) : new Color(0.09f, 0.16f, 0.19f)))
            : new Color(0.07f, 0.11f, 0.13f);
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = fill,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        }, rect);
    }

    private void DrawTrim(Rect2 rect)
    {
        Color top = _enabled ? new Color(1f, 0.95f, 0.78f, _hovered ? 0.28f : 0.16f) : new Color(0.8f, 0.8f, 0.8f, 0.05f);
        Color bottom = _enabled ? new Color(0f, 0f, 0f, 0.22f) : new Color(0f, 0f, 0f, 0.10f);
        DrawRect(new Rect2(rect.Position + new Vector2(5f, 2f), new Vector2(rect.Size.X - 10f, 2f)), top, true);
        DrawRect(new Rect2(rect.Position + new Vector2(5f, rect.Size.Y - 4f), new Vector2(rect.Size.X - 10f, 1.5f)), bottom, true);
    }

    private void DrawGlyph(Rect2 rect)
    {
        Vector2 center = rect.GetCenter() + new Vector2(0f, 1f);
        float stroke = _enabled ? 3.2f : 2.5f;
        Color glyph = _enabled
            ? (_hovered ? new Color(0.99f, 0.95f, 0.84f) : new Color(0.95f, 0.88f, 0.69f))
            : new Color(0.58f, 0.56f, 0.50f);
        Color shadow = new Color(0f, 0f, 0f, _enabled ? 0.22f : 0.12f);

        Vector2 arcCenter = center + new Vector2(_isUndo ? 4f : -4f, 0f);
        float start = _isUndo ? 0.55f : 2.60f;
        float end = _isUndo ? 4.95f : 7.00f;
        DrawArc(arcCenter + Vector2.Down, 9.5f, start, end, 28, shadow, stroke + 1f, true);
        DrawArc(arcCenter, 9.5f, start, end, 28, glyph, stroke, true);

        Vector2 tip = center + new Vector2(_isUndo ? -12f : 12f, -3f);
        Vector2 wingA = tip + new Vector2(_isUndo ? 7f : -7f, -6f);
        Vector2 wingB = tip + new Vector2(_isUndo ? 7f : -7f, 6f);
        DrawLine(tip + Vector2.Down, wingA + Vector2.Down, shadow, stroke + 1f, true);
        DrawLine(tip + Vector2.Down, wingB + Vector2.Down, shadow, stroke + 1f, true);
        DrawLine(tip, wingA, glyph, stroke, true);
        DrawLine(tip, wingB, glyph, stroke, true);

        Vector2 tailStart = center + new Vector2(_isUndo ? 3f : -3f, 10f);
        Vector2 tailEnd = tailStart + new Vector2(_isUndo ? 6f : -6f, 0f);
        DrawLine(tailStart + Vector2.Down, tailEnd + Vector2.Down, shadow, stroke, true);
        DrawLine(tailStart, tailEnd, glyph, stroke - 0.4f, true);
    }

    private void OnMouseEntered()
    {
        _hovered = true;
        QueueRedraw();
        ShowHoverTip();
    }

    private void OnMouseExited()
    {
        _hovered = false;
        _pressed = false;
        QueueRedraw();
        NHoverTipSet.Remove(this);
    }

    private void RefreshHoverTip()
    {
        if (_hovered)
            ShowHoverTip();
    }

    private void ShowHoverTip()
    {
        NHoverTipSet.Remove(this);
        if (string.IsNullOrWhiteSpace(_hoverTitle) && string.IsNullOrWhiteSpace(_hoverDescription))
            return;

        NHoverTipSet hoverTipSet = NHoverTipSet.CreateAndShow(this, CreateHoverTip(_hoverTitle, _hoverDescription), HoverTipAlignment.Center);
        float xOffset = _hoverAlignment switch
        {
            HoverTipAlignment.Left => -18f,
            HoverTipAlignment.Right => 18f,
            _ => 0f
        };
        hoverTipSet.SetExtraFollowOffset(new Vector2(xOffset, -Size.Y * 3.6f));
        hoverTipSet.SetFollowOwner();
    }

    private static HoverTip CreateHoverTip(string title, string description)
    {
        object boxed = new HoverTip(new LocString("static_hover_tips", "END_TURN.title"), string.Empty);
        HoverTipTitleProperty?.SetValue(boxed, string.IsNullOrWhiteSpace(title) ? null : title);
        HoverTipDescriptionProperty?.SetValue(boxed, description ?? string.Empty);
        HoverTipIdProperty?.SetValue(boxed, $"undo-hud:{title}:{description}");
        return (HoverTip)boxed;
    }
}
