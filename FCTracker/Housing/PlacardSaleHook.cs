namespace FCTracker.Housing;

using System;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;

/// <summary>
/// Hooks the game's "handle placard sale info" function, which fires whenever a housing
/// placard is viewed. The server hands us a structured record with the lottery phase, the
/// tenant type (FC vs personal), the phase deadline and - crucially - <b>our own</b> result
/// for that plot, all as function arguments. That beats scraping addon text, and it is
/// locale independent.
///
/// Signature approach adapted from PaissaHouse. If it breaks on a game patch the hook
/// simply doesn't install and everything else keeps working.
/// </summary>
public sealed unsafe class PlacardSaleHook : IDisposable
{
    public enum HousingKind : byte
    {
        OwnedHouse           = 0,
        UnownedHouse         = 1,
        FreeCompanyApartment = 2,
        Apartment            = 3,
    }

    public enum PurchaseKind : byte
    {
        Unavailable = 0,
        FCFS        = 1,
        Lottery     = 2,
    }

    public enum TenantKind : byte
    {
        Unrestricted = 0,
        FreeCompany  = 1,
        Personal     = 2,
    }

    public enum AvailabilityKind : byte
    {
        Available       = 1,
        InResultsPeriod = 2,
        Unavailable     = 3,
    }

    /// <summary>
    /// Byte 0x03 of the sale info: the viewing player's own standing in this plot's lottery.
    /// PaissaHouse leaves it as "Unknown1"; the field is named and enumerated by the Natalan
    /// server reimplementation. This is the whole win/lose/claimed signal in one byte.
    /// </summary>
    public enum PlayerResultKind : byte
    {
        NoEntry       = 0,
        Entered       = 1,
        Winner        = 2,
        WinnerForfeit = 3,
        Loser         = 4,
        RefundExpired = 5,
    }

    public sealed class SaleInfo
    {
        public uint  TerritoryTypeId;
        public byte  WardId;           // 0-based, as the game passes it
        public byte  PlotId;           // 0-based
        public short ApartmentNumber;

        public HousingKind      HousingType;
        public PurchaseKind     PurchaseType;
        public TenantKind       TenantType;
        public AvailabilityKind Availability;
        public PlayerResultKind PlayerResult;

        public uint PhaseEndsAtUnix;   // unix seconds; for a lottery this is the phase deadline
        public uint EntryCount;

        public bool IsLottery     => this.PurchaseType == PurchaseKind.Lottery;
        public bool IsFreeCompany => this.TenantType   == TenantKind.FreeCompany;
        public bool InResults     => this.Availability == AvailabilityKind.InResultsPeriod;

        /// <summary>True when the viewing character has a stake in this plot's lottery.</summary>
        public bool HasOwnEntry => this.PlayerResult != PlayerResultKind.NoEntry;

        public DateTime? PhaseEndsAtUtc =>
            this.PhaseEndsAtUnix == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(this.PhaseEndsAtUnix).UtcDateTime;
    }

    private delegate void HandlePlacardSaleInfoDelegate(
        void* agentBase, byte housingType, ushort territoryTypeId, byte wardId, byte plotId,
        short apartmentNumber, IntPtr placardSaleInfoPtr, long a8);

    private Hook<HandlePlacardSaleInfoDelegate>? hook;

    // Movups pattern inside the function body; the function start sits at -0xA8.
    // Assigned by reflection via [Signature]; silence "never assigned".
#pragma warning disable CS0649
    [Signature("41 0F 10 06 0F 11 43 48 41 0F 10 4E 10 0F 11 4B 58")]
    private IntPtr movupsAddress;
#pragma warning restore CS0649

    private readonly Action<SaleInfo> onSale;

    public bool Installed { get; private set; }

    public PlacardSaleHook(Action<SaleInfo> onSale)
    {
        this.onSale = onSale;

        try
        {
            Svc.Hook.InitializeFromAttributes(this);

            if (this.movupsAddress != IntPtr.Zero)
            {
                this.hook = Svc.Hook.HookFromAddress<HandlePlacardSaleInfoDelegate>(this.movupsAddress - 0xA8, this.Detour);
                this.hook.Enable();
                this.Installed = true;
                Svc.Log.Information("[FCTracker lottery] placard sale-info hook installed.");
            }
            else
            {
                Svc.Log.Warning("[FCTracker lottery] placard sale-info signature not found; bid auto-capture is disabled.");
            }
        }
        catch (Exception e)
        {
            Svc.Log.Warning($"[FCTracker lottery] placard hook failed to install ({e.Message}); bid auto-capture is disabled.");
        }
    }

    public void Dispose()
    {
        try
        {
            this.hook?.Dispose();
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[FCTracker lottery] failed disposing placard hook");
        }

        this.hook      = null;
        this.Installed = false;
    }

    private void Detour(
        void* agentBase, byte housingType, ushort territoryTypeId, byte wardId, byte plotId,
        short apartmentNumber, IntPtr placardSaleInfoPtr, long a8)
    {
        this.hook!.Original(agentBase, housingType, territoryTypeId, wardId, plotId, apartmentNumber, placardSaleInfoPtr, a8);

        try
        {
            if (placardSaleInfoPtr == IntPtr.Zero)
                return;

            // PlacardSaleInfo layout, offsets within the struct:
            //   0x00 PurchaseType, 0x01 TenantType, 0x02 AvailabilityType, 0x03 PlayerResult,
            //   0x08 PhaseEndsAt (unix seconds), 0x10 EntryCount
            byte* p = (byte*)placardSaleInfoPtr;

            SaleInfo info = new()
                            {
                                TerritoryTypeId = territoryTypeId,
                                WardId          = wardId,
                                PlotId          = plotId,
                                ApartmentNumber = apartmentNumber,
                                HousingType     = (HousingKind)housingType,
                                PurchaseType    = (PurchaseKind)p[0x00],
                                TenantType      = (TenantKind)p[0x01],
                                Availability    = (AvailabilityKind)p[0x02],
                                PlayerResult    = (PlayerResultKind)p[0x03],
                                PhaseEndsAtUnix = *(uint*)(p + 0x08),
                                EntryCount      = *(uint*)(p + 0x10),
                            };

            // The player-result byte is community-reverse-engineered rather than officially
            // documented, so log the raw value next to everything else. If a plot ever reports
            // an outcome that disagrees with the game, this line is where to look.
            Svc.Log.Debug($"[FCTracker lottery] placard T{territoryTypeId} W{wardId + 1} P{plotId + 1} " +
                          $"purchase={info.PurchaseType} tenant={info.TenantType} avail={info.Availability} " +
                          $"playerResult={info.PlayerResult}(raw {p[0x03]}) entries={info.EntryCount} " +
                          $"phaseEnds={info.PhaseEndsAtUtc:u}");

            this.onSale(info);
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[FCTracker lottery] placard sale-info detour failed");
        }
    }
}
