namespace FCTracker;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using IPC;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using NightmareUI.Censoring;
using Services;
using UI;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

[JsonObject(MemberSerialization.OptIn)]
public class Configuration
{
    public static Configuration Instance { get; set; } = null!;

    [JsonProperty]
    public GatheredData GatheredData { get; set; } = new();
    public List<GatheredData> ImportedData { get; set; } = [];

    [JsonProperty]
    public List<DataImportConfig> DataImportConfig {get; set; } = [];

    [JsonProperty]
    public int ConfigVersion { get; set; } = 0;

    [JsonProperty]
    public CharViewData CharViewData { get; set; }

    [JsonProperty]
    public GlobalData GlobalData { get; set; } = new();

    public static  AutoRetainerAPI.AutoRetainerApi AR_API = new();

    private static ARData? arData;

    public static ARData ARData
    {
        get
        {
            if (arData.HasValue)
                return arData.Value;

            ARData data = new();

            foreach (FCData fcData in Instance.AllFCData.Values)
            {
                ARData? fcARData = fcData.AutoRetainerData;
                data.RepairCount += fcARData?.RepairCount ?? 0;
                data.FuelCount += fcARData?.FuelCount ?? 0;
            }
            arData = data;

            return arData.Value;
        }
    }

    public void RefreshImportedData()
    {
        this.ImportedData.Clear();

        foreach (DataImportConfig config in this.DataImportConfig)
        {
            GatheredData? data = config.LoadData();
            if (data != null)
                this.ImportedData.Add(data);
        }

        ARDataBust();
    }

    public static void ARDataBust() => arData = null;
    
    public Dictionary<ulong, CharData> AllCharData
    {
        get
        {
            Dictionary<ulong, CharData> allCharData = new(this.GatheredData.CharByCID);
            foreach (GatheredData data in this.ImportedData)
                allCharData.AddRange(data.CharByCID);

            return allCharData;
        }
    }

    public Dictionary<ulong, FCData> AllFCData
    {
        get
        {
            Dictionary<ulong, FCData> allFCData = new(this.GatheredData.FCData);
            foreach (GatheredData data in this.ImportedData)
                allFCData.AddRange(data.FCData);

            return allFCData;
        }
    }

    public ulong? GetFCIdForCID(ulong cid) => 
        this.GatheredData.CharByCID.TryGetValue(cid, out CharData charData) ? charData.FC : null;
    
    public void ClearData() => 
        this.GatheredData.FCData.Clear();

    public void RemoveCurrentFCData()
    {
        if(Player.Available)
            if(this.GatheredData.CharByCID.TryGetValue(Player.CID, out CharData charData))
                if(charData.FC.HasValue)
                    this.GatheredData.FCData.Remove(charData.FC.Value);
    }

    public void UpdateCharData(CharData ch)
    {
        this.GatheredData.CharByCID[ch.CID] = ch;
        this.Save();
    }

    public void UpdateCurrentCharData()
    {
        if(this.GlobalData.ExcludedChars.Contains(Player.CID))
            return;

        if(!this.GatheredData.CharByCID.TryGetValue(Player.CID, out CharData charData))
            charData = new CharData { CID = Player.CID };

        charData.InventoryData.RefreshInventoryData();
        HouseHunterIPC.Instance.ClearCacheFor(Player.CID);

        this.GatheredData.CharByCID[Player.CID] = charData with
                                                  {
                                                      Name = Player.Name!,
                                                      WorldId = Player.HomeWorld.RowId,
                                                      GrandCompany = (GrandCompany)Player.GrandCompany,
                                                      GrandCompanyRank = PlayerHelper.GetGrandCompanyRank(),
                                                      LeveAllowances = Math.Min(100, PlayerHelper.LeveAllowances + 3),
                                                      LeveAllowanceTime = QuestManager.GetNextLeveAllowancesDateTime(),
                                                      LevelsCombat = PlayerHelper.GetHighestCombatLevelsFromSheet(),
                                                      LevelsGathering = PlayerHelper.GetGatheringLevelsFromSheet()
                                                  };
        this.Save();
    }

    public unsafe void UpdateCurrentFCData()
    {
        if (!Player.Available)
            return;

        if (this.GlobalData.ExcludedChars.Contains(Player.CID))
            return;

        InfoProxyFreeCompany* fcProxy = InfoProxyFreeCompany.Instance();

        if (fcProxy->Id == 0)
            return;

        static void VerboseLog(string message) => Svc.Log.Verbose($"[FC Data Collection] {message}");

        VerboseLog("Updating FC data");

        this.UpdateCurrentCharData();

        this.GatheredData.CharByCID[Player.CID] = this.GatheredData.CharByCID[Player.CID] with {FC = fcProxy->Id};

        if (!this.GatheredData.FCData.TryGetValue(fcProxy->Id, out FCData? fcData))
            fcData = new FCData
                     {
                         HomeWorldId  = fcProxy->HomeWorldId,
                         Id           = fcProxy->Id,
                         GrandCompany = fcProxy->GrandCompany,
                     };

        VerboseLog("Initial Setup");

        fcData.MemberCIDs.Add(Player.CID);

        if(!fcProxy->NameString.IsNullOrEmpty())
            fcData.FCName = fcProxy->NameString;
        fcData.TotalMembers = fcProxy->TotalMembers;
        fcData.MasterString = fcProxy->MasterString;
        fcData.Rank         = fcProxy->Rank;

        VerboseLog("Getting Founding Date");

        int date = AgentFreeCompanyProfile.Instance()->FoundationDate;
        fcData.FoundingDate = DateTimeOffset.FromUnixTimeSeconds(date).DateTime;

        VerboseLog("Getting Tag Data");
        StringArrayData* arrayDataString = RaptureAtkModule.Instance()->GetStringArrayData(48);
        if (arrayDataString->Size > 1)
        {
            CStringPointer x        = arrayDataString->StringArray[2];
            SeString       seString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(x));

            fcData.Tag = seString.GetText();
        }

        VerboseLog("Getting Member Data");

        arrayDataString = RaptureAtkModule.Instance()->GetStringArrayData(37);
        NumberArrayData* arrayDataNumber = RaptureAtkModule.Instance()->GetNumberArrayData(40);
        if (arrayDataString->Size > 1)
        {
            fcData.MemberData = [];
            for(int i = 0; i < Math.Min(200, fcData.TotalMembers); i++)
            {
                CStringPointer x        = arrayDataString->StringArray[i*5];
                SeString       seString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(x));

                fcData.MemberData.Add(new FCMemberData
                                      {
                                          Name   = seString.GetText(),
                                          FCRank = (byte) arrayDataNumber->IntArray[2406+i]
                                      });
            }
        }

        VerboseLog("Getting Rank Data");
        arrayDataString = RaptureAtkModule.Instance()->GetStringArrayData(43);
        if(arrayDataString->Size > 1)
            for(int i = 0; i < 15; i++)
            {
                CStringPointer x        = arrayDataString->StringArray[i];
                SeString       seString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(x));

                string text = seString.GetText();
                if(!string.IsNullOrEmpty(text))
                    fcData.RankData[i] = new FCRankData
                                         {
                                             ID   = (byte)i,
                                             Name = text
                                         };
            }

        VerboseLog("Getting Action Data");
        arrayDataNumber = RaptureAtkModule.Instance()->GetNumberArrayData(62);
        if (arrayDataNumber->Size > 1)
        {
            int activeActions = arrayDataNumber->IntArray[1];
            fcData.ActionsActive = new FCData.FCActionData[activeActions];
            for (int i = 0; i < activeActions; i++)
            {
                fcData.ActionsActive[i] = new FCData.FCActionData
                                          {
                                              ActionId         = (uint)arrayDataNumber->IntArray[2 + i * 3],
                                              Unk              = arrayDataNumber->IntArray[3 + i * 3],
                                              ExpirationTime   = DateTime.Now.Add(TimeSpan.FromSeconds(arrayDataNumber->IntArray[4 + i * 3]))
                                          };
            }

            int availableActions = arrayDataNumber->IntArray[11];
            fcData.ActionsAvailable = new FCData.FCActionData[availableActions];
            for (int i = 0; i < availableActions; i++)
            {
                fcData.ActionsAvailable[i] = new FCData.FCActionData
                                             {
                                                 ActionId         = (uint)arrayDataNumber->IntArray[12 + i * 3],
                                                 Unk              = arrayDataNumber->IntArray[13 + i * 3],
                                                 ExpirationTime   = DateTime.Now.Add(TimeSpan.FromSeconds(arrayDataNumber->IntArray[14 + i * 3]))
                                             };
            }
        }

        VerboseLog("Getting FC Points");

        fcData.FCPoints = *(int*)((nint)AgentModule.Instance()->GetAgentByInternalId(AgentId.FreeCompanyCreditShop) + 256);

        VerboseLog("Getting House Info");

        HouseId houseId = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
        if (houseId.Unit.Value < 255)
        {
            FCData.HouseInfo.ResidentialAetheryteKind? aetheryteKind = FCData.HouseInfo.GetResidentialAetheryteByTerritoryType(houseId.TerritoryTypeId);
            if (!fcData.HasHouse                        ||
                fcData.House!.Ward != houseId.WardIndex ||
                fcData.House.Plot != houseId.PlotIndex  ||
                fcData.House.City != aetheryteKind)
            {
                FCData.HouseInfo houseInfo = new()
                                             {
                                                 Ward = houseId.WardIndex,
                                                 Plot = houseId.Unit.PlotIndex
                                             };
                if (aetheryteKind != null)
                    houseInfo.City = aetheryteKind.Value;
                fcData.House = houseInfo;
            }
        }

        this.GatheredData.FCData[fcProxy->Id] = fcData;

        this.Save();
    }

    public void Save()
    {
        EzConfig.Save();
    }
}

public class FCTrackerSerializationFactory : DefaultSerializationFactory, ISerializationFactory
{
    public override string DefaultConfigFileName { get; } = "FCTrackerConfig.json";

    public new string Serialize(object config) =>
        base.Serialize(config);

    public override byte[] SerializeAsBin(object config) =>
        Encoding.UTF8.GetBytes(this.Serialize(config));
}

[JsonObject(MemberSerialization.OptOut)]
public class DataImportConfig
{
    public bool Enabled { get; set; }
    public required string Path { get; set; }

    [JsonIgnore]
    public GatheredData? Data;

    public GatheredData? LoadData()
    {
        if (!this.Enabled)
            return null;
        try
        {

            if(File.Exists(this.Path))
            {
                string json;
                using (StreamReader streamReader = new(this.Path, Encoding.UTF8))
                    json = streamReader.ReadToEnd();

                Configuration? node    = JsonConvert.DeserializeObject<Configuration>(json);
                if (node?.GatheredData != null)
                {
                    node.GatheredData.ImportSourceConfig = this;

                    foreach (ulong fcID in node.GatheredData.FCData.Keys)
                        node.GatheredData.FCData[fcID].SourceData = node.GatheredData;

                    foreach (ulong cid in node.GatheredData.CharByCID.Keys)
                        node.GatheredData.CharByCID[cid] = node.GatheredData.CharByCID[cid] with { SourceData = node.GatheredData };

                    return this.Data = node.GatheredData;
                }
            }
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, $"Error occurred while loading data from {this.Path}");
            return null;
        }
        return null;
    }
}

[JsonObject(MemberSerialization.OptOut)]
public class GatheredData()
{
    [JsonIgnore]
    public DataImportConfig? ImportSourceConfig;

    public Dictionary<ulong, CharData> CharByCID { get; set; } = [];

    public Dictionary<ulong, FCData> FCData { get; set; } = [];
}

[JsonObject(MemberSerialization.OptOut)]
public class GlobalData
{
    public bool GatherDataSelf = true;
    public List<ulong> ExcludedChars { get; set; } = [];
}

[JsonObject(MemberSerialization.OptOut)]
public struct CharViewData
{
    public bool CharsWithFC;
    public bool CharsWithFCWithHouse;

    public bool ShowOnlyHighestCombatLevel;
}

[JsonObject(MemberSerialization.OptOut)]
public struct CharData()
{
    [JsonIgnore]
    public GatheredData SourceData
    {
        get => field ??= Configuration.Instance.GatheredData;
        set;
    }

    public required ulong  CID     = 0;
    public          string Name    = null;
    public          uint   WorldId = 0;
    public          ulong? FC      = null;

    public InventoryTrackingData InventoryData
    {
        get => field ??= new InventoryTrackingData()
                         {
                             CID = this.CID
                         };
        set;
    }


    public GrandCompany GrandCompany     = GrandCompany.None;
    public uint         GrandCompanyRank = 0;

    public List<(uint cj, short level)> LevelsCombat
    {
        get => field;
        set
        {
            this.LevelsCombatResolved = null;
            field                     = value;
        }
    }

    [JsonIgnore]
    public List<(ClassJob cj, short level)> LevelsCombatResolved
    {
        get => field ??= this.LevelsCombat.Select(x => (Svc.Data.Excel.GetSheet<ClassJob>()!.GetRow(x.cj), x.level)).ToList();
        set;
    }

    public List<(uint cj, short level)> LevelsGathering
    {
        get => field;
        set
        {
            this.LevelsGatheringResolved = null;
            field = value;
        }
    }

    [JsonIgnore]
    public List<(ClassJob cj, short level)> LevelsGatheringResolved
    {
        get => field ??= this.LevelsGathering.Select(x => (Svc.Data.Excel.GetSheet<ClassJob>()!.GetRow(x.cj), x.level)).ToList();
        set;
    }

    public int      LeveAllowances    = 0;
    public DateTime LeveAllowanceTime = default;


    [JsonIgnore]
    public World? World => field ??= ExcelWorldHelper.Get(this.WorldId);
    [JsonIgnore]
    public string WorldName => this.World?.Name.ExtractText() ?? "???";

    public string GetName() =>
        this.Name.Length != 0 ? Censor.Character(this.Name, this.WorldName) : this.CID.ToString();

    public int LeveAllowancesNow
    {
        get
        {
            if (this.LeveAllowances is <= 0 or >= 100)
                return this.LeveAllowances;

            TimeSpan timePassed = DateTime.UtcNow - this.LeveAllowanceTime;
            return timePassed.Ticks <= 0 ?
                       this.LeveAllowances - 3 :
                       timePassed.TotalDays > 30 ?
                           100 :
                           Math.Min(100, this.LeveAllowances + (int)(timePassed.TotalHours / 12));
        }
    }

    public readonly override int GetHashCode() =>
        this.CID.GetHashCode();
}

[JsonObject(MemberSerialization.OptOut)]
public struct FCMemberData
{
    public required string Name   { get; set; }
    public required byte   FCRank { get; set; }
}

[JsonObject(MemberSerialization.OptOut)]
public struct FCRankData
{
    public required byte ID { get; set; }
    public required string Name { get; set; }
}

[JsonObject(MemberSerialization.OptOut)]
public struct ARData
{
    public int RepairCount { get; set; }
    public int FuelCount   { get; set; }
}

public enum HousingStatusCategory
{
    Ready,   // 30+ days, rank ≥6, no house
    Soon,    // <=7 days remaining
    Waiting, // >7 days remaining
    NeverVisited,
    VisitedIn7Days,
    VisitedIn30Days,
    VisitedIn40Days,
    DemolitionImminent,
}

[JsonObject(MemberSerialization.OptOut)]
public class FCData
{
    [JsonIgnore]
    private GatheredData? sourceData;
    [JsonIgnore] 
    public GatheredData SourceData { 
        get => this.sourceData ??= Configuration.Instance.GatheredData;
        set => this.sourceData = value;
    }

    [JsonIgnore]
    public World? World => field ??= ExcelWorldHelper.Get(this.HomeWorldId);
    public ulong        Id                               { get; set; }
    public string       FCName                           { get; set; } = string.Empty;
    public string       Tag                              { get; set; } = string.Empty;
    public uint         TotalMembers                     { get; set; }
    public string       MasterString                     { get; set; } = string.Empty;
    public uint         HomeWorldId                      { get; set; }
    public GrandCompany GrandCompany                     { get; set; }
    public uint         Rank                             { get; set; }
    public DateTime     FoundingDate                     { get; set; }
    public DateTime?    EligibilityDateReferenceOverride { get; set; }

    [JsonIgnore]
    public DateTime EligibilityDateReference => this.EligibilityDateReferenceOverride ?? this.FoundingDate;

    public int FCPoints { get; set; }

    [JsonIgnore]
    public HashSet<CharData>? MemberCIDsResolved
    {
        get => field ??= this.MemberCIDs.Select(cid => Configuration.Instance.AllCharData.GetValueOrDefault(cid)).Where(d => d.CID != 0).Select(d => d!).ToHashSet();
        set;
    }

    public HashSet<ulong>        MemberCIDs { get;      set; } = [];
    public HashSet<FCMemberData> MemberData { get;      set; } = [];

    [JsonProperty]
    private ARData? autoRetainerData;

    [JsonIgnore]
    public ARData? AutoRetainerData
    {
        get
        {
            if(this.autoRetainerData.HasValue)
                return this.autoRetainerData.Value;

            if (Configuration.AR_API.Ready && this.MemberCIDs.Count != 0)
            {
                ARData arData = new();

                foreach (ulong memberCID in this.MemberCIDs)
                {
                    OfflineCharacterData data = Configuration.AR_API.GetOfflineCharacterData(memberCID);
                    if(data != null)
                    {
                        arData.RepairCount += data.RepairKits;
                        arData.FuelCount   += data.Ceruleum;
                    }
                }

                this.autoRetainerData = arData;
            }
            return this.autoRetainerData;
        }
    }

    [JsonObject(MemberSerialization.OptOut)]
    public class HouseInfo
    {
        // From lifestream
        public enum ResidentialAetheryteKind
        {
            Goblet       = 9,
            LavenderBeds = 2,
            Limsa        = 8,
            Foundation   = 70,
            Shirogane    = 111,
        }

        public static ResidentialAetheryteKind? GetResidentialAetheryteByTerritoryType(uint territoryType)
        {
            TerritoryType? t = ExcelTerritoryHelper.Get(territoryType);
            if (t == null) 
                return null;
            return t.Value.PlaceNameRegion.RowId switch
            {
                2402 => ResidentialAetheryteKind.Shirogane,
                25 => ResidentialAetheryteKind.Foundation,
                23 => ResidentialAetheryteKind.LavenderBeds,
                24 => ResidentialAetheryteKind.Goblet,
                22 => ResidentialAetheryteKind.Limsa,
                _ => null
            };
        }

        public ResidentialAetheryteKind City { get; set; }
        public byte Ward { get; set; }
        public byte Plot { get; set; }
        public DateTime? LastVisited { get; set; }

        public void Visited() => 
            this.LastVisited = DateTime.UtcNow;

        [JsonIgnore]
        public int DaysSinceLastVisit =>
            this.LastVisited.HasValue ? (int)(DateTime.UtcNow - this.LastVisited.Value).TotalDays : int.MaxValue;

        public HousingStatusCategory GetVisitationStatus()
        {
            if (this.LastVisited == null)
                return HousingStatusCategory.NeverVisited;

            TimeSpan timeSinceVisit = DateTime.UtcNow - this.LastVisited.Value;
            return timeSinceVisit.Days switch
            {
                < 7 => HousingStatusCategory.VisitedIn7Days,
                < 30 => HousingStatusCategory.VisitedIn30Days,
                < 40 => HousingStatusCategory.VisitedIn40Days,
                _ => HousingStatusCategory.DemolitionImminent,
            };
        }
    }

    public HouseInfo? House { get; set; }

    [JsonIgnore]
    public string WorldName => this.World?.Name.ToString() ?? "??";

    [JsonIgnore]
    public string Datacenter => this.World?.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty;

    [JsonIgnore]
    public string Region => FCTrackerTheme.RegionString(this.World);

    [JsonIgnore]
    public bool HasHouse => 
        this.House != null;

    [JsonIgnore]
    public TimeSpan TimeSinceFounded =>
        this.FoundingDate == default ?
            TimeSpan.Zero :
            DateTime.Now - this.FoundingDate;

    [JsonIgnore]
    public TimeSpan TimeSinceEligibilityReference =>
        this.EligibilityDateReference == default ?
            TimeSpan.Zero :
            DateTime.Now - this.EligibilityDateReference;

    [JsonIgnore]
    public TimeSpan TimeUntilEligible => 
        new TimeSpan(30, 0, 0, 0) - this.TimeSinceEligibilityReference;

    [JsonIgnore]
    public bool IsEligible => 
        this.TimeSinceEligibilityReference.TotalDays >= 30 && this.Rank >= 6 && !this.HasHouse;

    [JsonIgnore]
    public DateTime EligibilityDate => 
        this.EligibilityDateReference == default ? DateTime.MaxValue : this.EligibilityDateReference.AddDays(30);

    [JsonIgnore]
    private bool? masterAvailable;
    [JsonIgnore]
    private ulong masterCID;

    [JsonIgnore]
    public bool MasterAvailable
    {
        get
        {
            if (this.masterAvailable.HasValue)
                return this.masterAvailable.Value;

            
            if (this.SourceData.ImportSourceConfig == null)
                foreach (KeyValuePair<ulong, CharData> ch in Configuration.Instance.GatheredData.CharByCID)
                    if (this.MemberCIDs.Contains(ch.Value.CID) && ch.Value.Name == this.MasterString)
                    {
                        this.masterCID       = ch.Value.CID;
                        this.masterAvailable = true;
                        break;
                    }

            return this.masterAvailable ??= false;
        }
    }

    [JsonObject(MemberSerialization.OptOut)]
    public class FCActionData
    {
        public  uint          ActionId { get; set; }

        [JsonIgnore]
        public CompanyAction? Action => field ??= Svc.Data.GetExcelSheet<CompanyAction>().GetRow(this.ActionId);

        public int      Unk              { get; set; }
        public DateTime ExpirationTime   { get; set; }
    }
    public FCActionData[] ActionsActive { get; set; } = [];
    public FCActionData[] ActionsAvailable { get; set; } = [];

    public void AddMember(ulong cid)
    {
        this.MemberCIDs.Add(cid);
        this.masterAvailable    = null;
        this.MemberCIDsResolved = null;
    }

    public void RecacheARData()
    {
        this.autoRetainerData = null;
        Configuration.ARDataBust();
    }

    public HousingStatusCategory GetStatusCategory() => 
        this.HasHouse ?
            this.House!.GetVisitationStatus() : 
            this.IsEligible ? 
                HousingStatusCategory.Ready : 
                this.TimeUntilEligible.TotalDays <= 7 ? 
                    HousingStatusCategory.Soon : 
                    HousingStatusCategory.Waiting;

    public string GetHousingStatusText() =>
        this.HasHouse ?
            $"{this.House!.City} - Ward {this.House.Ward + 1} - Plot {this.House.Plot + 1}" :
            this.IsEligible ?
                Censor.Hide(HouseHunterIPC.Instance.GetLotteryTextForFC(this) ?? "Eligible", "Bidding") :
                this.EligibilityDateReference == default ?
                    "Not yet founded" :
                    this.TimeSinceEligibilityReference.TotalDays >= 30 ?
                        "30d passed. Check Upcoming tab" :
                        $@"{this.@TimeUntilEligible:%d\d\ %h\h} left";

    public string GetHousingDemolitionText() =>
        this.HasHouse ?
            $"{(this.House!.LastVisited != null ? $"Last Visited: {(this.House.DaysSinceLastVisit > 0 ? $"{this.House.DaysSinceLastVisit}d ago" : "Today")}" : "Never visited")}" :
            string.Empty;

    [JsonIgnore]
    public bool LoggedIn =>
        FCTrackerPlugin.LoggedInCID.HasValue && this.MemberCIDs.Contains(FCTrackerPlugin.LoggedInCID.Value);

    public FCRankData?[] RankData { get; set; } = new FCRankData?[15];

    public string MembersString(bool external)
    {
            StringBuilder sb = new();
            sb.Append("FC Members:\n\t");

            if (this.MasterAvailable)
            {
                sb.Append(Censor.Character(this.MasterString));
                sb.Append($" ({this.RankData.SkipWhile(r => r == null).FirstOrDefault()?.Name})\n\t");
            }

            HashSet<string> memberNames = [this.MasterString];

            foreach (ulong memberCID in this.MemberCIDs)
                if(memberCID != this.masterCID && Configuration.Instance.AllCharData.TryGetValue(memberCID, out CharData charData) && 
                   !string.IsNullOrEmpty(charData.Name))
                {
                    memberNames.Add(charData.Name);
                    sb.Append(Censor.Character(charData.Name));

                    if (this.MemberData.FirstOrDefault(fcmd => fcmd.Name == charData.Name) is FCMemberData memberData)
                        sb.Append($" ({(this.RankData[memberData.FCRank] != null ? this.RankData[memberData.FCRank]!.Value.Name : memberData.FCRank)})");

                    sb.Append("\n\t");
                }

            if (external && this.MemberData.Count > this.MemberCIDs.Count)
            {
                sb.Append("\nOther FC Members:\n\t");

                foreach (FCMemberData member in this.MemberData)
                {
                    if (memberNames.Contains(member.Name) || string.IsNullOrEmpty(member.Name))
                        continue;
                    sb.Append(Censor.Character(member.Name));

                    sb.Append($" ({(this.RankData[member.FCRank] != null ? this.RankData[member.FCRank]!.Value.Name : member.FCRank)})");

                    sb.Append("\n\t");
                }
            }

            return sb.ToString().Trim();
    }
}