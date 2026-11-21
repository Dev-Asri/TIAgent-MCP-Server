using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TIAgent.Models
{
    public class TiaVersionInfo
    {
        public string VersionKey { get; set; }
        public string DisplayName { get; set; }
        public string AssemblyVersionPrefix { get; set; }
        public string PortalInstallFolder { get; set; }
        public string ResolvedFolder { get; set; }
        public string EngineeringDllPath { get; set; }
        public string HmiDllPath { get; set; }
        public bool IsInstalledInProgramFiles { get; set; }

        public override string ToString()
        {
            string suffix = IsInstalledInProgramFiles ? " (Kurulu)" : " (PublicAPI)";
            return $"{DisplayName}{suffix}";
        }
    }

    public class TiaProcessItem
    {
        public int ProcessId { get; set; }
        public string Mode { get; set; }
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string VersionName { get; set; }
        public object RawProcess { get; set; }

        public override string ToString()
        {
            string proj = string.IsNullOrWhiteSpace(ProjectName) ? "(Proje Açık Değil)" : ProjectName;
            return $"PID: {ProcessId} | {proj} ({VersionName})";
        }
    }

    public class TiaProjectItem
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public string Version { get; set; }
        public object RawProjectObject { get; set; }

        public override string ToString() => Name ?? "(İsimsiz Proje)";
    }

    public class TiaPlcItem
    {
        public string Name { get; set; }
        public string DeviceName { get; set; }
        public string TypeName { get; set; }
        public object RawPlcSoftware { get; set; }
        public object RawDevice { get; set; }

        public override string ToString() => Name ?? DeviceName ?? "PLC";
    }

    public class StepLogItem : INotifyPropertyChanged
    {
        private string _time;
        private string _icon;
        private string _iconColor;
        private string _category;
        private string _jobText;
        private string _jobTextTr;
        private string _jobTextEn;
        private string _statusTr;
        private string _statusEn;

        public string Time
        {
            get => _time;
            set { _time = value; OnPropertyChanged(); }
        }

        public string Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(); }
        }

        public string IconColor
        {
            get => _iconColor;
            set { _iconColor = value; OnPropertyChanged(); }
        }

        public string Category
        {
            get => _category;
            set { _category = value; OnPropertyChanged(); }
        }

        public string JobText
        {
            get => _jobText;
            set { _jobText = value; OnPropertyChanged(); }
        }

        public string JobTextTr
        {
            get => _jobTextTr;
            set => _jobTextTr = value;
        }

        public string JobTextEn
        {
            get => _jobTextEn;
            set => _jobTextEn = value;
        }

        public string StatusTr
        {
            get => _statusTr;
            set => _statusTr = value;
        }

        public string StatusEn
        {
            get => _statusEn;
            set => _statusEn = value;
        }

        public void UpdateLanguage(string lang)
        {
            bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
            if (isEn)
            {
                if (!string.IsNullOrEmpty(_jobTextEn))
                    JobText = _jobTextEn;
                if (!string.IsNullOrEmpty(_statusEn))
                    Icon = _statusEn;
            }
            else
            {
                if (!string.IsNullOrEmpty(_jobTextTr))
                    JobText = _jobTextTr;
                if (!string.IsNullOrEmpty(_statusTr))
                    Icon = _statusTr;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PlcTagEntry : INotifyPropertyChanged
    {
        private string _name;
        private string _dataType = "Bool";
        private string _logicalAddress = "%I0.0";
        private string _comment;
        private string _direction = "Input"; // Input, Output, Memory
        private string _tableName = "TIAgent_IO_Tags";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string DataType
        {
            get => _dataType;
            set { _dataType = value; OnPropertyChanged(); }
        }

        public string LogicalAddress
        {
            get => _logicalAddress;
            set { _logicalAddress = value; OnPropertyChanged(); }
        }

        public string Address
        {
            get => _logicalAddress;
            set { _logicalAddress = value; OnPropertyChanged(); }
        }

        public string Comment
        {
            get => _comment;
            set { _comment = value; OnPropertyChanged(); }
        }

        public string Direction
        {
            get => _direction;
            set { _direction = value; OnPropertyChanged(); }
        }

        public string TableName
        {
            get => _tableName;
            set { _tableName = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PlcDbMemberEntry : INotifyPropertyChanged
    {
        private string _name;
        private string _dataType = "Bool";
        private string _startValue = "";
        private string _comment = "";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string DataType
        {
            get => _dataType;
            set { _dataType = value; OnPropertyChanged(); }
        }

        public string StartValue
        {
            get => _startValue;
            set { _startValue = value; OnPropertyChanged(); }
        }

        public string Comment
        {
            get => _comment;
            set { _comment = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PlcDbEntry
    {
        public string Name { get; set; }
        public string DbType { get; set; } = "GlobalDB"; // "GlobalDB" or "InstanceDB"
        public string InstanceOfFbName { get; set; }
        public string Comment { get; set; }
        public List<PlcDbMemberEntry> Members { get; set; } = new List<PlcDbMemberEntry>();

        /// <summary>
        /// Bellek düzeni: "Optimized" veya "Standard" (Non-Optimized S7-TCP ofset erişimi için).
        /// </summary>
        public string MemoryLayout { get; set; } = "Optimized";

        /// <summary>
        /// Sabit DB numarası (DB1, DB2 vb. - S7-TCP bayt ofset erişimi için).
        /// </summary>
        public int? BlockNumber { get; set; }

        /// <summary>
        /// Doğrudan SimaticML GlobalDB XML içeriği (eğer dışarıdan hazır XML verildiyse).
        /// </summary>
        public string SimaticMlXml { get; set; }

        public override string ToString()
        {
            if (string.Equals(DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase))
                return $"{Name} [Instance DB -> {InstanceOfFbName}]";
            string layoutInfo = string.Equals(MemoryLayout, "Standard", StringComparison.OrdinalIgnoreCase) ? " · Standard" : "";
            string numInfo = BlockNumber.HasValue ? $" · DB{BlockNumber.Value}" : "";
            return $"{Name} [Global DB - {Members?.Count ?? 0} Değişken{layoutInfo}{numInfo}]";
        }
    }

    public class BlockInterfaceMember
    {
        public string Section { get; set; } = "Input"; // Input, Output, InOut, Static, Temp, Constant
        public string Name { get; set; }
        public string DataType { get; set; } = "Bool";
        public string DefaultValue { get; set; }
        public string Comment { get; set; }
    }

    public class VisualLadderElement
    {
        public string ElementType { get; set; } // "NO", "NC", "BranchStart", "BranchOr", "BranchEnd", "Coil", "SetCoil", "ResetCoil", "Box"
        public string TagName { get; set; }
        public string AddressOrType { get; set; }
        public string BoxTitle { get; set; }
        public List<string> BoxPins { get; set; } = new List<string>();
    }

    public class VisualLadderNetwork
    {
        public int NetworkNumber { get; set; }
        public string Title { get; set; }
        public string Comment { get; set; }
        public string Language { get; set; } = "LAD"; // "LAD" or "SCL"
        public string SclPreview { get; set; }
        public string AsciiLadderDiagram { get; set; }
        public List<VisualLadderElement> Elements { get; set; } = new List<VisualLadderElement>();
    }

    public class PlcBlockEntry
    {
        public string Name { get; set; }
        public string BlockType { get; set; } = "FB"; // "FB", "FC", "OB"
        public int? BlockNumber { get; set; }
        public string Language { get; set; } = "LAD"; // "LAD" or "SCL"
        public string Comment { get; set; }
        public string FolderPath { get; set; }
        public List<BlockInterfaceMember> InterfaceMembers { get; set; } = new List<BlockInterfaceMember>();
        public List<VisualLadderNetwork> Networks { get; set; } = new List<VisualLadderNetwork>();
        public string SclCode { get; set; }
        public string SimaticMlXml { get; set; }

        // If this entry represents adding/updating a call in Main [OB1]
        public bool IsMainObCall { get; set; }
        public string CallTargetBlockName { get; set; }
        public string CallTargetBlockType { get; set; } // "FB" or "FC"
        public string CallInstanceDbName { get; set; }
        public Dictionary<string, string> CallParameterBindings { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // If this entry represents a partial/targeted network update inside Main [OB1] (replaces only targeted networks, preserves all others)
        public bool IsPartialMainObUpdate { get; set; }
        public List<string> ReplaceNetworkTargets { get; set; } = new List<string>();
        public List<string> DeleteNetworkKeywords { get; set; } = new List<string>();

        public override string ToString()
        {
            if (IsMainObCall)
                return $"Main [OB1 -> {CallTargetBlockName} Çağrısı ({Language})]";
            return $"{Name} [{BlockType} · {Language} · {Networks?.Count ?? 0} Network]";
        }
    }

    public class TiaPackage
    {
        public string PackageId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string PackageName { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string SourcePrompt { get; set; }
        public string SourceChannel { get; set; } = "AI"; // "AI", "Sesli Komut", "Yazılı Komut", "Şablon"
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string TargetPlcName { get; set; }
        public string TargetFolder { get; set; } = "";
        public string Language { get; set; } = "LAD"; // "LAD" or "SCL"
        public string TagTableName { get; set; } = "TIAgent_IO_Taglari";
        public bool? AutoApprove { get; set; }

        public List<PlcTagEntry> Tags { get; set; } = new List<PlcTagEntry>();
        public List<PlcDbEntry> DataBlocks { get; set; } = new List<PlcDbEntry>();
        public List<PlcBlockEntry> Blocks { get; set; } = new List<PlcBlockEntry>();

        // ── Donanım (Devices & Networks) ve Technology Object talimatları ──
        /// <summary>Hedef PLC'nin PROFINET arayüzüne atanacak IP (opsiyonel, ör. 192.168.0.1)</summary>
        public string PlcIpAddress { get; set; }
        /// <summary>Hedef PLC rack'ine takılacak modüller (SM/CM/SB vb.)</summary>
        public List<HardwareModuleEntry> PlcModules { get; set; } = new List<HardwareModuleEntry>();
        /// <summary>Projeye eklenecek PROFINET cihazlar (V90, G120, ET200SP, GSDML cihazlar...)</summary>
        public List<HardwareDeviceEntry> HardwareDevices { get; set; } = new List<HardwareDeviceEntry>();
        /// <summary>PLC'nin Technology objects klasörüne eklenecek objeler (TO_PositioningAxis, PID_Compact...)</summary>
        public List<TechnologyObjectEntry> TechnologyObjects { get; set; } = new List<TechnologyObjectEntry>();
        /// <summary>Paket uygulanmadan önce silinecek cihaz / modül adları</summary>
        public List<string> DevicesToDelete { get; set; } = new List<string>();
        /// <summary>Paket uygulanmadan önce silinecek Technology Object adları</summary>
        public List<string> TechnologyObjectsToDelete { get; set; } = new List<string>();

        public bool HasHardwareContent()
        {
            return !string.IsNullOrWhiteSpace(PlcIpAddress) ||
                   (PlcModules?.Count ?? 0) > 0 ||
                   (HardwareDevices?.Count ?? 0) > 0 ||
                   (TechnologyObjects?.Count ?? 0) > 0 ||
                   (DevicesToDelete?.Count ?? 0) > 0 ||
                   (TechnologyObjectsToDelete?.Count ?? 0) > 0;
        }

        /// <summary>
        /// Büyük paketlerde TIA Portal'ın crash olmasını önlemek için 3 aşamalı aktarım uygular.
        /// </summary>
        public bool AutoStageLargePackage { get; set; } = true;

        /// <summary>
        /// Paketin büyük / çok aşamalı olup olmadığını tespit eder.
        /// </summary>
        public bool IsLargePackage()
        {
            int dbCount = DataBlocks?.Count ?? 0;
            int blkCount = Blocks?.Count ?? 0;
            bool hasHw = HasHardwareContent();
            bool hasTags = Tags != null && Tags.Count > 0;
            bool hasProtectedFb = Blocks != null && Blocks.Any(b =>
                (!string.IsNullOrEmpty(b.Name) && (b.Name.IndexOf("SINA_POS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                   b.Name.IndexOf("FB284", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                (!string.IsNullOrEmpty(b.SimaticMlXml) && b.SimaticMlXml.IndexOf("KnowHowProtection", StringComparison.OrdinalIgnoreCase) >= 0));

            // Donanım + Program Blokları veya DB'ler birlikteyse büyük pakettir
            if (hasHw && (dbCount > 0 || blkCount > 0)) return true;
            // Know-How Korumalı veya ağır FB (SINA_POS vb.) ve bloklar/DB'ler varsa büyük pakettir
            if (hasProtectedFb && (dbCount > 0 || blkCount > 2)) return true;
            // Toplam 5 veya daha fazla DB + Blok varsa büyük pakettir
            if (dbCount + blkCount >= 5) return true;
            // 3 veya daha fazla DB ve program blokları varsa
            if (dbCount >= 3 && blkCount >= 2) return true;

            return false;
        }

        /// <summary>
        /// Paketi TIA Portal kararlılığı için 3 güvenli aşamaya böler:
        /// Aşama 1: Donanım ve I/O Tagları
        /// Aşama 2: Global Veri Blokları (Standard / Optimized DB'ler)
        /// Aşama 3: Program Blokları (FBs, Instance DBs, FCs, OBs, Main [OB1])
        /// </summary>
        public List<TiaPackage> SplitIntoStages()
        {
            var stages = new List<TiaPackage>();

            // Aşama 1: Donanım ve I/O Tagları
            if (this.HasHardwareContent() || (this.Tags != null && this.Tags.Count > 0))
            {
                var stage1 = new TiaPackage
                {
                    PackageName = $"{this.PackageName}_Stage1_Donanim_Ve_Taglar",
                    Title = $"Aşama 1: Donanım ve I/O Tagları ({this.Title ?? this.PackageName})",
                    Description = $"3 aşamalı aktarım – Aşama 1: PROFINET cihazlar, modüller ve {this.Tags?.Count ?? 0} I/O tagı",
                    Language = this.Language,
                    TagTableName = this.TagTableName,
                    Tags = this.Tags != null ? new List<PlcTagEntry>(this.Tags) : new List<PlcTagEntry>(),
                    HardwareDevices = this.HardwareDevices != null ? new List<HardwareDeviceEntry>(this.HardwareDevices) : new List<HardwareDeviceEntry>(),
                    PlcModules = this.PlcModules != null ? new List<HardwareModuleEntry>(this.PlcModules) : new List<HardwareModuleEntry>(),
                    TechnologyObjects = this.TechnologyObjects != null ? new List<TechnologyObjectEntry>(this.TechnologyObjects) : new List<TechnologyObjectEntry>(),
                    PlcIpAddress = this.PlcIpAddress,
                    DevicesToDelete = this.DevicesToDelete != null ? new List<string>(this.DevicesToDelete) : null,
                    TechnologyObjectsToDelete = this.TechnologyObjectsToDelete != null ? new List<string>(this.TechnologyObjectsToDelete) : null,
                    AutoApprove = this.AutoApprove,
                    TargetPlcName = this.TargetPlcName,
                    TargetFolder = this.TargetFolder,
                    DataBlocks = new List<PlcDbEntry>(),
                    Blocks = new List<PlcBlockEntry>()
                };
                stages.Add(stage1);
            }

            // Aşama 2: Global Veri Blokları
            var globalDbs = this.DataBlocks?.Where(d => !string.Equals(d.DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<PlcDbEntry>();
            var dbBlocks = this.Blocks?.Where(b => string.Equals(b.BlockType, "DB", StringComparison.OrdinalIgnoreCase) || string.Equals(b.Language, "DB", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<PlcBlockEntry>();

            if (globalDbs.Count > 0 || dbBlocks.Count > 0)
            {
                var stage2 = new TiaPackage
                {
                    PackageName = $"{this.PackageName}_Stage2_Global_DBler",
                    Title = $"Aşama 2: Global Veri Blokları ({this.Title ?? this.PackageName})",
                    Description = $"3 aşamalı aktarım – Aşama 2: {globalDbs.Count + dbBlocks.Count} Global Veri Bloğu (DB)",
                    Language = this.Language,
                    TagTableName = this.TagTableName,
                    Tags = new List<PlcTagEntry>(),
                    HardwareDevices = new List<HardwareDeviceEntry>(),
                    PlcModules = new List<HardwareModuleEntry>(),
                    TechnologyObjects = new List<TechnologyObjectEntry>(),
                    AutoApprove = this.AutoApprove,
                    TargetPlcName = this.TargetPlcName,
                    TargetFolder = this.TargetFolder,
                    DataBlocks = globalDbs,
                    Blocks = dbBlocks
                };
                stages.Add(stage2);
            }

            // Aşama 3: Program Blokları (FBs, Instance DBs, FCs, OBs, Main [OB1])
            var instDbs = this.DataBlocks?.Where(d => string.Equals(d.DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<PlcDbEntry>();
            var progBlocks = this.Blocks?.Where(b => !string.Equals(b.BlockType, "DB", StringComparison.OrdinalIgnoreCase) && !string.Equals(b.Language, "DB", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<PlcBlockEntry>();

            if (instDbs.Count > 0 || progBlocks.Count > 0)
            {
                var stage3 = new TiaPackage
                {
                    PackageName = $"{this.PackageName}_Stage3_Program_Bloklari",
                    Title = $"Aşama 3: Program Blokları ve Main [OB1] ({this.Title ?? this.PackageName})",
                    Description = $"3 aşamalı aktarım – Aşama 3: {progBlocks.Count} program bloğu (FB/FC/OB) ve {instDbs.Count} Instance DB",
                    Language = this.Language,
                    TagTableName = this.TagTableName,
                    Tags = new List<PlcTagEntry>(),
                    HardwareDevices = new List<HardwareDeviceEntry>(),
                    PlcModules = new List<HardwareModuleEntry>(),
                    TechnologyObjects = new List<TechnologyObjectEntry>(),
                    AutoApprove = this.AutoApprove,
                    TargetPlcName = this.TargetPlcName,
                    TargetFolder = this.TargetFolder,
                    DataBlocks = instDbs,
                    Blocks = progBlocks
                };
                stages.Add(stage3);
            }

            return stages;
        }

        public string GetApprovalSummaryText(string plcName, string uiFolderOverride, string lang = "tr")
        {
            bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
            string effectiveFolder = !string.IsNullOrWhiteSpace(TargetFolder) ? TargetFolder.Trim() : (uiFolderOverride ?? "").Trim();
            string targetPath = string.IsNullOrEmpty(effectiveFolder)
                ? $"{plcName ?? "PLC"} > Program blocks"
                : $"{plcName ?? "PLC"} > Program blocks > {effectiveFolder}";

            var sb = new System.Text.StringBuilder();
            if (IsLargePackage())
            {
                sb.AppendLine(isEn
                    ? "⚡ Safe Import: Large package detected. Will be transferred in 3 stages (Hardware/Tags -> Global DBs -> Program Blocks) for TIA Portal stability."
                    : "⚡ Güvenli Aktarım: Büyük paket tespit edildi. TIA Portal kararlılığı için 3 aşamada (Donanım/Taglar → Global DB'ler → Program Blokları) aktarılacaktır.");
            }
            if (isEn)
            {
                sb.AppendLine($"AI Request: {PackageName} ({Language}) — {Title}");
                sb.AppendLine($"PLC Location: {targetPath}");
                sb.AppendLine("Process Details:");
            }
            else
            {
                sb.AppendLine($"AI Talebi: {PackageName} ({Language}) — {Title}");
                sb.AppendLine($"PLC Konumu: {targetPath}");
                sb.AppendLine("İşlem Detayları:");
            }

            foreach (var b in Blocks)
            {
                if (b.IsMainObCall)
                    sb.AppendLine($"  Main [OB1 — {b.CallTargetBlockName} çağrısı]");
                else
                    sb.AppendLine($"  {b.Name} [{b.BlockType} · {b.Language} · {b.Networks?.Count ?? 0} Network]");
            }

            if (DataBlocks != null && DataBlocks.Count > 0)
            {
                foreach (var db in DataBlocks)
                {
                    if (string.Equals(db.DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"  {db.Name} [Instance DB -> {db.InstanceOfFbName}]");
                    else
                        sb.AppendLine($"  {db.Name} [Global DB · {db.Members?.Count ?? 0} Tag]");
                }
            }

            if (Tags != null && Tags.Count > 0)
            {
                sb.AppendLine($"  PLC Tag Tablosu: {TagTableName} ({Tags.Count} I/O Tag: {string.Join(", ", Tags.ConvertAll(t => $"{t.Name}[{t.LogicalAddress}]"))})");
            }

            if (HasHardwareContent())
            {
                sb.AppendLine("Donanım / Technology Object:");
                if (DevicesToDelete != null && DevicesToDelete.Count > 0)
                    sb.AppendLine($"  ✗ Silinecek cihaz/modül: {string.Join(", ", DevicesToDelete)}");
                if (TechnologyObjectsToDelete != null && TechnologyObjectsToDelete.Count > 0)
                    sb.AppendLine($"  ✗ Silinecek TO: {string.Join(", ", TechnologyObjectsToDelete)}");
                if (!string.IsNullOrWhiteSpace(PlcIpAddress))
                    sb.AppendLine($"  PLC IP: {PlcIpAddress}");
                if (PlcModules != null)
                    foreach (var m in PlcModules)
                        sb.AppendLine($"  + PLC Modül: {m}");
                if (HardwareDevices != null)
                    foreach (var d in HardwareDevices)
                        sb.AppendLine($"  + Cihaz: {d}");
                if (TechnologyObjects != null)
                    foreach (var t in TechnologyObjects)
                        sb.AppendLine($"  + TO: {t}");
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Bir cihaza (veya PLC rack'ine) takılacak modül / alt modül / telegram.
    /// TypeIdentifier örnekleri:
    ///   "OrderNumber:6ES7 231-4HD32-0XB0/V2.0"  (SM 1231 AI4)
    ///   "GSD:GSDML-V2.35-SIEMENS-SINAMICS_V90-20200310.XML/SM/ID_xxx"
    /// TypeIdentifier bilinmiyorsa GsdModuleKeyword (ör. "Standard telegram 3") ile cihazın GSDML dosyasından otomatik çözülür.
    /// </summary>
    public class HardwareModuleEntry
    {
        public string Name { get; set; }
        public string TypeIdentifier { get; set; }
        public List<string> AlternativeTypeIdentifiers { get; set; } = new List<string>();
        public string GsdModuleKeyword { get; set; }
        public int? Position { get; set; }
        public string ParentItemName { get; set; }
        public int? InputStartAddress { get; set; }
        public int? OutputStartAddress { get; set; }
        public string Comment { get; set; }

        public override string ToString()
        {
            string id = !string.IsNullOrWhiteSpace(TypeIdentifier) ? TypeIdentifier : (GsdModuleKeyword ?? "?");
            string pos = Position.HasValue ? $" @Slot {Position}" : "";
            string addr = (InputStartAddress.HasValue ? $" I{InputStartAddress}" : "") + (OutputStartAddress.HasValue ? $" Q{OutputStartAddress}" : "");
            return $"{Name ?? "(modül)"} [{id}]{pos}{addr}";
        }
    }

    /// <summary>
    /// Projeye eklenecek donanım istasyonu (PROFINET IO cihazı, sürücü, uzak I/O, ikinci PLC vb.).
    /// TypeIdentifier örnekleri:
    ///   "OrderNumber:6ES7 155-6AU01-0BN0/V4.1"  (ET200SP IM155-6PN ST)
    ///   "GSD:GSDML-V2.35-SIEMENS-SINAMICS_V90-20200310.XML/DAP/DAP1"
    /// TypeIdentifier verilmezse GsdKeyword (ör. "SINAMICS_V90") ile TIA Portal'a yüklü GSDML dosyalarından otomatik çözülür.
    /// </summary>
    public class HardwareDeviceEntry
    {
        public string Name { get; set; }
        public string DeviceName { get; set; }
        public string TypeIdentifier { get; set; }
        public List<string> AlternativeTypeIdentifiers { get; set; } = new List<string>();
        public string GsdKeyword { get; set; }
        public string GsdDapKeyword { get; set; }
        public string IpAddress { get; set; }
        public string SubnetMask { get; set; }
        public string PnDeviceName { get; set; }
        public bool ConnectToPlcIoSystem { get; set; } = true;
        public bool ReplaceIfExists { get; set; } = false;
        public List<HardwareModuleEntry> Modules { get; set; } = new List<HardwareModuleEntry>();
        public string Comment { get; set; }

        public override string ToString()
        {
            string id = !string.IsNullOrWhiteSpace(TypeIdentifier) ? TypeIdentifier : ($"GSD~{GsdKeyword}");
            string ip = !string.IsNullOrWhiteSpace(IpAddress) ? $" IP {IpAddress}" : "";
            string mods = (Modules?.Count ?? 0) > 0 ? $" + {Modules.Count} modül" : "";
            return $"{Name} [{id}]{ip}{mods}";
        }
    }

    /// <summary>
    /// PLC Technology Object. Type örnekleri: TO_PositioningAxis, TO_SpeedAxis, TO_SynchronousAxis,
    /// TO_ExternalEncoder, TO_CommandTable, PID_Compact, PID_3Step, PID_Temp.
    /// Version boş bırakılırsa CPU'nun desteklediği en yüksek sürüm otomatik denenir.
    /// </summary>
    public class TechnologyObjectEntry
    {
        public string Name { get; set; }
        public string Type { get; set; } = "TO_PositioningAxis";
        public string Version { get; set; }
        public string DriveDeviceName { get; set; }
        public string TelegramModuleKeyword { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public bool ReplaceIfExists { get; set; } = false;
        public string Comment { get; set; }

        public override string ToString()
        {
            string drv = !string.IsNullOrWhiteSpace(DriveDeviceName) ? $" → {DriveDeviceName}" : "";
            string ver = !string.IsNullOrWhiteSpace(Version) ? $" V{Version}" : "";
            return $"{Name} [{Type}{ver}]{drv}";
        }
    }

    public class ProjectOverviewInfo
    {
        public string ProjectName { get; set; }
        public string ProjectPath { get; set; }
        public string TiaVersion { get; set; }
        public string ActivePlcName { get; set; }
        public string ActivePlcType { get; set; }
        public List<PlcTagEntry> ExistingTags { get; set; } = new List<PlcTagEntry>();
        public List<string> ExistingDataBlocks { get; set; } = new List<string>();
        public List<SclBlockItem> ExistingProgramBlocks { get; set; } = new List<SclBlockItem>();
    }

    public class BackupOperationItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Timestamp { get; set; }
        public string OperationName { get; set; }
        public string PackageName { get; set; }
        public string Prompt { get; set; }
        public string SourceChannel { get; set; }
        public string PlcName { get; set; }
        public string BackupFolderPath { get; set; }
        public List<string> BackedUpBlocks { get; set; } = new List<string>();
        public List<string> CreatedBlocks { get; set; } = new List<string>();
        public List<string> CreatedDataBlocks { get; set; } = new List<string>();
        public List<string> CreatedTags { get; set; } = new List<string>();
        public bool IsRolledBack { get; set; } = false;

        private string _language = "tr";
        public string Language
        {
            get => _language;
            set
            {
                _language = value;
                OnPropertyChanged(nameof(DisplaySummary));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(RollbackButtonText));
            }
        }

        public string DisplaySummary
        {
            get
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                var parts = new List<string>();
                if (BackedUpBlocks != null && BackedUpBlocks.Count > 0)
                    parts.Add(isEn ? $"Backed up: {string.Join(", ", BackedUpBlocks)}" : $"Yedeklenen: {string.Join(", ", BackedUpBlocks)}");
                if (CreatedBlocks != null && CreatedBlocks.Count > 0)
                    parts.Add(isEn ? $"Created: {string.Join(", ", CreatedBlocks)}" : $"Eklenen: {string.Join(", ", CreatedBlocks)}");
                if (CreatedDataBlocks != null && CreatedDataBlocks.Count > 0)
                    parts.Add($"DB: {string.Join(", ", CreatedDataBlocks)}");
                return parts.Count > 0 ? string.Join(" | ", parts) : (isEn ? "General operation backup" : "Genel işlem yedeği");
            }
        }

        public string StatusDisplay
        {
            get
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                return IsRolledBack
                    ? (isEn ? "↺ Rolled Back" : "↺ Geri Alındı")
                    : (isEn ? "✓ Active" : "✓ Aktif");
            }
        }

        public string StatusColor => IsRolledBack ? "#F59E0B" : "#10B981";

        public string RollbackButtonText
        {
            get
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                return isEn ? "↺ Rollback" : "↺ Geri Al";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

