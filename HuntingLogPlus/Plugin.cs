using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HuntingLogPlus.Data;
using HuntingLogPlus.Game;
using HuntingLogPlus.Ipc;
using HuntingLogPlus.Ui;
using KamiToolKit;

namespace HuntingLogPlus;

/// <summary>
/// Map flags, sorting and a full to-do list, built into the game's own Hunting Log window.
/// </summary>
public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] public static IDataManager DataManager { get; set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; set; } = null!;
    [PluginService] public static IClientState ClientState { get; set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] public static IGameGui GameGui { get; set; } = null!;
    [PluginService] public static IFramework Framework { get; set; } = null!;
    [PluginService] public static IPluginLog Log { get; set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    [PluginService] public static IAddonEventManager AddonEventManager { get; set; } = null!;
    [PluginService] public static ISeStringEvaluator Evaluator { get; set; } = null!;
    [PluginService] public static IKeyState KeyState { get; set; } = null!;

    private const string Command = "/hlplus";

    public static Configuration Config { get; private set; } = null!;
    internal static TeleportConsumer Teleport { get; private set; } = null!;

    private HuntingLogController? controller;
    private AutoAdvance? autoAdvance;
    private bool kamiToolKitReady;
    private bool commandAdded;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var path = Path.Combine(PluginInterface.AssemblyLocation.Directory!.FullName, "monsters.json");
        var data = HuntData.Load(path);

        await KamiToolKitLibrary.InitializeAsync(PluginInterface, "Hunting Log Plus");
        kamiToolKitReady = true;

        Teleport = new TeleportConsumer();
        autoAdvance = new AutoAdvance(data);
        controller = new HuntingLogController(data);
        await Framework.RunOnFrameworkThread(controller.Enable);

        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Hunting Log Plus lives in the Hunting Log window. \"/hlplus debug\" toggles click logging for troubleshooting.",
        });
        commandAdded = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (commandAdded)
            CommandManager.RemoveHandler(Command);

        ClientState.Login -= OnLogin;
        Framework.Update -= OnFrameworkUpdate;

        if (controller != null)
            await Framework.RunOnFrameworkThread(controller.Dispose);

        if (kamiToolKitReady)
            await KamiToolKitLibrary.DisposeAsync();
    }

    private void OnFrameworkUpdate(IFramework framework) => autoAdvance?.Tick();

    private void OnLogin() => autoAdvance?.OnLogin();

    private static void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().ToLowerInvariant() == "debug")
        {
            Config.DebugLog = !Config.DebugLog;
            Config.Save();
            HuntActions.Print($"Click logging is now {(Config.DebugLog ? "on. Events are written to the Dalamud log (/xllog)." : "off.")}");
            return;
        }

        HuntActions.Print("Open the Hunting Log to use it: click a monster's icon to flag it, or tick \"All remaining\" for the full list.");
    }
}
