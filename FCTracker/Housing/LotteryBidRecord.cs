namespace FCTracker.Housing;

using System;
using Newtonsoft.Json;

public enum LotteryOutcome
{
    Pending,
    Won,
    Lost,
    Claimed,
}

/// <summary>
/// Where an outcome came from. Observed means the game told us; Inferred means we
/// gave up waiting. The UI shows the difference so a timeout never reads as a result.
/// </summary>
public enum OutcomeSourceKind
{
    None,
    Observed,
    Inferred,
}

/// <summary>
/// One FC housing lottery entry, placed by one of your characters on one plot in one cycle.
/// Lives on <see cref="FCData.LotteryBids"/> so it rides the existing EzConfig save and the
/// cross-account import.
/// </summary>
[JsonObject(MemberSerialization.OptOut)]
public class LotteryBidRecord
{
    /// <summary>Which of your characters placed the bid.</summary>
    public ulong BidderCID { get; set; }

    public FCData.HouseInfo.ResidentialAetheryteKind City { get; set; }
    public uint TerritoryTypeId { get; set; }

    /// <summary>0-based, matching <see cref="FCData.HouseInfo.Ward"/>.</summary>
    public byte Ward { get; set; }

    /// <summary>0-based, matching <see cref="FCData.HouseInfo.Plot"/>.</summary>
    public byte Plot { get; set; }

    /// <summary>Your ticket number. The game only shows it once, at bid time. -1 = unknown.</summary>
    public int EntryNumber { get; set; } = -1;

    /// <summary>Deposit in gil, read from the entry confirmation dialog. 0 = unknown.</summary>
    public long Price { get; set; }

    /// <summary>
    /// True when the plot itself is restricted to free companies. An unrestricted plot accepts
    /// both FC and personal entries, and the placard doesn't say which kind ours was - the
    /// tooltip flags those rather than asserting a type we can't observe.
    /// </summary>
    public bool PlotRestrictedToFC { get; set; }

    public DateTime  EntryDateUtc        { get; set; } = DateTime.UtcNow;
    public DateTime? ResultsAvailableUtc { get; set; }
    public DateTime? ClaimDeadlineUtc    { get; set; }

    /// <summary>
    /// Identifies the lottery cycle. Derived from the observed results date rather than a
    /// hardcoded clock anchor, so it never drifts.
    /// </summary>
    public string CycleKey { get; set; } = string.Empty;

    public LotteryOutcome Outcome            { get; set; } = LotteryOutcome.Pending;
    public OutcomeSourceKind OutcomeSource { get; set; } = OutcomeSourceKind.None;
    public DateTime?      OutcomeRecordedUtc { get; set; }

    /// <summary>Short human-readable reason for the current outcome, shown in the row tooltip.</summary>
    public string OutcomeReason { get; set; } = string.Empty;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public static string BuildCycleKey(DateTime? resultsUtc, DateTime entryUtc) =>
        (resultsUtc ?? entryUtc).ToString("yyyy-MM-dd");

    [JsonIgnore]
    public bool IsResolved => this.Outcome != LotteryOutcome.Pending;

    [JsonIgnore]
    public bool ResultsOpen =>
        this.ResultsAvailableUtc.HasValue && DateTime.UtcNow >= this.ResultsAvailableUtc.Value;

    /// <summary>
    /// The enum is Lifestream's aetheryte list, where Mist is "Limsa" and Empyreum is
    /// "Foundation". Show what the game calls the district instead.
    /// </summary>
    [JsonIgnore]
    public string DistrictName => this.City switch
    {
        FCData.HouseInfo.ResidentialAetheryteKind.Limsa        => "Mist",
        FCData.HouseInfo.ResidentialAetheryteKind.LavenderBeds => "The Lavender Beds",
        FCData.HouseInfo.ResidentialAetheryteKind.Goblet       => "The Goblet",
        FCData.HouseInfo.ResidentialAetheryteKind.Shirogane    => "Shirogane",
        FCData.HouseInfo.ResidentialAetheryteKind.Foundation   => "Empyreum",
        _                                                      => this.City.ToString(),
    };

    [JsonIgnore]
    public string LocationText => $"{this.DistrictName} · Ward {this.Ward + 1} · Plot {this.Plot + 1}";

    [JsonIgnore]
    public string OutcomeText => this.Outcome switch
    {
        LotteryOutcome.Won     => "WON",
        LotteryOutcome.Lost    => "LOST",
        LotteryOutcome.Claimed => "CLAIMED",
        _                      => "PENDING",
    };

    public void Resolve(LotteryOutcome outcome, OutcomeSourceKind source, string reason)
    {
        this.Outcome            = outcome;
        this.OutcomeSource      = source;
        this.OutcomeReason      = reason;
        this.OutcomeRecordedUtc = DateTime.UtcNow;
    }
}
