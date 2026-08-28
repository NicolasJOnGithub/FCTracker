namespace FCTracker.Housing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// Walks every pending FC lottery bid: logs in as each of the FC's known characters, travels to the
/// plot, opens the placard, and records the result. A loss has its refund accepted; a win is left
/// unclaimed on purpose, so the decision to spend the FC's gil stays yours.
///
/// The sweep covers every known member because the game does not tell us afterwards who entered -
/// a bid's recorded bidder is a good guess, not a fact. To keep that affordable, the moment any
/// character gets a definitive answer for a plot, the remaining characters queued for that same
/// plot are dropped.
/// </summary>
public sealed class LotteryCheckRunner
{
    private const int MaxLogLines = 200;

    public sealed class Step
    {
        public required FCData           Fc        { get; init; }
        public required LotteryBidRecord Bid       { get; init; }
        public required CharData         Character { get; init; }
    }

    private readonly TaskManager      taskManager;
    private readonly List<Step>       queue = [];
    private readonly List<string>     log   = [];

    private int   index;
    private Step? current;

    public bool   Running { get; private set; }
    public string Status  { get; private set; } = string.Empty;

    public IReadOnlyList<string> Log       => this.log;
    public int                   Remaining => Math.Max(0, this.queue.Count - this.index);

    /// <summary>Dry run only reports what it would accept; it never confirms a dialog.</summary>
    public static bool DryRun => Configuration.Instance.GlobalData.LotteryCheckDryRun;

    public LotteryCheckRunner() =>
        this.taskManager = new TaskManager(new TaskManagerConfiguration
                                           {
                                               AbortOnTimeout  = false,
                                               TimeoutSilently = true,
                                               TimeLimitMS     = 30_000,
                                               ShowDebug       = true,
                                           });

    // ------------------------------------------------------------------ control

    public static bool HasWork(FCData fc) =>
        fc.SourceData.ImportSourceConfig == null &&
        fc.LotteryBids.Any(b => b.Outcome == LotteryOutcome.Pending);

    public static int PendingBidCount(IEnumerable<FCData> fcs) =>
        fcs.Where(fc => fc.SourceData.ImportSourceConfig == null)
           .Sum(fc => fc.LotteryBids.Count(b => b.Outcome == LotteryOutcome.Pending));

    public void Start(IEnumerable<FCData> fcs)
    {
        if (this.Running)
            return;

        this.queue.Clear();
        this.log.Clear();
        this.index   = 0;
        this.current = null;

        foreach (FCData fc in fcs.Where(HasWork))
        foreach (LotteryBidRecord bid in fc.LotteryBids.Where(b => b.Outcome == LotteryOutcome.Pending))
        foreach (CharData character in ResolveCharacters(fc, bid))
            this.queue.Add(new Step { Fc = fc, Bid = bid, Character = character });

        if (this.queue.Count == 0)
        {
            this.Status = "Nothing to check - no pending bids with a known character.";
            return;
        }

        this.Running = true;
        this.Note(DryRun
                      ? $"Dry run: {this.queue.Count} checks queued. Nothing will be confirmed."
                      : $"{this.queue.Count} checks queued.");

        this.EnqueueCurrent();
    }

    public void Stop()
    {
        this.taskManager.Abort();
        this.Running = false;
        this.current = null;
        this.Note("Stopped.");
        this.Status = "Stopped.";
    }

    /// <summary>
    /// The bidder we recorded goes first - it is usually right, so the plot resolves on the first
    /// login and the rest of the sweep is skipped.
    /// </summary>
    private static List<CharData> ResolveCharacters(FCData fc, LotteryBidRecord bid) =>
        fc.MemberCIDs
          .Select(cid => Configuration.Instance.GatheredData.CharByCID.GetValueOrDefault(cid))
          .Where(ch => ch.CID != 0 && !string.IsNullOrEmpty(ch.Name))
          .OrderByDescending(ch => ch.CID == bid.BidderCID)
          .ThenBy(ch => ch.Name)
          .ToList();

    // -------------------------------------------------------------- the queue

    private void EnqueueCurrent()
    {
        // Skip anything already answered - either by an earlier character on this plot, or
        // because the placard hook settled it while we were walking there.
        while (this.index < this.queue.Count && PlotAlreadyAnswered(this.queue[this.index]))
            this.index++;

        if (this.index >= this.queue.Count)
        {
            this.Finish();
            return;
        }

        Step step = this.queue[this.index];
        this.current = step;

        this.Status = $"{step.Character.GetName()} → {step.Bid.LocationText}";
        this.Note($"Checking {step.Bid.LocationText} as {step.Character.Name}");

        this.taskManager.Enqueue(() => this.EnsureCharacter(step), "lottery: login",          new TaskManagerConfiguration(180_000));
        this.taskManager.EnqueueDelay(500);
        this.taskManager.Enqueue(LifestreamIdle,                   "lottery: lifestream idle", new TaskManagerConfiguration(180_000));
        this.taskManager.Enqueue(() => PlayerHelper.IsReady,        "lottery: character ready", new TaskManagerConfiguration(120_000));

        this.taskManager.Enqueue(() => Travel(step),                "lottery: travel",          new TaskManagerConfiguration(30_000));
        this.taskManager.EnqueueDelay(500);
        this.taskManager.Enqueue(LifestreamIdle,                    "lottery: travel done",     new TaskManagerConfiguration(300_000));
        this.taskManager.Enqueue(() => PlayerHelper.IsReady,        "lottery: ready at plot",   new TaskManagerConfiguration(60_000));

        this.taskManager.Enqueue(() => OpenPlacard(),               "lottery: open placard",    new TaskManagerConfiguration(45_000));
        this.taskManager.Enqueue(() => this.HandleResult(step),     "lottery: read result",     new TaskManagerConfiguration(30_000));

        this.taskManager.Enqueue(this.Advance, "lottery: next");
    }

    private void Advance()
    {
        this.index++;
        this.PropagateResult();

        if (this.Running)
            this.EnqueueCurrent();
    }

    private void Finish()
    {
        this.Running = false;
        this.current = null;
        this.Status  = "Done.";
        this.Note("Finished.");
        Configuration.Instance.Save();
    }

    // ------------------------------------------------------------- plot answers

    /// <summary>
    /// Any resolved record for the same plot and cycle counts as an answer, whichever of the FC's
    /// characters produced it.
    /// </summary>
    private static LotteryBidRecord? AnsweredRecord(Step step) =>
        step.Fc.LotteryBids.FirstOrDefault(b => b.IsResolved                             &&
                                                b.TerritoryTypeId == step.Bid.TerritoryTypeId &&
                                                b.Ward            == step.Bid.Ward            &&
                                                b.Plot            == step.Bid.Plot            &&
                                                b.CycleKey        == step.Bid.CycleKey);

    private static bool PlotAlreadyAnswered(Step step) =>
        step.Bid.IsResolved || AnsweredRecord(step) != null;

    /// <summary>
    /// Same plot, any bidder or cycle, whose result the placard hook wrote within the last minute -
    /// i.e. it can only be from the placard we are looking at right now. Unlike
    /// <see cref="AnsweredRecord"/> this doesn't require the cycle key to match <paramref name="step"/>'s
    /// bid: that exact-cycle bookkeeping matters for cross-run dedup, but "what did the placard I'm
    /// standing at just say" doesn't need it, and requiring it needlessly risks a same-visit read
    /// getting declined as unanswered over a cycle-key computation that landed a day off.
    /// </summary>
    private static LotteryBidRecord? JustSeenRecord(Step step) =>
        step.Fc.LotteryBids
           .Where(b => b.TerritoryTypeId == step.Bid.TerritoryTypeId &&
                       b.Ward            == step.Bid.Ward            &&
                       b.Plot            == step.Bid.Plot            &&
                       DateTime.UtcNow - b.LastSeenUtc < TimeSpan.FromMinutes(1))
           .OrderByDescending(b => b.LastSeenUtc)
           .FirstOrDefault();

    /// <summary>
    /// The placard hook keys records per character, so a sweep can settle a plot under a different
    /// character than the row we started from. Carry that answer back to the bid we were checking.
    /// </summary>
    private void PropagateResult()
    {
        Step? step = this.current;
        if (step == null || step.Bid.IsResolved)
            return;

        LotteryBidRecord? answered = AnsweredRecord(step);
        if (answered == null)
            return;

        step.Bid.Resolve(answered.Outcome, answered.OutcomeSource,
                         $"{answered.OutcomeReason} (seen on {answered.BidderCID})");
        this.Note($"{step.Bid.LocationText}: {answered.OutcomeText}");
    }

    // ------------------------------------------------------------------- stages

    private static bool LifestreamIdle() => !ECommonsIPC.Lifestream.IsBusy();

    private bool EnsureCharacter(Step step)
    {
        if (Player.Available && Player.CID == step.Character.CID)
            return true;

        if (ECommonsIPC.Lifestream.IsBusy())
            return false;

        this.Note($"Logging in as {step.Character.Name} @ {step.Character.WorldName}");
        ECommonsIPC.Lifestream.ChangeCharacter(step.Character.Name, step.Character.WorldName);

        return true;
    }

    private static AddressBookEntryTupleShim Address(Step step) =>
        new($"{step.Fc.WorldName}-{step.Fc.Id}-Check",
            (int)step.Fc.HomeWorldId, (int)step.Bid.City, step.Bid.Ward + 1, 0, step.Bid.Plot + 1);

    /// <summary>
    /// The ECommons.IPC wrapper FCTracker consumes does not mirror Lifestream's own IsHere/
    /// IsQuickTravelAvailable subscribers - only GetCurrentPlotInfo is exposed, so "are we already
    /// there" is derived from that instead. It reports the same 0-based ward/plot HousingManager
    /// does, which matches FCTracker's own convention.
    /// </summary>
    private static bool AlreadyAtPlot(Step step)
    {
        (int Kind, int Ward, int Plot)? here = ECommonsIPC.Lifestream.GetCurrentPlotInfo();

        return here.HasValue                        &&
               here.Value.Kind == (int)step.Bid.City &&
               here.Value.Ward == step.Bid.Ward      &&
               here.Value.Plot == step.Bid.Plot;
    }

    private static bool Travel(Step step)
    {
        if (AlreadyAtPlot(step))
            return true;

        if (ECommonsIPC.Lifestream.IsBusy())
            return false;

        ECommonsIPC.Lifestream.GoToHousingAddress(Address(step).ToTuple());

        return true;
    }

    /// <summary>Close enough to interact with the placard directly.</summary>
    private const float PlacardInteractRadius = 12f;

    /// <summary>
    /// How far out to look for the placard at all. Wider than the interact radius because
    /// Lifestream sometimes drops a character short of the actual plot entrance - just enough
    /// that the placard is visible but out of reach - and we still need to find it to know which
    /// way to walk.
    /// </summary>
    private const float PlacardSearchRadius = 60f;

    /// <summary>
    /// Interact with the plot's placard. Retries until one of the placard windows is up, so a
    /// mis-targeted first attempt just tries again on the next tick. If Lifestream's drop-off left
    /// us out of interact range, close the gap with vnavmesh before trying to interact.
    /// </summary>
    private static unsafe bool OpenPlacard()
    {
        if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* yesno) && yesno->IsReady())
            return true;

        if (GenericHelpers.TryGetAddonByName("HousingSignBoard", out AtkUnitBase* board) && board->IsReady())
            return true;

        Vector3? position = Player.Object?.Position;
        if (position == null)
            return false;

        Vector3 me = position.Value;

        IGameObject? placard = Svc.Objects
                                  .Where(o => o.ObjectKind == ObjectKind.EventObj)
                                  .Where(o => Vector3.Distance(o.Position, me) < PlacardSearchRadius)
                                  .MinBy(o => Vector3.Distance(o.Position, me));

        if (placard == null)
            return false;

        if (Vector3.Distance(placard.Position, me) > PlacardInteractRadius)
        {
            MoveTowards(placard.Position);
            return false;
        }

        if (ECommonsIPC.Vnavmesh.Available && ECommonsIPC.Vnavmesh.IsRunning())
            ECommonsIPC.Vnavmesh.Stop();

        TargetSystem.Instance()->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)placard.Address, false);

        return false;
    }

    /// <summary>
    /// Walk toward a too-far placard using vnavmesh. Without it installed there is no safe way to
    /// close the gap, so this just leaves the step retrying until its own timeout - the same
    /// behaviour as before vnavmesh support existed, just with a log line explaining why.
    /// </summary>
    private static void MoveTowards(Vector3 destination)
    {
        if (!ECommonsIPC.Vnavmesh.Available)
        {
            if (EzThrottler.Throttle("lottery: vnavmesh missing", 30_000))
                Svc.Log.Warning("[FCTracker lottery check] placard is out of interact range and vnavmesh is not installed - cannot get closer.");

            return;
        }

        if (ECommonsIPC.Vnavmesh.PathfindInProgress())
            return;

        ECommonsIPC.Vnavmesh.PathfindAndMoveTo(destination, false);
    }

    /// <summary>
    /// Decide what to do with whatever the placard put on screen.
    ///
    /// Confirming requires two independent agreements: the placard's own result byte said this bid
    /// lost, AND the prompt text reads as a refund with no purchase wording anywhere in it. Anything
    /// else - a win, an unfamiliar prompt, a disagreement between the two - is declined untouched.
    /// </summary>
    private unsafe bool HandleResult(Step step)
    {
        if (GenericHelpers.TryGetAddonByName("HousingSignBoard", out AtkUnitBase* board) && board->IsReady())
        {
            this.Note($"{step.Character.Name}: no entry on this plot");
            Callback.Fire(board, true, -1);
            return true;
        }

        if (!GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addon) || !addon->IsReady())
            return false;

        string                     text = LotteryDialog.ReadText(addon);
        LotteryDialog.DialogKind   kind = LotteryDialog.Classify(text);
        LotteryDialog.LogClassification("SelectYesno", text, kind);

        LotteryBidRecord? answered      = AnsweredRecord(step) ?? JustSeenRecord(step);
        bool              placardSaysLost = (answered ?? step.Bid).Outcome == LotteryOutcome.Lost;

        if (kind == LotteryDialog.DialogKind.Refund && placardSaysLost)
        {
            long amount = LotteryDialog.ReadAmount(text);

            if (DryRun)
            {
                this.Note($"Dry run: would accept a refund of {amount:N0} gil on {step.Bid.LocationText}");
                Decline(addon);
            }
            else
            {
                this.Note($"Accepting a refund of {amount:N0} gil on {step.Bid.LocationText}");
                Callback.Fire(addon, true, 0);
            }

            return true;
        }

        if (kind == LotteryDialog.DialogKind.ClaimPlot)
            this.Note($"{step.Bid.LocationText}: WON - left unclaimed, claim it yourself");
        else if (kind == LotteryDialog.DialogKind.Refund)
            this.Note($"{step.Bid.LocationText}: refund offered but the placard did not report a loss " +
                      $"(bid={step.Bid.OutcomeText}, seen={answered?.OutcomeText ?? "nothing recent"}) - declined");
        else
            this.Note($"{step.Bid.LocationText}: unrecognised prompt ('{text}') - declined");

        Decline(addon);

        return true;
    }

    private static unsafe void Decline(AtkUnitBase* addon) => Callback.Fire(addon, true, 1);

    // -------------------------------------------------------------------- notes

    private void Note(string message)
    {
        this.log.Add($"{DateTime.Now:HH:mm:ss}  {message}");

        if (this.log.Count > MaxLogLines)
            this.log.RemoveRange(0, this.log.Count - MaxLogLines);

        Svc.Log.Information($"[FCTracker lottery check] {message}");
    }

    /// <summary>
    /// Lifestream's address tuple has ten positional fields; naming them here keeps the call sites
    /// from turning into a wall of bare literals.
    /// </summary>
    private readonly record struct AddressBookEntryTupleShim(
        string Name, int World, int City, int Ward, int PropertyType, int Plot)
    {
        public (string, int, int, int, int, int, int, bool, bool, string) ToTuple() =>
            (this.Name, this.World, this.City, this.Ward, this.PropertyType, this.Plot, -1, false, false, string.Empty);
    }
}
