using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace RetainerSorter.Ui;

internal enum RowTone
{
    Normal,
    Dim,
    Warning,
}

/// <summary> One line of the preview: a retainer and what will happen to it, or a note. </summary>
internal sealed record PreviewRow(string Left, string Right, RowTone Tone);

/// <summary> One rule in the rule list. </summary>
internal sealed record RuleRow(int Index, string Text, bool Selected);

/// <summary> One kind of item in the deposit preview. Clicking it toggles whether the player keeps it. </summary>
internal sealed record DepositRow(uint ItemId, string Left, string Right, RowTone Tone);

internal static class RowColors
{
    // Colours from the game's own palette ("UIColor"), which follow the player's UI theme:
    // 8 is standard window text, 3 muted grey, 508 a soft red, 25 yellow, 7 the text outline.
    public static Vector4 For(RowTone tone) => tone switch
    {
        RowTone.Dim => ColorHelper.GetColor(3),
        RowTone.Warning => ColorHelper.GetColor(508),
        _ => ColorHelper.GetColor(8),
    };

    public static Vector4 Outline => ColorHelper.GetColor(7);
}

internal sealed class PreviewRowNode : ListItemNode<PreviewRow>, IListItemNode
{
    public static float ItemHeight => 20.0f;

    private const float RightWidth = 300.0f;

    private readonly TextNode leftNode;
    private readonly TextNode rightNode;

    public PreviewRowNode()
    {
        leftNode = new TextNode
        {
            FontSize = 13,
            AlignmentType = AlignmentType.Left,
            TextFlags = TextFlags.Ellipsis,
            TextOutlineColor = RowColors.Outline,
        };
        leftNode.AttachNode(this);

        rightNode = new TextNode
        {
            FontSize = 13,
            AlignmentType = AlignmentType.Right,
            TextOutlineColor = RowColors.Outline,
        };
        rightNode.AttachNode(this);

        // Purely informational: no hover highlight, no clicks.
        DisableInteractions();
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        Layout();
    }

    protected override void SetNodeData(PreviewRow itemData)
    {
        leftNode.String = itemData.Left;
        rightNode.String = itemData.Right;
        leftNode.TextColor = RowColors.For(itemData.Tone);
        rightNode.TextColor = RowColors.For(itemData.Tone);
        Layout();
    }

    private void Layout()
    {
        // Notes have nothing on the right and may use the whole line.
        var rightWidth = ItemData is { Right.Length: > 0 } ? RightWidth : 0.0f;
        leftNode.Position = new Vector2(4.0f, 1.0f);
        leftNode.Size = new Vector2(Width - rightWidth - 12.0f, Height - 2.0f);
        rightNode.Position = new Vector2(Width - rightWidth - 4.0f, 1.0f);
        rightNode.Size = new Vector2(rightWidth, Height - 2.0f);
    }
}

internal sealed class DepositRowNode : ListItemNode<DepositRow>, IListItemNode
{
    public static float ItemHeight => 22.0f;

    private const float RightWidth = 260.0f;

    private readonly TextNode leftNode;
    private readonly TextNode rightNode;

    public DepositRowNode()
    {
        leftNode = new TextNode
        {
            FontSize = 13,
            AlignmentType = AlignmentType.Left,
            TextFlags = TextFlags.Ellipsis,
            TextOutlineColor = RowColors.Outline,
        };
        leftNode.AttachNode(this);

        rightNode = new TextNode
        {
            FontSize = 13,
            AlignmentType = AlignmentType.Right,
            TextFlags = TextFlags.Ellipsis,
            TextOutlineColor = RowColors.Outline,
        };
        rightNode.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        leftNode.Position = new Vector2(6.0f, 2.0f);
        leftNode.Size = new Vector2(Width - RightWidth - 16.0f, Height - 4.0f);
        rightNode.Position = new Vector2(Width - RightWidth - 6.0f, 2.0f);
        rightNode.Size = new Vector2(RightWidth, Height - 4.0f);
    }

    protected override void SetNodeData(DepositRow itemData)
    {
        leftNode.String = itemData.Left;
        rightNode.String = itemData.Right;
        leftNode.TextColor = RowColors.For(itemData.Tone);
        rightNode.TextColor = RowColors.For(itemData.Tone);
    }
}

internal sealed class RuleRowNode : ListItemNode<RuleRow>, IListItemNode
{
    public static float ItemHeight => 22.0f;

    private readonly TextNode textNode;

    public RuleRowNode()
    {
        textNode = new TextNode
        {
            FontSize = 13,
            AlignmentType = AlignmentType.Left,
            TextFlags = TextFlags.Ellipsis,
            TextOutlineColor = RowColors.Outline,
        };
        textNode.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        textNode.Position = new Vector2(6.0f, 2.0f);
        textNode.Size = new Vector2(Width - 12.0f, Height - 4.0f);
    }

    protected override void SetNodeData(RuleRow itemData)
    {
        textNode.String = itemData.Text;

        // The list forgets its own highlight whenever a row's text changes, so the chosen rule is
        // also shown by colour.
        textNode.TextColor = itemData.Selected ? ColorHelper.GetColor(25) : RowColors.For(RowTone.Normal);
    }
}
