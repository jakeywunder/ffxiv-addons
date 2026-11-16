using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using KamiToolKit.Nodes;

namespace RetainerSorter.Ui;

/// <summary>
/// Adds "Sort Retainers" and "Deposit Items" buttons to the game's retainer list window ("RetainerList").
///
/// The positions come from that window's layout file, ui/uld/RetainerList.uld: the footer's first
/// text row ends at x=564, leaving the right-hand end of that row free.
/// </summary>
internal sealed unsafe class RetainerListButtons : IDisposable
{
    private const string AddonName = "RetainerList";

    private static readonly Vector2 SortPosition = new(762.0f, 322.0f);
    private static readonly Vector2 DepositPosition = new(620.0f, 322.0f);
    private static readonly Vector2 ButtonSize = new(136.0f, 24.0f);

    private readonly AddonController controller;
    private readonly Action onSort;
    private readonly Action onDeposit;
    private TextButtonNode? sortButton;
    private TextButtonNode? depositButton;

    public RetainerListButtons(Action onSort, Action onDeposit)
    {
        this.onSort = onSort;
        this.onDeposit = onDeposit;
        controller = new AddonController
        {
            AddonName = AddonName,
            OnSetup = OnSetup,
            OnFinalize = OnFinalize,
        };
    }

    /// <summary> Must be called on the game's main thread. </summary>
    public void Enable() => controller.Enable();

    /// <summary> Must be called on the game's main thread. </summary>
    public void Dispose()
    {
        controller.Dispose();
        DisposeButtons();
    }

    private void OnSetup(AtkUnitBase* addon)
    {
        try
        {
            DisposeButtons();

            sortButton = new TextButtonNode
            {
                Position = SortPosition,
                Size = ButtonSize,
                String = "Sort Retainers",
                TextTooltip = "Sort items across your retainers by your own rules.",
                OnClick = onSort,
            };
            sortButton.AttachNode(addon);

            depositButton = new TextButtonNode
            {
                Position = DepositPosition,
                Size = ButtonSize,
                String = "Deposit Items",
                TextTooltip = "Put items from your inventory with the retainers that already hold them. Shows what would move first.",
                OnClick = onDeposit,
            };
            depositButton.AttachNode(addon);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not add the Retainer Sorter buttons to the retainer list.");
        }
    }

    private void OnFinalize(AtkUnitBase* addon) => DisposeButtons();

    private void DisposeButtons()
    {
        sortButton?.Dispose();
        sortButton = null;
        depositButton?.Dispose();
        depositButton = null;
    }
}
