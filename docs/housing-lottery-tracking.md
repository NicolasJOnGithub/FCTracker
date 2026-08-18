# Housing lottery bid tracking

How FC Tracker records Free Company housing lottery entries and decides whether one was won,
lost or claimed.

Implemented in `FCTracker/Housing/`. Introduced in PR
[#1](https://github.com/NicolasJOnGithub/FCTracker/pull/1).

---

## Why

The **Ready Now** tab lists FCs eligible to bid on a house. It used to show bidding information by
asking the **HouseHunter** plugin over IPC (`FCTracker/IPC/IPCSubscribers.cs`). That had three limits:

- HouseHunter only reports *live* participation for a character ID. No history, no outcome, and
  nothing at all if HouseHunter isn't installed.
- The tab was a flat list, unlike **All FCs**, which groups rows by world.
- Clicking the bid text silently Lifestreamed you to the plot, with no visible affordance.

The goal was for FC Tracker to record bids itself, group Ready Now by server, give each bid an
explicit travel button, and show a Pending / Won / Lost / Claimed status.

[HousingLottoTracker](https://github.com/dexcss/HousingLottoTracker) already solved parts of this, so
it was the starting point. Both plugins target Dalamud API 15 / `Dalamud.NET.Sdk 15.0.0`, so code
ports directly. Two things turned up during the port that changed the design substantially.

---

## What the research turned up

Both of these were verified against game data and upstream source, not inferred.

### 1. There is no lottery-entry chat message

HousingLottoTracker's documented "primary capture path" is `Game/ChatLotteryParser.cs`, which parses
a chat line like *"You have submitted a lottery entry for plot 38, ward 30, Shirogane…"*.

The entire English `LogMessage` sheet contains **two** rows mentioning "lottery" — 3497 and 6099 —
and both are error strings:

```
3497  Lottery entry and plot purchase unavailable. No more land may be purchased as a character
      on this service account already owns land on this world.
6099  You may only submit one lottery entry per lottery period.
```

There is nothing for that regex to match. The code is dead, and porting it would have carried the
bug across. Capture had to come from somewhere else.

### 2. The placard reports your own result

The game's `HandlePlacardSaleInfo` function receives a struct describing the plot. PaissaHouse reads
most of it and leaves byte `0x03` as `Unknown1`. That byte is `LandLotteryPlayerResult`:

| Value | Meaning |
|---|---|
| 0 | `NoEntry` |
| 1 | `Entered` |
| 2 | `Winner` |
| 3 | `WinnerForfeit` |
| 4 | `Loser` |
| 5 | `RefundExpired` |

One byte gives registration *and* the outcome, with no text parsing and no locale dependence. The
naming comes from the [Natalan](https://github.com/Veiyna/Natalan) server reimplementation, so it is
high-confidence-but-verify rather than official — see [Verifying the result byte](#verifying-the-result-byte).

Full struct layout as read in `PlacardSaleHook.cs`:

| Offset | Field |
|---|---|
| `0x00` | `PurchaseType` — 0 Unavailable, 1 FCFS, 2 Lottery |
| `0x01` | `TenantType` — 0 Unrestricted, 1 FreeCompany, 2 Personal |
| `0x02` | `AvailabilityType` — 1 Available, 2 InResultsPeriod, 3 Unavailable |
| `0x03` | `PlayerResult` — the table above |
| `0x08` | `PhaseEndsAt` — unix seconds |
| `0x10` | `EntryCount` |

---

## Why the old loss detection didn't work

HousingLottoTracker has **no positive loss signal at all**. A loss is only ever inferred:

1. `BidStore.cs:121-127` compares a `WinningNumber` scraped from the results placard against your
   entry number. That number only appears in a text node containing `"winning number"`, which
   requires standing at that plot during the results phase. In practice it rarely fires.
2. `BidStore.TickOutcomes` flips any still-Pending bid to `Lost` once the claim deadline passes.
   It lands **four days after** results open, so a real loss reads "Pending" for the whole results
   window; rows created from the Timers panel or by hand deliberately leave the deadline null, so
   they stay Pending forever; and any win the plugin failed to observe is silently rewritten as a
   loss.

So "Lost" was a timeout presented as a result. That is the thing this replaces.

### The constraint that shapes everything

**A loss is not knowable away from the plot.** The game never announces lottery results, and refunds
are not pushed to you — they are collected by hand at the placard. The entry confirmation (Addon
7046) says so directly:

> ・Unsuccessful entrants can receive a 100% refund of their deposit **by accessing this placard**
> during the results period.
>
> ・Should you fail to claim your plot of land during the results period despite having won the
> lottery, your claim will be forfeit and a 50% cancellation fee will be deducted from the deposit
> made at entry. You will be refunded the remaining 50% **upon accessing this placard**.

That is why outcomes are settled on placard reads rather than by timing, and why anything resolved by
the deadline sweep is marked *inferred* rather than asserted.

---

## How it works

### Signals, in descending order of trust

**1. Placard sale-info hook — primary.** `Housing/PlacardSaleHook.cs` hooks
`HandlePlacardSaleInfo` (PaissaHouse signature `41 0F 10 06 0F 11 43 48 41 0F 10 4E 10 0F 11 4B 58`,
function start at `-0xA8`). Opening a placard both registers the bid and settles it. Results and
claim dates come from `PhaseEndsAt` rather than a hardcoded cycle anchor.

If the signature breaks after a game patch the hook logs a warning and doesn't install; everything
else keeps working.

**2. The FC's owned house.** `Configuration.UpdateCurrentFCData()` already reads
`HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate)`. When the FC owns a plot one of its
bids was on, that bid was claimed. `LotteryTracker.SyncClaimedFromOwnedHouse` runs on every FC
refresh and costs nothing extra.

**3. Game log messages — fallback.** Via `IChatGui.LogMessage` (`Dalamud.Game.Chat.ILogMessage`),
matched by `LogMessageId` and typed parameters rather than regex, so it works in every client
language:

| Id | Text | Meaning |
|---|---|---|
| 3360 / 3365 / 3370 | `You purchase the deed to plot <n>, ward <n>, <district> for the company.` | Claimed |
| 1486 | `You are refunded <gil>.` | Refund collected |
| 1853 | `<n> gil is placed into the company chest.` | FC refund collected |
| 4285 | refund expired after 90 days | Lost |

This is a **fallback only**. A refund can only reach you at the placard, where signal 1 has already
settled the bid more precisely — the byte separates `Loser` from `WinnerForfeit`, the message doesn't.
It earns its place when the hook fails to install.

The refund *amount* distinguishes the two cases, per the entry notice: 100% of the deposit means the
lottery was lost (Addon 7124), 50% means a won plot whose claim window lapsed (Addon 7123). Both
resolve to `Lost`, with a reason string saying which happened.

Because 1486 and 1853 are generic — company chest gil moves for plenty of reasons — a bid is only
implicated when the amount is *exactly* its recorded deposit or exactly half of it **and** its results
phase has opened. Anything else is logged at debug and ignored.

**4. Deadline sweep — last resort.** `LotteryTracker.TickStaleOutcomes` marks a bid `Lost` once the
claim window closes with nothing observed. It sets `OutcomeSourceKind.Inferred`, and the UI renders
it with a trailing `?` and a tooltip saying so.

### Capturing the deposit

The entry confirmation dialog (`SelectYesNoTextScroll`, Addon 7046) is the only place the game states
the deposit amount, which the refund matcher needs. It's read on `PostSetup` and stashed for five
minutes so the placard read that follows the confirmation can attach it to the bid.

### Data model

`Housing/LotteryBidRecord.cs`, stored as `FCData.LotteryBids`. Living on `FCData` means it rides the
existing `EzConfig` save and the cross-account import (`DataImportConfig`) for free — no new
persistence layer, and HousingLottoTracker's `SharedStore.cs` is not needed.

Identity for upsert is `(BidderCID, TerritoryTypeId, Ward, Plot, CycleKey)`.

**`CycleKey` is derived from the observed results date**, not a clock anchor. HousingLottoTracker
anchors a 9-day cycle at `2026-06-18 15:00Z` while FC Tracker anchors at `2026-06-01 15:00` — 17 days
apart, not a multiple of 9, so at least one is wrong. Both the placard hook and the phase deadline
supply the real date, so no anchor guessing is needed.

`OutcomeSourceKind` (`None` / `Observed` / `Inferred`) is what lets the UI distinguish a real result
from a guess. That distinction is the whole point of the rewrite.

### FC vs personal

`TenantType` describes who the **plot** is open to, not who your entry was for. A plot restricted to
private buyers can't hold an FC bid and is skipped; an unrestricted plot is ambiguous, so it's
recorded with `PlotRestrictedToFC = false` and the tooltip says the game doesn't state which kind the
entry was. HousingLottoTracker guessed from chat wording and got it wrong often enough to need a
manual override.

### UI

`UI/Views/ReadyNowView.cs`, seven columns:

```
Status 75 | Free Company 420 | FC Points 80 | Bid 240 | Result 95 | ⟶ 34 | spacer
```

- **World separators** use the same sentinel-row idiom as `AllFCsView`. The group count is of rows
  actually drawn, not the global per-world total — All FCs prints the latter and can disagree with
  its own body under a filter.
- **Result** is a coloured badge; Ctrl-click cycles it to correct one by hand.
- **⟶** is a Lifestream button, replacing the invisible `Selectable`.
- FCs stay listed for a cycle after a bid resolves. Claiming a plot sets `HasHouse`, which clears
  `IsEligible`, so without that the FC would vanish exactly when its result became interesting.

`Housing/LifestreamNavigator.cs` holds the login-then-travel `TaskManager` chain, extracted out of
`AllFCsView` so both views share one copy. Ward and plot are 0-based in FC Tracker's data and 1-based
in Lifestream's address tuple; that conversion lives in one place now.

---

## Deliberately not ported

From HousingLottoTracker: `ChatLotteryParser.cs` (parses a message that doesn't exist),
`LottoCycle.cs` (drifting anchor), `PlacardReader.cs` text scraping (superseded by the hook),
`LotteryStatusParser.cs`, `SharedStore.cs`, and `PaissaClient.cs` / `AlertWatcher.cs` (open-plot
alerts, out of scope).

HouseHunter IPC is **kept** as a fallback for FCs with no local record, so nothing regresses for
existing users.

---

## Verifying the result byte

The `0x03` mapping is community-reverse-engineered. Every placard read logs the raw value:

```
[FCTracker lottery] placard T641 W30 P38 purchase=Lottery tenant=FreeCompany avail=InResultsPeriod
                    playerResult=Loser(raw 4) entries=12 phaseEnds=2026-06-25 03:00:00Z
```

One results cycle with `/xllog` open at debug level confirms it. The refund path is an independent
second signal if the mapping turns out wrong.

## Building

The three submodules must be checked out first:

```bash
git submodule update --init --recursive
dotnet build --configuration Release FCTracker/FCTracker.csproj   # needs DALAMUD_HOME
```

---

## References

- Game data: [xivapi/ffxiv-datamining](https://github.com/xivapi/ffxiv-datamining) — `LogMessage.csv`, `Addon.csv`
- Placard struct and signature: [PaissaHouse](https://github.com/zhudotexe/FFXIV_PaissaHouse) `Structures/Lottery.cs`
- Result byte naming: [Natalan](https://github.com/Veiyna/Natalan) `WorldServer/Game/Housing/Enums/LandLotteryPlayerResult.cs`
- `ILogMessage`: [Dalamud](https://github.com/goatcorp/Dalamud) `Dalamud/Game/Chat/LogMessage.cs`
- Lifestream address tuple: [Lifestream](https://github.com/NightmareXIV/Lifestream) `Lifestream/Data/AddressBookEntry.cs`
- Starting point: [HousingLottoTracker](https://github.com/dexcss/HousingLottoTracker)
