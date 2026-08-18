namespace FCTracker.UI.Views;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Housing;
using IPC;
using NightmareUI.Censoring;

public class ReadyNowView : IFCView
{
    public string Id => "ready";

    private static readonly Dictionary<string, string> HeaderTooltips = new()
    {
        ["FC Points"] = "FC points are required for fuel and the submarine licenses\n99.900 are required for one stack of fuel\n160.000 are required for 16 Dive credits for all 4 submarines.",
        ["Bid"]       = "The plot this FC's most recent housing lottery entry was placed on.\nCaptured automatically when a placard is opened.",
        ["Result"]    = "Outcome of that entry.\nA trailing ? means it was inferred rather than observed - hover for why.\nCtrl-click a result to correct it by hand.",
    };

    public (string Title, string Subtitle) GetHeaderInfo(FCViewContext ctx) =>
        ("Ready for Housing", $"{ctx.Data.GetReadyCount()} FCs eligible");

    public void Draw(FCViewContext ctx)
    {
        IReadOnlyList<FCData> readyFCs = ctx.Data.GetEligibleFCs();

        using ImRaii.ChildDisposable scrollArea = ImRaii.Child("##ReadyScroll", Vector2.Zero, false);
        if (!scrollArea.Success)
            return;

        if (readyFCs.Count == 0)
        {
            ImGui.SetCursorPos(new Vector2(14, 20));
            FCTrackerWidgets.IconLabel(FCTrackerTheme.TextSecondary, FontAwesomeIcon.Hourglass,
                "No FCs are currently eligible for housing.");
            return;
        }

        ImGui.SetCursorPos(new Vector2(14, 12));
        DrawBannerHeader(readyFCs.Count(fc => fc.IsEligible));

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8);

        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY        |
                                      ImGuiTableFlags.PadOuterX      |
                                      ImGuiTableFlags.SizingFixedFit |
                                      ImGuiTableFlags.Resizable;

        using ImRaii.TableDisposable table = ImRaii.Table("##ReadyTable", 7, flags);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);

        ImGui.TableSetupColumn("Status",    ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("FC",        ImGuiTableColumnFlags.WidthFixed, 420);
        ImGui.TableSetupColumn("FC Points", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Bid",       ImGuiTableColumnFlags.WidthFixed, 240);
        ImGui.TableSetupColumn("Result",    ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("##Travel",  ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("##Spacer",  ImGuiTableColumnFlags.WidthStretch);

        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, FCTrackerTheme.BackgroundHeader))
        using (ImRaii.PushColor(ImGuiCol.Text, FCTrackerTheme.TextSecondary))
            FCTrackerWidgets.TableHeadersRowWithTooltips(HeaderTooltips);

        // GetEligibleFCs is sorted by world, so a plain change-detect gives us the grouping.
        string? currentWorld = null;

        foreach (FCData fc in readyFCs)
        {
            if (fc.WorldName != currentWorld)
            {
                currentWorld = fc.WorldName;
                DrawWorldGroupHeader(fc, readyFCs.Count(other => other.WorldName == currentWorld));
            }

            DrawRow(fc);
        }
    }

    private static void DrawBannerHeader(int count)
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, FCTrackerTheme.AccentGreenDim))
        {
            using ImRaii.ChildDisposable banner = ImRaii.Child("##ReadyHeader", new Vector2(ImGui.GetContentRegionAvail().X - 28, 40), true);
            if (!banner.Success)
                return;

            ImGui.SetCursorPos(new Vector2(14, 10));
            FCTrackerWidgets.IconLabel(FCTrackerTheme.AccentGreen, FontAwesomeIcon.CheckCircle,
                $"{count} Free {(count == 1 ? "Company" : "Companies")} Ready for Housing");

            ImGui.SameLine(0, 20f);

            if (FCTrackerPlugin.Plugin.IsEntryPeriod)
            {
                TimeSpan timeForEntry = FCTrackerPlugin.Plugin.EntryPeriodCurrentEndDate - DateTime.UtcNow;

                FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentGreen,
                                             $"Entry period active {(timeForEntry.Days > 0 ? $"till {FCTrackerPlugin.Plugin.EntryPeriodCurrentEndDate:d}" : @$"for {@timeForEntry:%h\h\ %m\m}")}");
            }
            else
            {
                TimeSpan timeUntilNextEntry = FCTrackerPlugin.Plugin.EntryPeriodNextStartDate - DateTime.UtcNow;
                if (timeUntilNextEntry.Days <= 0)
                    FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentGreen,
                                                 @$"Next Entry period starting in {@timeUntilNextEntry:%h\h\ %m\m} to {FCTrackerPlugin.Plugin.EntryPeriodNextEndDate:d}");
                else
                    FCTrackerWidgets.ColoredText(FCTrackerTheme.TextPrimary,
                                                 $"Next Entry period active from {FCTrackerPlugin.Plugin.EntryPeriodNextStartDate:d} to {FCTrackerPlugin.Plugin.EntryPeriodNextEndDate:d}");
            }
        }
    }

    /// <summary>
    /// Server separator, matching the All FCs tab. The count is of the rows actually drawn
    /// underneath, not the global per-world total, so header and body always agree.
    /// </summary>
    private static void DrawWorldGroupHeader(FCData fc, int countInGroup)
    {
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(FCTrackerTheme.BackgroundSidebar));

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        FCTrackerWidgets.IconLabel(FCTrackerTheme.TextPrimary,
                                   FCTrackerTheme.GetRegionIcon(FCTrackerTheme.RegionString(fc.World)),
                                   Censor.World(fc.WorldName));

        if (!string.IsNullOrEmpty(fc.Datacenter))
        {
            ImGui.SameLine();
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, $"·  {fc.Datacenter}");
        }

        ImGui.TableNextColumn();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, $"{countInGroup}");

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
    }

    private static void DrawRow(FCData fc)
    {
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                              fc.LoggedIn
                                  ? ImGui.GetColorU32(FCTrackerTheme.RowHighlightColor)
                                  : ImGui.GetColorU32(fc.IsEligible ? FCTrackerTheme.AccentGreenDim : FCTrackerTheme.BackgroundCard));

        DrawStatusCell(fc);
        DrawFCCell(fc);

        ImGui.TableNextColumn();
        FCTrackerWidgets.ColoredText(FCTrackerTheme.GetFCPointColor(fc.FCPoints), fc.FCPoints.ToString("N0"));

        LotteryBidRecord?               bid   = fc.CurrentLotteryBid;
        HouseHunterIPC.LotterySaveData? hhBid = bid == null ? FirstHouseHunterBid(fc) : null;

        DrawBidCell(fc, bid, hhBid);
        DrawResultCell(bid);
        DrawTravelCell(fc, bid, hhBid);

        ImGui.TableNextColumn();
    }

    private static void DrawStatusCell(FCData fc)
    {
        ImGui.TableNextColumn();

        (Vector4 color, string label) = fc.IsEligible
                                            ? (FCTrackerTheme.AccentGreen, "READY")
                                            : (FCTrackerTheme.AccentBlue, "BID");

        Vector2      screenPos = ImGui.GetCursorScreenPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(new Vector2(screenPos.X + 4, screenPos.Y + 7), 4, ImGui.GetColorU32(color));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 14);

        FCTrackerWidgets.ColoredText(color, label);

        if (!fc.IsEligible && ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip("Kept here because this FC has a recent lottery entry.\nIt is no longer eligible to bid.");
    }

    private static void DrawFCCell(FCData fc)
    {
        ImGui.TableNextColumn();

        bool selectable = LifestreamNavigator.CanNavigate(fc);

        if (selectable)
        {
            selectable = ImGui.Selectable("##FCCell" + fc.Id);
            ImGui.SetItemAllowOverlap();
            ImGui.SameLine(0, 0);
        }

        FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentBlue, Censor.Hide(fc.Tag, FCTrackerPlugin.ScrambleTag));
        ImGui.SameLine(0, 6);
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextBright, Censor.Character(fc.FCName));

        if ((fc.MemberCIDs.Count > 0 || fc.MemberData.Count > 0) && ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(fc.MembersString(true));

        ImGui.SameLine(0, 10);
        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, $"· {Censor.World(fc.WorldName)} · {Censor.Character(fc.MasterString)}");

        if (selectable)
            LifestreamNavigator.ChangeToFCCharacter(fc);
    }

    private static void DrawBidCell(FCData fc, LotteryBidRecord? bid, HouseHunterIPC.LotterySaveData? hhBid)
    {
        ImGui.TableNextColumn();

        if (bid != null)
        {
            FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentBlue, Censor.Hide(bid.LocationText, "Bidding"));

            if (ImGui.IsItemHovered())
                FCTrackerWidgets.Tooltip(BuildBidTooltip(fc, bid));

            return;
        }

        if (hhBid != null)
        {
            string text = $"{FCData.HouseInfo.GetResidentialAetheryteByTerritoryType(hhBid.Territory)} · Ward {hhBid.Ward + 1} · Plot {hhBid.Plot + 1}";
            FCTrackerWidgets.ColoredText(FCTrackerTheme.AccentBlue, Censor.Hide(text, "Bidding"));

            if (ImGui.IsItemHovered())
                FCTrackerWidgets.Tooltip("Reported live by HouseHunter.\nFC Tracker records its own bids when you open a placard.");

            return;
        }

        FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, "—");
    }

    private static void DrawResultCell(LotteryBidRecord? bid)
    {
        ImGui.TableNextColumn();

        if (bid == null)
        {
            FCTrackerWidgets.ColoredText(FCTrackerTheme.TextMuted, "—");
            return;
        }

        Vector4 color = GetOutcomeColor(bid.Outcome);

        Vector2       cursorPos = ImGui.GetCursorScreenPos();
        ImDrawListPtr drawList  = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(new Vector2(cursorPos.X + 4, cursorPos.Y + 7), 4, ImGui.GetColorU32(color));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 14);

        // An inferred outcome is a guess, so say so instead of dressing it up as a result.
        FCTrackerWidgets.ColoredText(color, bid.OutcomeSource == OutcomeSourceKind.Inferred ? $"{bid.OutcomeText}?" : bid.OutcomeText);

        if (ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip(BuildOutcomeTooltip(bid));

        // Ctrl-click to correct an outcome by hand, matching the Ctrl-gated actions elsewhere.
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyCtrl)
        {
            bid.Resolve(NextOutcome(bid.Outcome), OutcomeSourceKind.Observed, "Set manually");
            Configuration.Instance.Save();
        }
    }

    private static LotteryOutcome NextOutcome(LotteryOutcome outcome) => outcome switch
    {
        LotteryOutcome.Pending => LotteryOutcome.Won,
        LotteryOutcome.Won     => LotteryOutcome.Lost,
        LotteryOutcome.Lost    => LotteryOutcome.Claimed,
        _                      => LotteryOutcome.Pending,
    };

    private static void DrawTravelCell(FCData fc, LotteryBidRecord? bid, HouseHunterIPC.LotterySaveData? hhBid)
    {
        ImGui.TableNextColumn();

        if (!LifestreamNavigator.CanNavigate(fc) || (bid == null && hhBid == null))
            return;

        bool clicked = FCTrackerWidgets.IconButton(FontAwesomeIcon.LocationArrow, $"bidtp{fc.Id}",
                                                  FCTrackerTheme.AccentBlueDim, FCTrackerTheme.AccentBlue);

        if (ImGui.IsItemHovered())
            FCTrackerWidgets.Tooltip("Travel to this plot with Lifestream");

        if (!clicked)
            return;

        if (bid != null)
        {
            LifestreamNavigator.GoToPlot(fc, bid.City, bid.Ward, bid.Plot, $"{fc.WorldName}-{fc.Id}-Bidding");
            return;
        }

        FCData.HouseInfo.ResidentialAetheryteKind? city = FCData.HouseInfo.GetResidentialAetheryteByTerritoryType(hhBid!.Territory);
        if (city.HasValue)
            LifestreamNavigator.GoToPlot(fc, city.Value, (byte)hhBid.Ward, (byte)hhBid.Plot, $"{fc.WorldName}-{fc.Id}-Bidding");
    }

    private static Vector4 GetOutcomeColor(LotteryOutcome outcome) => outcome switch
    {
        LotteryOutcome.Won     => FCTrackerTheme.AccentGreen,
        LotteryOutcome.Claimed => FCTrackerTheme.AccentBlue,
        LotteryOutcome.Lost    => FCTrackerTheme.AccentRed,
        _                      => FCTrackerTheme.TextSecondary,
    };

    private static string BuildBidTooltip(FCData fc, LotteryBidRecord bid)
    {
        StringBuilder sb = new();

        sb.AppendLine(bid.LocationText);

        if (bid.EntryNumber >= 0)
            sb.AppendLine($"Entry number: {bid.EntryNumber}");

        if (bid.Price > 0)
            sb.AppendLine($"Deposit: {bid.Price:N0} gil");

        if (bid.ResultsAvailableUtc.HasValue)
        {
            DateTime local = bid.ResultsAvailableUtc.Value.ToLocalTime();
            TimeSpan until = bid.ResultsAvailableUtc.Value - DateTime.UtcNow;

            sb.AppendLine(until > TimeSpan.Zero
                              ? @$"Results open {local:g} (in {until:%d\d\ %h\h})"
                              : $"Results opened {local:g}");
        }

        if (bid.ClaimDeadlineUtc.HasValue)
            sb.AppendLine($"Claim/refund by {bid.ClaimDeadlineUtc.Value.ToLocalTime():g}");

        if (!bid.PlotRestrictedToFC)
            sb.AppendLine("Plot accepts both FC and private entries - the game does not say which kind this was.");

        List<LotteryBidRecord> history = fc.LotteryBids.Where(b => b != bid).OrderByDescending(b => b.EntryDateUtc).ToList();

        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Earlier entries:");
            foreach (LotteryBidRecord past in history.Take(8))
                sb.AppendLine($"\t{past.EntryDateUtc.ToLocalTime():d} · {past.LocationText} · {past.OutcomeText}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildOutcomeTooltip(LotteryBidRecord bid)
    {
        StringBuilder sb = new();

        sb.AppendLine(bid.OutcomeSource == OutcomeSourceKind.Inferred
                          ? "Inferred, not observed:"
                          : "Reported by the game:");

        sb.AppendLine(string.IsNullOrEmpty(bid.OutcomeReason) ? bid.OutcomeText : bid.OutcomeReason);

        if (bid.OutcomeRecordedUtc.HasValue)
            sb.AppendLine($"Recorded {bid.OutcomeRecordedUtc.Value.ToLocalTime():g}");

        if (bid.Outcome == LotteryOutcome.Pending)
            sb.AppendLine("Open the plot's placard to settle this.");

        return sb.ToString().TrimEnd();
    }

    private static HouseHunterIPC.LotterySaveData? FirstHouseHunterBid(FCData fc) =>
        HouseHunterIPC.Instance.Available ? HouseHunterIPC.Instance.GetLotteryDataForFC(fc).FirstOrDefault() : null;
}
