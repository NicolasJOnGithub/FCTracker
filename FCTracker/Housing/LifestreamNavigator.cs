namespace FCTracker.Housing;

using System;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.IPC;

/// <summary>
/// Lifestream travel that works whether or not you are already on a character in the target FC.
/// Extracted from AllFCsView so the Ready Now bid button and the All FCs status cell share one
/// implementation instead of two copies of the same task chain.
/// </summary>
public static class LifestreamNavigator
{
    /// <summary>Name of a character we can log in as to reach this FC.</summary>
    public static string? ResolveLoginName(FCData fc)
    {
        if (fc.MasterAvailable)
            return fc.MasterString;

        foreach (ulong cid in fc.MemberCIDs)
            if (Configuration.Instance.GatheredData.CharByCID.TryGetValue(cid, out CharData charData) &&
                !string.IsNullOrEmpty(charData.Name))
                return charData.Name;

        return null;
    }

    public static bool CanNavigate(FCData fc) =>
        fc.SourceData.ImportSourceConfig == null && fc.MemberCIDs.Count != 0;

    /// <summary>Travel to the FC's own estate.</summary>
    public static void GoToFCHouse(FCData fc)
    {
        if (!fc.HasHouse)
            return;

        FCData.HouseInfo house = fc.House!;

        Run(fc, () =>
                {
                    if (Player.Available && fc.MemberCIDs.Contains(Player.CID))
                        ECommonsIPC.Lifestream.TeleportToFC();
                    else
                        GoToAddress(fc, house.City, house.Ward, house.Plot, $"{fc.WorldName}-{fc.Id}");
                });
    }

    /// <summary>Travel to an arbitrary plot on the FC's world - used for the plot an FC bid on.</summary>
    public static void GoToPlot(FCData fc, FCData.HouseInfo.ResidentialAetheryteKind city, byte ward, byte plot, string tag) =>
        Run(fc, () => GoToAddress(fc, city, ward, plot, tag));

    /// <summary>
    /// Wards and plots are 0-based everywhere in FCTracker's own data and 1-based in
    /// Lifestream's address tuple, so the conversion lives here rather than at each call site.
    /// </summary>
    private static void GoToAddress(FCData fc, FCData.HouseInfo.ResidentialAetheryteKind city, byte ward, byte plot, string tag) =>
        ECommonsIPC.Lifestream.GoToHousingAddress((tag, (int)fc.HomeWorldId, (int)city, ward + 1, 0, plot + 1, -1, false, false, string.Empty));

    /// <summary>
    /// Run <paramref name="travel"/> now if we're already in game, otherwise log in as a member
    /// of the FC first and travel once the character is ready.
    /// </summary>
    private static void Run(FCData fc, Action travel)
    {
        if (Svc.ClientState.IsLoggedIn)
        {
            travel();
            return;
        }

        string? loginName = ResolveLoginName(fc);
        if (loginName == null)
            return;

        TaskManager taskManager = FCTrackerPlugin.Plugin.TaskManager;

        taskManager.Enqueue(() => ECommonsIPC.Lifestream.ChangeCharacter(loginName, fc.WorldName));
        taskManager.EnqueueDelay(100);
        taskManager.Enqueue(() => !ECommonsIPC.Lifestream.IsBusy());
        taskManager.EnqueueDelay(100);
        taskManager.Enqueue(() => Svc.ClientState.IsLoggedIn);
        taskManager.Enqueue(() => PlayerHelper.IsReady);
        taskManager.Enqueue(travel);
    }

    /// <summary>Log in as a character belonging to this FC, without travelling anywhere.</summary>
    public static void ChangeToFCCharacter(FCData fc)
    {
        string? loginName = ResolveLoginName(fc);
        if (loginName != null)
            ECommonsIPC.Lifestream.ChangeCharacter(loginName, fc.WorldName);
    }
}
