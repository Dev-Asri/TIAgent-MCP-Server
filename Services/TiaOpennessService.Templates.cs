using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TIAgent.Models;

namespace TIAgent.Services
{
    public class TiaPlcTypeItem
    {
        public string Name { get; set; }
        public string GroupPath { get; set; }
        public object RawTypeObject { get; set; }
        public override string ToString() => Name;
    }

    public class TiaTagTableItem
    {
        public string Name { get; set; }
        public string GroupPath { get; set; }
        public object RawTagTableObject { get; set; }
        public List<PlcTagEntry> Tags { get; set; } = new List<PlcTagEntry>();
        public override string ToString() => Name;
    }

    public class TemplateExportConfig
    {
        public string TargetDirectory { get; set; }
        public bool ExportBlocks { get; set; } = true;
        public bool ExtractScl { get; set; } = true;
        public bool ExportUdts { get; set; } = true;
        public bool ExportTagTables { get; set; } = true;
        public bool ExportHardware { get; set; } = true;
        public bool GenerateAiSummary { get; set; } = true;
    }

    public class TemplateExportResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int BlocksExported { get; set; }
        public int SclFilesExtracted { get; set; }
        public int UdtsExported { get; set; }
        public int TagTablesExported { get; set; }
        public int TotalTags { get; set; }
        public string OutputDirectory { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public partial class TiaOpennessService
    {
        public void ExportTypeToXml(object plcType, string exportXmlPath)
        {
            Type exportOptionsType = FindEngineeringType("Siemens.Engineering.ExportOptions");
            object optVal = Enum.Parse(exportOptionsType, "WithDefaults");

            MethodInfo exportMethod = plcType.GetType().GetMethod("Export", new[] { typeof(FileInfo), exportOptionsType });
            if (exportMethod == null)
                throw new InvalidOperationException("PlcType.Export metodu bulunamadı.");

            exportMethod.Invoke(plcType, new object[] { new FileInfo(exportXmlPath), optVal });
        }

        public void ExportTagTableToXml(object tagTable, string exportXmlPath)
        {
            Type exportOptionsType = FindEngineeringType("Siemens.Engineering.ExportOptions");
            object optVal = Enum.Parse(exportOptionsType, "WithDefaults");

            MethodInfo exportMethod = tagTable.GetType().GetMethod("Export", new[] { typeof(FileInfo), exportOptionsType });
            if (exportMethod == null)
                throw new InvalidOperationException("PlcTagTable.Export metodu bulunamadı.");

            exportMethod.Invoke(tagTable, new object[] { new FileInfo(exportXmlPath), optVal });
        }

        public List<TiaPlcTypeItem> GetPlcTypes(TiaPlcItem plc)
        {
            if (plc?.RawPlcSoftware == null) return new List<TiaPlcTypeItem>();

            return RunOnOpennessThreadAsync(() =>
            {
                var list = new List<TiaPlcTypeItem>();
                object typeGroup = GetPropValue(plc.RawPlcSoftware, "TypeGroup");
                if (typeGroup != null)
                {
                    CollectTypesRecursive(typeGroup, "", list);
                }
                return list;
            }).GetAwaiter().GetResult();
        }

        private void CollectTypesRecursive(object currentGroup, string groupPath, List<TiaPlcTypeItem> list)
        {
            if (currentGroup == null) return;
            string groupName = GetPropString(currentGroup, "Name");
            string currentPath = string.IsNullOrEmpty(groupPath) ? groupName : $"{groupPath}/{groupName}";

            if (GetPropValue(currentGroup, "Types") is IEnumerable typesEnum)
            {
                foreach (var t in typesEnum)
                {
                    string name = GetPropString(t, "Name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(new TiaPlcTypeItem
                        {
                            Name = name,
                            GroupPath = currentPath,
                            RawTypeObject = t
                        });
                    }
                }
            }

            if (GetPropValue(currentGroup, "Groups") is IEnumerable subGroups)
            {
                foreach (var sub in subGroups)
                    CollectTypesRecursive(sub, currentPath, list);
            }
        }

        public List<TiaTagTableItem> GetPlcTagTables(TiaPlcItem plc)
        {
            if (plc?.RawPlcSoftware == null) return new List<TiaTagTableItem>();

            return RunOnOpennessThreadAsync(() =>
            {
                var list = new List<TiaTagTableItem>();
                object tagTableGroup = GetPropValue(plc.RawPlcSoftware, "TagTableGroup");
                if (tagTableGroup != null)
                {
                    CollectTagTablesRecursive(tagTableGroup, "", list);
                }
                return list;
            }).GetAwaiter().GetResult();
        }

        private void CollectTagTablesRecursive(object currentGroup, string groupPath, List<TiaTagTableItem> list)
        {
            if (currentGroup == null) return;
            string groupName = GetPropString(currentGroup, "Name");
            string currentPath = string.IsNullOrEmpty(groupPath) ? groupName : $"{groupPath}/{groupName}";

            if (GetPropValue(currentGroup, "TagTables") is IEnumerable tablesEnum)
            {
                foreach (var tbl in tablesEnum)
                {
                    string name = GetPropString(tbl, "Name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        var tags = new List<PlcTagEntry>();
                        if (GetPropValue(tbl, "Tags") is IEnumerable tagsEnum)
                        {
                            foreach (var tag in tagsEnum)
                            {
                                string tagName = GetPropString(tag, "Name");
                                string dt = GetPropString(tag, "DataTypeName");
                                string addr = GetPropString(tag, "LogicalAddress");
                                string dir = "Memory";
                                if (!string.IsNullOrEmpty(addr))
                                {
                                    if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) dir = "Input";
                                    else if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) dir = "Output";
                                }

                                string commentText = "";
                                try
                                {
                                    object cmtObj = GetPropValue(tag, "Comment");
                                    if (GetPropValue(cmtObj, "Items") is IEnumerable items)
                                    {
                                        foreach (var it in items)
                                        {
                                            string t = GetPropString(it, "Text");
                                            if (!string.IsNullOrWhiteSpace(t)) { commentText = t; break; }
                                        }
                                    }
                                }
                                catch { }

                                tags.Add(new PlcTagEntry
                                {
                                    Name = tagName,
                                    DataType = string.IsNullOrEmpty(dt) ? "Bool" : dt,
                                    LogicalAddress = addr,
                                    Direction = dir,
                                    TableName = name,
                                    Comment = commentText
                                });
                            }
                        }

                        list.Add(new TiaTagTableItem
                        {
                            Name = name,
                            GroupPath = currentPath,
                            RawTagTableObject = tbl,
                            Tags = tags
                        });
                    }
                }
            }

            if (GetPropValue(currentGroup, "Groups") is IEnumerable subGroups)
            {
                foreach (var sub in subGroups)
                    CollectTagTablesRecursive(sub, currentPath, list);
            }
        }

        public async Task<TemplateExportResult> ExportProjectTemplatesAsync(
            TiaProjectItem project,
            TiaPlcItem plc,
            TemplateExportConfig config,
            Action<string> log,
            Action<int, int, string> progress,
            CancellationToken ct)
        {
            var result = new TemplateExportResult();
            string targetBase = config.TargetDirectory;

            if (string.IsNullOrWhiteSpace(targetBase))
            {
                targetBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ornekler");
            }

            string safeProjectName = SanitizeFileName(project?.Name ?? "TIA_Project");
            string safePlcName = SanitizeFileName(plc?.Name ?? "PLC");
            string outputDir = Path.Combine(targetBase, $"{safeProjectName}_{safePlcName}");
            Directory.CreateDirectory(outputDir);
            result.OutputDirectory = outputDir;

            log($"[Başlangıç] Hedef Klasör: {outputDir}");

            // List collections for summary manifest
            var exportedBlockSummaries = new List<object>();
            var exportedUdtSummaries = new List<object>();
            var exportedTagSummaries = new List<object>();
            var allTagsList = new List<PlcTagEntry>();

            try
            {
                // 1. Hardware Overview
                if (config.ExportHardware && !ct.IsCancellationRequested)
                {
                    progress(0, 100, "Donanım konfigürasyonu alınıyor...");
                    log("[1/5] Donanım ve CPU konfigürasyonu taranıyor...");
                    try
                    {
                        dynamic hw = GetHardwareOverview(plc);
                        string hwMdPath = Path.Combine(outputDir, "00_Hardware_Config.md");
                        var sbHw = new StringBuilder();
                        sbHw.AppendLine($"# Donanım ve PLC Konfigürasyon Özeti");
                        sbHw.AppendLine($"- **Proje Adı:** {project?.Name}");
                        sbHw.AppendLine($"- **PLC İstasyonu:** {plc?.Name} ({plc?.TypeName})");
                        sbHw.AppendLine($"- **TIA Portal Sürümü:** {_lockedVersion?.DisplayName ?? "Bilinmiyor"}");
                        sbHw.AppendLine($"- **Dışa Aktarım Tarihi:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        sbHw.AppendLine();

                        sbHw.AppendLine("## Cihazlar ve Modüller");
                        if (hw != null && hw.devices != null)
                        {
                            foreach (var dev in hw.devices)
                            {
                                sbHw.AppendLine($"### Cihaz: {dev.name} ({dev.typeIdentifier})");
                                if (dev.ipAddresses != null && ((IEnumerable<string>)dev.ipAddresses).Any())
                                {
                                    sbHw.AppendLine($"- **IP Adresleri:** {string.Join(", ", (IEnumerable<string>)dev.ipAddresses)}");
                                }
                                sbHw.AppendLine("| Pozisyon | Modül / Arayüz Adı | Tip Tanımlayıcı | Adresler |");
                                sbHw.AppendLine("| :--- | :--- | :--- | :--- |");
                                if (dev.items != null)
                                {
                                    foreach (var item in dev.items)
                                    {
                                        sbHw.AppendLine($"| {item.position} | {item.name} | {item.typeIdentifier} | {item.addresses} |");
                                    }
                                }
                                sbHw.AppendLine();
                            }
                        }

                        if (hw != null && hw.technologyObjects != null)
                        {
                            var toList = (IEnumerable)hw.technologyObjects;
                            if (toList != null && toList.Cast<object>().Any())
                            {
                                sbHw.AppendLine("## Teknoloji Nesneleri (Technology Objects)");
                                foreach (dynamic to in toList)
                                {
                                    sbHw.AppendLine($"- **Ad:** {to.name} | **Tip:** {to.type} | **No:** {to.number}");
                                }
                                sbHw.AppendLine();
                            }
                        }

                        File.WriteAllText(hwMdPath, sbHw.ToString(), Encoding.UTF8);
                        log($"[✓] Donanım özeti kaydedildi: 00_Hardware_Config.md");
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Donanım özeti çıkarılırken uyarı: {ex.Message}");
                        log($"[!] Donanım özeti çıkarılamadı: {ex.Message}");
                    }
                }

                // 2. Tag Tables
                if (config.ExportTagTables && !ct.IsCancellationRequested)
                {
                    progress(15, 100, "PLC Tag Tabloları taranıyor...");
                    log("[2/5] PLC Tag Tabloları taranıyor ve dışa aktarılıyor...");
                    string tagsDir = Path.Combine(outputDir, "Tags");
                    Directory.CreateDirectory(tagsDir);

                    var tagTables = GetPlcTagTables(plc);
                    int tblCount = 0;

                    var csvBuilder = new StringBuilder();
                    csvBuilder.AppendLine("Tablo,Tag_Adi,Veri_Tipi,Adres,Yon,Aciklama");

                    foreach (var tbl in tagTables)
                    {
                        if (ct.IsCancellationRequested) break;
                        string safeTblName = SanitizeFileName(tbl.Name);
                        string xmlPath = Path.Combine(tagsDir, $"{safeTblName}.xml");

                        try
                        {
                            RunOnOpennessThreadAsync(() =>
                            {
                                ExportTagTableToXml(tbl.RawTagTableObject, xmlPath);
                                return true;
                            }).GetAwaiter().GetResult();
                            tblCount++;
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"Tag tablosu export hatası ({tbl.Name}): {ex.Message}");
                        }

                        foreach (var tag in tbl.Tags)
                        {
                            allTagsList.Add(tag);
                            string cleanComment = (tag.Comment ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
                            csvBuilder.AppendLine($"\"{tbl.Name}\",\"{tag.Name}\",\"{tag.DataType}\",\"{tag.LogicalAddress}\",\"{tag.Direction}\",\"{cleanComment}\"");
                        }

                        exportedTagSummaries.Add(new
                        {
                            name = tbl.Name,
                            tagCount = tbl.Tags.Count,
                            xmlFile = $"Tags/{safeTblName}.xml"
                        });
                    }

                    string csvPath = Path.Combine(tagsDir, "PLC_Tags.csv");
                    File.WriteAllText(csvPath, csvBuilder.ToString(), Encoding.UTF8);

                    result.TagTablesExported = tblCount;
                    result.TotalTags = allTagsList.Count;
                    log($"[✓] {tblCount} adet Tag Tablosu ve {allTagsList.Count} adet Tag aktarıldı (XML + CSV).");
                }

                // 3. User Defined Types (UDTs)
                if (config.ExportUdts && !ct.IsCancellationRequested)
                {
                    progress(35, 100, "Kullanıcı Veri Tipleri (UDT) taranıyor...");
                    log("[3/5] Kullanıcı Veri Tipleri (UDT) taranıyor ve dönüştürülüyor...");
                    string udtsDir = Path.Combine(outputDir, "UDTs");
                    Directory.CreateDirectory(udtsDir);

                    var types = GetPlcTypes(plc);
                    int udtCount = 0;

                    foreach (var t in types)
                    {
                        if (ct.IsCancellationRequested) break;
                        string safeName = SanitizeFileName(t.Name);
                        string xmlPath = Path.Combine(udtsDir, $"{safeName}.xml");
                        string sclPath = Path.Combine(udtsDir, $"{safeName}.scl");

                        try
                        {
                            RunOnOpennessThreadAsync(() =>
                            {
                                ExportTypeToXml(t.RawTypeObject, xmlPath);
                                return true;
                            }).GetAwaiter().GetResult();
                            udtCount++;

                            // SCL Struct formatına dönüştür
                            if (File.Exists(xmlPath))
                            {
                                string xmlContent = File.ReadAllText(xmlPath);
                                string sclUdt = ConvertUdtXmlToScl(xmlContent, t.Name);
                                File.WriteAllText(sclPath, sclUdt, Encoding.UTF8);
                            }

                            exportedUdtSummaries.Add(new
                            {
                                name = t.Name,
                                group = t.GroupPath,
                                xmlFile = $"UDTs/{safeName}.xml",
                                sclFile = $"UDTs/{safeName}.scl"
                            });
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"UDT export hatası ({t.Name}): {ex.Message}");
                        }
                    }

                    result.UdtsExported = udtCount;
                    log($"[✓] {udtCount} adet UDT dışa aktarıldı ve SCL struct formatına çevrildi.");
                }

                // 4. Program Blocks (OB, FB, FC, DB)
                if (config.ExportBlocks && !ct.IsCancellationRequested)
                {
                    progress(55, 100, "Program Blokları taranıyor...");
                    log("[4/5] Program Blokları (OB, FB, FC, DB) taranıyor ve dışa aktarılıyor...");

                    string blocksBaseDir = Path.Combine(outputDir, "Blocks");
                    string obDir = Path.Combine(blocksBaseDir, "OB");
                    string fbDir = Path.Combine(blocksBaseDir, "FB");
                    string fcDir = Path.Combine(blocksBaseDir, "FC");
                    string dbDir = Path.Combine(blocksBaseDir, "DB");

                    Directory.CreateDirectory(obDir);
                    Directory.CreateDirectory(fbDir);
                    Directory.CreateDirectory(fcDir);
                    Directory.CreateDirectory(dbDir);

                    var allBlocks = new List<SclBlockItem>();
                    var dbNames = new List<string>();

                    RunOnOpennessThreadAsync(() =>
                    {
                        object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                        if (blockGroup != null)
                        {
                            CollectAllBlocksRecursive(blockGroup, "", plc.RawPlcSoftware, allBlocks, dbNames);
                        }
                        return true;
                    }).GetAwaiter().GetResult();

                    int blkIndex = 0;
                    int totalBlks = allBlocks.Count;

                    foreach (var blk in allBlocks)
                    {
                        if (ct.IsCancellationRequested) break;
                        blkIndex++;
                        progress(55 + (int)((blkIndex / (double)Math.Max(1, totalBlks)) * 35), 100, $"Blok aktarılıyor: {blk.Name} ({blk.BlockType}) [{blkIndex}/{totalBlks}]");

                        string safeBlkName = SanitizeFileName(blk.Name);
                        string subDir = blk.BlockType switch
                        {
                            "OB" => obDir,
                            "FB" => fbDir,
                            "FC" => fcDir,
                            _ => dbDir
                        };

                        string xmlPath = Path.Combine(subDir, $"{safeBlkName}.xml");

                        try
                        {
                            RunOnOpennessThreadAsync(() =>
                            {
                                ExportBlockToXml(blk.RawBlockObject, xmlPath);
                                return true;
                            }).GetAwaiter().GetResult();
                            result.BlocksExported++;

                            string relSclPath = null;
                            string relLadderReportPath = null;
                            int ladderNetworksCount = 0;

                            // SCL, DB veya Ladder (LAD) mantığını ayıklama
                            if (config.ExtractScl && File.Exists(xmlPath))
                            {
                                string sclPath = Path.Combine(subDir, $"{safeBlkName}.scl");
                                try
                                {
                                    if (blk.BlockType == "DB")
                                    {
                                        string dbScl = ConvertDbXmlToScl(File.ReadAllText(xmlPath), blk.Name, blk.BlockNumber);
                                        File.WriteAllText(sclPath, dbScl, Encoding.UTF8);
                                        result.SclFilesExtracted++;
                                        relSclPath = $"Blocks/{blk.BlockType}/{safeBlkName}.scl";
                                    }
                                    else if (string.Equals(blk.ProgrammingLanguage, "LAD", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(blk.ProgrammingLanguage, "FBD", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var ladParsed = LadCodeParser.ParseLadderXml(xmlPath);
                                        if (ladParsed != null && ladParsed.Networks.Count > 0)
                                        {
                                            ladderNetworksCount = ladParsed.Networks.Count;
                                            string ladMdPath = Path.Combine(subDir, $"{safeBlkName}.ladder.md");
                                            File.WriteAllText(ladMdPath, ladParsed.MarkdownReport, Encoding.UTF8);
                                            relLadderReportPath = $"Blocks/{blk.BlockType}/{safeBlkName}.ladder.md";

                                            if (!string.IsNullOrWhiteSpace(ladParsed.PseudoSclCode))
                                            {
                                                File.WriteAllText(sclPath, ladParsed.PseudoSclCode, Encoding.UTF8);
                                                result.SclFilesExtracted++;
                                                relSclPath = $"Blocks/{blk.BlockType}/{safeBlkName}.scl";
                                            }
                                        }
                                        else
                                        {
                                            var parsed = SclCodeParser.ParseBlockXml(xmlPath);
                                            if (!string.IsNullOrWhiteSpace(parsed.FullSclCode) || !string.IsNullOrWhiteSpace(parsed.OriginalSclCode))
                                            {
                                                string codeToWrite = !string.IsNullOrWhiteSpace(parsed.FullSclCode) ? parsed.FullSclCode : parsed.OriginalSclCode;
                                                File.WriteAllText(sclPath, codeToWrite, Encoding.UTF8);
                                                result.SclFilesExtracted++;
                                                relSclPath = $"Blocks/{blk.BlockType}/{safeBlkName}.scl";
                                            }
                                        }
                                    }
                                    else
                                    {
                                        var parsed = SclCodeParser.ParseBlockXml(xmlPath);
                                        if (!string.IsNullOrWhiteSpace(parsed.FullSclCode) || !string.IsNullOrWhiteSpace(parsed.OriginalSclCode))
                                        {
                                            string codeToWrite = !string.IsNullOrWhiteSpace(parsed.FullSclCode) ? parsed.FullSclCode : parsed.OriginalSclCode;
                                            File.WriteAllText(sclPath, codeToWrite, Encoding.UTF8);
                                            result.SclFilesExtracted++;
                                            relSclPath = $"Blocks/{blk.BlockType}/{safeBlkName}.scl";
                                        }
                                    }
                                }
                                catch { }
                            }

                            exportedBlockSummaries.Add(new
                            {
                                name = blk.Name,
                                type = blk.BlockType,
                                number = blk.BlockNumber,
                                language = blk.ProgrammingLanguage,
                                group = blk.GroupPath,
                                xmlFile = $"Blocks/{blk.BlockType}/{safeBlkName}.xml",
                                sclFile = relSclPath,
                                ladderReportFile = relLadderReportPath,
                                networkCount = ladderNetworksCount
                            });
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"Blok export hatası ({blk.Name}): {ex.Message}");
                        }
                    }

                    log($"[✓] {result.BlocksExported} adet Program Bloğu aktarıldı, {result.SclFilesExtracted} adet SCL kaynak dosyası oluşturuldu.");
                }

                // 5. AI Training Manifest & Summary
                if (config.GenerateAiSummary && !ct.IsCancellationRequested)
                {
                    progress(92, 100, "Yapay zekâ eğitim özeti ve manifest hazırlanıyor...");
                    log("[5/5] Yapay zekâ eğitim kılavuzu (AI_Project_Summary.md & manifest.json) oluşturuluyor...");

                    GenerateAiTrainingSummaryFiles(outputDir, project, plc, allTagsList, exportedBlockSummaries, exportedUdtSummaries, exportedTagSummaries);
                    log("[✓] Yapay Zekâ Eğitim Kılavuzu hazırlandı: AI_Project_Summary.md ve AI_Training_Manifest.json");
                }

                progress(100, 100, "Tamamlandı!");
                result.Success = true;
                log($"[BAŞARILI] Tüm TIA şablon paketi hazırlandı! Konum: {outputDir}");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.ToString();
                log($"[HATA] Şablon çıkarma işlemi başarısız: {ex.Message}");
            }

            return result;
        }

        private static string ConvertUdtXmlToScl(string xmlContent, string udtName)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var sb = new StringBuilder();
                sb.AppendLine($"TYPE \"{udtName}\"");
                sb.AppendLine("VERSION : 0.1");
                sb.AppendLine("   STRUCT");

                var members = doc.Descendants().Where(d => d.Name.LocalName == "Member");
                foreach (var m in members)
                {
                    string name = m.Attribute("Name")?.Value ?? "Member";
                    string dt = m.Attribute("Datatype")?.Value ?? "Bool";
                    string cmt = m.Descendants().FirstOrDefault(d => d.Name.LocalName == "MultiLanguageText")?.Value;
                    string cmtStr = string.IsNullOrWhiteSpace(cmt) ? "" : $" // {cmt}";
                    sb.AppendLine($"      \"{name}\" : {dt};{cmtStr}");
                }

                sb.AppendLine("   END_STRUCT;");
                sb.AppendLine("END_TYPE");
                return sb.ToString();
            }
            catch
            {
                return $"TYPE \"{udtName}\"\nVERSION : 0.1\n   STRUCT\n   END_STRUCT;\nEND_TYPE";
            }
        }

        private static string ConvertDbXmlToScl(string xmlContent, string dbName, int? dbNumber)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var sb = new StringBuilder();
                sb.AppendLine($"DATA_BLOCK \"{dbName}\"");
                sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                sb.AppendLine("VERSION : 0.1");
                if (dbNumber.HasValue) sb.AppendLine($"// DB Numarası: {dbNumber.Value}");
                sb.AppendLine("VAR");

                var members = doc.Descendants().Where(d => d.Name.LocalName == "Member");
                foreach (var m in members)
                {
                    string name = m.Attribute("Name")?.Value ?? "Item";
                    string dt = m.Attribute("Datatype")?.Value ?? "Bool";
                    string cmt = m.Descendants().FirstOrDefault(d => d.Name.LocalName == "MultiLanguageText")?.Value;
                    string cmtStr = string.IsNullOrWhiteSpace(cmt) ? "" : $" // {cmt}";
                    sb.AppendLine($"   \"{name}\" : {dt};{cmtStr}");
                }

                sb.AppendLine("END_VAR");
                sb.AppendLine("BEGIN");
                sb.AppendLine("END_DATA_BLOCK");
                return sb.ToString();
            }
            catch
            {
                return $"DATA_BLOCK \"{dbName}\"\nVERSION : 0.1\nVAR\nEND_VAR\nBEGIN\nEND_DATA_BLOCK";
            }
        }

        private void GenerateAiTrainingSummaryFiles(
            string outputDir,
            TiaProjectItem project,
            TiaPlcItem plc,
            List<PlcTagEntry> tags,
            List<object> blocks,
            List<object> udts,
            List<object> tagTables)
        {
            // 1. Analyze Tag Naming Conventions
            int inputCount = tags.Count(t => t.Direction == "Input" || (t.LogicalAddress ?? "").StartsWith("%I", StringComparison.OrdinalIgnoreCase));
            int outputCount = tags.Count(t => t.Direction == "Output" || (t.LogicalAddress ?? "").StartsWith("%Q", StringComparison.OrdinalIgnoreCase));
            int memoryCount = tags.Count(t => t.Direction == "Memory" || (t.LogicalAddress ?? "").StartsWith("%M", StringComparison.OrdinalIgnoreCase));

            bool usesIPrefix = tags.Any(t => t.Name.StartsWith("i_", StringComparison.OrdinalIgnoreCase) || t.Name.StartsWith("di_", StringComparison.OrdinalIgnoreCase));
            bool usesQPrefix = tags.Any(t => t.Name.StartsWith("q_", StringComparison.OrdinalIgnoreCase) || t.Name.StartsWith("do_", StringComparison.OrdinalIgnoreCase));
            bool usesXPrefix = tags.Any(t => t.Name.StartsWith("x", StringComparison.OrdinalIgnoreCase) && t.DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase));

            // 2. Generate AI_Project_Summary.md
            var md = new StringBuilder();
            md.AppendLine($"# Yapay Zekâ Eğitim & Proje Kılavuzu: {project?.Name}");
            md.AppendLine();
            md.AppendLine("> [!IMPORTANT]");
            md.AppendLine($"> Bu doküman, yapay zekâ asistanlarının (Antigravity, Cursor, Claude vb.) bu projede kullanılan mimari standartları, isimlendirme konvansiyonlarını ve PLC blok şablonlarını birebir öğrenerek projeye uygun kod üretmesi için otomatik derlenmiştir.");
            md.AppendLine();
            md.AppendLine("## 1. Proje ve Donanım Bilgileri");
            md.AppendLine($"- **Proje Adı:** `{project?.Name}`");
            md.AppendLine($"- **Hedef PLC:** `{plc?.Name}` ({plc?.TypeName})");
            md.AppendLine($"- **TIA Portal Versiyonu:** `{_lockedVersion?.DisplayName ?? "Bilinmiyor"}`");
            md.AppendLine($"- **Dışa Aktarım Tarihi:** `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
            md.AppendLine($"- **Toplam Blok:** `{blocks.Count}` | **Toplam UDT:** `{udts.Count}` | **Toplam Tag:** `{tags.Count}`");
            md.AppendLine();

            md.AppendLine("## 2. İsimlendirme Konvansiyonu & Standartlar");
            md.AppendLine("Projede kullanılan tag ve değişken isimlendirme kuralları:");
            if (usesIPrefix) md.AppendLine("- **Dijital / Analog Girişler:** Genellikle `i_` veya `di_` ön eki ile başlar.");
            if (usesQPrefix) md.AppendLine("- **Çıkışlar (Valf, Motor vb.):** Genellikle `q_` veya `do_` ön eki ile başlar.");
            if (usesXPrefix) md.AppendLine("- **Boolean Durum Değişkenleri:** `x` ön eki ile başlar (Örn: `xRun`, `xAutoMode`).");
            md.AppendLine($"- **Giriş Tag Sayısı:** `{inputCount}` | **Çıkış Tag Sayısı:** `{outputCount}` | **Merker/Bellek Tag Sayısı:** `{memoryCount}`");
            md.AppendLine();

            md.AppendLine("## 3. Kullanıcı Veri Tipleri (UDT)");
            md.AppendLine("Projenin standart veri yapıları (`UDTs/` klasöründe tam SCL tanımları mevcuttur):");
            if (udts.Count == 0) md.AppendLine("- *Bu projede UDT bulunamadı.*");
            else
            {
                foreach (dynamic u in udts)
                {
                    md.AppendLine($"- **`{u.name}`** → `{u.sclFile}`");
                }
            }
            md.AppendLine();

            md.AppendLine("## 4. Ana Program Blokları & Ladder (LAD) Mimarisi");
            md.AppendLine("| Blok Adı | Tip | Dil | Ağ Sayısı | Kaynak Kod / Mantık Dosyası | Ladder Raporu |");
            md.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");
            foreach (dynamic b in blocks.Take(50)) // İlk 50 bloğu tablolaştır
            {
                string sclOrXml = !string.IsNullOrEmpty((string)b.sclFile) ? (string)b.sclFile : (string)b.xmlFile;
                string ladRep = !string.IsNullOrEmpty((string)b.ladderReportFile) ? (string)b.ladderReportFile : "-";
                int netCount = b.networkCount != null ? (int)b.networkCount : 0;
                string netCountStr = netCount > 0 ? netCount.ToString() : "-";
                md.AppendLine($"| `{b.name}` | `{b.type}` | `{b.language}` | `{netCountStr}` | `{sclOrXml}` | `{ladRep}` |");
            }
            if (blocks.Count > 50)
            {
                md.AppendLine($"| ... | ... | ... | ... | *(ve {blocks.Count - 50} adet daha blok manifest.json içinde listelenmiştir)* | - |");
            }
            md.AppendLine();

            md.AppendLine("## 5. Ladder (LAD) Ağ Yapılarının Yapay Zekâ Tarafından Öğrenilmesi");
            md.AppendLine("Bu projede bulunan `Main` (OB1) ve diğer LAD/FBD bloklarının şebeke mantıkları çözümlenmiştir:");
            md.AppendLine("1. **ASCII Devre Şemaları:** Her Ladder bloğunun yanında `{BlokAdi}.ladder.md` dosyası üretilmiştir. Burada kontaklar, kilitler ve bobinler görselleştirilmiştir.");
            md.AppendLine("2. **Mantıksal SCL Eşdeğeri:** `{BlokAdi}.scl` dosyası, Ladder devrelerini yapay zekânın doğrudan anlayabileceği mantıksal ifadelere ve FB çağrılarına dönüştürülmüş olarak içerir.");
            md.AppendLine("3. **Çağrı Sıraları:** OB1 içinde hangi fonksiyon ve fonksiyon bloklarının hangi sırayla ve hangi instance DB'lerle çağrıldığı açıkça listelenmiştir.");
            md.AppendLine();

            md.AppendLine("## 6. Yapay Zekâ İçin Kod Üretim Talimatı (Prompt Yönergesi)");
            md.AppendLine("Yapay zekâ bu proje tarzında yeni bir devre veya blok yazarken şu prensiplere kesinlikle uymalıdır:");
            md.AppendLine("1. **UDT Önceliği:** Valf, silindir veya motor gibi standart aktüatörler için doğrudan `UDTs/` klasöründeki struct tiplerini kullanın.");
            md.AppendLine("2. **Tag İsimlendirmesi:** `Tags/PLC_Tags.csv` dosyasındaki mevcut PLC tag tablosu isimlendirme formatına sadık kalın.");
            md.AppendLine("3. **Ladder Devre Mantığı:** Main (OB1) ve diğer bloklardaki kontak dizilimlerine, kilit (interlock) mantıklarına ve çağrı zincirine birebir uyun.");
            md.AppendLine("4. **Mimari Uyumluluk:** Yeni fonksiyon veya blok üretirken `Blocks/` klasöründeki mevcut FB/FC mantığını ve kilit (interlock) devrelerini örnek alın.");
            md.AppendLine();

            string mdPath = Path.Combine(outputDir, "AI_Project_Summary.md");
            File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);

            // 3. Generate AI_Training_Manifest.json
            var manifestObj = new
            {
                projectName = project?.Name,
                plcName = plc?.Name,
                plcType = plc?.TypeName,
                tiaVersion = _lockedVersion?.DisplayName,
                exportDate = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                metrics = new
                {
                    totalBlocks = blocks.Count,
                    totalUdts = udts.Count,
                    totalTagTables = tagTables.Count,
                    totalTags = tags.Count
                },
                namingConventions = new
                {
                    usesIPrefix,
                    usesQPrefix,
                    usesXPrefix,
                    inputCount,
                    outputCount,
                    memoryCount
                },
                udts = udts,
                tagTables = tagTables,
                blocks = blocks
            };

            string json = SerializeSimpleJson(manifestObj);
            string jsonPath = Path.Combine(outputDir, "AI_Training_Manifest.json");
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
        }

        public void CloseProject(TiaProjectItem project)
        {
            if (project?.RawProjectObject == null) return;

            RunOnOpennessThreadAsync(() =>
            {
                try
                {
                    MethodInfo closeMethod = project.RawProjectObject.GetType().GetMethod("Close", Type.EmptyTypes);
                    if (closeMethod != null)
                    {
                        closeMethod.Invoke(project.RawProjectObject, null);
                        _log($"[Güvenli Kapatma] '{project.Name}' projesi değişiklik yapılmadan kapatıldı.");
                    }
                }
                catch (Exception ex)
                {
                    _log($"[Bilgi] Proje kapatılırken: {ex.Message}");
                }
                return true;
            }).GetAwaiter().GetResult();
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return clean.Trim();
        }

        private static string SerializeSimpleJson(object obj)
        {
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                return serializer.Serialize(obj);
            }
            catch
            {
                return "{}";
            }
        }
    }
}
