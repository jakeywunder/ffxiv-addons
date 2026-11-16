using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KamiToolKit;
using RetainerSorter.Data;
using RetainerSorter.Game;
using RetainerSorter.Ui;

namespace RetainerSorter;

/// <summary>
/// Sorts items across all of a character's retainers by rules the player sets up. Opened with the
/// "Sort Retainers" button this adds to the retainer list at a summoning bell.
/// </summary>
public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] public static IDataManager DataManager { get; set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; set; } = null!;
    [PluginService] public static IClientState ClientState { get; set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] public static IFramework Framework { get; set; } = null!;
    [PluginService] public static IPluginLog Log { get; set; } = null!;

    private const string Command = "/rsort";
    private const long PassiveReadIntervalMs = 1000;

    public static Configuration Config { get; private set; } = null!;
    internal static ItemCatalog Catalog { get; private set; } = null!;
    internal static RetainerCache Cache { get; private set; } = null!;
    internal static WorldReader Reader { get; private set; } = null!;
    internal static Executor Executor { get; private set; } = null!;

    private SorterWindow? window;
    private RetainerListButtons? buttons;
    private bool kamiToolKitReady;
    private bool commandAdded;
    private bool updateHooked;
    private bool installerButtonsHooked;
    private bool retainerWasOpen;
    private long nextPassiveRead;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Normalize();

        Catalog = new ItemCatalog(DataManager.Excel);
        Cache = new RetainerCache(PluginInterface.GetPluginConfigDirectory());
        var transit = new TransitStore();
        Reader = new WorldReader(Catalog, Cache, transit);
        Executor = new Executor(Catalog, Cache, transit, Reader, new AutoRetainerIpc());

        await KamiToolKitLibrary.InitializeAsync(PluginInterface, "Retainer Sorter");
        kamiToolKitReady = true;

        window = new SorterWindow
        {
            InternalName = "RetainerSorter",
            Title = "Retainer Sorter",
            Subtitle = "one rule per kind of item",
            Size = SorterWindow.WindowSize,
        };

        buttons = new RetainerListButtons(() => window.Toggle(), () => window.OpenDeposit());
        await Framework.RunOnFrameworkThread(buttons.Enable);

        // The plugin installer's "Open" and settings buttons both lead to the one window.
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        installerButtonsHooked = true;

        Framework.Update += OnFrameworkUpdate;
        updateHooked = true;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Retainer Sorter window. \"/rsort stop\" stops a sort that is running.",
        });
        commandAdded = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (commandAdded)
            CommandManager.RemoveHandler(Command);

        if (updateHooked)
            Framework.Update -= OnFrameworkUpdate;

        if (installerButtonsHooked)
        {
            PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        }

        await Framework.RunOnFrameworkThread(() =>
        {
            Executor?.Dispose();
            Cache?.Save();
            buttons?.Dispose();
            window?.Dispose();
        });

        if (kamiToolKitReady)
            await KamiToolKitLibrary.DisposeAsync();
    }

    internal static void Print(string message) => ChatGui.Print($"[Retainer Sorter] {message}");

    /// <summary> Native windows must be opened from the game's main thread. </summary>
    private void OpenWindow() => _ = Framework.RunOnFrameworkThread(() => window?.Open());

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
            return;

        try
        {
            Executor.Tick();

            // Whenever a retainer's inventory happens to be open, remember what is in it. That keeps
            // the saved contents current even when the player rearranges things by hand.
            var now = Environment.TickCount64;
            if (!Executor.IsRunning && now >= nextPassiveRead)
            {
                nextPassiveRead = now + PassiveReadIntervalMs;
                var open = WorldReader.OpenRetainerId();
                if (open != 0)
                {
                    Cache.Set(open, Inventories.ReadRetainer());
                    retainerWasOpen = true;
                }
                else
                {
                    Cache.Save();

                    // The player just closed a retainer they may have rearranged by hand.
                    if (retainerWasOpen)
                    {
                        retainerWasOpen = false;
                        RetainerNotes.Refresh();
                    }
                }
            }

            window?.Tick();
        }
        catch (Exception e)
        {
            Log.Error(e, "Retainer Sorter update failed.");
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (Executor.IsRunning)
                Executor.RequestStop();
            else
                Print("Nothing is running.");

            return;
        }

        window?.Toggle();
    }
}
