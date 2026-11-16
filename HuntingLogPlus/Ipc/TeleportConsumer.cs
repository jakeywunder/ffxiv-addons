using System;
using Dalamud.Plugin.Ipc;

namespace HuntingLogPlus.Ipc;

/// <summary> Talks to the Teleporter plugin, if it is installed. From Hunty. </summary>
internal sealed class TeleportConsumer
{
    private bool available;
    private long lastCheck;

    private ICallGateSubscriber<bool> isInitialized = null!;
    private ICallGateSubscriber<uint, byte, bool> teleport = null!;

    public TeleportConsumer()
    {
        try
        {
            isInitialized = Plugin.PluginInterface.GetIpcSubscriber<bool>("Teleport.ChatMessage");
            teleport = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport");
        }
        catch (Exception e)
        {
            Plugin.Log.Debug($"Failed to subscribe to Teleport\nReason: {e}");
        }
    }

    public bool IsAvailable
    {
        get
        {
            if (lastCheck + 5000 > Environment.TickCount64)
                return available;

            try
            {
                lastCheck = Environment.TickCount64;

                isInitialized.InvokeFunc();
                available = true;
            }
            catch
            {
                available = false;
            }

            return available;
        }
    }

    public bool UseTeleport(uint aetheryteId)
    {
        try
        {
            return teleport.InvokeFunc(aetheryteId, 0);
        }
        catch
        {
            Plugin.ChatGui.PrintError("Teleporter plugin is not responding.");
            return false;
        }
    }
}
