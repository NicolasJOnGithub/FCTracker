namespace FCTracker.UI.Views;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using ECommons.IPC;
using Housing;
using NightmareUI.Censoring;
using Services;

public class AllFCsView : IFCView
{
    public string Id => "all";

    private static readonly Dictionary<string, string> HeaderTooltips = new()
    {
        ["Free Company"] = "House bidding requires your FC to have GC rank 6\n\nClick to log into a char belonging to the FC",
        ["Members"]      = "House bidding requires 4 members",
        ["Founded"]      = "House bidding requires 30 days to have passed since joining the FC\nFor simplicity it is assumed you joined the FC by creating it",
        ["FC Points"]    = "FC points are required for fuel and the submarine licenses\n99.900 are required for one stack of fuel\n160.000 are required for 16 Dive credits for all 4 submarines.",
        ["Status"]       = "The state of the house of the FC.\nClick to walk to the house if available",
        ["Demolition"]   = "Houses are demolished if not visited in 45 days"
    };

    public (string Title, string Subtitle) GetHeaderInfo(FCViewContext ctx)
    {
        IReadOnlyList<FCData> fcs = ctx.GetFilteredFCs();
        int worldCount = fcs.Select(fc => fc.WorldName).Distinct().Count();
        return ("All Free Companies", $"{fcs.Count} FCs across {worldCount} worlds");
    }

    public void Draw(FCViewContext ctx)
    {
        bool all = !ctx.Sidebar.SelectionActive && ctx.SearchText.IsNullOrEmpty();

        IReadOnlyList<FCData> fcs = all ? ctx.Data.GetAllFCs() : ctx.GetFilteredFCs();

        FCTrackerLayout.DrawSummaryStrip(
            left: [
                ("Total:", ctx.Data.GetTotalCount(), FCTrackerTheme.AccentBlue),
                ("Ready:", ctx.Data.GetReadyCount(), FCTrackerTheme.AccentGreen),
                ("Soon:", ctx.Data.GetPending7DayCount(), FCTrackerTheme.AccentYellow),
                ("Pending:", ctx.Data.GetPending30DayCount(), FCTrackerTheme.AccentOrange)
            ],
            right: [
                ("Salvage:", fcs.Sum(fc => fc.MemberCIDsResolved!.Sum(ch => ch.InventoryData.GetSalvageCount)), FCTrackerTheme.AccentYellow, () =>
                                                                                                                                             {
                                                                                                                                                 IEnumerable<int[]> counts = fcs.SelectMany(fc => fc.MemberCIDsResolved!.Select(ch => ch.InventoryData.GetSalvageCounts));

                                                                                                                                                 int[] totalCounts = new int[InventoryTrackingData.SalvageItems.Length];

                                                                                                                                                 foreach (int[] count in counts)
                                                                                                                                                     for (int i = 0; i < count.Length; i++)
                                                                                                                                                         totalCounts[i] += count[i];
                                                                                                                                                 return InventoryTrackingData.GetSalvageText(totalCounts);
                                                                                                                                             }),
                ("Repair:", all ? Configuration.ARData.RepairCount : fcs.Sum(fcd => fcd.AutoRetainerData?.RepairCount ?? 0), FCTrackerTheme.AccentPurple, null),
                ("Fuel:",   all ? Configuration.ARData.FuelCount   : fcs.Sum(fcd => fcd.AutoRetainerData?.FuelCount   ?? 0), FCTrackerTheme.AccentRed, null)
            ]);

        DrawFCTable(ctx);
    }

    private static void DrawFCTable(FCViewContext ctx)
    {
        List<FCData> fcs = ctx.GetFilteredFCs().OrderBy(fc => fc.WorldName).ThenBy(fc => fc.FCName).ToList();

        using var tableChild = ImRaii.Child("##FCTableArea", Vector2.Zero, false);
        if (!tableChild.Success) return;

        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY        |
                                      ImGuiTableFlags.PadOuterX      |
                                      ImGuiTableFlags.SizingFixedFit |
                                      ImGuiTableFlags.Resizable;

        using ImRaii.TableDisposable table = ImRaii.Table("##FCTable", 12, flags);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthFixed, 260);
        ImGui.TableSetupColumn("Master",       ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Members",      ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Founded",      ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("FC Points",    ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Actions",      ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Fuel",         ImGuiTableColumnFlags.WidthFixed, 20);
        ImGui.TableSetupColumn("Repair",       ImGuiTableColumnFlags.WidthFixed, 20);
        ImGui.TableSetupColumn("Salvage",      ImGuiTableColumnFlags.WidthFixed, 20);
        ImGui.TableSetupColumn("Status",       ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Demolition",   ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("##Spacer",     ImGuiTableColumnFlags.WidthStretch);

        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, FCTrackerTheme.BackgroundHeader))
        using (ImRaii.PushColor(ImGuiCol.Text, FCTrackerTheme.TextSecondary))
            FCTrackerWidgets.TableHeadersRowWithTooltips(HeaderTooltips);

        string? currentWorld = null;

        foreach (FCData fc in fcs)
        {
            if (fc.WorldName != currentWorld)
            {
                currentWorld = fc.WorldName;
                DrawWorldGroupHeader(ctx, fc);
            }

            DrawFCRow(fc);
        }
    }

    private static void DrawWorldGroupHeader(FCViewContext ctx, FCData fc)
    {
        int worldCount = ctx.Data.GetFCCountForWorld(fc.WorldName);

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(FCTrackerTheme.BackgroundSidebar));

        ImGui.TableNextColumn();
        FCTrackerWidgets.IconLabel(FCTrackerTheme.TextPrimary, FCTrackerTheme.GetRegionIcon(FCTrackerTheme.RegionString(fc.World)), Censor.World(fc.WorldName));

        if (!string.IsNullOrEmpty(fc.Datacenter))
        {
            ImGui.SameLine();
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, $"·  {fc.Datacenter}");
        }

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, $"{worldCount}");

        ImGui.TableNextColumn();
    }

    private static void DrawFCRow(FCData fc)
    {
        ImGui.TableNextRow();

        if(fc.LoggedIn)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, FCTrackerTheme.RowHighlightColor);

        if (fc.GetStatusCategory() == HousingStatusCategory.Ready)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(FCTrackerTheme.AccentGreenDim));

        ImGui.TableNextColumn();
        DrawFCNameCell(fc);

        ImGui.TableNextColumn();

        bool masterSelectable = fc.SourceData.ImportSourceConfig == null && fc.MasterAvailable;

        if (masterSelectable)
        {
            ImGui.Selectable("##FCMasterCell" + fc.Id);
            ImGui.SetItemAllowOverlap();
            ImGui.SameLine(0, 0);
        }

        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextSecondary,  Censor.Character(fc.MasterString));

        if (masterSelectable && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            ECommonsIPC.Lifestream.ChangeCharacter(fc.MasterString, fc.WorldName);
        
        ImGui.TableNextColumn();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.GetFCMemberColor(fc.TotalMembers), fc.TotalMembers.ToString());

        if ((fc.MemberCIDs.Count > 0 || fc.MemberData.Count > 0) && ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(fc.MembersString(true));

        ImGui.TableNextColumn();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, fc.FoundingDate == default ? "—" : $"{fc.TimeSinceFounded:%d}d ago");
        if(fc.FoundingDate != default && ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(fc.FoundingDate.ToString(CultureInfo.CurrentCulture));

        ImGui.TableNextColumn();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.GetFCPointColor(fc.FCPoints), fc.FCPoints.ToString("N0"));

        ImGui.TableNextColumn();
        DrawActionCell(fc);

        ImGui.TableNextColumn();
        DrawFuelAndRepairCells(fc);

        ImGui.TableNextColumn();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentYellow, fc.MemberCIDsResolved?.Sum(ch => ch.InventoryData.GetSalvageCount).ToString("##,#0") ?? "—");
        if(ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(fc.MemberCIDsResolved?.Select(ch => $"{Censor.Character(ch.Name)}:\n\t{InventoryTrackingData.GetSalvageText(ch.InventoryData.GetSalvageCounts)}").Join("\n") ?? "No salvage data");

        ImGui.TableNextColumn();
        DrawStatusCell(fc);

        ImGui.TableNextColumn();

        DrawDemolitionCell(fc);
        ImGui.TableNextColumn();


        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        using (ImRaii.PushColor(ImGuiCol.Button, FCTrackerTheme.AccentRed with { W = 0.15f.Scale() }))
        using (ImRaii.PushColor(ImGuiCol.Text, FCTrackerTheme.AccentRed))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{FontAwesomeIcon.TrashAlt.ToIconString()}###deleteData{fc.Id}", new Vector2(28f.Scale(), 0)))
            {
                if (fc.MemberCIDsResolved != null)
                    foreach (CharData charData in fc.MemberCIDsResolved)
                        Configuration.Instance.GatheredData.CharByCID.Remove(charData.CID);

                Configuration.Instance.GatheredData.FCData.Remove(fc.Id);
                Configuration.Instance.Save();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            FCTrackerWidgets.Tooltip("Clear fc data\nHold Ctrl to enable");
    }

    private static void DrawFuelAndRepairCells(FCData fc)
    {
        if (fc.AutoRetainerData.HasValue)
        {
            ARData data = fc.AutoRetainerData.Value;

            FCTrackerWidgets.ColoredText(FCTrackerTheme.GetPlayerFuelColor(data.FuelCount), data.FuelCount.ToString("##,#0"));

            ImGui.TableNextColumn();

            FCTrackerWidgets.ColoredText(FCTrackerTheme.GetPlayerRepairColor(data.RepairCount), data.RepairCount.ToString("##,#0"));
        } else
        {
            ImGui.TableNextColumn();
        }
    }

    private static void DrawFCNameCell(FCData fc)
    {
        bool selectable = fc.SourceData.ImportSourceConfig == null && fc.MemberCIDs.Count != 0;

        if (selectable)
        {
            ImGui.Selectable("##FCNameCell" + fc.Id);
            ImGui.SetItemAllowOverlap();
            ImGui.SameLine(0, 0);
        }

        FCTrackerWidgets.ColoredText(FCTrackerTheme.GetRankColor(fc.Rank), $"{fc.Rank}");

        ImGui.SameLine(0, 8);
        FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentBlue, Censor.Hide(fc.Tag, FCTrackerPlugin.ScrambleTag));

        ImGui.SameLine(0, 6);
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextPrimary, Censor.Character(fc.FCName));

        if (selectable && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            LifestreamNavigator.ChangeToFCCharacter(fc);

        if(fc.MemberCIDs.Count > 0 && ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(fc.MembersString(false));
    }

    private static void DrawActionCell(FCData fc)
    {
        if (fc.ActionsActive.Length > 0)
        {
            FCTrackerWidgets.ColoredText(FCTrackerTheme.GetActionColor(fc.ActionsActive.Length), $"{fc.ActionsActive.Length} active");
            ImGui.SameLine(0, 6);
        }

        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextSecondary, $"{fc.ActionsAvailable.Length} available");

        if((fc.ActionsActive.Length > 0 || fc.ActionsAvailable.Length > 0) && ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(
                                     (fc.ActionsActive.Length > 0 ?
                                         $"Active Actions:\n\t{string.Join("\n\t", fc.ActionsActive.Select(ad => $@"{ad.Action?.Name}: {ad.ExpirationTime - DateTime.Now:%h\h\ %m\m}"))}\n" : string.Empty) +
                                     (fc.ActionsAvailable.Length > 0 ?
                                         $"Available Actions:\n\t{string.Join("\n\t", fc.ActionsAvailable.Select(ad => ad.Action?.Name))}" : string.Empty));
    }

    private static void DrawStatusCell(FCData fc)
    {
        ImGui.AlignTextToFramePadding();

        bool selectable = fc.SourceData.ImportSourceConfig == null && fc.MemberCIDs.Count != 0 && fc.HasHouse;

        if (selectable)
        {
            ImGui.Selectable("##FCStatusCell" + fc.Id);
            ImGui.SetItemAllowOverlap();
            ImGui.SameLine(0, 0);
        }

        Vector4 color = FCTrackerTheme.GetStatusColor(fc.GetStatusCategory(), false);

        Vector2 cursorPos = ImGui.GetCursorScreenPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(
            new Vector2(cursorPos.X + 4, cursorPos.Y + 12),
            4,
            ImGui.GetColorU32(color)
        );

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 14);

        FCTrackerWidgets.ColoredText(color, fc.HasHouse ? Censor.Hide(fc.GetHousingStatusText(), "House owned") : fc.GetHousingStatusText());

        if (!fc.HasHouse)
        {
            ImGui.SameLine();

            using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
            {
                Vector4 accentColor = (fc.EligibilityDateReferenceOverride == null ? FCTrackerTheme.AccentBlue : FCTrackerTheme.AccentRed);
                using (ImRaii.PushColor(ImGuiCol.Button, accentColor with { W = 0.15f.Scale() }))
                using (ImRaii.PushColor(ImGuiCol.Text, accentColor))
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    if (ImGui.Button($"{FontAwesomeIcon.Sync.ToIconString()}###setEligibilityOverrideDate{fc.Id}", new Vector2(28f.Scale(), 0)))
                    {
                        if(fc.EligibilityDateReferenceOverride == null)
                            fc.EligibilityDateReferenceOverride = DateTime.Now;
                        else
                            fc.EligibilityDateReferenceOverride = null;

                        Configuration.Instance.Save();
                    }
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                FCTrackerWidgets.Tooltip("Set Calculation Date to right now or reset to Founding Date\nHold Ctrl to enable");
        }


        if (selectable && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            LifestreamNavigator.GoToFCHouse(fc);
    }

    private static void DrawDemolitionCell(FCData fc)
    {
        if (!fc.HasHouse)
        {
            ImGui.Text("—");
            return;
        }

        string  text  = fc.GetHousingDemolitionText();
        Vector4 color = FCTrackerTheme.GetStatusColor(fc.House!.GetVisitationStatus(), true);

        FCTrackerWidgets.ColoredText(color, text);
    }
}