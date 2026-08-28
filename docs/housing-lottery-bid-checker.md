# Housing lottery bid checker

An automated sweep that logs into every character in an FC, visits each plot the FC has a pending
lottery bid on, and records the result. A loss has its refund accepted. A win is left unclaimed.

Implemented in `FCTracker/Housing/LotteryCheckRunner.cs` and `Housing/LotteryDialog.cs`. Introduced
in PR [#2](https://github.com/NicolasJOnGithub/FCTracker/pull/2), on top of the bid tracking in
[housing-lottery-tracking.md](housing-lottery-tracking.md) — read that first; this assumes the
placard hook, `LotteryBidRecord`, and the Ready Now columns it describes already exist.

---

## Why

Bid tracking settles a bid the moment its placard is opened. That still meant opening every plot by
hand, on every character that might have entered — tedious for an FC running several bids across
several alts. The ask was a single button: go check them all, accept the losses, leave the wins for
a deliberate decision.

---

## Design constraints

Two things made this harder than "click through a queue":

**The game never says who entered.** A bid's `BidderCID` is FC Tracker's own record of who placed
it — a good guess, not a fact. A bid captured from a placard read by one alt is silent about whether
another alt also entered the same plot, and a bid recorded before the checker existed has no
`BidderCID` history to trust blindly. So the correct scope is "every character in the FC", not "the
one character we happen to have on file".

**Confirming the wrong dialog spends real gil.** Accepting a lottery refund and finalizing a plot
purchase are **both** `SelectYesno` prompts. There is nothing at the addon-lifecycle level that
tells them apart except the text on screen. Getting this wrong doesn't corrupt a data row — it
spends the FC's gil on a plot nobody meant to buy yet, or misses a refund that was sitting there.
That risk is why the guard described below is deliberately one-sided.

---

## How the sweep is built

### Work queue

`Start(fcs)` builds one `Step` (FC, bid, character) per pending bid per known FC member:

```csharp
foreach (FCData fc in fcs.Where(HasWork))
foreach (LotteryBidRecord bid in fc.LotteryBids.Where(b => b.Outcome == LotteryOutcome.Pending))
foreach (CharData character in ResolveCharacters(fc, bid))
    queue.Add(new Step { Fc = fc, Bid = bid, Character = character });
```

`ResolveCharacters` orders the recorded bidder first, then the rest of the FC's known members
alphabetically. In the common case — the bidder we recorded is right — the very first login settles
the plot and the rest of that plot's queue is skipped (see below), so the full-membership scope costs
almost nothing extra when the guess was correct, and only pays for itself when it wasn't.

### One step, five stages

Each `Step` runs through its own private `TaskManager` (separate from the plugin's main one, so a
long check doesn't block anything else):

1. **Ensure character** — `Lifestream.ChangeCharacter(name, world)` if not already logged in as them.
2. **Travel** — `Lifestream.GoToHousingAddress(...)` to the plot's address, skipped if already there.
3. **Open the placard** — interact with the nearest `EventObj` within 12 yalms until a
   `SelectYesno` or `HousingSignBoard` window appears. Retried on a timer rather than assumed to
   work first try, since the nearest object isn't guaranteed to be the placard. Lifestream's
   drop-off is sometimes a little short of actual interact range, so the placard is first searched
   for within 60 yalms and, if the nearest one found is beyond the 12-yalm interact radius, closed
   in on via `vnavmesh`'s `SimpleMove.PathfindAndMoveTo` before interacting. Without vnavmesh
   installed there's no safe way to close that gap, so the stage just keeps retrying until its own
   timeout, same as before.
4. **Read the result** — the existing placard sale-info hook fires as a side effect of opening it,
   and settles `PlayerResult` the same way it does for a manual visit.
5. **Handle the dialog** — see below.

Every stage has a generous, independent timeout (three minutes for a login, five for travel) and
none of them abort the run on failure — a stuck step just times out and the queue moves to the next
one, rather than wedging.

### Skipping answered plots

Before starting the next queued step, the runner checks whether the plot has already been answered —
either by the step that just ran, or because two bids on the same plot in the same cycle exist for
different characters:

```csharp
private static LotteryBidRecord? AnsweredRecord(Step step) =>
    step.Fc.LotteryBids.FirstOrDefault(b => b.IsResolved &&
                                            b.TerritoryTypeId == step.Bid.TerritoryTypeId &&
                                            b.Ward == step.Bid.Ward && b.Plot == step.Bid.Plot &&
                                            b.CycleKey == step.Bid.CycleKey);
```

This is also why a hit under one character is written back to the bid the run actually started from.
The placard hook keys its records **per character** — `LotteryTracker.FindRecord` matches on
`BidderCID` — so when alt B settles a plot that was recorded against alt A, the hook creates or
updates B's own row, not A's. Left alone, A's original bid would sit `Pending` forever while B's
copy silently held the answer. `PropagateResult` carries the outcome back after each step:

```csharp
step.Bid.Resolve(answered.Outcome, answered.OutcomeSource,
                 $"{answered.OutcomeReason} (seen on {answered.BidderCID})");
```

---

## The dialog guard

`LotteryDialog.Classify` reads the on-screen text and returns one of three kinds. It is written to
fail closed: anything it doesn't recognise, or that mentions both a refund and a purchase, is
**never** confirmed.

```csharp
public enum DialogKind
{
    Other,      // not recognised - always declined
    Refund,     // Addon 7124 (lost) or 7123 (forfeited win, 50% refund)
    ClaimPlot,  // Addon 7122 / 7128 - won, offering to finalise the purchase - always declined
}
```

Purchase wording is checked **first** and wins outright:

```csharp
if (PurchaseWording.Any(lower.Contains))     // "finalize your purchase", "claim your plot",
    return DialogKind.ClaimPlot;             // "congratulations", "wish to deposit", "enter the lottery"

return RefundWording.Any(lower.Contains)     // "accept a full refund", "accept the remaining refund",
    ? DialogKind.Refund                      // "better luck in the future"
    : DialogKind.Other;
```

A dialog is only confirmed when **two independent things agree**:

```csharp
LotteryBidRecord? answered        = AnsweredRecord(step) ?? JustSeenRecord(step);
bool              placardSaysLost = (answered ?? step.Bid).Outcome == LotteryOutcome.Lost;

if (kind == LotteryDialog.DialogKind.Refund && placardSaysLost)
{
    // ... accept
}
```

The placard's own result byte (`0x03`, see the tracking doc) has to say `Loser` or `WinnerForfeit`,
**and** the dialog text has to read as a refund with no purchase wording anywhere in it. Either one
alone is declined. A win — `ClaimPlot` — is always declined regardless of what the placard said,
because claiming spends the FC's gil and that decision is left to a person.

`AnsweredRecord` requires the cycle key to match `step.Bid`'s — needed for the cross-run dedup
`PlotAlreadyAnswered`/`PropagateResult` do, but that's a `yyyy-MM-dd` string derived from a results
timestamp computed two different ways depending on which phase the placard was viewed in (see
`ResultsStartFor` in the tracking doc); a real loss was observed in a first live run where the
refund got declined even though the outcome was later recorded correctly as Lost via
`PropagateResult` — the leading theory is the two computations landed a day apart and the cycle
keys never matched at the moment `HandleResult` needed them to. `JustSeenRecord` sidesteps that:
same plot, any bidder or cycle, whose record was touched by the hook in the last minute, which can
only be from the placard being looked at right now. Worth confirming this was in fact the cause
next time a loss comes up in the log with dry run on.

The refund *amount* is only used for logging which case happened (a full deposit back means the
lottery was lost; half means a won plot whose claim window lapsed), matching the outcome text the
tracking doc already assigns for those two cases.

### Dry run

`Configuration.GlobalData.LotteryCheckDryRun`, **on by default**. When set, a dialog that would be
accepted is logged instead — `Dry run: would accept a refund of N gil on …` — and then declined the
same as if it hadn't matched. Nothing is ever confirmed while dry run is on, including in the
`Other`/`ClaimPlot` branches, which are always declined either way.

The setting lives in Settings, not just in memory, so it survives a restart — the first live run
should be one you watch in `/xllog`, not one you have to remember to re-enable caution for next
session.

---

## UI

Two entry points into the same runner instance (`FCTrackerPlugin.Plugin.LotteryCheckRunner`), so a
per-FC check and the full sweep can't run concurrently and stomp on each other:

- **Banner button**, `Check bids (N)`, where `N` is the total pending-bid count across every FC
  currently shown. Turns into a `Stop` button while running.
- **Per-row button**, shown only on FCs with at least one pending bid, that starts a sweep scoped to
  just that FC.

While running, a status line shows the current character/plot and how many checks remain; hovering
it shows the last 20 log lines. Both buttons and the status line read the same `Running` /
`Remaining` / `Log` state, so either trigger drives the identical queue-and-guard logic above — there
is exactly one place this behaviour is implemented.

---

## What to verify in game

This automation was written and reviewed without a compiler or a running game on hand, so real
observation before trusting it unattended still matters:

- **Placard targeting.** "Nearest `EventObj` within 60 yalms, walked in via vnavmesh if beyond the
  12-yalm interact radius" is still a heuristic. If a plot has another interactable closer than the
  real placard, the wrong thing might get targeted first — harmless, since the stage just keeps
  retrying until a placard window actually appears, but worth watching once. Also confirm vnavmesh
  is actually installed for any character expected to check a bid — without it, a too-far placard
  just times out the same as it did before this fix.
- **The dialog wording.** `LotteryDialog`'s word lists are taken from the `Addon` sheet's English
  text. If anything about the dialog differs from what's documented in the tracking doc — different
  wording, a prompt neither list matches — it will correctly fall through to `Other` and be declined,
  but the goal is for real bids to actually get resolved, not just declined safely. Run the first
  cycle with dry run on and read the log.
- **The cycle-key race behind `JustSeenRecord`** (see above) — the log now says explicitly what the
  bid's own outcome was and what the freshest observation said whenever a refund gets declined for
  disagreeing with the placard, so a real occurrence should be diagnosable straight from the log
  next time instead of guessed at.

## Building

Same as the tracking feature — see [housing-lottery-tracking.md](housing-lottery-tracking.md#building).

---

## References

- Result dialog wording: `Addon` sheet rows 7122–7129, 7046 — see the tracking doc's
  [Verifying the result byte](housing-lottery-tracking.md#verifying-the-result-byte) section for how
  those were sourced.
- Lifestream IPC surface actually available to FC Tracker: `ECommons.IPC`'s
  [`Subscribers/Lifestream/LifestreamIPC.cs`](https://github.com/NightmareXIV/ECommons.IPC/blob/main/ECommons.IPC/Subscribers/Lifestream/LifestreamIPC.cs) —
  narrower than Lifestream's own IPC surface; `GetCurrentPlotInfo` is exposed there, `IsHere` is not.
