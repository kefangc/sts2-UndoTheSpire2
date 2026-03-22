// 文件说明：实现 HUD 按钮的交互与展示。
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
    private bool _useHud2;
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

    public bool UseHud2
    {
        get => _useHud2;
        set
        {
            if (_useHud2 == value)
                return;

            _useHud2 = value;
            QueueRedraw();
        }
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
        if (_useHud2)
            DrawHud2(rect);
        else
            DrawHud1(rect);
    }

    private void DrawHud1(Rect2 rect)
    {
        DrawPanelShadow(rect, 9, _enabled ? 0.25f : 0.13f, 2f);

        Color border = _enabled
            ? (_pressed ? new Color(0.50f, 0.39f, 0.17f) : (_hovered ? new Color(0.84f, 0.66f, 0.28f) : new Color(0.67f, 0.51f, 0.23f)))
            : new Color(0.33f, 0.30f, 0.26f);
        DrawRoundedPanel(rect, border, 9);

        Rect2 inner = rect.Grow(-2.5f);
        Color fill = _enabled
            ? (_pressed ? new Color(0.10f, 0.14f, 0.17f) : (_hovered ? new Color(0.11f, 0.18f, 0.22f) : new Color(0.09f, 0.16f, 0.19f)))
            : new Color(0.07f, 0.11f, 0.13f);
        DrawRoundedPanel(inner, fill, 8);

        Color topGloss = _enabled ? new Color(1f, 0.96f, 0.82f, _hovered ? 0.20f : 0.11f) : new Color(1f, 1f, 1f, 0.04f);
        Color bottomShade = new Color(0f, 0f, 0f, _enabled ? 0.22f : 0.10f);
        DrawRect(new Rect2(inner.Position + new Vector2(6f, 2f), new Vector2(inner.Size.X - 12f, 1.5f)), topGloss, true);
        DrawRect(new Rect2(inner.Position + new Vector2(6f, inner.Size.Y - 3.5f), new Vector2(inner.Size.X - 12f, 1.2f)), bottomShade, true);

        Color glyph = _enabled
            ? (_hovered ? new Color(1f, 0.97f, 0.86f) : new Color(0.95f, 0.89f, 0.70f))
            : new Color(0.57f, 0.56f, 0.50f);
        DrawFlatArrowGlyph(inner, glyph, new Color(0f, 0f, 0f, _enabled ? 0.28f : 0.16f));
    }

    private void DrawHud2(Rect2 rect)
    {
        DrawPanelShadow(rect, 9, _enabled ? 0.33f : 0.16f, 3f);

        Color bronzeOuter = _enabled
            ? (_pressed ? new Color(0.39f, 0.30f, 0.20f) : (_hovered ? new Color(0.57f, 0.46f, 0.30f) : new Color(0.48f, 0.39f, 0.26f)))
            : new Color(0.27f, 0.24f, 0.20f);
        DrawRoundedPanel(rect, bronzeOuter, 9);

        Rect2 trim = rect.Grow(-1.7f);
        Color coldTrim = _enabled
            ? (_hovered ? new Color(0.28f, 0.35f, 0.38f) : new Color(0.24f, 0.30f, 0.33f))
            : new Color(0.18f, 0.21f, 0.23f);
        DrawRoundedPanel(trim, coldTrim, 8);

        Rect2 frame = trim.Grow(-1.8f);
        Color frameFill = _enabled
            ? (_pressed ? new Color(0.08f, 0.14f, 0.17f) : (_hovered ? new Color(0.10f, 0.18f, 0.21f) : new Color(0.09f, 0.16f, 0.19f)))
            : new Color(0.06f, 0.10f, 0.12f);
        DrawRoundedPanel(frame, frameFill, 7);

        Rect2 inner = frame.Grow(-2.2f);
        Color slab = _enabled
            ? (_pressed ? new Color(0.07f, 0.13f, 0.16f) : (_hovered ? new Color(0.10f, 0.20f, 0.24f) : new Color(0.08f, 0.17f, 0.21f)))
            : new Color(0.05f, 0.09f, 0.12f);
        DrawRoundedPanel(inner, slab, 6);

        DrawRect(new Rect2(inner.Position + new Vector2(4f, 1.5f), new Vector2(inner.Size.X - 8f, 1.4f)), new Color(0.82f, 0.90f, 0.93f, _enabled ? 0.16f : 0.05f), true);
        DrawRect(new Rect2(inner.Position + new Vector2(4f, inner.Size.Y - 3.1f), new Vector2(inner.Size.X - 8f, 1.5f)), new Color(0f, 0f, 0f, _enabled ? 0.28f : 0.12f), true);

        DrawRect(new Rect2(inner.Position + new Vector2(3.8f, 5.2f), new Vector2(inner.Size.X - 7.6f, 1.1f)), new Color(0.50f, 0.67f, 0.72f, _enabled ? 0.11f : 0.04f), true);
        DrawRect(new Rect2(inner.Position + new Vector2(3.8f, inner.Size.Y - 6.2f), new Vector2(inner.Size.X - 7.6f, 1.1f)), new Color(0.03f, 0.08f, 0.10f, _enabled ? 0.22f : 0.10f), true);

        Color rivet = _enabled ? new Color(0.66f, 0.58f, 0.41f, 0.90f) : new Color(0.43f, 0.42f, 0.39f, 0.45f);
        DrawCircle(frame.Position + new Vector2(6.8f, 6.8f), 1.25f, rivet);
        DrawCircle(new Vector2(frame.End.X - 6.8f, frame.Position.Y + 6.8f), 1.25f, rivet);
        DrawCircle(new Vector2(frame.Position.X + 6.8f, frame.End.Y - 6.8f), 1.25f, rivet);
        DrawCircle(frame.End - new Vector2(6.8f, 6.8f), 1.25f, rivet);

        Color notch = new Color(0.03f, 0.08f, 0.10f, _enabled ? 0.40f : 0.20f);
        DrawColoredPolygon(
        [
            inner.Position + new Vector2(1.8f, 1.8f),
            inner.Position + new Vector2(6.0f, 1.8f),
            inner.Position + new Vector2(1.8f, 6.0f)
        ], notch);
        DrawColoredPolygon(
        [
            new Vector2(inner.End.X - 1.8f, inner.Position.Y + 1.8f),
            new Vector2(inner.End.X - 6.0f, inner.Position.Y + 1.8f),
            new Vector2(inner.End.X - 1.8f, inner.Position.Y + 6.0f)
        ], notch);

        Color glyph = _enabled
            ? (_hovered ? new Color(0.92f, 0.97f, 0.99f) : new Color(0.82f, 0.93f, 0.96f))
            : new Color(0.45f, 0.54f, 0.58f);
        DrawFlatArrowGlyph(inner, glyph, new Color(0f, 0f, 0f, _enabled ? 0.30f : 0.16f));
    }

    private void DrawFlatArrowGlyph(Rect2 rect, Color color, Color shadow)
    {
        Vector2 center = rect.GetCenter() + new Vector2(0f, 0.5f);
        float radius = 9.0f;
        float stroke = 4.0f;
        const int segments = 32;

        float pathStart = _isUndo ? Mathf.Pi : 0f;
        float pathEnd = _isUndo ? Mathf.Pi * 2.5f : Mathf.Pi * 1.5f;

        void DrawPass(Vector2 offset, Color passColor, float passStroke)
        {
            Vector2 previous = center + offset + AngleToVector(pathStart) * radius;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = Mathf.Lerp(pathStart, pathEnd, t);
                Vector2 current = center + offset + AngleToVector(angle) * radius;
                DrawLine(previous, current, passColor, passStroke, true);
                previous = current;
            }

            Vector2 endPoint = center + offset + AngleToVector(pathEnd) * radius;
            Vector2 tangent = AngleToVector(pathEnd + Mathf.Pi / 2f);
            Vector2 normal = AngleToVector(pathEnd);

            Vector2 tip = endPoint + tangent * 5.5f;
            Vector2 baseCenter = endPoint - tangent * 2.5f;
            Vector2 wing1 = baseCenter + normal * 6.0f;
            Vector2 wing2 = baseCenter - normal * 6.0f;

            DrawColoredPolygon([tip, wing1, wing2], passColor);
        }

        DrawPass(Vector2.Down * 1.0f, shadow, stroke + 1f);
        DrawPass(Vector2.Zero, color, stroke);
    }

    private static Vector2 AngleToVector(float angle)
    {
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private void DrawPanelShadow(Rect2 rect, int radius, float alpha, float yOffset)
    {
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, alpha),
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius
        }, new Rect2(rect.Position + new Vector2(0f, yOffset), rect.Size));
    }

    private void DrawRoundedPanel(Rect2 rect, Color color, int radius)
    {
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius
        }, rect);
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
