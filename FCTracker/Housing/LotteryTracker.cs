namespace FCTracker.Housing;

using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// Records FC housing lottery bids and resolves their outcome.
///
/// Three signals feed this, in descending order of trust:
///   1. The placard sale-info hook. Byte 0x03 of the server's record is our own standing in
///      that plot's lottery, so viewing a placard both registers the bid and settles it.
///   2. The FC's owned-house data FCTracker already collects, which confirms a claim.
///   3. Game log messages, matched by id rather than text so they work in every client language:
///      the FC deed purchase, and a collected refund. The refund is a fallback only - it cannot
///      reach you anywhere but the placard, so signal 1 has normally settled the bid already.
///
/// A loss is not knowable away from the plot. The game never announces lottery results; you find
/// out by opening the placard. That is why this settles bids on placard reads rather than trying
/// to time them, and why the deadline sweep is marked inferred rather than observed.
///
/// Deliberately NOT used: a chat confirmation at bid time. There isn't one - the entire
/// LogMessage sheet contains two rows mentioning "lottery" and both are error strings.
/// </summary>
public sealed class LotteryTracker : IDisposable
{
    /// <summary>"You purchase the deed to plot N, ward N, District for the company."</summary>
    private static readonly HashSet<uint> FCDeedPurchasedIds = [3360, 3365, 3370];

    /// <summary>"You are refunded N gil." / "N gil is placed into the company chest."</summary>
    private static readonly HashSet<uint> RefundIds = [1486, 1853];

    /// <summary>"As more than 90 days have passed since the results period ended, your refund has expired."</summary>
    private const uint RefundExpiredId = 4285;

    /// <summary>How long a deposit read from the entry dialog stays applicable to the next placard read.</summary>
    private static readonly TimeSpan DepositStashLifetime = TimeSpan.FromMinutes(5);

    private PlacardSaleHook? placardHook;

    private long     pendingDepositGil;
    private DateTime pendingDepositAtUtc;

    public bool HookInstalled => this.placardHook?.Installed ?? false;

    public LotteryTracker()
    {
        this.placardHook    =  new PlacardSaleHook(this.OnPlacardSaleInfo);
        Svc.Chat.LogMessage += this.OnLogMessage;

        // The entry confirmation is the one place the game states the deposit, and we need
        // that number to recognise the matching refund later.
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesNoTextScroll", this.OnEntryConfirmation);
    }

    public void Dispose()
    {
        Svc.Chat.LogMessage -= this.OnLogMessage;
        Svc.AddonLifecycle.UnregisterListener(this.OnEntryConfirmation);
        this.placardHook?.Dispose();
        this.placardHook = null;
    }

    // ------------------------------------------------------------ entry dialog

    /// <summary>
    /// "Do you wish to deposit N gil to enter the lottery for this plot?" - stash the amount so
    /// the placard read that follows the confirmation can attach it to the bid.
    /// </summary>
    private unsafe void OnEntryConfirmation(AddonEvent type, AddonArgs args)
    {
        try
        {
            // API 15 hands us an AtkUnitBasePtr wrapper rather than a raw pointer.
            string text = LotteryDialog.ReadText((AtkUnitBase*)args.Addon.Address);
            if (text.Length == 0 || !text.Contains("lottery", StringComparison.OrdinalIgnoreCase))
                return;

            long gil = LotteryDialog.ReadAmount(text);
            if (gil <= 0)
                return;

            this.pendingDepositGil   = gil;
            this.pendingDepositAtUtc = DateTime.UtcNow;

            Svc.Log.Debug($"[FCTracker lottery] entry dialog deposit of {gil:N0} gil noted");
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[FCTracker lottery] entry dialog read failed");
        }
    }

    // ---------------------------------------------------------------- placard

    private void OnPlacardSaleInfo(PlacardSaleHook.SaleInfo info)
    {
        try
        {
            if (!info.IsLottery)
                return;

            // TenantType describes who the PLOT is open to, not who our entry was for. A
            // plot restricted to private buyers can never hold an FC bid, so skip it; an
            // unrestricted plot is ambiguous and we keep it, flagged, rather than guess.
            if (info.TenantType == PlacardSaleHook.TenantKind.Personal)
                return;

            if (!Player.Available)
                return;

            ulong cid = Player.CID;
            if (cid == 0)
                return;

            if (!Configuration.Instance.GatheredData.FCData.TryGetValue(
                    Configuration.Instance.GetFCIdForCID(cid) ?? 0, out FCData? fc))
                return;

            LotteryBidRecord? existing = FindRecord(fc, cid, info);

            if (!info.HasOwnEntry)
            {
                // No entry of ours on this plot. Never delete a stored record on the strength
                // of this - once a refund is collected or a plot claimed the flag legitimately
                // returns to NoEntry, and wiping history there would lose the result.
                return;
            }

            LotteryBidRecord record = existing ?? NewRecord(cid, info);

            record.LastSeenUtc        = DateTime.UtcNow;
            record.TerritoryTypeId    = info.TerritoryTypeId;
            record.Ward               = info.WardId;
            record.Plot               = info.PlotId;
            record.PlotRestrictedToFC = info.TenantType == PlacardSaleHook.TenantKind.FreeCompany;

            ApplyPhaseTiming(record, info);
            ApplyPlayerResult(record, info);

            if (record.Price == 0 &&
                this.pendingDepositGil > 0 &&
                DateTime.UtcNow - this.pendingDepositAtUtc < DepositStashLifetime)
            {
                record.Price           = this.pendingDepositGil;
                this.pendingDepositGil = 0;
            }

            if (existing == null)
            {
                fc.LotteryBids.Add(record);
                Svc.Log.Information($"[FCTracker lottery] registered bid {record.LocationText} for FC {fc.FCName} " +
                                    $"(result {info.PlayerResult}, cycle {record.CycleKey})");
            }

            Configuration.Instance.Save();
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[FCTracker lottery] placard capture failed");
        }
    }

    private static LotteryBidRecord NewRecord(ulong cid, PlacardSaleHook.SaleInfo info) =>
        new()
        {
            BidderCID       = cid,
            TerritoryTypeId = info.TerritoryTypeId,
            Ward            = info.WardId,
            Plot            = info.PlotId,
            City            = FCData.HouseInfo.GetResidentialAetheryteByTerritoryType(info.TerritoryTypeId)
                           ?? default,
            EntryDateUtc = DateTime.UtcNow,
            CycleKey     = LotteryBidRecord.BuildCycleKey(null, DateTime.UtcNow),
        };

    /// <summary>
    /// The phase deadline means different things in each phase: during entry it is the moment
    /// results open, during results it is the claim/refund cutoff. Derive both from whichever
    /// one the game just told us, so we never guess from a hardcoded cycle anchor.
    /// </summary>
    private static void ApplyPhaseTiming(LotteryBidRecord record, PlacardSaleHook.SaleInfo info)
    {
        DateTime? resultsStart = ResultsStartFor(info);
        if (resultsStart == null)
            return;

        record.ResultsAvailableUtc = resultsStart;
        record.ClaimDeadlineUtc    = resultsStart.Value + TimeSpan.FromDays(FCTrackerPlugin.RESULTS_PERIOD_DURATION_DAYS);
        record.CycleKey            = LotteryBidRecord.BuildCycleKey(resultsStart, record.EntryDateUtc);
    }

    private static void ApplyPlayerResult(LotteryBidRecord record, PlacardSaleHook.SaleInfo info)
    {
        switch (info.PlayerResult)
        {
            case PlacardSaleHook.PlayerResultKind.Entered:
                // Still running. Don't downgrade an outcome we already settled.
                if (record.Outcome == LotteryOutcome.Pending)
                    record.OutcomeReason = "Entry accepted, awaiting results";
                break;

            case PlacardSaleHook.PlayerResultKind.Winner:
                // Owned already means the purchase went through.
                if (info.HousingType == PlacardSaleHook.HousingKind.OwnedHouse)
                    record.Resolve(LotteryOutcome.Claimed, OutcomeSourceKind.Observed, "Won and plot claimed");
                else
                    record.Resolve(LotteryOutcome.Won, OutcomeSourceKind.Observed, "Won - claim the plot before the deadline");
                break;

            case PlacardSaleHook.PlayerResultKind.Loser:
                record.Resolve(LotteryOutcome.Lost, OutcomeSourceKind.Observed, "Another entry was drawn");
                break;

            case PlacardSaleHook.PlayerResultKind.WinnerForfeit:
                record.Resolve(LotteryOutcome.Lost, OutcomeSourceKind.Observed,
                               "Won but the claim window expired - deposit refunded less the 50% cancellation fee");
                break;

            case PlacardSaleHook.PlayerResultKind.RefundExpired:
                record.Resolve(LotteryOutcome.Lost, OutcomeSourceKind.Observed, "Lost, and the refund window has expired");
                break;
        }
    }

    /// <summary>
    /// The moment results open for the plot being viewed, whichever phase we caught it in.
    /// Null when the game gave us no deadline.
    /// </summary>
    private static DateTime? ResultsStartFor(PlacardSaleHook.SaleInfo info) =>
        info.PhaseEndsAtUtc == null
            ? null
            : info.InResults
                ? info.PhaseEndsAtUtc.Value - TimeSpan.FromDays(FCTrackerPlugin.RESULTS_PERIOD_DURATION_DAYS)
                : info.PhaseEndsAtUtc.Value;

    private static LotteryBidRecord? FindRecord(FCData fc, ulong cid, PlacardSaleHook.SaleInfo info)
    {
        List<LotteryBidRecord> onPlot = fc.LotteryBids
                                          .Where(b => b.BidderCID       == cid                  &&
                                                      b.TerritoryTypeId == info.TerritoryTypeId &&
                                                      b.Ward            == info.WardId          &&
                                                      b.Plot            == info.PlotId)
                                          .ToList();

        if (onPlot.Count == 0)
            return null;

        DateTime? resultsStart = ResultsStartFor(info);

        if (resultsStart == null)
            return onPlot.FirstOrDefault(b => !b.IsResolved);

        string cycleKey = LotteryBidRecord.BuildCycleKey(resultsStart, DateTime.UtcNow);

        // Same plot, same cycle - that's the same bid.
        LotteryBidRecord? exact = onPlot.FirstOrDefault(b => b.CycleKey == cycleKey);
        if (exact != null)
            return exact;

        // A row we created before the game had told us the timing carries a placeholder key.
        // Adopt it rather than leaving a duplicate behind. Anything else on this plot belongs
        // to an earlier cycle and must stay untouched.
        return onPlot.FirstOrDefault(b => !b.IsResolved && b.ResultsAvailableUtc == null);
    }

    // ------------------------------------------------------------ log messages

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            uint id = message.LogMessageId;

            if (FCDeedPurchasedIds.Contains(id))
            {
                this.ResolveDeedPurchased(message);
                return;
            }

            if (RefundIds.Contains(id))
            {
                this.ResolveRefund(message);
                return;
            }

            if (id == RefundExpiredId)
                this.ResolveOldest(LotteryOutcome.Lost, "Refund window expired (90 days after results)");
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[FCTracker lottery] log message handling failed");
        }
    }

    /// <summary>"You purchase the deed to plot N, ward N, District for the company."</summary>
    private void ResolveDeedPurchased(ILogMessage message)
    {
        FCData? fc = CurrentFC();
        if (fc == null)
            return;

        (int plot1Based, int ward1Based) = ReadPlotAndWard(message);

        LotteryBidRecord? record = fc.LotteryBids
                                     .Where(b => b.BidderCID == Player.CID)
                                     .Where(b => plot1Based <= 0 || ward1Based <= 0 ||
                                                 (b.Plot == plot1Based - 1 && b.Ward == ward1Based - 1))
                                     .OrderByDescending(b => b.EntryDateUtc)
                                     .FirstOrDefault(b => b.Outcome is LotteryOutcome.Won or LotteryOutcome.Pending);

        if (record == null)
            return;

        record.Resolve(LotteryOutcome.Claimed, OutcomeSourceKind.Observed, "Deed purchased for the company");
        Configuration.Instance.Save();
        Svc.Log.Information($"[FCTracker lottery] {record.LocationText} claimed for FC {fc.FCName}");
    }

    /// <summary>
    /// A collected refund - "You are refunded N gil." / "N gil is placed into the company chest."
    ///
    /// This is a fallback, not a primary signal. A refund is never pushed to you: the game's own
    /// entry notice says an unsuccessful entrant is refunded "by accessing this placard", and a
    /// forfeited winner "upon accessing this placard". So it can only arrive while you are stood
    /// at the placard, where the sale-info hook has already settled the bid more precisely. It
    /// earns its place only when that hook failed to install after a game patch.
    ///
    /// The amount is what distinguishes the two outcomes, per the entry notice: an unsuccessful
    /// entrant gets 100% back, a winner who let the claim window lapse gets 50% after the
    /// cancellation fee. Both end as Lost here, but the reason should say which happened.
    /// </summary>
    private void ResolveRefund(ILogMessage message)
    {
        FCData? fc = CurrentFC();
        if (fc == null)
            return;

        long amount = ReadFirstAmount(message);
        if (amount <= 0)
            return;

        // These ids are generic - company chest gil moves for plenty of reasons - so a bid is
        // only implicated when the amount is exactly its deposit or exactly half of it, and its
        // results phase has opened. Without a recorded deposit we let the placard settle it.
        LotteryBidRecord? full = null;
        LotteryBidRecord? half = null;

        foreach (LotteryBidRecord bid in fc.LotteryBids.OrderByDescending(b => b.EntryDateUtc))
        {
            if (bid.Outcome != LotteryOutcome.Pending || !bid.ResultsOpen || bid.Price <= 0)
                continue;

            if (bid.Price == amount)
                full ??= bid;
            else if (bid.Price / 2 == amount)
                half ??= bid;
        }

        if (full != null)
        {
            full.Resolve(LotteryOutcome.Lost, OutcomeSourceKind.Observed,
                         $"Full deposit of {amount:N0} gil refunded - another entry was drawn");
        }
        else if (half != null)
        {
            half.Resolve(LotteryOutcome.Lost, OutcomeSourceKind.Observed,
                         $"Won, but the claim window lapsed - {amount:N0} gil refunded after the 50% cancellation fee");
        }
        else
        {
            Svc.Log.Debug($"[FCTracker lottery] refund-shaped message {message.LogMessageId} for {amount:N0} gil " +
                          "matched no pending bid's deposit; ignored");
            return;
        }

        Configuration.Instance.Save();

        LotteryBidRecord resolved = full ?? half!;
        Svc.Log.Information($"[FCTracker lottery] {resolved.LocationText} resolved from refund of {amount:N0} gil");
    }

    private void ResolveOldest(LotteryOutcome outcome, string reason)
    {
        FCData? fc = CurrentFC();

        LotteryBidRecord? record = fc?.LotteryBids
                                     .Where(b => b.Outcome == LotteryOutcome.Pending)
                                     .OrderBy(b => b.EntryDateUtc)
                                     .FirstOrDefault();

        if (record == null)
            return;

        record.Resolve(outcome, OutcomeSourceKind.Observed, reason);
        Configuration.Instance.Save();
    }

    private static FCData? CurrentFC()
    {
        if (!Player.Available || Player.CID == 0)
            return null;

        ulong? fcId = Configuration.Instance.GetFCIdForCID(Player.CID);

        return fcId.HasValue && Configuration.Instance.GatheredData.FCData.TryGetValue(fcId.Value, out FCData? fc)
                   ? fc
                   : null;
    }

    private static (int Plot, int Ward) ReadPlotAndWard(ILogMessage message)
    {
        List<int> ints = [];

        for (int i = 0; i < message.Parameters.Count; i++)
            if (message.TryGetIntParameter(i, out int value))
                ints.Add(value);

        // "plot N, ward N, District" - plot first, ward second.
        return ints.Count >= 2 ? (ints[0], ints[1]) : (-1, -1);
    }

    private static long ReadFirstAmount(ILogMessage message)
    {
        for (int i = 0; i < message.Parameters.Count; i++)
            if (message.TryGetIntParameter(i, out int value) && value > 0)
                return value;

        return 0;
    }

    // ------------------------------------------------------------------ claims

    /// <summary>
    /// Called after FC data is refreshed. If the FC now owns the plot one of its bids was on,
    /// that bid was claimed - regardless of whether we saw the deed message at the time.
    /// </summary>
    public static bool SyncClaimedFromOwnedHouse(FCData fc)
    {
        if (!fc.HasHouse || fc.LotteryBids.Count == 0)
            return false;

        FCData.HouseInfo house   = fc.House!;
        bool             changed = false;

        foreach (LotteryBidRecord bid in fc.LotteryBids)
        {
            if (bid.Outcome == LotteryOutcome.Claimed)
                continue;

            if (bid.Ward == house.Ward && bid.Plot == house.Plot && bid.City == house.City)
            {
                bid.Resolve(LotteryOutcome.Claimed, OutcomeSourceKind.Observed, "The FC owns this plot");
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Last resort for bids we never got to observe: once the claim window has closed with no
    /// signal either way, record a loss but mark it inferred so the UI can say so rather than
    /// present a timeout as a result.
    /// </summary>
    public static bool TickStaleOutcomes(FCData fc)
    {
        bool     changed = false;
        DateTime now     = DateTime.UtcNow;

        foreach (LotteryBidRecord bid in fc.LotteryBids)
        {
            if (bid.Outcome != LotteryOutcome.Pending)
                continue;

            if (bid.ClaimDeadlineUtc.HasValue && now > bid.ClaimDeadlineUtc.Value)
            {
                bid.Resolve(LotteryOutcome.Lost, OutcomeSourceKind.Inferred, "Claim window elapsed with no result observed");
                changed = true;
            }
        }

        return changed;
    }
}
