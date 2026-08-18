namespace FCTracker.Windows;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.Interop;
using FCTracker.UI;
using NightmareUI.Censoring;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public class ConfigWindow : Window, IDisposable
{
    public ConfigWindow() : base("FC Tracker — Settings###FCTrackerConfigWindow",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.Size = new Vector2(520, 420);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() => GC.SuppressFinalize(this);

    public override void Draw()
    {
        using (FCTrackerTheme.Push())
        {
            DrawHeader();

            using (ImRaii.ChildDisposable body = ImRaii.Child("##ConfigBody", new Vector2(0, 68), false))
            {
                if (!body.Success)
                    return;


                ImGui.SetCursorPos(new Vector2(14, 12));
                /*
                FCTrackerWidgets.IconLabel(FCTrackerTheme.TextSecondary, FontAwesomeIcon.Cogs, "No settings yet — coming soon.");
                ImGui.SetCursorPosX(14);*/

                if(FCTrackerWidgets.Checkbox("Scramble Names", ref Censor.Config.Enabled))
                    Configuration.Instance.Save();

                GlobalData globalData = Configuration.Instance.GlobalData;

                ImGui.SetCursorPosX(14);
                if (FCTrackerWidgets.Checkbox("Track Inventories if Allagan Tools is not available", ref globalData.GatherDataSelf))
                    Configuration.Instance.Save();

                ImGui.SetCursorPosX(14);
                if (FCTrackerWidgets.Checkbox("Bid check: dry run", ref globalData.LotteryCheckDryRun))
                    Configuration.Instance.Save();

                if (ImGui.IsItemHovered())
                    FCTrackerWidgets.Tooltip(
                        "While on, checking bids reports what it would confirm and confirms nothing.\n\n" +
                        "Turn it off to have losing bids' refunds accepted automatically. A won plot is\n" +
                        "never claimed either way - that spends the FC's gil, so it stays your call.");
            }
            DrawImportData();
            DrawExcludedCharData();
        }
    }

    private static void DrawHeader()
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, FCTrackerTheme.BackgroundHeader))
        {
            using ImRaii.ChildDisposable headerChild = ImRaii.Child("##ConfigHeader", new Vector2(0, 42), true, ImGuiWindowFlags.NoScrollbar);
            if (!headerChild.Success) 
                return;

            ImGui.SetCursorPos(new Vector2(14, 11));

            FCTrackerWidgets.Icon(FCTrackerTheme.AccentBlue, FontAwesomeIcon.Cog);

            ImGui.SameLine();
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextBright, "Settings");

            ImGui.SameLine();
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextSecondary, "  Configure FC Tracker");
        }
        ImGui.Spacing();
    }

    private static void DrawImportData()
    {
        using ImRaii.ChildDisposable body = ImRaii.Child("##ConfigBody-ImportData", new Vector2(0, 84 + 28 * Configuration.Instance.DataImportConfig.Count), false);
        if (!body.Success)
            return;

        ImGui.SetCursorPos(new Vector2(14, 8));
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextBright, "Import other accounts data");

        ImGui.Spacing();

        for (int index = 0; index < Configuration.Instance.DataImportConfig.Count; index++)
        {
            ImGui.SetCursorPosX(10);

            DataImportConfig importConfig = Configuration.Instance.DataImportConfig[index];

            bool enabled = importConfig.Enabled;
            if (FCTrackerWidgets.Checkbox($"###DataImport_{index}", ref enabled))
            {
                importConfig.Enabled = enabled;
                Configuration.Instance.RefreshImportedData();
                Configuration.Instance.Save();
            }

            ImGui.SameLine();
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextPrimary, $"{index + 1}: {Censor.Hide(importConfig.Path)}");
            ImGui.SameLine();
            if (FCTrackerWidgets.IconButton(FontAwesomeIcon.TrashAlt, $"DataDelete_{index}", FCTrackerTheme.AccentRed with { W = 0.15f }, FCTrackerTheme.AccentRed))
            {
                Configuration.Instance.DataImportConfig.RemoveAt(index);
                index--;

                Configuration.Instance.RefreshImportedData();
                Configuration.Instance.Save();
            }
        }

        ImGui.SetCursorPosX(14);
        if (FCTrackerWidgets.IconButton(FontAwesomeIcon.File, "DataImport", FCTrackerTheme.AccentBlueDim, FCTrackerTheme.AccentBlue))
            OpenFileDialog.SelectFile(NewImportData, title: "Select FC Tracker Data File", fileTypes: [("Json", ["json"])]);
        ImGui.SameLine();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextPrimary, "Add new data file");

        ImGui.SetCursorPosX(14);

        if (FCTrackerWidgets.IconButton(FontAwesomeIcon.SyncAlt, "DataRefresh", FCTrackerTheme.AccentBlueDim, FCTrackerTheme.AccentBlue))
            Configuration.Instance.RefreshImportedData();
        ImGui.SameLine();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextPrimary, "Refresh Data");
    }

    private static void NewImportData(OpenFileName ofn)
    {
        try
        {
            Configuration.Instance.DataImportConfig.Add(new DataImportConfig
                                                        {
                                                            Path    = ofn.file,
                                                            Enabled = false
                                                        });
            Configuration.Instance.Save();
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, $"Error occurred while loading data");
        }
    }

    private static void DrawExcludedCharData()
    {
        using ImRaii.ChildDisposable body = ImRaii.Child("##ConfigBody-ExcludedCharData", Vector2.Zero, false);
        if (!body.Success)
            return;

        ImGui.SetCursorPos(new Vector2(14, 8));
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextBright, "Excluded Characters");
        ImGui.Spacing();

        ImGui.SetCursorPosX(14);
        using (ImRaii.DisabledDisposable _ = 
            ImRaii.Disabled(FCTrackerPlugin.LoggedInCID == null || Configuration.Instance.GlobalData.ExcludedChars.Contains(FCTrackerPlugin.LoggedInCID.Value)))
        {
            if (FCTrackerWidgets.IconButton(FontAwesomeIcon.Plus, "ExcludeCharacter", FCTrackerTheme.AccentBlueDim, FCTrackerTheme.AccentBlue))
            {
                if (FCTrackerPlugin.LoggedInCID != null)
                {
                    IEnumerable<FCData> fcDatas = Configuration.Instance.GatheredData.FCData.Values.ToList();
                    foreach (FCData fcData in fcDatas)
                    {
                        fcData.MemberCIDs.Remove(FCTrackerPlugin.LoggedInCID.Value);
                        if(fcData.MemberCIDs.Count == 0)
                            Configuration.Instance.GatheredData.FCData.Remove(fcData.Id);
                    }

                    Configuration.Instance.GlobalData.ExcludedChars.Add(FCTrackerPlugin.LoggedInCID.Value);
                }
            }
        }

        for (int index = 0; index < Configuration.Instance.GlobalData.ExcludedChars.Count; index++)
        {
            ulong excludedChar = Configuration.Instance.GlobalData.ExcludedChars[index];

            ImGui.SetCursorPosX(10);

            if (FCTrackerWidgets.IconButton(FontAwesomeIcon.TrashAlt, $"CharDelete_{index}", FCTrackerTheme.AccentRed with { W = 0.15f }, FCTrackerTheme.AccentRed))
            {
                Configuration.Instance.GlobalData.ExcludedChars.RemoveAt(index);
                index--;

                Configuration.Instance.RefreshImportedData();
                Configuration.Instance.Save();
                continue;
            }

            ImGui.SameLine();
            string charName = Configuration.Instance.AllCharData.TryGetValue(excludedChar, out CharData ch) ? $"{ch.Name}@{ch.WorldName}" : excludedChar.ToString();
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextPrimary, $"{index + 1}: {Censor.Hide(charName)}");
        }
    }
}
