using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TIAgent.Services
{
    public class ParsedLadderNetwork
    {
        public int NetworkNumber { get; set; }
        public string Title { get; set; }
        public string Comment { get; set; }
        public List<string> InputsAndContacts { get; set; } = new List<string>();
        public List<string> OutputsAndCoils { get; set; } = new List<string>();
        public List<string> BlockCalls { get; set; } = new List<string>();
        public List<string> LogicExpressions { get; set; } = new List<string>();
        public string AsciiDiagram { get; set; }
    }

    public class ParsedLadderBlock
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; }
        public string Language { get; set; } = "LAD";
        public List<ParsedLadderNetwork> Networks { get; set; } = new List<ParsedLadderNetwork>();
        public string MarkdownReport { get; set; }
        public string PseudoSclCode { get; set; }
    }

    public static class LadCodeParser
    {
        public static ParsedLadderBlock ParseLadderXml(string xmlPath)
        {
            if (!File.Exists(xmlPath)) return null;

            try
            {
                var doc = XDocument.Load(xmlPath);
                var root = doc.Root;
                if (root == null) return null;

                var blockElem = root.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
                if (blockElem == null) return null;

                var attrList = blockElem.Element("AttributeList");
                string bName = attrList?.Element("Name")?.Value ?? Path.GetFileNameWithoutExtension(xmlPath);
                string bLang = attrList?.Element("ProgrammingLanguage")?.Value ?? "LAD";

                string bType = "OB";
                string localName = blockElem.Name.LocalName;
                if (localName.EndsWith(".OB", StringComparison.OrdinalIgnoreCase)) bType = "OB";
                else if (localName.EndsWith(".FB", StringComparison.OrdinalIgnoreCase)) bType = "FB";
                else if (localName.EndsWith(".FC", StringComparison.OrdinalIgnoreCase)) bType = "FC";

                var result = new ParsedLadderBlock
                {
                    BlockName = bName,
                    BlockType = bType,
                    Language = bLang
                };

                var compileUnits = blockElem.Descendants()
                    .Where(d => d.Name.LocalName.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int netIndex = 1;
                foreach (var cu in compileUnits)
                {
                    var net = ParseCompileUnit(cu, netIndex++);
                    if (net != null)
                    {
                        result.Networks.Add(net);
                    }
                }

                result.MarkdownReport = GenerateMarkdownReport(result);
                result.PseudoSclCode = GeneratePseudoScl(result);

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static ParsedLadderNetwork ParseCompileUnit(XElement cu, int netNum)
        {
            var net = new ParsedLadderNetwork { NetworkNumber = netNum };

            // 1. Title & Comment
            net.Title = ExtractMultilingualText(cu, "Title");
            net.Comment = ExtractMultilingualText(cu, "Comment");

            var flgNet = cu.Descendants().FirstOrDefault(d => d.Name.LocalName == "FlgNet");
            if (flgNet == null)
            {
                // SCL compile unit or empty
                return net;
            }

            var partsElem = flgNet.Element(flgNet.Name.Namespace + "Parts") ?? flgNet.Element("Parts");
            var wiresElem = flgNet.Element(flgNet.Name.Namespace + "Wires") ?? flgNet.Element("Wires");

            var accessDict = new Dictionary<string, string>(); // UId -> Symbol Name
            var partDict = new Dictionary<string, XElement>();   // UId -> Part XElement
            var wireOperandMap = new Dictionary<string, string>(); // Part UId -> Operand Symbol
            var wireConnections = new List<(string fromPart, string toPart)>();

            if (partsElem != null)
            {
                foreach (var child in partsElem.Elements())
                {
                    string uid = child.Attribute("UId")?.Value;
                    if (string.IsNullOrEmpty(uid)) continue;

                    string elemName = child.Name.LocalName;
                    if (elemName == "Access")
                    {
                        string sym = ExtractSymbolFromAccess(child);
                        if (!string.IsNullOrEmpty(sym))
                            accessDict[uid] = sym;
                    }
                    else if (elemName == "Part")
                    {
                        partDict[uid] = child;
                    }
                }
            }

            if (wiresElem != null)
            {
                foreach (var wire in wiresElem.Elements())
                {
                    var idents = wire.Elements().Where(e => e.Name.LocalName == "IdentCon").ToList();
                    var nameCons = wire.Elements().Where(e => e.Name.LocalName == "NameCon").ToList();

                    // Check if wire connects an Access (operand) to a Part
                    if (idents.Count > 0 && nameCons.Count > 0)
                    {
                        foreach (var id in idents)
                        {
                            string idUid = id.Attribute("UId")?.Value;
                            if (idUid != null && accessDict.TryGetValue(idUid, out string operandSym))
                            {
                                foreach (var nc in nameCons)
                                {
                                    string partUid = nc.Attribute("UId")?.Value;
                                    string paramName = nc.Attribute("Name")?.Value ?? "operand";
                                    if (partUid != null)
                                    {
                                        wireOperandMap[$"{partUid}:{paramName}"] = operandSym;
                                        if (paramName.Equals("operand", StringComparison.OrdinalIgnoreCase))
                                        {
                                            wireOperandMap[partUid] = operandSym;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Check wire connections between parts (e.g. contact out -> coil in)
                    var outCons = nameCons.Where(nc => nc.Attribute("Name")?.Value == "out").ToList();
                    var inCons = nameCons.Where(nc => nc.Attribute("Name")?.Value == "in").ToList();
                    foreach (var oc in outCons)
                    {
                        string fromId = oc.Attribute("UId")?.Value;
                        foreach (var ic in inCons)
                        {
                            string toId = ic.Attribute("UId")?.Value;
                            if (fromId != null && toId != null)
                            {
                                wireConnections.Add((fromId, toId));
                            }
                        }
                    }
                }
            }

            var contacts = new List<string>();
            var coils = new List<string>();
            var calls = new List<string>();

            // Process parts
            var coilToContactsMap = new Dictionary<string, List<string>>();

            foreach (var kvp in partDict)
            {
                string pUid = kvp.Key;
                var part = kvp.Value;
                string pName = part.Attribute("Name")?.Value ?? "";

                wireOperandMap.TryGetValue(pUid, out string op);
                bool isNegated = part.Elements().Any(e => e.Name.LocalName == "Negated");

                if (pName.Equals("Contact", StringComparison.OrdinalIgnoreCase))
                {
                    string contactStr = string.IsNullOrEmpty(op) ? "Contact" : op;
                    if (isNegated) contactStr = $"NOT {contactStr}";
                    contacts.Add(contactStr);
                }
                else if (pName.Equals("Coil", StringComparison.OrdinalIgnoreCase))
                {
                    string coilStr = string.IsNullOrEmpty(op) ? "Coil" : op;
                    if (isNegated) coilStr = $"( / ) {coilStr}";
                    coils.Add(coilStr);
                }
                else if (pName.Equals("SCoil", StringComparison.OrdinalIgnoreCase) || pName.Equals("SetCoil", StringComparison.OrdinalIgnoreCase))
                {
                    string coilStr = string.IsNullOrEmpty(op) ? "SetCoil" : op;
                    coils.Add($"( S ) {coilStr}");
                }
                else if (pName.Equals("RCoil", StringComparison.OrdinalIgnoreCase) || pName.Equals("ResetCoil", StringComparison.OrdinalIgnoreCase))
                {
                    string coilStr = string.IsNullOrEmpty(op) ? "ResetCoil" : op;
                    coils.Add($"( R ) {coilStr}");
                }
                else if (pName.Equals("Call", StringComparison.OrdinalIgnoreCase))
                {
                    var callInfo = part.Descendants().FirstOrDefault(d => d.Name.LocalName == "CallInfo");
                    string callName = callInfo?.Attribute("Name")?.Value ?? "FunctionBlock";
                    string instName = callInfo?.Descendants().FirstOrDefault(d => d.Name.LocalName == "Component")?.Attribute("Name")?.Value;

                    var paramList = new List<string>();
                    foreach (var param in callInfo?.Descendants().Where(d => d.Name.LocalName == "Parameter") ?? Enumerable.Empty<XElement>())
                    {
                        string pPName = param.Attribute("Name")?.Value;
                        if (pPName != null && wireOperandMap.TryGetValue($"{pUid}:{pPName}", out string assignedVal))
                        {
                            paramList.Add($"{pPName} := {assignedVal}");
                        }
                    }

                    string callStr = string.IsNullOrEmpty(instName)
                        ? $"CALL \"{callName}\"({string.Join(", ", paramList)})"
                        : $"CALL \"{callName}\", \"{instName}\"({string.Join(", ", paramList)})";

                    calls.Add(callStr);
                }
                else if (pName.Equals("TON", StringComparison.OrdinalIgnoreCase) || pName.Equals("TOF", StringComparison.OrdinalIgnoreCase) || pName.Equals("TP", StringComparison.OrdinalIgnoreCase))
                {
                    var instComp = part.Descendants().FirstOrDefault(d => d.Name.LocalName == "Component")?.Attribute("Name")?.Value;
                    wireOperandMap.TryGetValue($"{pUid}:PT", out string ptVal);
                    calls.Add($"TIMER {pName} \"{instComp ?? "Timer"}\" (PT := {ptVal ?? "T#0s"})");
                }
            }

            // Build wire connectivity for coils (rung-level tracing)
            foreach (var (fromP, toP) in wireConnections)
            {
                if (partDict.TryGetValue(toP, out var toPart) && toPart.Attribute("Name")?.Value.IndexOf("Coil", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!coilToContactsMap.TryGetValue(toP, out var list))
                    {
                        list = new List<string>();
                        coilToContactsMap[toP] = list;
                    }

                    if (partDict.TryGetValue(fromP, out var fromPart) && fromPart.Attribute("Name")?.Value.Equals("Contact", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        wireOperandMap.TryGetValue(fromP, out string cOp);
                        if (!string.IsNullOrEmpty(cOp))
                        {
                            bool cNeg = fromPart.Elements().Any(e => e.Name.LocalName == "Negated");
                            list.Add(cNeg ? $"NOT \"{cOp}\"" : $"\"{cOp}\"");
                        }
                    }
                }
            }

            net.InputsAndContacts = contacts;
            net.OutputsAndCoils = coils;
            net.BlockCalls = calls;

            // Generate Logic Expression (Pseudo SCL equivalent)
            if (coilToContactsMap.Count > 0)
            {
                foreach (var kv in coilToContactsMap)
                {
                    string coilUid = kv.Key;
                    var contactList = kv.Value;
                    wireOperandMap.TryGetValue(coilUid, out string coilTarget);
                    if (string.IsNullOrEmpty(coilTarget)) coilTarget = "Coil";

                    var cPart = partDict[coilUid];
                    string cName = cPart.Attribute("Name")?.Value ?? "";
                    string cond = contactList.Count > 0 ? string.Join(" AND ", contactList) : "TRUE";

                    if (cName.Equals("SCoil", StringComparison.OrdinalIgnoreCase) || cName.Equals("SetCoil", StringComparison.OrdinalIgnoreCase))
                    {
                        net.LogicExpressions.Add($"IF {cond} THEN \"{coilTarget}\" := TRUE; END_IF;");
                    }
                    else if (cName.Equals("RCoil", StringComparison.OrdinalIgnoreCase) || cName.Equals("ResetCoil", StringComparison.OrdinalIgnoreCase))
                    {
                        net.LogicExpressions.Add($"IF {cond} THEN \"{coilTarget}\" := FALSE; END_IF;");
                    }
                    else
                    {
                        net.LogicExpressions.Add($"\"{coilTarget}\" := {cond};");
                    }
                }
            }
            else if (contacts.Count > 0 && coils.Count > 0)
            {
                string condition = string.Join(" AND ", contacts.Select(c => c.StartsWith("NOT ") ? $"NOT \"{c.Substring(4)}\"" : $"\"{c}\""));
                foreach (var c in coils)
                {
                    if (c.StartsWith("( S ) "))
                    {
                        net.LogicExpressions.Add($"IF {condition} THEN \"{c.Substring(6)}\" := TRUE; END_IF;");
                    }
                    else if (c.StartsWith("( R ) "))
                    {
                        net.LogicExpressions.Add($"IF {condition} THEN \"{c.Substring(6)}\" := FALSE; END_IF;");
                    }
                    else if (c.StartsWith("( / ) "))
                    {
                        net.LogicExpressions.Add($"\"{c.Substring(6)}\" := NOT ({condition});");
                    }
                    else
                    {
                        net.LogicExpressions.Add($"\"{c}\" := {condition};");
                    }
                }
            }
            else if (coils.Count > 0)
            {
                foreach (var c in coils)
                {
                    net.LogicExpressions.Add($"\"{c}\" := TRUE; // Koşulsuz aktif");
                }
            }

            foreach (var call in calls)
            {
                net.LogicExpressions.Add(call + ";");
            }

            // Generate ASCII Diagram
            var asciiSb = new StringBuilder();
            if (contacts.Count > 0 || coils.Count > 0)
            {
                string contactAscii = string.Join("--", contacts.Select(c => c.StartsWith("NOT ") ? $"[/ {c.Substring(4)} /]" : $"[ {c} ]"));
                string coilAscii = string.Join("  ", coils.Select(c => c.StartsWith("(") ? c : $"( {c} )"));
                asciiSb.AppendLine($"|--{contactAscii}----------------{coilAscii}--|");
            }
            net.AsciiDiagram = asciiSb.ToString().TrimEnd();

            return net;
        }

        private static string ExtractSymbolFromAccess(XElement access)
        {
            var components = access.Descendants().Where(d => d.Name.LocalName == "Component").ToList();
            if (components.Count > 0)
            {
                return string.Join(".", components.Select(c => c.Attribute("Name")?.Value));
            }

            var constant = access.Descendants().FirstOrDefault(d => d.Name.LocalName == "ConstantValue");
            if (constant != null)
            {
                return constant.Value;
            }

            return null;
        }

        private static string ExtractMultilingualText(XElement parent, string compName)
        {
            var multiText = parent.Descendants().FirstOrDefault(d =>
                d.Name.LocalName == "MultilingualText" && d.Attribute("CompositionName")?.Value == compName);
            if (multiText != null)
            {
                var textElem = multiText.Descendants().FirstOrDefault(d => d.Name.LocalName == "Text");
                if (textElem != null && !string.IsNullOrWhiteSpace(textElem.Value))
                    return textElem.Value.Trim();
            }
            return "";
        }

        private static string GenerateMarkdownReport(ParsedLadderBlock block)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Ladder (LAD) Blok Mantık Özeti: `{block.BlockName}` ({block.BlockType})");
            sb.AppendLine();
            sb.AppendLine($"> [!NOTE]");
            sb.AppendLine($"> Bu dosya, `{block.BlockName}` bloğunun Ladder (LAD) ağlarını yapay zekânın kilit (interlock), çağrı ve çalışma mantığını kolayca öğrenmesi için otomatik derlenmiştir.");
            sb.AppendLine();

            foreach (var net in block.Networks)
            {
                string title = string.IsNullOrEmpty(net.Title) ? $"Ağ {net.NetworkNumber}" : $"Ağ {net.NetworkNumber}: {net.Title}";
                sb.AppendLine($"## {title}");
                if (!string.IsNullOrEmpty(net.Comment))
                {
                    sb.AppendLine($"> **Açıklama:** {net.Comment}");
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(net.AsciiDiagram))
                {
                    sb.AppendLine("```text");
                    sb.AppendLine(net.AsciiDiagram);
                    sb.AppendLine("```");
                }

                if (net.InputsAndContacts.Count > 0)
                {
                    sb.AppendLine($"- **Kontaklar / Giriş Koşulları:** {string.Join(", ", net.InputsAndContacts.Select(c => $"`{c}`"))}");
                }
                if (net.OutputsAndCoils.Count > 0)
                {
                    sb.AppendLine($"- **Bobinler / Çıkışlar:** {string.Join(", ", net.OutputsAndCoils.Select(c => $"`{c}`"))}");
                }
                if (net.BlockCalls.Count > 0)
                {
                    sb.AppendLine($"- **Çağrılan Bloklar:**");
                    foreach (var call in net.BlockCalls)
                        sb.AppendLine($"  - `{call}`");
                }

                if (net.LogicExpressions.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Mantıksal SCL Karşılığı:**");
                    sb.AppendLine("```scl");
                    foreach (var expr in net.LogicExpressions)
                    {
                        sb.AppendLine(expr);
                    }
                    sb.AppendLine("```");
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string GeneratePseudoScl(ParsedLadderBlock block)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// =============================================================================");
            sb.AppendLine($"// Ladder Mantık Eşdeğeri: {block.BlockName} ({block.BlockType})");
            sb.AppendLine($"// =============================================================================");
            sb.AppendLine();

            foreach (var net in block.Networks)
            {
                string title = string.IsNullOrEmpty(net.Title) ? $"Network {net.NetworkNumber}" : $"Network {net.NetworkNumber}: {net.Title}";
                sb.AppendLine($"// -----------------------------------------------------------------------------");
                sb.AppendLine($"// {title}");
                if (!string.IsNullOrEmpty(net.Comment))
                    sb.AppendLine($"// Açıklama: {net.Comment}");
                sb.AppendLine($"// -----------------------------------------------------------------------------");

                if (net.LogicExpressions.Count > 0)
                {
                    foreach (var expr in net.LogicExpressions)
                        sb.AppendLine(expr);
                }
                else
                {
                    sb.AppendLine("// (Boş ağ veya özel fonksiyon)");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
