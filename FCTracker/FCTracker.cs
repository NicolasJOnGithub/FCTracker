namespace FCTracker;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Windows;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Utility;
using ECommons.Automation.NeoTaskManager;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Housing;
using Services;
using UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Callback = ECommons.Automation.Callback;

[UsedImplicitly]
public sealed class FCTrackerPlugin : IDalamudPlugin
{
    public static FCTrackerPlugin Plugin { get; private set; } = null!;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/fctrack";

    public readonly WindowSystem windowSystem = new("FCTracker");
    private         FCTrackerWindow MainWindow { get; init; } = null!;
    private         ConfigWindow    ConfigWindow { get; init; } = null!;
    public          IFCDataProvider DataProvider { get; init; } = null!;

    private readonly (string[], string, Action<string[]>)[] commands = null!;

    public TaskManager TaskManager { get; init; } = null!;

    /// <summary>Records FC housing lottery bids and resolves their outcome.</summary>
    public LotteryTracker LotteryTracker { get; private set; } = null!;

    public static ulong? LoggedInCID { get; set; }

    public int Version { get; init; }

    
    public readonly DateTime EntryDateTimeReference       = new(2026, 6, 1, 15, 0, 0);
    public const    int      ENTRY_PERIOD_DURATION_DAYS   = 5;
    public const    int      RESULTS_PERIOD_DURATION_DAYS = 4;
    public const    int      CYCLE_DURATION_DAYS          = ENTRY_PERIOD_DURATION_DAYS + RESULTS_PERIOD_DURATION_DAYS;

    public          DateTime EntryPeriodCurrentStartDate => DateTime.UtcNow.AddDays(-((DateTime.UtcNow - this.EntryDateTimeReference).TotalDays % CYCLE_DURATION_DAYS));
    public          DateTime EntryPeriodCurrentEndDate   => this.EntryPeriodCurrentStartDate.AddDays(ENTRY_PERIOD_DURATION_DAYS);
    public          bool     IsEntryPeriod               => DateTime.UtcNow < this.EntryPeriodCurrentEndDate;
    public          DateTime EntryPeriodNextStartDate    => this.EntryPeriodCurrentStartDate.AddDays(CYCLE_DURATION_DAYS);
    public          DateTime EntryPeriodNextEndDate      => this.EntryPeriodNextStartDate.AddDays(ENTRY_PERIOD_DURATION_DAYS);

    public const string ScrambleTag = "«FC»";

    public FCTrackerPlugin()
    {
        try
        {
            Plugin = this;

            ECommonsMain.Init(PluginInterface, this, Module.DalamudReflector, Module.ObjectFunctions);

            EzConfig.DefaultSerializationFactory = new FCTrackerSerializationFactory();
            Configuration.Instance               = EzConfig.Init<Configuration>();

            DirectoryInfo configDirectory = PluginInterface.ConfigDirectory;

            this.Version =
                ((PluginInterface.IsDev ? new Version(0, 0, 0, 291) :
                  PluginInterface.IsTesting ? PluginInterface.Manifest.TestingAssemblyVersion ?? PluginInterface.Manifest.AssemblyVersion : PluginInterface.Manifest.AssemblyVersion)!).Revision;

            if (!configDirectory.Exists)
                configDirectory.Create();
            
            this.DataProvider = new ConfigurationFCDataService();
            this.MainWindow = new FCTrackerWindow(this.DataProvider);
            this.ConfigWindow = new ConfigWindow();
            this.windowSystem.AddWindow(this.MainWindow);
            this.windowSystem.AddWindow(this.ConfigWindow);


            this.TaskManager = new TaskManager(new TaskManagerConfiguration
                                               {
                                                   AbortOnTimeout  = false,
                                                   TimeoutSilently = true,
                                                   TimeLimitMS     = 10_000,
                                                   ShowDebug       = true
                                               });

            this.LotteryTracker = new LotteryTracker();

            if (Svc.ClientState.IsLoggedIn)
                this.ClientStateOnLogin();

            this.commands =
            [
                (["config", "cfg"], "opens config window / modifies config", _ => this.ToggleConfigUi()),
            ];
            
            Svc.Commands.AddHandler("/fct", new CommandInfo(this.OnCommand));
            Svc.Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = string.Join("\n", this.commands.Select(tuple => $"/fctrack or /fct {string.Join(" / ", tuple.Item1)} -> {tuple.Item2}"))
            });

            PluginInterface.UiBuilder.Draw         += this.windowSystem.Draw;
            PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
            PluginInterface.UiBuilder.OpenMainUi   += this.ToggleMainUi;

            Svc.ClientState.Login  += this.ClientStateOnLogin;
            Svc.ClientState.Logout += this.ClientStateOnLogout;
            Svc.ClientState.TerritoryChanged += this.ClientStateOnTerritoryChanged;

            Configuration.Instance.RefreshImportedData();
        }
        catch (Exception e)
        {
            Svc.Log.Info($"Failed loading plugin\n{e}");
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw         -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= this.ToggleMainUi;
        Svc.ClientState.Login                  -= this.ClientStateOnLogin;
        Svc.ClientState.Logout                 -= this.ClientStateOnLogout;
        Svc.ClientState.TerritoryChanged       -= this.ClientStateOnTerritoryChanged;

        this.LotteryTracker?.Dispose();

        this.windowSystem.RemoveAllWindows();

        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();

        Svc.Commands.RemoveHandler(CommandName);
        Svc.Commands.RemoveHandler("/fct");
    }

    private void OnCommand(string command, string args)
    {
        Match match = RegexHelper.ArgumentParserRegex().Match(args.ToLower());
        List<string> matches = [];

        while (match.Success)
        {
            matches.Add(match.Groups[match.Groups[1].Length > 0 ? 1 : 0].Value);
            match = match.NextMatch();
        }

        string[] argsArray = matches.Count > 0 ? [.. matches] : [string.Empty];
        string check = argsArray[0];

        Svc.Log.Debug("command with: " + args);

        foreach ((string[] keywords, _, Action<string[]> action) in this.commands)
            if (keywords.Any(key => check.StartsWith(key)))
            {
                Svc.Log.Debug("Activating command: " + string.Join(" / ", keywords));
                action(argsArray);
                return;
            }

        switch (argsArray[0])
        {
            default:
                this.MainWindow.Toggle();
                break;
        }
    }

    private void ClientStateOnLogin()
    {
        Configuration.Instance.RefreshImportedData();
		LoggedInCID = Player.CID;
        this.GetFCInfo();
    }

    private void ClientStateOnLogout(int type, int code)
    {
        if (Configuration.Instance.GatheredData.CharByCID.TryGetValue(Player.CID, out CharData charData))
        {
            ulong? fcId = charData.FC;
            if (fcId.HasValue)
                if(Configuration.Instance.GatheredData.FCData.TryGetValue(fcId.Value, out FCData? fcData))
                    fcData.RecacheARData();

            Configuration.Instance.UpdateCurrentCharData();
        }
        LoggedInCID = null;
    }

    private unsafe void ClientStateOnTerritoryChanged(uint t)
    {
        this.TaskManager.Enqueue(() => Player.Available);
        this.TaskManager.Enqueue(() =>
                                 {
                                     HousingManager* housing = HousingManager.Instance();
                                     if (housing->IsInside())
                                     {
                                         HouseId houseId = housing->GetCurrentHouseId();
                                         if (!houseId.IsApartment)
                                         {
                                             ulong? fc = Configuration.Instance.GetFCIdForCID(Player.CID);
                                             if (fc.HasValue)
                                                 if (Configuration.Instance.GatheredData.FCData.TryGetValue(fc.Value, out FCData? fcData))
                                                     if (fcData.HasHouse)
                                                     {
                                                         FCData.HouseInfo fcHouse = fcData.House!;
                                                         if (houseId.WardIndex                                                                == fcHouse.Ward       &&
                                                             houseId.PlotIndex                                                                == fcHouse.Plot       &&
                                                             houseId.WorldId                                                                  == fcData.HomeWorldId &&
                                                             FCData.HouseInfo.GetResidentialAetheryteByTerritoryType(houseId.TerritoryTypeId) == fcHouse.City)
                                                         {
                                                             fcData.House!.Visited();
                                                             Configuration.Instance.Save();
                                                         }
                                                     }
                                         }
                                     }
                                 });
    }

    public unsafe void GetFCInfo()
    {
        this.TaskManager.Enqueue(() => PlayerHelper.IsReadyFull);
        this.TaskManager.Enqueue(() =>
                                 {
                                     if (Configuration.Instance.GlobalData.ExcludedChars.Contains(Player.CID))
                                     {
                                         this.TaskManager.Abort();
                                         return;
                                     }

                                     if (Player.Character->FreeCompanyTagString.Length <= 0)
                                     {
                                         Configuration.Instance.UpdateCurrentCharData();
                                         this.TaskManager.Abort();
                                     }
                                 }, "Character Test");
        this.TaskManager.Enqueue(() => AgentFreeCompany.Instance()->Hide());
        this.TaskManager.Enqueue(() => AgentFreeCompanyProfile.Instance()->Hide());
        this.TaskManager.Enqueue(() => AgentFreeCompany.Instance()->Show());
        this.TaskManager.Enqueue(() => AgentFreeCompany.Instance()->IsAddonShown());
        this.TaskManager.Enqueue(() => AgentFreeCompany.Instance()->IsAddonReady());
        this.TaskManager.Enqueue(() =>
                                 {
                                     if (GenericHelpers.TryGetAddonByName("FreeCompany", out AtkUnitBase* fcAddon) && fcAddon->IsReady())
                                     {
                                         Callback.Fire(fcAddon, true, 0, 5u);
                                         return true;
                                     }
                                     return false;
                                 }, "FC Status exec");
        this.TaskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("FreeCompanyStatus", out AtkUnitBase* fcAddon) && fcAddon->IsReady(), "FCStatus check");
        this.TaskManager.Enqueue(() =>
                                 {
                                     StringArrayData* arrayData = RaptureAtkModule.Instance()->GetStringArrayData(49);
                                     if (arrayData->Size > 4)
                                     {
                                         CStringPointer x        = arrayData->StringArray[6];
                                         SeString       seString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(x));
                                         string         text     = seString.GetText();
                                         return text.Length > 0;
                                     }
                                     return false;
                                 });
        this.TaskManager.Enqueue(() =>
                                 {
                                     if (GenericHelpers.TryGetAddonByName("FreeCompany", out AtkUnitBase* fcAddon) && fcAddon->IsReady())
                                     {
                                         Callback.Fire(fcAddon, true, 0, 1u);
                                         if (GenericHelpers.TryGetAddonByName("FreeCompanyStatus", out fcAddon) && fcAddon->IsReady())
                                         {
                                             Callback.Fire(fcAddon, true, -2);
                                             return true;
                                         }
                                     }
                                     return false;
                                 }, "FC Member exec");
        this.TaskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("FreeCompanyMember", out AtkUnitBase* fcAddon) && fcAddon->IsReady(),                                    "FCMember check");
        this.TaskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("FreeCompanyMember", out AtkUnitBase* fcAddon) && fcAddon->GetComponentListById(24)->GetItemCount() > 0, "FCMember check 2");

        this.TaskManager.Enqueue(() =>
                                 {
                                     if (GenericHelpers.TryGetAddonByName("FreeCompanyMember", out AtkUnitBase* fcAddon) && fcAddon->IsReady())
                                     {
                                         Callback.Fire(fcAddon, true, 3, 0u);
                                         return true;
                                     }
                                     return false;
                                 }, "FC Member Context exec");
        this.TaskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("ContextMenu", out AtkUnitBase* fcAddon) && fcAddon->IsReady(), "FCMember Context check");

        int foundationDate = -1;
        this.TaskManager.Enqueue(() => foundationDate = AgentFreeCompanyProfile.Instance()->FoundationDate, "FC Foundation Date Cache Grab");

        this.TaskManager.Enqueue(() =>
                                 {
                                     if (GenericHelpers.TryGetAddonByName("ContextMenu", out AtkUnitBase* fcAddon) && fcAddon->IsReady())
                                     {
                                         ReadOnlySeString seString = Svc.Data.GetExcelSheet<Addon>()[2807].Text;

                                         int entryCount = fcAddon->AtkValues[0].Int;
                                         for (int i = 0; i < entryCount; i++)
                                         {
                                             if(fcAddon->AtkValues[8 + i].String.ExtractText() == seString.ExtractText())
                                             {
                                                 Callback.Fire(fcAddon, true, 0, i, 0u);
                                                 return true;
                                             }
                                         }
                                     }
                                     return false;
                                 }, "FC Member Profile exec");
        this.TaskManager.Enqueue(() => GenericHelpers.TryGetAddonByName("FreeCompanyProfile", out AtkUnitBase* fcAddon) && fcAddon->IsReady(), "FCMember Profile check");
        this.TaskManager.Enqueue(() => AgentFreeCompanyProfile.Instance()->IsAddonShown());
        this.TaskManager.Enqueue(() => AgentFreeCompanyProfile.Instance()->IsAddonReady());
        this.TaskManager.Enqueue(() => foundationDate != AgentFreeCompanyProfile.Instance()->FoundationDate, "FC Foundation Date Check", new TaskManagerConfiguration(2000));

        this.TaskManager.Enqueue(() => Configuration.Instance.UpdateCurrentFCData());
        this.TaskManager.Enqueue(() => AgentFreeCompany.Instance()->Hide());
        this.TaskManager.Enqueue(() => AgentFreeCompanyProfile.Instance()->Hide());
    }

    public       void   ToggleConfigUi() => this.ConfigWindow.Toggle();
    public       void   ToggleMainUi()   => this.MainWindow.Toggle();
}
