using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HuntingLogPlus.Game;
using KamiToolKit.Classes;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace HuntingLogPlus.Ui;

/// <summary>
/// One row of the "All remaining" list, laid out like the game's own Hunting Log rows:
/// icon, name, kill count and progress bar, with the location and distance underneath.
/// </summary>
internal sealed class TargetRowNode : ListItemNode<TargetRow>, IListItemNode
{
    public static float ItemHeight => 48.0f;

    private const string BarTexture = "ui/uld/MonsterNoteBook.tex";
    private const float IconSize = 44.0f;
    private const float TextLeft = 54.0f;
    private const float ClassIconSize = 22.0f;
    private const float DimmedAlpha = 0.55f;

    private readonly IconImageNode iconNode;
    private readonly IconImageNode classIconNode;
    private readonly TextNode nameNode;
    private readonly TextNode progressNode;
    private readonly SimpleNineGridNode barBackgroundNode;
    private readonly SimpleNineGridNode barFillNode;
    private readonly TextNode locationNode;
    private readonly TextNode distanceNode;

    private float barWidth;

    public TargetRowNode()
    {
        iconNode = new IconImageNode
        {
            FitTexture = true,
        };
        iconNode.AttachNode(this);

        nameNode = new TextNode
        {
            FontSize = 14,
            AlignmentType = AlignmentType.Left,
            TextFlags = TextFlags.Ellipsis,
            TextColor = ColorHelper.GetColor(2),
        };
        nameNode.AttachNode(this);

        progressNode = new TextNode
        {
            FontSize = 14,
            AlignmentType = AlignmentType.Right,
            TextColor = ColorHelper.GetColor(2),
        };
        progressNode.AttachNode(this);

        classIconNode = new IconImageNode
        {
            FitTexture = true,
        };
        classIconNode.AttachNode(this);

        barBackgroundNode = new SimpleNineGridNode
        {
            TexturePath = BarTexture,
            TextureCoordinates = new Vector2(2.0f, 2.0f),
            TextureSize = new Vector2(32.0f, 8.0f),
            LeftOffset = 2,
            RightOffset = 2,
        };
        barBackgroundNode.AttachNode(this);

        barFillNode = new SimpleNineGridNode
        {
            TexturePath = BarTexture,
            TextureCoordinates = new Vector2(2.0f, 12.0f),
            TextureSize = new Vector2(32.0f, 8.0f),
            LeftOffset = 2,
            RightOffset = 2,
            IsVisible = false,
        };
        barFillNode.AttachNode(this);

        locationNode = new TextNode
        {
            FontSize = 12,
            AlignmentType = AlignmentType.Left,
            TextFlags = TextFlags.Ellipsis,
            TextColor = ColorHelper.GetColor(3),
        };
        locationNode.AttachNode(this);

        distanceNode = new TextNode
        {
            FontSize = 12,
            AlignmentType = AlignmentType.Right,
            TextColor = ColorHelper.GetColor(3),
        };
        distanceNode.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        iconNode.Size = new Vector2(IconSize, IconSize);
        iconNode.Position = new Vector2(4.0f, 2.0f);

        classIconNode.Size = new Vector2(ClassIconSize, ClassIconSize);
        classIconNode.Position = new Vector2(Width - ClassIconSize - 4.0f, 1.0f);

        progressNode.Size = new Vector2(56.0f, 20.0f);
        progressNode.Position = new Vector2(classIconNode.X - progressNode.Width - 4.0f, 3.0f);

        nameNode.Size = new Vector2(progressNode.X - TextLeft - 4.0f, 20.0f);
        nameNode.Position = new Vector2(TextLeft, 3.0f);

        barWidth = Width - TextLeft - 6.0f;
        barBackgroundNode.Size = new Vector2(barWidth, 6.0f);
        barBackgroundNode.Position = new Vector2(TextLeft, 24.0f);
        barFillNode.Position = barBackgroundNode.Position;

        distanceNode.Size = new Vector2(70.0f, 16.0f);
        distanceNode.Position = new Vector2(Width - distanceNode.Width - 6.0f, 31.0f);

        locationNode.Size = new Vector2(distanceNode.X - TextLeft - 4.0f, 16.0f);
        locationNode.Position = new Vector2(TextLeft, 31.0f);

        if (ItemData is { } row)
            Refresh(row);
    }

    protected override void SetNodeData(TargetRow itemData)
    {
        iconNode.IconId = itemData.Monster.Icon;
        classIconNode.IconId = ClassIcon(itemData.Job);
        nameNode.String = itemData.Monster.DisplayName;
        Refresh(itemData);
    }

    /// <summary> Called while the list is visible so kill counts and distances stay current. </summary>
    public override void Update()
    {
        if (ItemData is { } row)
            Refresh(row);
    }

    private void Refresh(TargetRow row)
    {
        progressNode.String = $"{row.Killed}/{row.Monster.Count}";

        var fraction = row.Monster.Count == 0 ? 0.0f : (float)row.Killed / row.Monster.Count;
        barFillNode.IsVisible = fraction > 0.0f;
        barFillNode.Size = new Vector2(barWidth * fraction, 6.0f);

        locationNode.String = row.Location.Label;
        distanceNode.String = row.HasDistance ? $"{row.Distance:F0}y" : string.Empty;

        // Kills only count for the current class and the Grand Company; fade the rest.
        Alpha = row.Creditable ? 1.0f : DimmedAlpha;
    }

    /// <summary> Class icons are 62000 + class id; Grand Company crests are 60567-60569. </summary>
    private static uint ClassIcon(uint job) => job switch
    {
        10001 => 60567,
        10002 => 60568,
        10003 => 60569,
        _ => 62000 + job,
    };
}
