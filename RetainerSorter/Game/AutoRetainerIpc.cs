using System;
using Dalamud.Plugin.Ipc;

namespace RetainerSorter.Game;

/// <summary>
/// Talks to the AutoRetainer plugin, if it is installed. Both plugins drive the retainer list, so
/// the sorter tells AutoRetainer to hold off while it works and waits whenever AutoRetainer is busy.
/// </summary>
internal sealed class AutoRetainerIpc
{
    private readonly ICallGateSubscriber<bool> getSuppressed = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
    private readonly ICallGateSubscriber<bool, object> setSuppressed = Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
    private readonly ICallGateSubscriber<bool> isBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");

    public bool Available
    {
        get
        {
            try
            {
                getSuppressed.InvokeFunc();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool Suppressed
    {
        get
        {
            try
            {
                return getSuppressed.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                setSuppressed.InvokeAction(value);
            }
            catch (Exception e)
            {
                Plugin.Log.Debug($"AutoRetainer did not take the request to {(value ? "hold off" : "carry on")}: {e.Message}");
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            try
            {
                return isBusy.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }
}
