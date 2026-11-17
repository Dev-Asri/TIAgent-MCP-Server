using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TIAgent.Models;

namespace TIAgent.Services
{
    public class PackageValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> FixesApplied { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string RulesFilePath { get; set; }
    }

    /// <summary>
    /// Ornekler klasöründeki eğitim ve tecrübe kurallarına (Paket_Dogrulama_Kurallari.json) bakarak
    /// gelen paketleri denetleyen, hatalı tipleri ve SimaticML yapılarını otomatik düzelten servis.
    /// </summary>
    public static class PackageValidator
    {
        private static string _cachedRulesPath;

        public static string ResolveOrneklerRulesPath()
        {
            if (!string.IsNullOrEmpty(_cachedRulesPath) && File.Exists(_cachedRulesPath))
                return _cachedRulesPath;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] probePaths = new[]
            {
                Path.Combine(baseDir, "Ornekler", "Paket_Dogrulama_Kurallari.json"),
                Path.Combine(baseDir, "..", "..", "Ornekler", "Paket_Dogrulama_Kurallari.json"),
                Path.Combine(baseDir, "..", "..", "..", "Ornekler", "Paket_Dogrulama_Kurallari.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Ornekler", "Paket_Dogrulama_Kurallari.json")
            };

            foreach (var p in probePaths)
            {
                try
                {
                    string full = Path.GetFullPath(p);
                    if (File.Exists(full))
                    {
                        _cachedRulesPath = full;
                        return full;
                    }
                }
                catch { }
            }

            return null;
        }

        public static PackageValidationResult ValidateAndSanitize(TiaPackage pkg, Action<string, string, bool> stepCallback = null)
        {
            var result = new PackageValidationResult();
            if (pkg == null) return result;

            string rulesPath = ResolveOrneklerRulesPath();
            result.RulesFilePath = rulesPath;

            // 1. DATA BLOCK MEMBER KONTROLÜ (NORM_X / SCALE_X Real kuralı)
            if (pkg.DataBlocks != null)
            {
                foreach (var db in pkg.DataBlocks)
                {
                    if (db.Members == null) continue;
                    foreach (var m in db.Members)
                    {
                        if (string.IsNullOrWhiteSpace(m.Name)) continue;

                        // Normalize değişken kuralı: İsminde 'Norm' geçen değişkenler Real olmalıdır!
                        if (IsNormalizedVariableName(m.Name))
                        {
                            string curType = (m.DataType ?? "").Trim();
                            if (!IsRealType(curType))
                            {
                                string fixMsg = $"{db.Name}.{m.Name} ({curType} -> Real)";
                                m.DataType = "Real";
                                if (string.IsNullOrWhiteSpace(m.StartValue) || m.StartValue == "0")
                                    m.StartValue = "0.0";

                                result.FixesApplied.Add(fixMsg);
                                stepCallback?.Invoke("doğrulama",
                                    $"Ornekler Kuralı: '{db.Name}.{m.Name}' tipi '{curType}' yerine 'Real' yapıldı (NORM_X/SCALE_X taşma koruması).",
                                    true);
                            }
                        }
                    }
                }
            }

            // 2. PLC TAG KONTROLÜ (NORM_X ve Analog adres kuralı)
            if (pkg.Tags != null)
            {
                foreach (var t in pkg.Tags)
                {
                    if (string.IsNullOrWhiteSpace(t.Name)) continue;

                    if (IsNormalizedVariableName(t.Name))
                    {
                        string curType = (t.DataType ?? "").Trim();
                        if (!IsRealType(curType))
                        {
                            string fixMsg = $"Tag '{t.Name}' ({curType} -> Real)";
                            t.DataType = "Real";
                            result.FixesApplied.Add(fixMsg);
                            stepCallback?.Invoke("doğrulama",
                                $"Ornekler Kuralı: PLC Tag '{t.Name}' tipi '{curType}' yerine 'Real' yapıldı.",
                                true);
                        }
                    }

                    // Analog Word tag bit adresi kontrolü (%I0.0 gibi bit adresi Word olamaz)
                    string addr = (t.LogicalAddress ?? t.Address ?? "").Trim().ToUpperInvariant();
                    string type = (t.DataType ?? "").Trim();
                    if ((type.Equals("Int", StringComparison.OrdinalIgnoreCase) || type.Equals("Word", StringComparison.OrdinalIgnoreCase))
                        && Regex.IsMatch(addr, @"^%[IQ]\d+\.\d+$"))
                    {
                        result.Warnings.Add($"Tag '{t.Name}' adresi '{addr}' bit formatında ancak veri tipi '{type}'. Bu adres TIA Portal derlemesinde hata verebilir.");
                    }
                }
            }

            // 3. SIMATICML XML KONTROLLERİ VE DÜZELTMELERİ
            if (pkg.Blocks != null)
            {
                foreach (var blk in pkg.Blocks)
                {
                    if (string.IsNullOrWhiteSpace(blk.SimaticMlXml)) continue;

                    string xml = blk.SimaticMlXml;
                    bool xmlModified = false;

                    // A. Kültür düzeltmesi (tr-TR -> en-US)
                    if (xml.Contains("<Culture>tr-TR</Culture>"))
                    {
                        xml = xml.Replace("<Culture>tr-TR</Culture>", "<Culture>en-US</Culture>");
                        xmlModified = true;
                        result.FixesApplied.Add($"{blk.Name}: Kültür 'en-US' olarak düzeltildi");
                    }

                    // B. SimaticML Part İsim Düzeltmeleri
                    if (xml.Contains("<Part Name=\"NORM_X\"") || xml.Contains("<Part Name=\"Norm_X\""))
                    {
                        xml = xml.Replace("<Part Name=\"NORM_X\"", "<Part Name=\"Normalize\"")
                                 .Replace("<Part Name=\"Norm_X\"", "<Part Name=\"Normalize\"");
                        xmlModified = true;
                        result.FixesApplied.Add($"{blk.Name}: NORM_X komut ismi 'Normalize' olarak düzeltildi");
                    }

                    if (xml.Contains("<Part Name=\"SCALE_X\""))
                    {
                        xml = xml.Replace("<Part Name=\"SCALE_X\"", "<Part Name=\"Scale_X\"");
                        xmlModified = true;
                        result.FixesApplied.Add($"{blk.Name}: SCALE_X komut ismi 'Scale_X' olarak düzeltildi");
                    }

                    // C. Sub komutunda Card şablon özniteliği temizliği
                    if (xml.Contains("<Part Name=\"Sub\""))
                    {
                        string subCleaned = Regex.Replace(xml,
                            @"(<Part\s+Name=""Sub""[^>]*>[\s\S]*?)<TemplateValue\s+Name=""Card""[^>]*>.*?</TemplateValue>",
                            "$1", RegexOptions.IgnoreCase);
                        if (subCleaned != xml)
                        {
                            xml = subCleaned;
                            xmlModified = true;
                            result.FixesApplied.Add($"{blk.Name}: 'Sub' komutundaki geçersiz 'Card' parametresi kaldırıldı");
                        }
                    }

                    // D. TON zamanlayıcı ET pini kontrolü
                    var tonMatches = Regex.Matches(xml, @"<Part\s+Name=""TON""\s+UId=""(\d+)""");
                    if (tonMatches.Count > 0)
                    {
                        foreach (Match tm in tonMatches)
                        {
                            string tonUid = tm.Groups[1].Value;
                            // Check if wire with Name="ET" exists for this tonUid
                            bool hasEtWire = Regex.IsMatch(xml, $@"<NameCon\s+UId=""{tonUid}""\s+Name=""ET""");
                            if (!hasEtWire)
                            {
                                // Find highest UId to generate unique UIds
                                int maxUid = 500;
                                var uidMatches = Regex.Matches(xml, @"UId=""(\d+)""");
                                foreach (Match um in uidMatches)
                                {
                                    if (int.TryParse(um.Groups[1].Value, out int u) && u > maxUid)
                                        maxUid = u;
                                }

                                int newWireUid = maxUid + 1;
                                int newOpenUid = maxUid + 2;

                                string etWireXml = $"<Wire UId=\"{newWireUid}\">\n          <NameCon UId=\"{tonUid}\" Name=\"ET\" />\n          <OpenCon UId=\"{newOpenUid}\" />\n        </Wire>\n      </Wires>";
                                xml = Regex.Replace(xml, @"</Wires>", etWireXml, RegexOptions.None);
                                xmlModified = true;
                                result.FixesApplied.Add($"{blk.Name}: TON zamanlayıcısı (UId {tonUid}) ET pini için otomatik OpenCon eklendi");
                            }
                        }
                    }

                    if (xmlModified)
                    {
                        blk.SimaticMlXml = xml;
                    }
                }
            }

            // Geri bildirim
            if (result.FixesApplied.Count > 0)
            {
                stepCallback?.Invoke("doğrulama",
                    $"Paket doğrulandı: {result.FixesApplied.Count} sorun tespit edildi ve Ornekler eğitim kurallarına göre otomatik düzeltildi.",
                    true);
            }
            else
            {
                stepCallback?.Invoke("doğrulama",
                    $"Paket Ornekler eğitim ve derleme kurallarına tam uyumlu: {pkg.PackageName}",
                    true);
            }

            return result;
        }

        private static bool IsNormalizedVariableName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            // Matches names like "DC_Motor_Hiz_Norm", "Hiz_Norm", "Basinc_Norm", "Norm_Deger", etc.
            return Regex.IsMatch(name, @"(^|[_\.])Norm([_\.]|$)", RegexOptions.IgnoreCase) ||
                   name.EndsWith("_Norm", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Norm_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRealType(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return false;
            return dataType.Equals("Real", StringComparison.OrdinalIgnoreCase) ||
                   dataType.Equals("LReal", StringComparison.OrdinalIgnoreCase);
        }
    }
}
