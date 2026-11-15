using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TIAgent.Models;

namespace TIAgent.Services
{
    public static class PlcPackageGenerator
    {
        // When true, FB/FC based packages are converted into plain LAD networks written directly into Main [OB1]
        [ThreadStatic] private static bool _inlineIntoMain;

        /// <summary>
        /// "FC FB kullanmadan", "Main içinde", "doğrudan OB1'e" gibi ifadeler -> devre FB/FC yerine Main [OB1] içine yazılır.
        /// </summary>
        public static bool IsInlineMainRequest(string lower)
        {
            if (string.IsNullOrWhiteSpace(lower)) return false;
            return (lower.Contains("kullanma") && (lower.Contains("fb") || lower.Contains("fc")))
                || (lower.Contains("değil") && (lower.Contains("fb") || lower.Contains("fc")))
                || (lower.Contains("degil") && (lower.Contains("fb") || lower.Contains("fc")))
                || (lower.Contains("olmadan") && (lower.Contains("fb") || lower.Contains("fc")))
                || (lower.Contains("olmasın") && (lower.Contains("fb") || lower.Contains("fc")))
                || (lower.Contains("olmasin") && (lower.Contains("fb") || lower.Contains("fc")))
                || (lower.Contains("istemiyorum") && (lower.Contains("fb") || lower.Contains("fc")))
                || lower.Contains("fb'siz") || lower.Contains("fbsiz") || lower.Contains("fc'siz") || lower.Contains("fcsiz")
                || lower.Contains("main içinde") || lower.Contains("main icinde") || lower.Contains("main içine") || lower.Contains("main icine")
                || lower.Contains("main blok") || lower.Contains("main blog")
                || lower.Contains("main ob1") || lower.Contains("main de") || lower.Contains("main'de") || lower.Contains("main'e")
                || lower.Contains("main'in içine") || lower.Contains("main e yaz") || lower.Contains("main'e yaz")
                || lower.Contains("ob1 içinde") || lower.Contains("ob1 icinde") || lower.Contains("ob1 içine") || lower.Contains("ob1 icine") || lower.Contains("ob1 de") || lower.Contains("ob1'de")
                || lower.Contains("doğrudan main") || lower.Contains("dogrudan main") || lower.Contains("direkt main");
        }

        public static TiaPackage GenerateFromPrompt(
            string prompt,
            string preferredLanguage = null,
            string targetFolder = null,
            string tiaVersion = "V20",
            string sourceChannel = "Yazılı Komut")
        {
            if (string.IsNullOrWhiteSpace(prompt)) return null;

            string lowerAll = prompt.ToLowerInvariant();
            bool previousInline = _inlineIntoMain;
            _inlineIntoMain = IsInlineMainRequest(lowerAll);
            try
            {
                return GenerateFromPromptCore(prompt, preferredLanguage, targetFolder, tiaVersion, sourceChannel);
            }
            finally
            {
                _inlineIntoMain = previousInline;
            }
        }

        private static TiaPackage GenerateFromPromptCore(
            string prompt,
            string preferredLanguage,
            string targetFolder,
            string tiaVersion,
            string sourceChannel)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return null;

            string lower = prompt.ToLowerInvariant();

            // Determine language: LAD default, SCL if requested
            string lang = "LAD";
            if (!string.IsNullOrWhiteSpace(preferredLanguage))
            {
                if (preferredLanguage.IndexOf("SCL", StringComparison.OrdinalIgnoreCase) >= 0)
                    lang = "SCL";
                else if (preferredLanguage.IndexOf("LAD", StringComparison.OrdinalIgnoreCase) >= 0)
                    lang = "LAD";
            }
            if (lower.Contains("scl de") || lower.Contains("scl'de") || lower.Contains("scl olarak") || lower.Contains("scl dilinde") || lower.Contains("scl yaz"))
            {
                lang = "SCL";
            }
            else if (lower.Contains("ladder") || lower.Contains("lad ") || lower.Contains("kontak"))
            {
                lang = "LAD";
            }

            string folder = targetFolder ?? "";
            var folderMatch = Regex.Match(prompt, @"klas[öo]r\s*[:=]\s*([a-zA-Z0-9_\/\\]+)", RegexOptions.IgnoreCase);
            if (folderMatch.Success && string.IsNullOrWhiteSpace(folder))
            {
                folder = folderMatch.Groups[1].Value.Trim();
            }

            // Sadece doğrudan SCL kodu / blok tanımı girildiyse derle
            if (Regex.IsMatch(prompt, @"\b(FUNCTION_BLOCK|FUNCTION)\b", RegexOptions.IgnoreCase) ||
                (prompt.Contains(":=") && prompt.Contains(";")))
            {
                return BuildCustomSclCodePackage(prompt, lang, folder, tiaVersion, sourceChannel);
            }

            // Donanım Modülü Ekleme İstekleri (ör. "PLC ye 16I 16Q modul ekle", "DI 16/DQ 16 x24VDC ST olarak ekle")
            bool isModuleRequest = (lower.Contains("modül") || lower.Contains("modul") || lower.Contains("sm 12") || lower.Contains("sm12") || lower.Contains("sb 12") || lower.Contains("di 16") || lower.Contains("dq 16") || lower.Contains("16i") || lower.Contains("16q") || lower.Contains("8i") || lower.Contains("8q")) &&
                                   (lower.Contains("ekle") || lower.Contains("tak") || lower.Contains("add") || lower.Contains("plug") || lower.Contains("tanımla") || lower.Contains("tanimla") || lower.Contains("oluştur") || lower.Contains("olustur"));

            if (isModuleRequest)
            {
                return BuildHardwareModulePackage(prompt, folder, tiaVersion, sourceChannel);
            }

            // T-IA Agent ASLA kendi kendine hazır devre (motor, pres, vb.) üretmez!
            // Tüm paketler AI tarafından oluşturulup köprü üzerinden gönderilir.
            return null;
        }

        public static TiaPackage BuildHardwareModulePackage(string prompt, string folder, string tiaVersion, string sourceChannel)
        {
            string lower = prompt.ToLowerInvariant();
            string modName = "SM_Module";
            if (lower.Contains("16i") || lower.Contains("16di") || lower.Contains("di 16") || lower.Contains("16"))
                modName = "SM_DI16_DQ16";
            else if (lower.Contains("8i") || lower.Contains("8di") || lower.Contains("di 8") || lower.Contains("8"))
                modName = "SM_DI8_DQ8";

            var pkg = new TiaPackage
            {
                PackageName = "Paket_DonanimModulu",
                Title = "PLC Donanım Modülü Ekleme",
                Description = $"Donanım modül talebi ({sourceChannel}): {prompt}",
                SourcePrompt = prompt,
                SourceChannel = sourceChannel,
                TargetFolder = folder,
                Language = "LAD",
                AutoApprove = true
            };

            pkg.PlcModules.Add(new HardwareModuleEntry
            {
                Name = modName,
                TypeIdentifier = prompt,
                Comment = prompt
            });

            return pkg;
        }

        // =========================================================================================
        // DOĞRUDAN SCL KODU DERLEYİCİ
        // =========================================================================================
        public static TiaPackage BuildCustomSclCodePackage(
            string rawSclOrStatements,
            string lang,
            string folder,
            string tiaVersion,
            string sourceChannel)
        {
            string bName = "FB_OzelKontrol";
            string bType = "FB";

            var headerMatch = Regex.Match(rawSclOrStatements, @"\b(FUNCTION_BLOCK|FUNCTION)\s+""?([a-zA-Z0-9_]+)""?", RegexOptions.IgnoreCase);
            if (headerMatch.Success)
            {
                bType = headerMatch.Groups[1].Value.StartsWith("FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase) ? "FB" : "FC";
                bName = headerMatch.Groups[2].Value.Trim();
            }

            string bodyCode = rawSclOrStatements;
            int beginIdx = rawSclOrStatements.IndexOf("BEGIN", StringComparison.OrdinalIgnoreCase);
            if (beginIdx >= 0)
            {
                bodyCode = rawSclOrStatements.Substring(beginIdx + 5);
                bodyCode = Regex.Replace(bodyCode, @"\b(END_FUNCTION_BLOCK|END_FUNCTION)\b[\s\S]*$", "", RegexOptions.IgnoreCase).Trim();
            }

            var pkg = new TiaPackage
            {
                PackageName = bName,
                Title = $"Özel Otomasyon Bloğu ({bName})",
                Description = "Kullanıcı / AI tarafından verilen özel mantıksal ifadelerden üretilen paket.",
                SourcePrompt = rawSclOrStatements,
                SourceChannel = sourceChannel,
                TargetFolder = folder,
                Language = lang,
                TagTableName = "Ozel_IO_Taglari"
            };

            var iface = new List<BlockInterfaceMember>();
            var stmts = SclCodeParser.SplitSclStatements(bodyCode);
            var titledStmts = new List<Tuple<string, string>>();
            int nIdx = 1;
            foreach (var s in stmts)
            {
                titledStmts.Add(Tuple.Create($"Network {nIdx++}", s));
            }

            string instDbName = $"DB_{bName}";
            if (bType == "FB")
            {
                pkg.DataBlocks.Add(new PlcDbEntry { Name = instDbName, DbType = "InstanceDB", InstanceOfFbName = bName });
            }

            FinalizePackageBlocks(pkg, bName, bType, instDbName, iface, titledStmts, new Dictionary<string, string>(), tiaVersion);
            return pkg;
        }

        // =========================================================================================
        // ORTAK SIMATICML DERLEYİCİ VE GÖRSEL LADDER ŞEMASI OLUŞTURUCU
        // =========================================================================================
        public static void FinalizePackageBlocks(
            TiaPackage pkg,
            string blockName,
            string blockType,
            string instDbName,
            List<BlockInterfaceMember> iface,
            List<Tuple<string, string>> titledStatements,
            Dictionary<string, string> mainCallBindings,
            string tiaVersion)
        {
            // "FC FB kullanmadan Main içinde yaz" -> convert the FB/FC logic into plain networks inside Main [OB1]
            if (_inlineIntoMain && !string.Equals(blockType, "OB", StringComparison.OrdinalIgnoreCase))
            {
                InlinePackageIntoMainOb(pkg, blockName, iface, titledStatements, mainCallBindings, tiaVersion);
                return;
            }

            var extTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var globalSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in pkg.Tags)
            {
                extTypes[t.Name] = t.DataType;
                globalSymbols.Add(t.Name);
            }

            foreach (var db in pkg.DataBlocks)
            {
                globalSymbols.Add(db.Name);
                if (db.Members != null)
                {
                    foreach (var m in db.Members)
                    {
                        extTypes[$"{db.Name}.{m.Name}"] = m.DataType;
                        extTypes[m.Name] = m.DataType;
                    }
                }
            }

            bool isOb = string.Equals(blockType, "OB", StringComparison.OrdinalIgnoreCase);
            int? blockNum = isOb ? (int?)1 : null;

            bool forceScl = string.Equals(pkg.Language, "SCL", StringComparison.OrdinalIgnoreCase);
            var parsed = SclCodeParser.BuildParsedBlockFromSpec(
                blockName,
                blockType,
                blockNum,
                iface,
                titledStatements,
                extTypes,
                globalSymbols,
                tiaVersion,
                forceScl);

            var converter = new SclToLadConverter();
            string simaticMl = converter.ConvertToSimaticMl(parsed, blockName, blockNum, tiaVersion, useLadderDb: true);

            // If converter inferred any additional LADDERDB helper tags, add LADDERDB to package DataBlocks
            if (converter.LastGeneratedLadderDbTags.Count > 0)
            {
                var ladderDb = pkg.DataBlocks.FirstOrDefault(d => string.Equals(d.Name, "LADDERDB", StringComparison.OrdinalIgnoreCase));
                if (ladderDb == null)
                {
                    ladderDb = new PlcDbEntry { Name = "LADDERDB", DbType = "GlobalDB", Comment = "Yardımcı Ladder Değişkenleri" };
                    pkg.DataBlocks.Add(ladderDb);
                }
                foreach (var kv in converter.LastGeneratedLadderDbTags)
                {
                    if (!ladderDb.Members.Any(m => string.Equals(m.Name, kv.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        ladderDb.Members.Add(new PlcDbMemberEntry { Name = kv.Key, DataType = kv.Value });
                    }
                }
            }

            // Build complete SCL source file representation as well
            string fullSclSource = BuildStandaloneSclSource(blockName, blockType, iface, titledStatements);

            // Build visual ladder networks for UI preview
            var visualNetworks = new List<VisualLadderNetwork>();
            int netNum = 1;
            foreach (var stmt in parsed.Statements)
            {
                visualNetworks.Add(BuildVisualNetworkFromStatement(netNum++, stmt, pkg));
            }

            var mainBlockEntry = new PlcBlockEntry
            {
                Name = blockName,
                BlockType = blockType,
                BlockNumber = blockNum,
                Language = pkg.Language,
                FolderPath = isOb ? "" : pkg.TargetFolder,
                InterfaceMembers = iface,
                Networks = visualNetworks,
                SclCode = fullSclSource,
                SimaticMlXml = simaticMl,
                IsMainObCall = false
            };
            pkg.Blocks.Add(mainBlockEntry);

            // Add Main [OB1] Call Entry only when primary block is an FB or FC (not when writing directly to Main [OB1])
            if (!isOb)
            {
                pkg.Blocks.Add(new PlcBlockEntry
                {
                    Name = "Main",
                    BlockType = "OB",
                    BlockNumber = 1,
                    Language = "LAD",
                    IsMainObCall = true,
                    CallTargetBlockName = blockName,
                    CallTargetBlockType = blockType,
                    CallInstanceDbName = blockType == "FB" ? instDbName : null,
                    CallParameterBindings = mainCallBindings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        private static void InlinePackageIntoMainOb(
            TiaPackage pkg,
            string blockName,
            List<BlockInterfaceMember> iface,
            List<Tuple<string, string>> titledStatements,
            Dictionary<string, string> mainCallBindings,
            string tiaVersion)
        {
            string suffix = Regex.Replace(blockName ?? "Devre", @"^(FB|FC)_", "", RegexOptions.IgnoreCase);
            string helperDbName = "DB_" + suffix + "_Main";
            PlcDbEntry helperDb = null;

            // Map every interface member (#X) to its bound global tag, or to a member of a helper global DB
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in iface ?? new List<BlockInterfaceMember>())
            {
                string bound = null;
                if (mainCallBindings != null && mainCallBindings.TryGetValue(m.Name, out var b) && !string.IsNullOrWhiteSpace(b))
                    bound = b.Trim();

                if (bound != null)
                {
                    map[m.Name] = Regex.IsMatch(bound, @"^[A-Za-z_][A-Za-z0-9_]*$") ? $"\"{bound}\"" : bound;
                }
                else
                {
                    if (helperDb == null)
                    {
                        helperDb = new PlcDbEntry
                        {
                            Name = helperDbName,
                            DbType = "GlobalDB",
                            Comment = $"{suffix} devresinin Main [OB1] yardımcı değişkenleri"
                        };
                        pkg.DataBlocks.Add(helperDb);
                    }
                    helperDb.Members.Add(new PlcDbMemberEntry { Name = m.Name, DataType = m.DataType, Comment = m.Comment });
                    map[m.Name] = $"\"{helperDbName}\".{m.Name}";
                }
            }

            var inlineStatements = new List<Tuple<string, string>>();
            foreach (var ts in titledStatements ?? new List<Tuple<string, string>>())
            {
                string code = Regex.Replace(ts.Item2 ?? "", @"#([A-Za-z_][A-Za-z0-9_]*)",
                    mm => map.TryGetValue(mm.Groups[1].Value, out var rep) ? rep : mm.Value);
                string title = Regex.Replace(ts.Item1 ?? "", @"^Network\s*\d+\s*:\s*", "", RegexOptions.IgnoreCase);

                var parts = SclCodeParser.SplitSclStatements(code);
                if (parts.Count <= 1)
                {
                    inlineStatements.Add(Tuple.Create($"{suffix}: {title}", code));
                }
                else
                {
                    for (int pi = 0; pi < parts.Count; pi++)
                    {
                        string pCode = parts[pi];
                        string pTitle = $"{suffix}: {title} (#{pi + 1})";
                        var tm = Regex.Match(pCode, @"^([A-Za-z0-9_""\.]+)\s*:=", RegexOptions.IgnoreCase);
                        if (tm.Success) pTitle = $"{suffix}: {title} [{tm.Groups[1].Value}]";
                        inlineStatements.Add(Tuple.Create(pTitle, pCode));
                    }
                }
            }

            // No FB/FC -> no instance DB
            pkg.DataBlocks.RemoveAll(d =>
                string.Equals(d.DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.InstanceOfFbName, blockName, StringComparison.OrdinalIgnoreCase));

            pkg.PackageName = "Main_OB1_" + suffix;
            pkg.Title = (pkg.Title ?? suffix) + " — FB/FC'siz, doğrudan Main [OB1] içinde";
            pkg.TargetFolder = "";

            FinalizePackageBlocks(pkg, "Main", "OB", null, new List<BlockInterfaceMember>(), inlineStatements,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), tiaVersion);

            var mainBlk = pkg.Blocks.FirstOrDefault(x => !x.IsMainObCall && string.Equals(x.Name, "Main", StringComparison.OrdinalIgnoreCase));
            if (mainBlk != null)
            {
                // Only networks of this circuit are replaced; every other Main [OB1] network is preserved
                mainBlk.IsPartialMainObUpdate = true;
                mainBlk.DeleteNetworkKeywords.Add(blockName);
            }
        }

        private static string BuildStandaloneSclSource(
            string blockName,
            string blockType,
            List<BlockInterfaceMember> iface,
            List<Tuple<string, string>> titledStatements)
        {
            var sb = new StringBuilder();
            bool isFb = string.Equals(blockType, "FB", StringComparison.OrdinalIgnoreCase);
            bool isOb = string.Equals(blockType, "OB", StringComparison.OrdinalIgnoreCase);
            if (isOb)
                sb.AppendLine($"ORGANIZATION_BLOCK \"{blockName}\"");
            else if (isFb)
                sb.AppendLine($"FUNCTION_BLOCK \"{blockName}\"");
            else
                sb.AppendLine($"FUNCTION \"{blockName}\" : Void");
            sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
            sb.AppendLine("VERSION : 0.1");

            void AppendSection(string secName, string sclBlockHeader)
            {
                var list = iface?.Where(x => string.Equals(x.Section, secName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (list != null && list.Count > 0)
                {
                    sb.AppendLine($"   {sclBlockHeader}");
                    foreach (var m in list)
                    {
                        sb.AppendLine($"      {m.Name} : {m.DataType ?? "Bool"};");
                    }
                    sb.AppendLine("   END_VAR");
                }
            }

            AppendSection("Input", "VAR_INPUT");
            AppendSection("Output", "VAR_OUTPUT");
            AppendSection("InOut", "VAR_IN_OUT");
            if (isFb) AppendSection("Static", "VAR");
            AppendSection("Temp", "VAR_TEMP");

            sb.AppendLine("BEGIN");
            if (titledStatements != null)
            {
                foreach (var pair in titledStatements)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Item1))
                        sb.AppendLine($"   // {pair.Item1}");
                    sb.AppendLine("   " + (pair.Item2 ?? "").Replace("\r\n", "\r\n   "));
                    sb.AppendLine();
                }
            }
            sb.AppendLine(isOb ? "END_ORGANIZATION_BLOCK" : (isFb ? "END_FUNCTION_BLOCK" : "END_FUNCTION"));
            return sb.ToString();
        }

        private static VisualLadderNetwork BuildVisualNetworkFromStatement(int netNum, SclStatementInfo stmt, TiaPackage pkg)
        {
            var vNet = new VisualLadderNetwork
            {
                NetworkNumber = netNum,
                Title = stmt.Title ?? $"Network {netNum}",
                Comment = stmt.ConversionSummary,
                Language = stmt.IsLadActive ? "LAD" : "SCL",
                SclPreview = stmt.RawSclCode
            };

            string raw = (stmt.RawSclCode ?? "").Trim();

            // Build clean ASCII / Visual Ladder representation for preview
            var ascii = new StringBuilder();
            var ifMatch = Regex.Match(raw, @"^IF\s+([\s\S]+?)\s+THEN\s+([\s\S]+?)\s*END_IF;?$", RegexOptions.IgnoreCase);
            if (ifMatch.Success)
            {
                string cond = ifMatch.Groups[1].Value.Trim();
                string act = ifMatch.Groups[2].Value.Trim().TrimEnd(';');

                PopulateVisualContactsFromCondition(cond, vNet.Elements, pkg);

                string coilSym = "--( )--";
                string coilType = "Coil";
                string target = act;
                if (act.IndexOf(":= TRUE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    coilSym = "--( S )--";
                    coilType = "SetCoil";
                    target = act.Split(new[] { ":=" }, StringSplitOptions.None)[0].Trim();
                }
                else if (act.IndexOf(":= FALSE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    coilSym = "--( R )--";
                    coilType = "ResetCoil";
                    target = act.Split(new[] { ":=" }, StringSplitOptions.None)[0].Trim();
                }
                else if (act.Contains(":="))
                {
                    var parts = act.Split(new[] { ":=" }, StringSplitOptions.None);
                    target = parts[0].Trim();
                    coilSym = $"--[ MOVE {parts[1].Trim()} -> {target} ]--";
                    coilType = "Box";
                }

                vNet.Elements.Add(new VisualLadderElement
                {
                    ElementType = coilType,
                    TagName = target,
                    AddressOrType = ResolveTagAddressOrType(target, pkg),
                    BoxTitle = coilSym
                });

                ascii.Append($"|--- {FormatConditionAsLadderText(cond)} ----{coilSym} {target} |");
            }
            else if (Regex.IsMatch(raw, @"\.(TON|TOF|TP|TONR)\s*\(", RegexOptions.IgnoreCase))
            {
                var tm = Regex.Match(raw, @"^(?<inst>[\s\S]+?)\.(?<type>TON|TOF|TP|TONR)\s*\(\s*IN\s*:=\s*(?<in>[^,]+),\s*PT\s*:=\s*(?<pt>[^,\)]+)(?:,\s*Q\s*=>\s*(?<q>[^,\)]+))?(?:,\s*ET\s*=>\s*(?<et>[^\)]+))?", RegexOptions.IgnoreCase);
                if (tm.Success)
                {
                    string inst = tm.Groups["inst"].Value.Trim();
                    string tType = tm.Groups["type"].Value.Trim().ToUpperInvariant();
                    string inExpr = tm.Groups["in"].Value.Trim();
                    string ptExpr = tm.Groups["pt"].Value.Trim();
                    string qExpr = tm.Groups["q"].Value.Trim();

                    PopulateVisualContactsFromCondition(inExpr, vNet.Elements, pkg);
                    vNet.Elements.Add(new VisualLadderElement
                    {
                        ElementType = "Box",
                        TagName = inst,
                        AddressOrType = $"PT: {ptExpr}",
                        BoxTitle = $"[{tType} · {ptExpr}]"
                    });
                    if (!string.IsNullOrEmpty(qExpr))
                    {
                        vNet.Elements.Add(new VisualLadderElement
                        {
                            ElementType = "Coil",
                            TagName = qExpr,
                            AddressOrType = ResolveTagAddressOrType(qExpr, pkg),
                            BoxTitle = "--( Q )--"
                        });
                    }
                    ascii.Append($"|--- {FormatConditionAsLadderText(inExpr)} ----[ {tType} {inst} (PT: {ptExpr}) ]----( Q ) {qExpr} |");
                }
                else
                {
                    ascii.Append($"|--- [ {raw} ] ---|");
                }
            }
            else if (raw.Contains(":="))
            {
                int idx = raw.IndexOf(":=", StringComparison.Ordinal);
                string target = raw.Substring(0, idx).Trim();
                string expr = raw.Substring(idx + 2).Trim().TrimEnd(';');

                PopulateVisualContactsFromCondition(expr, vNet.Elements, pkg);
                vNet.Elements.Add(new VisualLadderElement
                {
                    ElementType = "Coil",
                    TagName = target,
                    AddressOrType = ResolveTagAddressOrType(target, pkg),
                    BoxTitle = "--( )--"
                });

                ascii.Append($"|--- {FormatConditionAsLadderText(expr)} ----(   ) {target} |");
            }
            else
            {
                ascii.Append($"|--- [ {raw} ] ---|");
            }

            vNet.AsciiLadderDiagram = ascii.ToString();
            return vNet;
        }

        private static void PopulateVisualContactsFromCondition(string expr, List<VisualLadderElement> elements, TiaPackage pkg)
        {
            var tokens = Regex.Matches(expr, @"(NOT\s+)?(#?[""a-zA-Z0-9_\.]+)");
            foreach (Match m in tokens)
            {
                string tok = m.Groups[2].Value.Trim();
                if (tok.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isNc = !string.IsNullOrWhiteSpace(m.Groups[1].Value);
                elements.Add(new VisualLadderElement
                {
                    ElementType = isNc ? "NC" : "NO",
                    TagName = tok,
                    AddressOrType = ResolveTagAddressOrType(tok, pkg),
                    BoxTitle = isNc ? "--| / |--" : "--|   |--"
                });
            }
        }

        private static string FormatConditionAsLadderText(string cond)
        {
            if (string.IsNullOrWhiteSpace(cond)) return "";
            string s = cond;
            s = Regex.Replace(s, @"\bNOT\s+([#""a-zA-Z0-9_\.]+)", "--|/|--[$1]");
            s = Regex.Replace(s, @"\bAND\b", " --- ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bOR\b", " // ", RegexOptions.IgnoreCase);
            return s;
        }

        private static string ResolveTagAddressOrType(string tagOrSymbol, TiaPackage pkg)
        {
            if (string.IsNullOrWhiteSpace(tagOrSymbol) || pkg == null) return "";
            string clean = tagOrSymbol.Trim().TrimStart('#').Trim('\"');
            var tag = pkg.Tags?.FirstOrDefault(t => string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (tag != null) return $"{tag.LogicalAddress} ({tag.DataType})";
            if (tagOrSymbol.StartsWith("#")) return "Local Param";
            if (tagOrSymbol.Contains(".")) return "DB Tag";
            return "Bool";
        }
    }
}

