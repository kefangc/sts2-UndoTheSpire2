using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private static void ClearTransientCardVisuals()
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            ClearNodeChildren(combatRoom.BackCombatVfxContainer);
            ClearNodeChildren(combatRoom.CombatVfxContainer);
            ClearNodeChildren(combatRoom.Ui.CardPreviewContainer);
            ClearNodeChildren(combatRoom.Ui.MessyCardPreviewContainer);
        }

        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi == null)
            return;

        ClearNodeChildren(globalUi.CardPreviewContainer);
        ClearNodeChildren(globalUi.MessyCardPreviewContainer);
        ClearNodeChildren(globalUi.GridCardPreviewContainer);
        ClearNodeChildren(globalUi.EventCardPreviewContainer);
        RemoveCardFlyVfxNodes(globalUi.TopBar?.TrailContainer);
    }

    private static void RemoveCardFlyVfxNodes(Node? root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
            return;

        foreach (Node child in root.GetChildren().Cast<Node>().ToList())
        {
            RemoveCardFlyVfxNodes(child);
            if (child is NCardTrailVfx orphanTrailVfx)
            {
                orphanTrailVfx.GetParent()?.RemoveChild(orphanTrailVfx);
                QueueFreeNodeSafelyOnce(orphanTrailVfx);
                continue;
            }

            if (child is NCard orphanCardNode)
            {
                orphanCardNode.GetParent()?.RemoveChild(orphanCardNode);
                QueueFreeNodeSafelyOnce(orphanCardNode);
                continue;
            }

            if (child is not NCardFlyVfx flyVfx)
                continue;

            NeutralizeCardFlyVfxCompletion(flyVfx);
            if (GetPrivateFieldValue<Node>(flyVfx, "_vfx") is { } trailVfx && GodotObject.IsInstanceValid(trailVfx))
            {
                trailVfx.GetParent()?.RemoveChild(trailVfx);
                QueueFreeNodeSafelyOnce(trailVfx);
            }

            if (GetPrivateFieldValue<NCard>(flyVfx, "_card") is { } cardNode && GodotObject.IsInstanceValid(cardNode))
            {
                cardNode.GetParent()?.RemoveChild(cardNode);
                QueueFreeNodeSafelyOnce(cardNode);
            }

            flyVfx.GetParent()?.RemoveChild(flyVfx);
            QueueFreeNodeSafelyOnce(flyVfx);
        }
    }

    private static void NeutralizeCardFlyVfxCompletion(NCardFlyVfx flyVfx)
    {
        try
        {
            object? completionSource = UndoReflectionUtil.FindProperty(flyVfx.GetType(), nameof(NCardFlyVfx.SwooshAwayCompletion))?.GetValue(flyVfx);
            UndoReflectionUtil.FindMethod(completionSource?.GetType(), "TrySetResult")?.Invoke(completionSource, []);
            UndoReflectionUtil.TrySetPropertyValue(flyVfx, nameof(NCardFlyVfx.SwooshAwayCompletion), null);
        }
        catch
        {
        }
    }

    private static void ClearSelectedHandCardsUi(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        foreach (NSelectedHandCardHolder selectedHolder in selectedContainer.Holders.ToList())
        {
            selectedHolder.SetClickable(false);
            selectedHolder.FocusMode = Control.FocusModeEnum.None;
            selectedHolder.MouseFilter = Control.MouseFilterEnum.Ignore;
            selectedHolder.Hitbox.SetEnabled(false);
            selectedHolder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
            ClearObjectTweenFields(selectedHolder);
            if (selectedHolder.CardNode != null)
                ClearObjectTweenFields(selectedHolder.CardNode);
            selectedHolder.GetParent()?.RemoveChild(selectedHolder);
            QueueFreeNodeSafelyOnce(selectedHolder);
        }
    }

    private static void ResetSelectedHandCardContainerState(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        ClearObjectTweenFields(selectedContainer);
        selectedContainer.Hand = hand;
        ulong id = selectedContainer.GetInstanceId();
        if (SelectedHandContainerDefaultPositions.TryGetValue(id, out Vector2 defaultPosition))
            selectedContainer.Position = defaultPosition;
        if (SelectedHandContainerDefaultScales.TryGetValue(id, out Vector2 defaultScale))
            selectedContainer.Scale = defaultScale;
        else
            selectedContainer.Scale = Vector2.One;
        selectedContainer.FocusMode = Control.FocusModeEnum.None;
        selectedContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
        ClearNodeChildren(selectedContainer);
        InvokePrivateMethod(selectedContainer, "RefreshHolderPositions");
        InvokePrivateMethod(hand, "UpdateSelectedCardContainer", 0);
    }
}
