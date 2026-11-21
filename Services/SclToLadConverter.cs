using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TIAgent.Models;

namespace TIAgent.Services
{
    public class SclToLadConverter
    {
        private ParsedSclBlock _currentSclBlock;
        private int _uidCounter = 10;
        private int _tempVarIndex = 0;
        private readonly Dictionary<string, string> _declaredTempVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _interfaceVarTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Tuple<string, string>> _arrayBounds = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _localInterfaceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _existingLadderDbTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _jumpTargetLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _blockReturnAlias = null;
        private string _returnMemberName = null;
        private string _currentCustomTitle = null;

        public bool UseLadderDbForUndefinedTags { get; set; } = true;
        public string LadderDbName { get; set; } = "LADDERDB";
        public Dictionary<string, string> LastGeneratedLadderDbTags { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private int NextUId() => ++_uidCounter;

        private static readonly HashSet<string> SclKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IF", "THEN", "ELSIF", "ELSE", "END_IF",
            "CASE", "OF", "END_CASE",
            "FOR", "TO", "BY", "DO", "END_FOR",
            "WHILE", "END_WHILE",
            "REPEAT", "UNTIL", "END_REPEAT",
            "AND", "OR", "XOR", "NOT", "MOD", "DIV",
            "RETURN", "EXIT", "CONTINUE", "GOTO",
            "REGION", "END_REGION",
            "SET", "RESET", "INC", "DEC",
            "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD"
        };

        private static readonly HashSet<string> SclBooleans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TRUE", "FALSE"
        };

        private static readonly HashSet<string> SclBuiltInInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "POKE", "POKE_BOOL", "POKE_BLK",
            "PEEK", "PEEK_BOOL", "PEEK_BYTE", "PEEK_WORD", "PEEK_DWORD",
            "FILL_BLK", "MOVE_BLK", "MOVE_BLK_VARIANT", "UMOVE_BLK",
            "READ_DBL", "WRIT_DBL"
        };

        private static readonly HashSet<string> ConvSupportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt",
            "Real", "LReal",
            "Bcd16", "Bcd32",
            "Char", "WChar"
        };

        private static bool IsConvInstructionSupported(string srcType, string destType)
        {
            if (string.IsNullOrWhiteSpace(srcType) || string.IsNullOrWhiteSpace(destType))
                return false;

            if (!ConvSupportedTypes.Contains(srcType) || !ConvSupportedTypes.Contains(destType))
                return false;

            if (string.Equals(srcType, destType, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private string _activeEngineeringVersion = "V20";
        private string _flgNetVersion = "v5";
        private string _structuredTextVersion = "v4";
        private string _interfaceVersion = "v5";
        private bool _supportsNamespace = true;

        /// <summary>
        /// Converts a ParsedSclBlock into a LAD block containing LAD networks and fallback SCL networks.
        /// </summary>
        public string ConvertToSimaticMl(ParsedSclBlock sclBlock, string targetBlockName, int? targetBlockNumber, string tiaVersion = "V20", bool? useLadderDb = null)
        {
            _currentSclBlock = sclBlock;
            if (useLadderDb.HasValue)
                UseLadderDbForUndefinedTags = useLadderDb.Value;

            _uidCounter = 10;
            _tempVarIndex = 0;
            _declaredTempVars.Clear();
            _interfaceVarTypes.Clear();
            _arrayBounds.Clear();
            _localInterfaceNames.Clear();
            _existingLadderDbTypes.Clear();
            _jumpTargetLabels.Clear();
            _blockReturnAlias = null;
            _returnMemberName = null;
            LastGeneratedLadderDbTags.Clear();

            if (sclBlock?.ExistingLadderDbTypes != null)
            {
                foreach (var kv in sclBlock.ExistingLadderDbTypes)
                {
                    _existingLadderDbTypes[kv.Key] = kv.Value;
                }
            }

            // Resolve target engineering version:
            // ALWAYS prioritize the active TIA Portal version (tiaVersion) because the target TIA Portal's
            // Openness Import method strictly requires the XML <Engineering version="..." /> to match its own version.
            string engVer = !string.IsNullOrWhiteSpace(tiaVersion) ? tiaVersion.Trim() :
                            (!string.IsNullOrWhiteSpace(sclBlock.EngineeringVersion) ? sclBlock.EngineeringVersion.Trim() : "V20");

            // Normalize key e.g. "V15.1", "V15", "V16", "V20"
            if (engVer.StartsWith("15.1")) engVer = "V15.1";
            else if (engVer.StartsWith("15")) engVer = "V15";
            else if (engVer.StartsWith("14")) engVer = "V14";
            else if (engVer.StartsWith("16")) engVer = "V16";
            else if (engVer.StartsWith("17")) engVer = "V17";
            else if (engVer.StartsWith("18")) engVer = "V18";
            else if (engVer.StartsWith("19")) engVer = "V19";
            else if (engVer.StartsWith("20")) engVer = "V20";
            else if (engVer.StartsWith("21")) engVer = "V21";

            _activeEngineeringVersion = engVer;

            if (engVer.StartsWith("V14", StringComparison.OrdinalIgnoreCase) ||
                engVer.StartsWith("V15", StringComparison.OrdinalIgnoreCase))
            {
                _flgNetVersion = "v3";
                _structuredTextVersion = "v2";
                _interfaceVersion = "v3";
                _supportsNamespace = false;
            }
            else if (engVer.StartsWith("V16", StringComparison.OrdinalIgnoreCase) ||
                     engVer.StartsWith("V17", StringComparison.OrdinalIgnoreCase))
            {
                _flgNetVersion = "v4";
                _structuredTextVersion = "v3";
                _interfaceVersion = "v4";
                _supportsNamespace = true;
            }
            else if (engVer.StartsWith("V18", StringComparison.OrdinalIgnoreCase))
            {
                _flgNetVersion = "v5";
                _structuredTextVersion = "v3";
                _interfaceVersion = "v4";
                _supportsNamespace = true;
            }
            else
            {
                // V19, V20, V21
                _flgNetVersion = "v5";
                _structuredTextVersion = "v4";
                _interfaceVersion = "v5";
                _supportsNamespace = true;
            }

            var sbCompileUnits = new StringBuilder();
            int networkIndex = 1;

            // Ensure any compound or nested IF / CASE / FOR / WHILE / REPEAT statements in sclBlock.Statements are expanded
            var normalizedStatements = new List<SclStatementInfo>();
            int reIndex = 1;
            foreach (var origStmt in sclBlock.Statements)
            {
                var expandedList = SclCodeParser.ExpandIfBranches(origStmt.RawSclCode ?? "");
                if (expandedList.Count > 1)
                {
                    foreach (var expCode in expandedList)
                    {
                        normalizedStatements.Add(new SclStatementInfo
                        {
                            Index = reIndex++,
                            Title = origStmt.Title ?? $"Network {reIndex - 1}",
                            RawSclCode = expCode.Trim(),
                            KeepAsScl = origStmt.KeepAsScl
                        });
                    }
                }
                else
                {
                    origStmt.Index = reIndex++;
                    if (expandedList.Count == 1)
                        origStmt.RawSclCode = expandedList[0].Trim();
                    normalizedStatements.Add(origStmt);
                }
            }
            sclBlock.Statements = normalizedStatements;

            ExtractInterfaceVarTypes(sclBlock);

            if (sclBlock?.Statements != null)
            {
                foreach (var s in sclBlock.Statements)
                {
                    if (string.IsNullOrWhiteSpace(s?.RawSclCode)) continue;
                    foreach (Match gm in Regex.Matches(s.RawSclCode, @"\bGOTO\s+([a-zA-Z_][a-zA-Z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        _jumpTargetLabels.Add(gm.Groups[1].Value.Trim());
                    }
                }
            }

            if (UseLadderDbForUndefinedTags)
            {
                ScanAndInferUndefinedGlobalTags(sclBlock);
            }

            foreach (var stmt in sclBlock.Statements)
            {
                // Check if statement can be translated to LAD
                if (TryConvertStatementToLad(stmt, networkIndex, out string ladXml, out string ladSummary))
                {
                    stmt.CanConvertToLad = true;
                    stmt.ConversionSummary = ladSummary;

                    if (stmt.KeepAsScl)
                    {
                        // User explicitly selected to keep this network as SCL
                        string sclXml = GenerateSclCompileUnit(stmt, networkIndex);
                        stmt.GeneratedSimaticMlXml = sclXml;
                        sbCompileUnits.AppendLine(sclXml);
                    }
                    else
                    {
                        stmt.GeneratedSimaticMlXml = ladXml;
                        sbCompileUnits.AppendLine(ladXml);
                    }
                }
                else
                {
                    // Fallback to SCL Network
                    string sclXml = GenerateSclCompileUnit(stmt, networkIndex);
                    stmt.CanConvertToLad = false;
                    stmt.KeepAsScl = false;
                    stmt.ConversionSummary = "Ladder'a çevrilemedi -> SCL Network olarak eklendi";
                    stmt.GeneratedSimaticMlXml = sclXml;
                    sbCompileUnits.AppendLine(sclXml);
                }

                networkIndex++;
            }

            // Build full SimaticML Document
            string bTypeUpper = (sclBlock.BlockType ?? "FC").Trim().ToUpperInvariant();
            string blockTag = bTypeUpper == "FB" ? "SW.Blocks.FB" : (bTypeUpper == "OB" ? "SW.Blocks.OB" : "SW.Blocks.FC");
            if (bTypeUpper == "OB" && !targetBlockNumber.HasValue)
            {
                targetBlockNumber = 1;
            }
            string numElem = targetBlockNumber.HasValue ? $"<Number>{targetBlockNumber.Value}</Number>" : "";
            string secondaryTypeLine = bTypeUpper == "OB" ? "      <SecondaryType>ProgramCycle</SecondaryType>\r\n" : "";

            string attrListXml;
            if (sclBlock.OriginalAttributeList != null)
            {
                var attrClone = new XElement(sclBlock.OriginalAttributeList);

                // Update Name
                var nameEl = attrClone.Element("Name");
                if (nameEl != null) nameEl.Value = targetBlockName;
                else attrClone.Add(new XElement("Name", targetBlockName));

                // Namespace handling (Not supported in V14/V15/V15.1 schema)
                if (_supportsNamespace)
                {
                    if (attrClone.Element("Namespace") == null)
                    {
                        attrClone.Add(new XElement("Namespace"));
                    }
                }
                else
                {
                    attrClone.Element("Namespace")?.Remove();
                }

                // Update Number or AutoNumber
                if (targetBlockNumber.HasValue)
                {
                    var numEl = attrClone.Element("Number");
                    if (numEl != null) numEl.Value = targetBlockNumber.Value.ToString();
                    else attrClone.Add(new XElement("Number", targetBlockNumber.Value));

                    attrClone.Element("AutoNumber")?.Remove();
                }
                else
                {
                    var autoEl = attrClone.Element("AutoNumber");
                    if (autoEl != null) autoEl.Value = "true";
                    else attrClone.Add(new XElement("AutoNumber", "true"));

                    attrClone.Element("Number")?.Remove();
                }

                // Update ProgrammingLanguage to LAD
                var langEl = attrClone.Element("ProgrammingLanguage");
                if (langEl != null) langEl.Value = "LAD";
                else attrClone.Add(new XElement("ProgrammingLanguage", "LAD"));

                // Inject declared temp variables if any
                var ifaceEl = attrClone.Element("Interface");
                if (ifaceEl != null && _declaredTempVars.Count > 0)
                {
                    EnsureTempVariablesDeclared(ifaceEl, _declaredTempVars);
                }

                // Adjust Interface namespace if present
                string cloneStr = attrClone.ToString();
                cloneStr = Regex.Replace(cloneStr, @"xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v\d+""",
                    $@"xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/{_interfaceVersion}""");

                attrListXml = cloneStr;
            }
            else
            {
                string adjustedInterfaceXml = Regex.Replace(
                    sclBlock.InterfaceXml ?? "",
                    @"xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v\d+""",
                    $@"xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/{_interfaceVersion}"""
                );

                if (_declaredTempVars.Count > 0)
                {
                    try
                    {
                        var ifaceEl = XElement.Parse(adjustedInterfaceXml);
                        EnsureTempVariablesDeclared(ifaceEl, _declaredTempVars);
                        adjustedInterfaceXml = ifaceEl.ToString();
                    }
                    catch { }
                }

                string nsLine = _supportsNamespace ? "      <Namespace />\r\n" : "";
                attrListXml = $@"    <AttributeList>
      <AutoNumber>{(targetBlockNumber.HasValue ? "false" : "true")}</AutoNumber>
      <HeaderAuthor>T-IA Agent</HeaderAuthor>
      <HeaderFamily></HeaderFamily>
      <HeaderName></HeaderName>
      <HeaderVersion>{sclBlock.HeaderVersion}</HeaderVersion>
      {adjustedInterfaceXml}
      <IsIECCheckEnabled>false</IsIECCheckEnabled>
      <MemoryLayout>{sclBlock.MemoryLayout}</MemoryLayout>
      <Name>{targetBlockName}</Name>
{nsLine}      {numElem}
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
{secondaryTypeLine}      <SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>";
            }

            var docSb = new StringBuilder();
            docSb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            docSb.AppendLine("<Document>");
            docSb.AppendLine($"  <Engineering version=\"{engVer}\" />");
            docSb.AppendLine("  <DocumentInfo>");
            docSb.AppendLine($"    <Created>{DateTime.UtcNow:o}</Created>");
            docSb.AppendLine("    <ExportSetting>WithDefaults</ExportSetting>");
            docSb.AppendLine("  </DocumentInfo>");
            docSb.AppendLine($"  <{blockTag} ID=\"0\">");
            docSb.AppendLine(attrListXml);
            docSb.AppendLine("    <ObjectList>");
            docSb.AppendLine(sbCompileUnits.ToString());
            docSb.AppendLine($"    </ObjectList>");
            docSb.AppendLine($"  </{blockTag}>");
            docSb.AppendLine("</Document>");

            return docSb.ToString();
        }

        private bool TryConvertStatementToLad(SclStatementInfo stmt, int networkIndex, out string ladXml, out string summary)
        {
            ladXml = null;
            summary = null;
            _currentCustomTitle = stmt?.Title;

            string code = stmt.RawSclCode;
            if (string.IsNullOrWhiteSpace(code)) return false;

            // Strip comments
            string cleanCode = StripComments(code).Trim();
            if (string.IsNullOrWhiteSpace(cleanCode)) return false;

            // 0-A) Standalone Label declaration: e.g. "SON:;", "SON:", or known GOTO target "SON;"
            var standaloneLabelMatch = Regex.Match(cleanCode, @"^#?""?([a-zA-Z_][a-zA-Z0-9_]*)""?\s*(?<colon>:)?\s*;?$");
            if (standaloneLabelMatch.Success)
            {
                string labelName = standaloneLabelMatch.Groups[1].Value.Trim();
                bool hasColon = standaloneLabelMatch.Groups["colon"].Success;
                if (!SclKeywords.Contains(labelName) &&
                    (hasColon || _jumpTargetLabels.Contains(labelName)) &&
                    TryBuildLabelLadder(labelName, networkIndex, out ladXml))
                {
                    summary = $"LAD Etiket (Label) Network'ü: {labelName}:";
                    return true;
                }
            }

            // 0-B) Standalone GOTO statement: e.g. "GOTO SON;"
            var standaloneGotoMatch = Regex.Match(cleanCode, @"^GOTO\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (standaloneGotoMatch.Success)
            {
                string labelName = standaloneGotoMatch.Groups[1].Value.Trim();
                if (TryBuildUnconditionalJumpLadder(labelName, networkIndex, out ladXml))
                {
                    summary = $"LAD Atlama (JMP) Network'ü: GOTO {labelName}";
                    return true;
                }
            }

            // 0-C) Standalone RETURN statement: e.g. "RETURN;"
            if (Regex.IsMatch(cleanCode, @"^RETURN\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (TryBuildUnconditionalReturnLadder(networkIndex, out ladXml))
                {
                    summary = "LAD Çıkış (RET) Network'ü: RETURN";
                    return true;
                }
            }

            // Check if statement has a leading label: e.g. "SON: #x := 1;"
            string networkLabel = null;
            var leadingLabelMatch = Regex.Match(cleanCode, @"^([a-zA-Z_][a-zA-Z0-9_]*)\s*:(?!=)\s*([\s\S]+)$");
            if (leadingLabelMatch.Success)
            {
                string candidateLbl = leadingLabelMatch.Groups[1].Value.Trim();
                if (!SclKeywords.Contains(candidateLbl))
                {
                    networkLabel = candidateLbl;
                    cleanCode = leadingLabelMatch.Groups[2].Value.Trim();
                }
            }

            bool converted = TryConvertCleanStatementToLad(cleanCode, networkIndex, out ladXml, out summary);
            if (converted && !string.IsNullOrEmpty(networkLabel) && !string.IsNullOrEmpty(ladXml))
            {
                ladXml = InjectLabelIntoFlgNet(ladXml, networkLabel);
                summary = $"[{networkLabel}:] " + summary;
            }
            return converted;
        }

        private bool TryConvertCleanStatementToLad(string cleanCode, int networkIndex, out string ladXml, out string summary)
        {
            ladXml = null;
            summary = null;

            // 0-D) Standalone SET / RESET procedure calls (from S7-SCL manual s7sclapb.chm): e.g. "SET(#tag);" or "RESET(#tag);"
            var setMatch = Regex.Match(cleanCode, @"^SET\s*\(\s*(.+?)\s*\);?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (setMatch.Success)
            {
                string target = setMatch.Groups[1].Value.Trim();
                if (IsValidIdentifier(target))
                {
                    if (TryBuildSetResetLadder(target, true, networkIndex, out ladXml))
                    {
                        summary = $"LAD Set Network'ü (SET): {target}";
                        return true;
                    }
                }
            }
            var resetMatch = Regex.Match(cleanCode, @"^RESET\s*\(\s*(.+?)\s*\);?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (resetMatch.Success)
            {
                string target = resetMatch.Groups[1].Value.Trim();
                if (IsValidIdentifier(target))
                {
                    if (TryBuildSetResetLadder(target, false, networkIndex, out ladXml))
                    {
                        summary = $"LAD Reset Network'ü (RESET): {target}";
                        return true;
                    }
                }
            }

            // 0-E) Standalone INC / DEC procedure calls (from S7-SCL manual s7sclapb.chm): e.g. "INC(#x);" or "INC(#x, 2);"
            var incMatch = Regex.Match(cleanCode, @"^INC\s*\(\s*(?<var>[^,\)]+)(?:\s*,\s*(?<step>[^\)]+))?\s*\);?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (incMatch.Success)
            {
                string target = incMatch.Groups["var"].Value.Trim();
                string step = incMatch.Groups["step"].Success ? incMatch.Groups["step"].Value.Trim() : "1";
                if (IsValidIdentifier(target) && IsSimpleOperand(step))
                {
                    if (TryBuildBinaryMathLadder(target, target, "+", step, networkIndex, out ladXml))
                    {
                        summary = $"LAD Artırma (INC): {target} := {target} + {step}";
                        return true;
                    }
                }
            }
            var decMatch = Regex.Match(cleanCode, @"^DEC\s*\(\s*(?<var>[^,\)]+)(?:\s*,\s*(?<step>[^\)]+))?\s*\);?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (decMatch.Success)
            {
                string target = decMatch.Groups["var"].Value.Trim();
                string step = decMatch.Groups["step"].Success ? decMatch.Groups["step"].Value.Trim() : "1";
                if (IsValidIdentifier(target) && IsSimpleOperand(step))
                {
                    if (TryBuildBinaryMathLadder(target, target, "-", step, networkIndex, out ladXml))
                    {
                        summary = $"LAD Azaltma (DEC): {target} := {target} - {step}";
                        return true;
                    }
                }
            }

            // 0-F) Standalone Timer call (TON, TOF, TP, TONR): e.g. "TON3".TON(IN:=#surestart, PT:=#beklemetime, Q=>#surecikis, ET=>#zmn);
            if (TryBuildTimerLadder(cleanCode, null, networkIndex, out ladXml, out string timerSummary))
            {
                summary = timerSummary;
                return true;
            }

            // Note: POKE_BOOL / POKE / POKE_BLK are SCL-only instructions in TIA Portal (not valid LAD Part instructions).
            // They fall back cleanly to SCL networks (SW.Blocks.CompileUnit with ProgrammingLanguage=SCL).

            // 1) Pattern: Assignment: Target := Expression; (CMP, FunctionBox, Math, Calc, Boolean, Set/Reset, or Move)
            var assignMatch = Regex.Match(cleanCode, @"^(.+?)\s*:=\s*([\s\S]+?);?$");
            if (assignMatch.Success)
            {
                string target = assignMatch.Groups[1].Value.Trim();
                string expr = StripOuterParentheses(assignMatch.Groups[2].Value.Trim());

                if (IsValidIdentifier(target))
                {
                    bool isTargetBool = string.Equals(GetVariableBaseType(target), "Bool", StringComparison.OrdinalIgnoreCase);

                    // Unwrap any Time conversion wrapper (e.g. DINT_TO_TIME(#x * 1000) -> #x * 1000, TIME_TO_DINT(TON.ET) / 1000)
                    string unwrappedExpr = UnwrapTimeConversions(expr);
                    if (!string.Equals(unwrappedExpr, expr, StringComparison.Ordinal))
                    {
                        if (TryParseArithmeticExpression(unwrappedExpr, out string uLeft, out string uOp, out string uRight) &&
                            IsSimpleOperand(uLeft) && IsSimpleOperand(uRight))
                        {
                            if (TryBuildBinaryMathLadder(target, uLeft, uOp, uRight, networkIndex, out ladXml))
                            {
                                summary = $"LAD Zaman Dönüşüm / Matematik ({uOp}): {target} := {expr}";
                                return true;
                            }
                        }
                        else if (IsSimpleOperand(unwrappedExpr))
                        {
                            if (TryBuildMoveLadder(target, unwrappedExpr, networkIndex, out ladXml))
                            {
                                summary = $"LAD Move Network ({expr}): {target} := {unwrappedExpr}";
                                return true;
                            }
                        }
                    }

                    // 0. Direct Boolean Set / Reset: Target := TRUE / FALSE (or BOOL#TRUE / BOOL#FALSE, or 1 / 0 when Target is Bool)
                    if (expr.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                        expr.Equals("BOOL#TRUE", StringComparison.OrdinalIgnoreCase) ||
                        expr.Equals("BOOL#1", StringComparison.OrdinalIgnoreCase) ||
                        (isTargetBool && expr == "1"))
                    {
                        if (TryBuildSetResetLadder(target, true, networkIndex, out ladXml))
                        {
                            summary = $"LAD Set Network'ü (S): {target} := {expr}";
                            return true;
                        }
                    }
                    if (expr.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                        expr.Equals("BOOL#FALSE", StringComparison.OrdinalIgnoreCase) ||
                        expr.Equals("BOOL#0", StringComparison.OrdinalIgnoreCase) ||
                        (isTargetBool && expr == "0"))
                    {
                        if (TryBuildSetResetLadder(target, false, networkIndex, out ladXml))
                        {
                            summary = $"LAD Reset Network'ü (R): {target} := {expr}";
                            return true;
                        }
                    }

                    // 0-B. Direct Bool := Bool Contact-to-Coil Assignment (e.g. "OUT_LED" := "IN_BTN";)
                    if (isTargetBool && IsValidIdentifier(expr))
                    {
                        if (TryBuildBooleanLadder(target, expr, networkIndex, out ladXml))
                        {
                            summary = $"LAD Bobin (Coil) Network'ü: {target} := {expr}";
                            return true;
                        }
                    }

                    // A-0. Built-in Function / Conversion Call (INT_TO_REAL, INT_TO_BCD16, ROUND, TRUNC, ABS, SQRT, NORM_X, SCALE_X, UPPER_BOUND, LOWER_BOUND, etc.)
                    if (TryParseFunctionBoxCall(target, expr, out IfActionItem fnAct))
                    {
                        if (fnAct.IsFunctionBox)
                        {
                            if (TryBuildFunctionBoxLadder(fnAct, networkIndex, out ladXml))
                            {
                                summary = $"LAD Fonksiyon ({fnAct.FuncPartName}): {target} := {expr}";
                                return true;
                            }
                        }
                        else if (!string.IsNullOrEmpty(fnAct.SourceValue))
                        {
                            if (TryBuildMoveLadder(target, fnAct.SourceValue, networkIndex, out ladXml))
                            {
                                summary = $"LAD Move Network ({expr} -> {fnAct.SourceValue}): {target} := {fnAct.SourceValue}";
                                return true;
                            }
                        }
                    }

                    // A. Comparison Assignment: Target := Left (<, >, <=, >=, =, <>) Right;
                    if (TryFindComparisonOperator(expr, out _, out _, out _))
                    {
                        if (TryBuildBooleanLadder(target, expr, networkIndex, out ladXml))
                        {
                            summary = $"LAD Karşılaştırma Network'ü: {target} := {expr}";
                            return true;
                        }
                    }

                    // Pre-extract type conversions embedded in arithmetic/calculate expressions (e.g. INT_TO_REAL(#HamDeger) / 27648.0)
                    string mathExpr = expr;
                    List<PreConvertItem> preConverts = null;
                    if (TypeConvertRegex.IsMatch(expr) && Regex.IsMatch(expr, @"[\+\-\*\/]"))
                    {
                        mathExpr = ExtractPreConversions(expr, target, out preConverts);
                    }

                    // B-0. Unary minus on non-constant operand: Target := -#x; -> 0 - #x
                    if (mathExpr.StartsWith("-") && !IsConstantLiteral(mathExpr))
                    {
                        string inner = StripOuterParentheses(mathExpr.Substring(1).Trim());
                        if (IsSimpleOperand(inner))
                        {
                            if (TryBuildBinaryMathLadder(target, "0", "-", inner, networkIndex, out ladXml, preConverts))
                            {
                                summary = $"LAD Matematik (-): {target} := {expr}";
                                return true;
                            }
                        }
                    }

                    // B. Simple Binary Math: Target := Left (+, -, *, /, MOD, **) Right;
                    if (TryParseArithmeticExpression(mathExpr, out string bLeft, out string bOp, out string bRight) &&
                        IsSimpleOperand(bLeft) && IsSimpleOperand(bRight))
                    {
                        if (TryBuildBinaryMathLadder(target, bLeft, bOp, bRight, networkIndex, out ladXml, preConverts))
                        {
                            summary = $"LAD Matematik ({bOp}): {target} := {expr}";
                            return true;
                        }
                    }

                    // C. Complex Calculate Math Expression (with +, -, *, / outside array brackets)
                    string mathWithoutBrackets = Regex.Replace(mathExpr, @"\[[^\]]*\]", "");
                    if (Regex.IsMatch(mathWithoutBrackets, @"[\+\-\*\/]"))
                    {
                        if (TryBuildCalculateLadder(target, mathExpr, networkIndex, out ladXml, preConverts))
                        {
                            summary = $"LAD Hesaplama (CALC): {target} := {expr}";
                            return true;
                        }
                    }

                    // D. Boolean logic expression (AND, OR, NOT, XOR, &, ~)
                    bool hasLogicOperators = Regex.IsMatch(expr, @"\b(AND|OR|XOR|NOT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || expr.Contains("&") || expr.Contains("~");
                    if (hasLogicOperators)
                    {
                        if (TryBuildBooleanLadder(target, expr, networkIndex, out ladXml))
                        {
                            summary = $"LAD Boolean Network: {target} := {expr}";
                            return true;
                        }
                    }

                    // E. Move (Direct Assignment for non-Bool types: Tag := Tag; Tag := Number; Tag := 'String'; Tag := DINT#50000)
                    List<PreCalcItem> movePreCalcs = null;
                    string moveCleanTarget = ExtractIndexPreCalculations(target, ref movePreCalcs);
                    string moveCleanExpr = ExtractIndexPreCalculations(expr, ref movePreCalcs);
                    if (!isTargetBool && (IsValidIdentifier(moveCleanExpr) || IsConstantLiteral(moveCleanExpr)) && IsValidIdentifier(moveCleanTarget))
                    {
                        if (TryBuildMoveLadder(moveCleanTarget, moveCleanExpr, networkIndex, out ladXml, movePreCalcs))
                        {
                            summary = $"LAD Move Network: {target} := {expr}";
                            return true;
                        }
                    }
                }
            }

            // 3) Pattern: Conditional IF Condition THEN Actions END_IF;
            var ifMatch = Regex.Match(cleanCode, @"^IF\s+([\s\S]+?)\s+THEN\s+([\s\S]+?)\s*END_IF;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (ifMatch.Success)
            {
                string cond = ifMatch.Groups[1].Value.Trim();
                string body = ifMatch.Groups[2].Value.Trim();

                // Check if body is a Timer call: e.g. IF cond THEN "TON1".TON(...); END_IF;
                if (TryBuildTimerLadder(body, cond, networkIndex, out ladXml, out string timerCondSummary))
                {
                    summary = timerCondSummary;
                    return true;
                }

                // If body contains unexpanded loops or case keywords, let SCL handle it
                if (!Regex.IsMatch(body, @"\b(FOR|WHILE|REPEAT|CASE|EXIT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var actions = ParseIfActions(body);
                    if (actions != null && actions.Count > 0)
                    {
                        if (TryBuildConditionalLadder(cond, actions, networkIndex, out ladXml))
                        {
                            summary = $"LAD Koşul Network'ü: IF {cond} ({actions.Count} Çıkış)";
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsConstantLiteral(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return false;
            string clean = StripOuterParentheses(expr).Trim();
            if (clean.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                return true;
            if (double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                return true;
            if (clean.StartsWith("'") && clean.EndsWith("'") && clean.Length >= 2)
                return true;
            return TryParseTypedOrRadixConstant(clean, out _, out _, out _);
        }

        private static bool IsIllegalMathType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            return string.Equals(type, "Time", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "LTime", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "TOD", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Time_Of_Day", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "DTL", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Date_And_Time", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "String", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "WString", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Char", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "WChar", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "Bool", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSimpleOperand(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return false;
            string clean = StripOuterParentheses(expr).Trim();
            return IsConstantLiteral(clean) || IsValidIdentifier(clean);
        }

        private static bool TryParseTypedOrRadixConstant(string expr, out string constType, out string constValue, out string scope)
        {
            constType = null;
            constValue = null;
            scope = "LiteralConstant";

            if (string.IsNullOrWhiteSpace(expr)) return false;
            string clean = StripOuterParentheses(expr).Trim();

            // 1. Untyped radix literals: 16#FF, 2#1010, 8#77
            var radixMatch = Regex.Match(clean, @"^(?<base>16|2|8)#(?<digits>[0-9a-fA-F_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (radixMatch.Success)
            {
                int radix = int.Parse(radixMatch.Groups["base"].Value);
                string digits = radixMatch.Groups["digits"].Value.Replace("_", "");
                try
                {
                    long val = Convert.ToInt64(digits, radix);
                    constType = (val >= short.MinValue && val <= short.MaxValue) ? "Int" : "DInt";
                    constValue = val.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            // 2. Typed constants: <TYPE>#<VALUE> (e.g. DINT#50000, REAL#3.5, INT#10, CHAR#'0', STRING#'abc', WSTRING#',', T#5s, BYTE#16#FF)
            var typedMatch = Regex.Match(
                clean,
                @"^(?<type>BOOL|BYTE|WORD|DWORD|LWORD|SINT|USINT|INT|UINT|DINT|UDINT|LINT|ULINT|REAL|LREAL|CHAR|WCHAR|STRING|WSTRING|TIME|T|LTIME|LT)#(?<val>[\s\S]+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!typedMatch.Success)
                return false;

            string rawType = typedMatch.Groups["type"].Value.ToUpperInvariant();
            string rawVal = typedMatch.Groups["val"].Value.Trim();
            if (string.IsNullOrEmpty(rawVal))
                return false;

            if (rawType == "T" || rawType == "TIME" || rawType == "LT" || rawType == "LTIME")
            {
                constType = (rawType == "LT" || rawType == "LTIME") ? "LTime" : "Time";
                constValue = $"T#{rawVal}";
                scope = "TypedConstant";
                return true;
            }

            constType = NormalizePlcDatatype(rawType) ?? "DInt";

            if (constType == "Bool")
            {
                constValue = (rawVal == "1" || rawVal.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) ? "TRUE" : "FALSE";
                return true;
            }

            if (constType == "Char" || constType == "WChar" || constType == "String" || constType == "WString")
            {
                string inner = (rawVal.StartsWith("'") && rawVal.EndsWith("'") && rawVal.Length >= 2)
                    ? rawVal.Substring(1, rawVal.Length - 2)
                    : rawVal;
                constValue = $"'{inner}'";
                return true;
            }

            // Check if value part is itself a radix literal e.g. BYTE#16#FF
            var innerRadix = Regex.Match(rawVal, @"^(?<base>16|2|8)#(?<digits>[0-9a-fA-F_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (innerRadix.Success)
            {
                int radix = int.Parse(innerRadix.Groups["base"].Value);
                string digits = innerRadix.Groups["digits"].Value.Replace("_", "");
                try
                {
                    long val = Convert.ToInt64(digits, radix);
                    constValue = val.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            string cleanNum = rawVal.Replace("_", "");
            if (double.TryParse(cleanNum, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                constValue = cleanNum;
                return true;
            }

            return false;
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();

            // Cannot contain semicolon, assignment, or newlines
            if (trimmed.Contains("\n") || trimmed.Contains("\r") || trimmed.Contains(";") || trimmed.Contains(":="))
                return false;

            // Reject typed or radix constants (e.g. DINT#50000, REAL#3.5, 16#FF)
            if (TryParseTypedOrRadixConstant(trimmed, out _, out _, out _))
                return false;

            // Strip local prefix '#'
            if (trimmed.StartsWith("#"))
                trimmed = trimmed.Substring(1).Trim();

            // Reject plain numbers or keywords
            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;
            if (SclKeywords.Contains(trimmed))
                return false;

            // Check if there are balanced array brackets
            int openBrackets = 0;
            int closeBrackets = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '[') openBrackets++;
                else if (trimmed[i] == ']') closeBrackets++;
            }
            if (openBrackets != closeBrackets) return false;

            // Verify each bracketed dimension if present
            if (openBrackets > 0)
            {
                var bracketMatches = Regex.Matches(trimmed, @"\[(?<dimContent>[^\]]+)\]");
                foreach (Match bm in bracketMatches)
                {
                    string inside = bm.Groups["dimContent"].Value.Trim();
                    if (string.IsNullOrWhiteSpace(inside)) return false;
                    var dims = inside.Split(',');
                    foreach (var d in dims)
                    {
                        string dim = d.Trim();
                        if (string.IsNullOrWhiteSpace(dim)) return false;
                    }
                }
            }

            // Outside of array brackets, verify the tag path
            string tagPath = Regex.Replace(trimmed, @"\[[^\]]*\]", "");
            string withoutQuotes = Regex.Replace(tagPath, @"""[^""]*""", "");

            // Unquoted tag path cannot contain '#' (local prefix was already stripped)
            if (withoutQuotes.Contains("#"))
                return false;

            // Unquoted tag path cannot contain math or comparison operators
            if (Regex.IsMatch(withoutQuotes, @"[\+\-\*\/\<\>\=\&\|\^\~]"))
                return false;

            // Match all valid components in tagPath: either "quoted component" or unquoted identifier
            var matches = Regex.Matches(tagPath, @"(?:""([^""]+)""|([a-zA-Z_ğüşıöçĞÜŞİÖÇ%][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]*))");
            if (matches.Count == 0)
                return false;

            // Ensure there are no illegal characters outside of dots '.' and spaces
            string stripped = Regex.Replace(tagPath, @"(?:""([^""]+)""|([a-zA-Z_ğüşıöçĞÜŞİÖÇ%][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]*))", "");
            string remaining = stripped.Replace(".", "").Replace(" ", "").Trim();
            if (!string.IsNullOrEmpty(remaining))
                return false;

            return true;
        }

        private bool IsBooleanExpression(string expr)
        {
            if (expr.Contains(";") || Regex.IsMatch(expr, @"\b(FOR|WHILE|CASE|THEN)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;

            // Must not contain math symbols like +, *, /
            if (Regex.IsMatch(expr, @"[\+\*\/]"))
                return false;

            return true;
        }

        private bool TryBuildComparisonLadder(string targetVar, string leftVal, string op, string rightVal, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (!IsValidIdentifier(targetVar)) return false;

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                string partOp = op == "<" ? "Lt" :
                                op == ">" ? "Gt" :
                                op == "<=" ? "Le" :
                                op == ">=" ? "Ge" :
                                op == "<>" ? "Ne" : "Eq";

                string srcType = ResolveNumericSrcType(leftVal, rightVal);

                int leftAccId = NextUId();
                parts.AppendLine(FormatAccessXml(leftVal, leftAccId, srcType));

                int rightAccId = NextUId();
                parts.AppendLine(FormatAccessXml(rightVal, rightAccId, srcType));

                int targetAccId = NextUId();
                parts.AppendLine(FormatAccessXml(targetVar, targetAccId));

                int cmpPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"{partOp}\" UId=\"{cmpPartId}\">");
                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{srcType}</TemplateValue>");
                parts.AppendLine($"          </Part>");

                int coilId = NextUId();
                parts.AppendLine($"          <Part Name=\"Coil\" UId=\"{coilId}\" />");

                // Powerrail to cmp pre
                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                wires.AppendLine($"            <Powerrail />");
                wires.AppendLine($"            <NameCon UId=\"{cmpPartId}\" Name=\"pre\" />");
                wires.AppendLine($"          </Wire>");

                // in1 and in2
                int in1Wire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{in1Wire}\">");
                wires.AppendLine($"            <IdentCon UId=\"{leftAccId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{cmpPartId}\" Name=\"in1\" />");
                wires.AppendLine($"          </Wire>");

                int in2Wire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{in2Wire}\">");
                wires.AppendLine($"            <IdentCon UId=\"{rightAccId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{cmpPartId}\" Name=\"in2\" />");
                wires.AppendLine($"          </Wire>");

                // cmp out to coil in
                int outWire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{outWire}\">");
                wires.AppendLine($"            <NameCon UId=\"{cmpPartId}\" Name=\"out\" />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                // coil operand
                int coilOpWire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{coilOpWire}\">");
                wires.AppendLine($"            <IdentCon UId=\"{targetAccId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"operand\" />");
                wires.AppendLine($"          </Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: Karşılaştırma ({targetVar} := {leftVal} {op} {rightVal})");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildBooleanLadder(string targetVar, string boolExpr, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (!IsValidIdentifier(targetVar)) return false;

                var boolPreCalcs = new List<PreCalcItem>();
                var rawAst = ParseBoolExpr(boolExpr, boolPreCalcs);
                if (rawAst == null) return false;
                var ast = NormalizeBoolAst(rawAst, false);
                if (ast == null) return false;

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int lastPowerOutId = -1;
                string lastPowerOutPin = "Powerrail";

                if (boolPreCalcs.Count > 0)
                {
                    EmitPreCalcBoxes(boolPreCalcs, parts, wires, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref lastPowerOutId, ref lastPowerOutPin);
                }

                // Target access & coil
                int targetAccessId = NextUId();
                parts.AppendLine(FormatAccessXml(targetVar, targetAccessId));

                int coilId = NextUId();
                parts.AppendLine($"          <Part Name=\"Coil\" UId=\"{coilId}\" />");

                var powerrailSinks = new List<Tuple<int, string>>();
                var internalWires = new StringBuilder();

                var finalOut = BuildBoolExprNode(ast, powerrailSinks, parts, internalWires);
                if (finalOut == null || powerrailSinks.Count == 0)
                    return false;

                // Powerrail wire (connects to all entry points of the boolean AST)
                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                if (lastPowerOutId < 0)
                {
                    wires.AppendLine($"            <Powerrail />");
                }
                else
                {
                    wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                }
                foreach (var item in powerrailSinks)
                {
                    wires.AppendLine($"            <NameCon UId=\"{item.Item1}\" Name=\"{item.Item2}\" />");
                }
                wires.AppendLine($"          </Wire>");

                wires.Append(internalWires.ToString());

                // Connect final output of boolean AST to the coil
                int mergeWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{mergeWireId}\">");
                wires.AppendLine($"            <NameCon UId=\"{finalOut.Item1}\" Name=\"{finalOut.Item2}\" />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                // Coil operand wire
                int coilOpWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{coilOpWireId}\">");
                wires.AppendLine($"            <IdentCon UId=\"{targetAccessId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"operand\" />");
                wires.AppendLine($"          </Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {targetVar} Kontrolü");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildSetResetLadder(string targetVar, bool isSet, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (!IsValidIdentifier(targetVar)) return false;

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int targetAccessId = NextUId();
                parts.AppendLine(FormatAccessXml(targetVar, targetAccessId));

                string coilPartName = isSet ? "SCoil" : "RCoil";
                int coilId = NextUId();
                parts.AppendLine($"          <Part Name=\"{coilPartName}\" UId=\"{coilId}\" />");

                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                wires.AppendLine($"            <Powerrail />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                int opWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{opWireId}\">");
                wires.AppendLine($"            <IdentCon UId=\"{targetAccessId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"operand\" />");
                wires.AppendLine($"          </Wire>");

                string actTitle = isSet ? "Set" : "Reset";
                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {targetVar} {actTitle}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildMoveLadder(string targetVar, string sourceVal, int networkIndex, out string xml, List<PreCalcItem> preCalcs = null)
        {
            xml = null;
            try
            {
                if (!IsValidIdentifier(targetVar)) return false;

                string targetType = GetVariableBaseType(targetVar);

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int lastPowerOutId = -1;
                string lastPowerOutPin = "Powerrail";

                if (preCalcs != null && preCalcs.Count > 0)
                {
                    EmitPreCalcBoxes(preCalcs, parts, wires, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref lastPowerOutId, ref lastPowerOutPin);
                }

                int srcAccessId = NextUId();
                parts.AppendLine(FormatAccessXml(sourceVal, srcAccessId, targetType));

                int targetAccessId = NextUId();
                parts.AppendLine(FormatAccessXml(targetVar, targetAccessId));

                int movePartId = NextUId();
                parts.AppendLine($"          <Part Name=\"Move\" UId=\"{movePartId}\">");
                parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">1</TemplateValue>");
                parts.AppendLine($"          </Part>");

                // Powerrail or previous eno to en
                int enWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{enWireId}\">");
                if (lastPowerOutId == -1)
                    wires.AppendLine($"            <Powerrail />");
                else
                    wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                wires.AppendLine($"            <NameCon UId=\"{movePartId}\" Name=\"en\" />");
                wires.AppendLine($"          </Wire>");

                // Source to in
                int inWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{inWireId}\">");
                wires.AppendLine($"            <IdentCon UId=\"{srcAccessId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{movePartId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                // out1 to target
                int outWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{outWireId}\">");
                wires.AppendLine($"            <IdentCon UId=\"{targetAccessId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{movePartId}\" Name=\"out1\" />");
                wires.AppendLine($"          </Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: Değer Aktarımı ({targetVar} := {sourceVal})");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryParseFunctionBoxCall(string targetVar, string expr, out IfActionItem actionItem)
        {
            actionItem = null;
            if (!IsValidIdentifier(targetVar) || string.IsNullOrWhiteSpace(expr))
                return false;

            string clean = StripOuterParentheses(expr).Trim();

            var fnMatch = Regex.Match(clean, @"^(?<fn>[a-zA-Z_][a-zA-Z0-9_]*)\s*\(\s*(?<args>[\s\S]*)\s*\)$");
            if (!fnMatch.Success)
                return false;

            string fnName = fnMatch.Groups["fn"].Value.Trim();
            string argsRaw = fnMatch.Groups["args"].Value.Trim();

            // Ensure the opening '(' after fnName matches the final ')' at the end of clean (not "FUNC1(a) + FUNC2(b)")
            int openIdx = clean.IndexOf('(');
            int depth = 0;
            bool inSingle = false;
            bool inDouble = false;
            for (int i = openIdx; i < clean.Length; i++)
            {
                char c = clean[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (!inSingle && !inDouble)
                {
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0 && i < clean.Length - 1)
                            return false; // Closed before end -> compound expression
                    }
                }
            }

            var argDict = ParseCallArguments(argsRaw, out List<string> posArgs);

            // 1. Explicit Type Conversion: <SRC>_TO_<DEST>(IN := x) or <SRC>_TO_<DEST>(x) (Slides 14 & 19)
            var convMatch = Regex.Match(fnName, @"^(?<src>[a-zA-Z0-9]+)_TO_(?<dest>[a-zA-Z0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (convMatch.Success)
            {
                string srcType = NormalizeConvertType(convMatch.Groups["src"].Value);
                string destType = NormalizeConvertType(convMatch.Groups["dest"].Value);
                string inVal = argDict.TryGetValue("IN", out string namedIn) ? namedIn : (posArgs.Count == 1 ? posArgs[0] : null);

                if (!string.IsNullOrEmpty(srcType) && !string.IsNullOrEmpty(destType) &&
                    IsConvInstructionSupported(srcType, destType) && IsSimpleOperand(inVal))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Convert",
                        FuncSrcType = srcType,
                        FuncDestType = destType,
                        FuncInputPins = new List<Tuple<string, string>> { Tuple.Create("in", inVal.Trim()) },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            string upperFn = fnName.ToUpperInvariant();

            // 2. Rounding / Truncation functions: ROUND, TRUNC, CEIL, FLOOR (Slide 14)
            if (upperFn == "ROUND" || upperFn == "TRUNC" || upperFn == "CEIL" || upperFn == "FLOOR")
            {
                string inVal = argDict.TryGetValue("IN", out string namedIn) ? namedIn : (posArgs.Count == 1 ? posArgs[0] : null);
                if (IsSimpleOperand(inVal))
                {
                    string partName = upperFn == "ROUND" ? "Round" :
                                      upperFn == "TRUNC" ? "Trunc" :
                                      upperFn == "CEIL" ? "Ceil" : "Floor";
                    string inType = GetVariableBaseType(inVal);
                    string srcType = string.Equals(inType, "LReal", StringComparison.OrdinalIgnoreCase) ? "LReal" : "Real";
                    string destType = GetVariableBaseType(targetVar) ?? "DInt";
                    if (string.Equals(destType, "Bool", StringComparison.OrdinalIgnoreCase))
                        destType = "DInt";

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = partName,
                        FuncSrcType = srcType,
                        FuncDestType = destType,
                        FuncInputPins = new List<Tuple<string, string>> { Tuple.Create("in", inVal.Trim()) },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 3. Unary Math functions: ABS, SQR, SQRT, SIN, COS, TAN, ASIN, ACOS, ATAN, EXP, LN, FRAC (Slide 14)
            if (upperFn == "ABS" || upperFn == "SQR" || upperFn == "SQRT" ||
                upperFn == "SIN" || upperFn == "COS" || upperFn == "TAN" ||
                upperFn == "ASIN" || upperFn == "ACOS" || upperFn == "ATAN" ||
                upperFn == "EXP" || upperFn == "LN" || upperFn == "FRAC")
            {
                string inVal = argDict.TryGetValue("IN", out string namedIn) ? namedIn : (posArgs.Count == 1 ? posArgs[0] : null);
                if (IsSimpleOperand(inVal))
                {
                    string partName;
                    switch (upperFn)
                    {
                        case "ABS": partName = "Abs"; break;
                        case "SQR": partName = "Sqr"; break;
                        case "SQRT": partName = "Sqrt"; break;
                        case "SIN": partName = "Sin"; break;
                        case "COS": partName = "Cos"; break;
                        case "TAN": partName = "Tan"; break;
                        case "ASIN": partName = "ASin"; break;
                        case "ACOS": partName = "ACos"; break;
                        case "ATAN": partName = "ATan"; break;
                        case "EXP": partName = "Exp"; break;
                        case "LN": partName = "Ln"; break;
                        default: partName = "Frac"; break;
                    }

                    string srcType;
                    if (upperFn == "ABS")
                    {
                        srcType = ResolveNumericSrcType(inVal, targetVar, targetVar);
                    }
                    else
                    {
                        string tTarget = GetVariableBaseType(targetVar);
                        string tIn = GetVariableBaseType(inVal);
                        srcType = (string.Equals(tTarget, "LReal", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tIn, "LReal", StringComparison.OrdinalIgnoreCase)) ? "LReal" : "Real";
                    }

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = partName,
                        FuncSrcType = srcType,
                        FuncDestType = null,
                        FuncInputPins = new List<Tuple<string, string>> { Tuple.Create("in", inVal.Trim()) },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 3-C. PEEK functions: PEEK_BOOL, PEEK, PEEK_BYTE, PEEK_WORD, PEEK_DWORD
            if (upperFn == "PEEK_BOOL" || upperFn == "PEEK" || upperFn == "PEEK_BYTE" || upperFn == "PEEK_WORD" || upperFn == "PEEK_DWORD")
            {
                string area = argDict.TryGetValue("area", out var a) ? a : (posArgs.Count > 0 ? posArgs[0] : null);
                string dbNum = argDict.TryGetValue("dbNumber", out var db) ? db : (posArgs.Count > 1 ? posArgs[1] : null);
                string byteOff = argDict.TryGetValue("byteOffset", out var by) ? by : (posArgs.Count > 2 ? posArgs[2] : null);

                if (!string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(dbNum) && !string.IsNullOrWhiteSpace(byteOff))
                {
                    var pins = new List<Tuple<string, string>>
                    {
                        Tuple.Create("AREA", area.Trim()),
                        Tuple.Create("DBNUMBER", dbNum.Trim()),
                        Tuple.Create("BYTEOFFSET", byteOff.Trim())
                    };

                    string partName = upperFn;
                    if (upperFn == "PEEK_BOOL")
                    {
                        string bitOff = argDict.TryGetValue("bitOffset", out var bi) ? bi : (posArgs.Count > 3 ? posArgs[3] : null);
                        if (!string.IsNullOrWhiteSpace(bitOff))
                        {
                            pins.Add(Tuple.Create("BITOFFSET", bitOff.Trim()));
                        }
                    }

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = partName,
                        FuncInputPins = pins,
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 4. Scaling functions: NORM_X and SCALE_X (Slide 14)
            if (upperFn == "NORM_X" || upperFn == "SCALE_X")
            {
                string minVal = argDict.TryGetValue("MIN", out string nMin) ? nMin : (posArgs.Count >= 1 ? posArgs[0] : null);
                string valArg = argDict.TryGetValue("VALUE", out string nVal) ? nVal : (posArgs.Count >= 2 ? posArgs[1] : null);
                string maxVal = argDict.TryGetValue("MAX", out string nMax) ? nMax : (posArgs.Count >= 3 ? posArgs[2] : null);

                // Cascade check: SCALE_X(MIN := ..., VALUE := NORM_X(...), MAX := ...) (Tek Dalda Cascade NORM_X -> SCALE_X)
                if (upperFn == "SCALE_X" && IsSimpleOperand(minVal) && IsSimpleOperand(maxVal) && !string.IsNullOrWhiteSpace(valArg))
                {
                    string trimmedVal = StripOuterParentheses(valArg).Trim();
                    if (Regex.IsMatch(trimmedVal, @"^NORM_X\s*\(", RegexOptions.IgnoreCase))
                    {
                        if (TryParseFunctionBoxCall("__NORM_TEMP__", trimmedVal, out IfActionItem normAct) && normAct != null && normAct.IsFunctionBox && normAct.FuncPartName == "Normalize")
                        {
                            string destType = GetVariableBaseType(targetVar) ?? ResolveNumericSrcType(minVal, maxVal);

                            var scaleAct = new IfActionItem
                            {
                                TargetVar = targetVar,
                                IsFunctionBox = true,
                                FuncPartName = "Scale_X",
                                FuncSrcType = "Real",
                                FuncDestType = destType,
                                FuncInputPins = new List<Tuple<string, string>>
                                {
                                    Tuple.Create("min", minVal.Trim()),
                                    Tuple.Create("max", maxVal.Trim())
                                },
                                FuncOutputPin = "out"
                            };

                            normAct.IsCascadeBox = true;
                            normAct.SecondBox = scaleAct;
                            normAct.TargetVar = targetVar;
                            actionItem = normAct;
                            return true;
                        }
                    }
                }

                if (IsSimpleOperand(minVal) && IsSimpleOperand(valArg) && IsSimpleOperand(maxVal))
                {
                    // Siemens SimaticML LAD instruction name for NORM_X is "Normalize", for SCALE_X is "Scale_X"
                    string partName = upperFn == "NORM_X" ? "Normalize" : "Scale_X";
                    string srcType;
                    string destType;

                    if (upperFn == "NORM_X")
                    {
                        srcType = ResolveNumericSrcType(minVal, valArg, maxVal);
                        string tOut = GetVariableBaseType(targetVar);
                        destType = string.Equals(tOut, "LReal", StringComparison.OrdinalIgnoreCase) ? "LReal" : "Real";
                    }
                    else
                    {
                        string tVal = GetVariableBaseType(valArg);
                        srcType = string.Equals(tVal, "LReal", StringComparison.OrdinalIgnoreCase) ? "LReal" : "Real";
                        destType = GetVariableBaseType(targetVar) ?? ResolveNumericSrcType(minVal, maxVal);
                    }

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = partName,
                        FuncSrcType = srcType,
                        FuncDestType = destType,
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("min", minVal.Trim()),
                            Tuple.Create("value", valArg.Trim()),
                            Tuple.Create("max", maxVal.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 5. Array Bound functions: UPPER_BOUND and LOWER_BOUND (Slides 2 & 30)
            if (upperFn == "UPPER_BOUND" || upperFn == "LOWER_BOUND")
            {
                string arrArg = argDict.TryGetValue("ARR", out string nArr) ? nArr : (posArgs.Count >= 1 ? posArgs[0] : null);
                string dimArg = argDict.TryGetValue("DIM", out string nDim) ? nDim : (posArgs.Count >= 2 ? posArgs[1] : "1");

                if (!string.IsNullOrWhiteSpace(arrArg) && IsSimpleOperand(dimArg))
                {
                    string lookupKey = NormalizeVarLookupKey(arrArg);
                    if (!string.IsNullOrEmpty(lookupKey) && _arrayBounds.TryGetValue(lookupKey, out Tuple<string, string> bounds))
                    {
                        // Fixed-size array with known bounds in Interface/DB -> resolve directly to constant Move
                        string boundConst = upperFn == "LOWER_BOUND" ? bounds.Item1 : bounds.Item2;
                        if (!string.IsNullOrEmpty(boundConst))
                        {
                            actionItem = new IfActionItem
                            {
                                TargetVar = targetVar,
                                SourceValue = boundConst
                            };
                            return true;
                        }
                    }

                    // Variable-length Array[*] -> emit UPPER_BOUND / LOWER_BOUND instruction part
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = upperFn,
                        FuncSrcType = null,
                        FuncDestType = GetVariableBaseType(targetVar) ?? "DInt",
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("ARR", arrArg.Trim()),
                            Tuple.Create("DIM", dimArg.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 6. S7-SCL Class B BCD Conversion Aliases (s7sclapb.chm scl00241.htm)
            if (upperFn == "BCD_TO_INT" || upperFn == "INT_TO_BCD" || upperFn == "BCD_TO_DINT" || upperFn == "DINT_TO_BCD")
            {
                string inVal = argDict.TryGetValue("IN", out string nIn) ? nIn : (posArgs.Count == 1 ? posArgs[0] : null);
                if (IsSimpleOperand(inVal))
                {
                    string sType = (upperFn == "BCD_TO_INT") ? "Bcd16" :
                                   (upperFn == "INT_TO_BCD") ? "Int" :
                                   (upperFn == "BCD_TO_DINT") ? "Bcd32" : "DInt";
                    string dType = (upperFn == "BCD_TO_INT") ? "Int" :
                                   (upperFn == "INT_TO_BCD") ? "Bcd16" :
                                   (upperFn == "BCD_TO_DINT") ? "DInt" : "Bcd32";
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Convert",
                        FuncSrcType = sType,
                        FuncDestType = dType,
                        FuncInputPins = new List<Tuple<string, string>> { Tuple.Create("in", inVal.Trim()) },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 7. Selection Functions: SEL, LIMIT, MAX, MIN, MUX (s7sclapb.chm scl00256.htm#SCL00403)
            if (upperFn == "SEL")
            {
                string gVal = argDict.TryGetValue("G", out string nG) ? nG : (posArgs.Count >= 1 ? posArgs[0] : null);
                string in0 = argDict.TryGetValue("IN0", out string n0) ? n0 : (posArgs.Count >= 2 ? posArgs[1] : null);
                string in1 = argDict.TryGetValue("IN1", out string n1) ? n1 : (posArgs.Count >= 3 ? posArgs[2] : null);

                if (IsSimpleOperand(gVal) && IsSimpleOperand(in0) && IsSimpleOperand(in1))
                {
                    string srcType = ResolveNumericSrcType(in0, in1, targetVar);
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Sel",
                        FuncSrcType = srcType,
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("g", gVal.Trim()),
                            Tuple.Create("in0", in0.Trim()),
                            Tuple.Create("in1", in1.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "LIMIT")
            {
                string mn = argDict.TryGetValue("MN", out string nMn) ? nMn : (posArgs.Count >= 1 ? posArgs[0] : null);
                string inVal = argDict.TryGetValue("IN", out string nIn) ? nIn : (posArgs.Count >= 2 ? posArgs[1] : null);
                string mx = argDict.TryGetValue("MX", out string nMx) ? nMx : (posArgs.Count >= 3 ? posArgs[2] : null);

                if (IsSimpleOperand(mn) && IsSimpleOperand(inVal) && IsSimpleOperand(mx))
                {
                    string srcType = ResolveNumericSrcType(mn, inVal, mx);
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Limit",
                        FuncSrcType = srcType,
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("mn", mn.Trim()),
                            Tuple.Create("in", inVal.Trim()),
                            Tuple.Create("mx", mx.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "MAX" || upperFn == "MIN")
            {
                var inList = new List<string>();
                if (posArgs.Count >= 2)
                {
                    inList.AddRange(posArgs);
                }
                else
                {
                    for (int i = 1; i <= 32; i++)
                    {
                        if (argDict.TryGetValue($"IN{i}", out string argVal))
                            inList.Add(argVal);
                        else if (inList.Count >= 2)
                            break;
                    }
                }

                if (inList.Count >= 2 && inList.All(IsSimpleOperand))
                {
                    string partName = (upperFn == "MAX") ? "Max" : "Min";
                    string srcType = ResolveNumericSrcType(inList[0], inList[1], targetVar);
                    for (int i = 2; i < inList.Count; i++)
                    {
                        srcType = ResolveNumericSrcType(srcType, inList[i], targetVar);
                    }

                    var pins = new List<Tuple<string, string>>();
                    for (int i = 0; i < inList.Count; i++)
                    {
                        pins.Add(Tuple.Create($"in{i + 1}", inList[i].Trim()));
                    }

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = partName,
                        FuncSrcType = srcType,
                        FuncCardinality = inList.Count,
                        FuncInputPins = pins,
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "MUX")
            {
                string kVal = argDict.TryGetValue("K", out string nK) ? nK : (posArgs.Count >= 1 ? posArgs[0] : null);
                var inList = new List<string>();
                if (posArgs.Count >= 2)
                {
                    for (int i = 1; i < posArgs.Count; i++) inList.Add(posArgs[i]);
                }
                else
                {
                    for (int i = 0; i <= 32; i++)
                    {
                        if (argDict.TryGetValue($"IN{i}", out string argVal))
                            inList.Add(argVal);
                        else if (inList.Count >= 2)
                            break;
                    }
                }

                if (IsSimpleOperand(kVal) && inList.Count >= 2 && inList.All(IsSimpleOperand))
                {
                    string srcType = ResolveNumericSrcType(inList[0], inList[1], targetVar);
                    for (int i = 2; i < inList.Count; i++)
                    {
                        srcType = ResolveNumericSrcType(srcType, inList[i], targetVar);
                    }

                    var pins = new List<Tuple<string, string>> { Tuple.Create("k", kVal.Trim()) };
                    for (int i = 0; i < inList.Count; i++)
                    {
                        pins.Add(Tuple.Create($"in{i}", inList[i].Trim()));
                    }

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Mux",
                        FuncSrcType = srcType,
                        FuncCardinality = inList.Count,
                        FuncInputPins = pins,
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 8. Bit Shift and Rotate Functions: SHL, SHR, ROL, ROR (s7sclapb.chm scl00248.htm)
            if (upperFn == "SHL" || upperFn == "SHR" || upperFn == "ROL" || upperFn == "ROR")
            {
                string inVal = argDict.TryGetValue("IN", out string nIn) ? nIn : (posArgs.Count >= 1 ? posArgs[0] : null);
                string nVal = argDict.TryGetValue("N", out string nN) ? nN : (posArgs.Count >= 2 ? posArgs[1] : null);

                if (IsSimpleOperand(inVal) && IsSimpleOperand(nVal))
                {
                    string partName = (upperFn == "SHL") ? "Shl" :
                                      (upperFn == "SHR") ? "Shr" :
                                      (upperFn == "ROL") ? "Rol" : "Ror";
                    string srcType = GetVariableBaseType(inVal) ?? GetVariableBaseType(targetVar) ?? "Word";
                    if (srcType.Equals("Bool", StringComparison.OrdinalIgnoreCase))
                        srcType = "Word";

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = partName,
                        FuncSrcType = srcType,
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("in", inVal.Trim()),
                            Tuple.Create("n", nVal.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            // 9. String Functions: LEN, CONCAT, LEFT, RIGHT, MID, DELETE, INSERT, REPLACE, FIND (s7sclapb.chm scl00253.htm)
            if (upperFn == "LEN")
            {
                string sVal = argDict.TryGetValue("S", out string nS) ? nS :
                              (argDict.TryGetValue("IN", out string nIn) ? nIn : (posArgs.Count >= 1 ? posArgs[0] : null));
                if (IsSimpleOperand(sVal))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Len",
                        FuncInputPins = new List<Tuple<string, string>> { Tuple.Create("in", sVal.Trim()) },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "CONCAT")
            {
                var inList = new List<string>();
                if (posArgs.Count >= 2)
                {
                    inList.AddRange(posArgs);
                }
                else
                {
                    for (int i = 1; i <= 32; i++)
                    {
                        if (argDict.TryGetValue($"IN{i}", out string argVal))
                            inList.Add(argVal);
                        else if (inList.Count >= 2)
                            break;
                    }
                }

                if (inList.Count >= 2 && inList.All(IsSimpleOperand))
                {
                    var pins = new List<Tuple<string, string>>();
                    for (int i = 0; i < inList.Count; i++)
                    {
                        pins.Add(Tuple.Create($"in{i + 1}", inList[i].Trim()));
                    }

                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Concat",
                        FuncCardinality = inList.Count,
                        FuncInputPins = pins,
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "LEFT" || upperFn == "RIGHT")
            {
                string inVal = argDict.TryGetValue("IN", out string nIn) ? nIn : (posArgs.Count >= 1 ? posArgs[0] : null);
                string lVal = argDict.TryGetValue("L", out string nL) ? nL : (posArgs.Count >= 2 ? posArgs[1] : null);

                if (IsSimpleOperand(inVal) && IsSimpleOperand(lVal))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = (upperFn == "LEFT") ? "Left" : "Right",
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("in", inVal.Trim()),
                            Tuple.Create("l", lVal.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "MID" || upperFn == "DELETE")
            {
                string inVal = argDict.TryGetValue("IN", out string nIn) ? nIn : (posArgs.Count >= 1 ? posArgs[0] : null);
                string lVal = argDict.TryGetValue("L", out string nL) ? nL : (posArgs.Count >= 2 ? posArgs[1] : null);
                string pVal = argDict.TryGetValue("P", out string nP) ? nP : (posArgs.Count >= 3 ? posArgs[2] : null);

                if (IsSimpleOperand(inVal) && IsSimpleOperand(lVal) && IsSimpleOperand(pVal))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = (upperFn == "MID") ? "Mid" : "Delete",
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("in", inVal.Trim()),
                            Tuple.Create("l", lVal.Trim()),
                            Tuple.Create("p", pVal.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "INSERT")
            {
                string in1 = argDict.TryGetValue("IN1", out string n1) ? n1 : (posArgs.Count >= 1 ? posArgs[0] : null);
                string in2 = argDict.TryGetValue("IN2", out string n2) ? n2 : (posArgs.Count >= 2 ? posArgs[1] : null);
                string pVal = argDict.TryGetValue("P", out string nP) ? nP : (posArgs.Count >= 3 ? posArgs[2] : null);

                if (IsSimpleOperand(in1) && IsSimpleOperand(in2) && IsSimpleOperand(pVal))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Insert",
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("in1", in1.Trim()),
                            Tuple.Create("in2", in2.Trim()),
                            Tuple.Create("p", pVal.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "REPLACE")
            {
                string in1 = argDict.TryGetValue("IN1", out string n1) ? n1 : (posArgs.Count >= 1 ? posArgs[0] : null);
                string in2 = argDict.TryGetValue("IN2", out string n2) ? n2 : (posArgs.Count >= 2 ? posArgs[1] : null);
                string lVal = argDict.TryGetValue("L", out string nL) ? nL : (posArgs.Count >= 3 ? posArgs[2] : null);
                string pVal = argDict.TryGetValue("P", out string nP) ? nP : (posArgs.Count >= 4 ? posArgs[3] : null);

                if (IsSimpleOperand(in1) && IsSimpleOperand(in2) && IsSimpleOperand(lVal) && IsSimpleOperand(pVal))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Replace",
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("in1", in1.Trim()),
                            Tuple.Create("in2", in2.Trim()),
                            Tuple.Create("l", lVal.Trim()),
                            Tuple.Create("p", pVal.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            if (upperFn == "FIND")
            {
                string in1 = argDict.TryGetValue("IN1", out string n1) ? n1 : (posArgs.Count >= 1 ? posArgs[0] : null);
                string in2 = argDict.TryGetValue("IN2", out string n2) ? n2 : (posArgs.Count >= 2 ? posArgs[1] : null);

                if (IsSimpleOperand(in1) && IsSimpleOperand(in2))
                {
                    actionItem = new IfActionItem
                    {
                        TargetVar = targetVar,
                        IsFunctionBox = true,
                        FuncPartName = "Find",
                        FuncInputPins = new List<Tuple<string, string>>
                        {
                            Tuple.Create("in1", in1.Trim()),
                            Tuple.Create("in2", in2.Trim())
                        },
                        FuncOutputPin = "out"
                    };
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, string> ParseCallArguments(string argsRaw, out List<string> positionalArgs)
        {
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            positionalArgs = new List<string>();
            if (string.IsNullOrWhiteSpace(argsRaw)) return named;

            var parts = new List<string>();
            int parenDepth = 0;
            int bracketDepth = 0;
            bool inSingle = false;
            bool inDouble = false;
            int start = 0;

            for (int i = 0; i < argsRaw.Length; i++)
            {
                char c = argsRaw[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (!inSingle && !inDouble)
                {
                    if (c == '(') parenDepth++;
                    else if (c == ')') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (c == '[') bracketDepth++;
                    else if (c == ']') bracketDepth = Math.Max(0, bracketDepth - 1);
                    else if (parenDepth == 0 && bracketDepth == 0 && c == ',')
                    {
                        parts.Add(argsRaw.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
            }
            if (start < argsRaw.Length)
                parts.Add(argsRaw.Substring(start).Trim());

            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var assignMatch = Regex.Match(p, @"^([a-zA-Z_][a-zA-Z0-9_]*)\s*(?::=|=>)\s*([\s\S]+)$");
                if (assignMatch.Success)
                {
                    string pName = assignMatch.Groups[1].Value.Trim();
                    string pVal = assignMatch.Groups[2].Value.Trim();
                    named[pName] = pVal;
                    positionalArgs.Add(pVal);
                }
                else
                {
                    positionalArgs.Add(p.Trim());
                }
            }

            return named;
        }

        private static string NormalizeConvertType(string rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType)) return null;
            string upper = rawType.Trim().ToUpperInvariant();
            if (upper == "BCD16" || upper == "BCD") return "Bcd16";
            if (upper == "BCD32") return "Bcd32";
            return NormalizePlcDatatype(rawType);
        }

        private static string UnwrapTimeConversions(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return expr;
            string clean = expr.Trim();
            // Unwrap top-level or embedded DINT_TO_TIME(...), TIME_TO_DINT(...), TIME_TO_INT(...), INT_TO_TIME(...)
            clean = Regex.Replace(clean, @"\b(?:DINT_TO_TIME|TIME_TO_DINT|TIME_TO_INT|INT_TO_TIME|LINT_TO_LTIME|LTIME_TO_LINT|UDINT_TO_TIME|TIME_TO_UDINT)\s*\(\s*([^\)]+?)\s*\)", "$1", RegexOptions.IgnoreCase);
            return clean.Trim();
        }

        private bool TryBuildTimerLadder(string timerCallCode, string condExpr, int networkIndex, out string xml, out string summary)
        {
            xml = null;
            summary = null;
            try
            {
                if (string.IsNullOrWhiteSpace(timerCallCode)) return false;
                string clean = timerCallCode.Trim().TrimEnd(';');

                string instName = null;
                string timerType = null;
                string argsRaw = null;

                // Match: callee(args...) where callee can be:
                // "TIMERDB"."T_CiftElFark"
                // "TON3".TON
                // "TIMERDB"."T_CiftElFark".TON
                // TON."TON3"
                // #myTimer
                // TON1
                // "TON1"
                var callMatch = Regex.Match(clean, @"^(?<callee>(?:#?\""[^\""\r\n]+\""|#?[a-zA-Z_][a-zA-Z0-9_]*)(?:\.(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*))*)\s*\(\s*(?<args>[\s\S]*)\s*\)$");
                if (callMatch.Success)
                {
                    string callee = callMatch.Groups["callee"].Value.Trim();
                    argsRaw = callMatch.Groups["args"].Value.Trim();

                    // Check if callee ends with .TON / .TOF / .TP / .TONR
                    int lastDot = callee.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        string afterLastDot = callee.Substring(lastDot + 1).Trim().Trim('\"', '#').ToUpperInvariant();
                        if (afterLastDot == "TON" || afterLastDot == "TOF" || afterLastDot == "TP" || afterLastDot == "TONR")
                        {
                            timerType = afterLastDot;
                            instName = callee.Substring(0, lastDot).Trim();
                        }
                    }

                    // Check if callee starts with TON. / TOF. / TP. / TONR.
                    if (string.IsNullOrEmpty(timerType))
                    {
                        int firstDot = callee.IndexOf('.');
                        if (firstDot > 0)
                        {
                            string beforeFirstDot = callee.Substring(0, firstDot).Trim().Trim('\"', '#').ToUpperInvariant();
                            if (beforeFirstDot == "TON" || beforeFirstDot == "TOF" || beforeFirstDot == "TP" || beforeFirstDot == "TONR")
                            {
                                timerType = beforeFirstDot;
                                instName = callee.Substring(firstDot + 1).Trim();
                            }
                        }
                    }

                    // If timerType not yet determined, check args and variable types
                    if (string.IsNullOrEmpty(timerType))
                    {
                        bool hasInArg = Regex.IsMatch(argsRaw, @"\bIN\s*(?::=|=>)", RegexOptions.IgnoreCase);
                        bool hasPtArg = Regex.IsMatch(argsRaw, @"\bPT\s*(?::=|=>)", RegexOptions.IgnoreCase);
                        bool hasRArg = Regex.IsMatch(argsRaw, @"\bR\s*(?::=|=>)", RegexOptions.IgnoreCase);

                        string cleanCand = callee.Trim('\"').TrimStart('#');
                        string candType = GetVariableBaseType(cleanCand);

                        bool isKnownTimerType = string.Equals(candType, "IEC_TIMER", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(candType, "TON", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(candType, "TOF", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(candType, "TP", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(candType, "TONR", StringComparison.OrdinalIgnoreCase) ||
                                                Regex.IsMatch(cleanCand, @"^(?:TON|TOF|TP|TONR)\d*$", RegexOptions.IgnoreCase);

                        if ((hasInArg && hasPtArg) || isKnownTimerType)
                        {
                            instName = callee;
                            if (hasRArg || cleanCand.IndexOf("TONR", StringComparison.OrdinalIgnoreCase) >= 0)
                                timerType = "TONR";
                            else if (cleanCand.IndexOf("TOF", StringComparison.OrdinalIgnoreCase) >= 0)
                                timerType = "TOF";
                            else if (cleanCand.IndexOf("TP", StringComparison.OrdinalIgnoreCase) >= 0)
                                timerType = "TP";
                            else
                                timerType = "TON";
                        }
                    }
                }

                if (string.IsNullOrEmpty(instName) || string.IsNullOrEmpty(timerType))
                    return false;

                // Parse arguments: IN, PT, Q, ET, R using robust argument splitter
                var argList = SplitCallArguments(argsRaw);
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var arg in argList)
                {
                    var pm = Regex.Match(arg.Trim(), @"^(?<name>IN|PT|Q|ET|R)\s*(?::=|=>)\s*(?<val>[\s\S]+)$", RegexOptions.IgnoreCase);
                    if (pm.Success)
                    {
                        args[pm.Groups["name"].Value.Trim().ToUpperInvariant()] = pm.Groups["val"].Value.Trim();
                    }
                }

                args.TryGetValue("IN", out string inVal);
                args.TryGetValue("PT", out string ptVal);
                args.TryGetValue("Q", out string qVal);
                args.TryGetValue("ET", out string etVal);
                args.TryGetValue("R", out string rVal);

                if (string.IsNullOrEmpty(ptVal))
                    return false; // PT is mandatory for timers

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                string cleanInst = instName.Trim('\"').TrimStart('#');
                string instScope = "GlobalVariable";
                var compElements = new StringBuilder();

                if (_localInterfaceNames.Contains(cleanInst))
                {
                    instScope = "LocalVariable";
                    compElements.AppendLine($"            <Component Name=\"{System.Security.SecurityElement.Escape(cleanInst)}\" />");
                }
                else if (cleanInst.Contains("."))
                {
                    var segs = cleanInst.Split('.');
                    foreach (var seg in segs)
                    {
                        string s = seg.Trim('\"').Trim();
                        compElements.AppendLine($"            <Component Name=\"{System.Security.SecurityElement.Escape(s)}\" />");
                    }
                }
                else if (_interfaceVarTypes.ContainsKey(cleanInst) || (_currentSclBlock?.DefinedGlobalSymbols != null && _currentSclBlock.DefinedGlobalSymbols.Contains(cleanInst)))
                {
                    compElements.AppendLine($"            <Component Name=\"{System.Security.SecurityElement.Escape(cleanInst)}\" />");
                }
                else if (UseLadderDbForUndefinedTags)
                {
                    LastGeneratedLadderDbTags[cleanInst] = "IEC_TIMER";
                    _interfaceVarTypes[cleanInst] = "IEC_TIMER";
                    _interfaceVarTypes[$"{LadderDbName}.{cleanInst}"] = "IEC_TIMER";
                    compElements.AppendLine($"            <Component Name=\"{System.Security.SecurityElement.Escape(LadderDbName)}\" />");
                    compElements.AppendLine($"            <Component Name=\"{System.Security.SecurityElement.Escape(cleanInst)}\" />");
                }
                else
                {
                    compElements.AppendLine($"            <Component Name=\"{System.Security.SecurityElement.Escape(cleanInst)}\" />");
                }

                int timerPartId = NextUId();
                int instUId = NextUId();

                int ptAccId = NextUId();
                parts.AppendLine(FormatAccessXml(ptVal, ptAccId, "Time"));

                int? qAccId = null;
                int? qCoilId = null;
                if (!string.IsNullOrWhiteSpace(qVal))
                {
                    qAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(qVal, qAccId.Value, "Bool"));
                    qCoilId = NextUId();
                }

                int? etAccId = null;
                if (!string.IsNullOrWhiteSpace(etVal))
                {
                    etAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(etVal, etAccId.Value, "Time"));
                }

                int? rAccId = null;
                if (timerType == "TONR" && !string.IsNullOrWhiteSpace(rVal))
                {
                    rAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(rVal, rAccId.Value, "Bool"));
                }

                // Add Timer Part
                parts.AppendLine($"          <Part Name=\"{timerType}\" Version=\"1.0\" UId=\"{timerPartId}\">");
                parts.AppendLine($"            <Instance Scope=\"{instScope}\" UId=\"{instUId}\">");
                parts.Append(compElements.ToString());
                parts.AppendLine($"            </Instance>");
                parts.AppendLine($"            <TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue>");
                parts.AppendLine($"          </Part>");

                if (qCoilId.HasValue)
                {
                    parts.AppendLine($"          <Part Name=\"Coil\" UId=\"{qCoilId.Value}\" />");
                }

                // Wire IN: Combine condExpr and inVal
                string effCond;
                if (string.IsNullOrWhiteSpace(condExpr))
                    effCond = inVal;
                else if (string.IsNullOrWhiteSpace(inVal) || inVal.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                    effCond = condExpr;
                else if (inVal.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                    effCond = "FALSE";
                else
                    effCond = $"({condExpr}) AND ({inVal})";

                if (string.IsNullOrWhiteSpace(effCond) || effCond.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                {
                    int powerWire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{powerWire}\"><Powerrail /><NameCon UId=\"{timerPartId}\" Name=\"IN\" /></Wire>");
                }
                else
                {
                    var rawAst = ParseBoolExpr(effCond);
                    if (rawAst == null) return false;
                    var ast = NormalizeBoolAst(rawAst, false);
                    if (ast == null) return false;

                    var powerrailSinks = new List<Tuple<int, string>>();
                    var internalWires = new StringBuilder();

                    var finalOut = BuildBoolExprNode(ast, powerrailSinks, parts, internalWires);
                    if (finalOut == null || powerrailSinks.Count == 0) return false;

                    int powerWire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{powerWire}\">");
                    wires.AppendLine($"            <Powerrail />");
                    foreach (var sink in powerrailSinks)
                    {
                        wires.AppendLine($"            <NameCon UId=\"{sink.Item1}\" Name=\"{sink.Item2}\" />");
                    }
                    wires.AppendLine($"          </Wire>");

                    wires.Append(internalWires.ToString());

                    int inWire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{inWire}\"><NameCon UId=\"{finalOut.Item1}\" Name=\"{finalOut.Item2}\" /><NameCon UId=\"{timerPartId}\" Name=\"IN\" /></Wire>");
                }

                // Wire PT
                int ptWire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{ptWire}\"><IdentCon UId=\"{ptAccId}\" /><NameCon UId=\"{timerPartId}\" Name=\"PT\" /></Wire>");

                // Wire Q -> Coil or OpenCon
                if (qCoilId.HasValue && qAccId.HasValue)
                {
                    int qWire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{qWire}\"><NameCon UId=\"{timerPartId}\" Name=\"Q\" /><NameCon UId=\"{qCoilId.Value}\" Name=\"in\" /></Wire>");

                    int qOpWire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{qOpWire}\"><IdentCon UId=\"{qAccId.Value}\" /><NameCon UId=\"{qCoilId.Value}\" Name=\"operand\" /></Wire>");
                }
                else
                {
                    int qWire = NextUId();
                    int openConId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{qWire}\"><NameCon UId=\"{timerPartId}\" Name=\"Q\" /><OpenCon UId=\"{openConId}\" /></Wire>");
                }

                // Wire ET -> IdentCon or OpenCon
                if (etAccId.HasValue)
                {
                    int etWire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{etWire}\"><IdentCon UId=\"{etAccId.Value}\" /><NameCon UId=\"{timerPartId}\" Name=\"ET\" /></Wire>");
                }
                else
                {
                    int etWire = NextUId();
                    int openConId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{etWire}\"><NameCon UId=\"{timerPartId}\" Name=\"ET\" /><OpenCon UId=\"{openConId}\" /></Wire>");
                }

                // Wire R (TONR)
                if (timerType == "TONR")
                {
                    if (rAccId.HasValue)
                    {
                        int rWire = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{rWire}\"><IdentCon UId=\"{rAccId.Value}\" /><NameCon UId=\"{timerPartId}\" Name=\"R\" /></Wire>");
                    }
                    else
                    {
                        int rWire = NextUId();
                        int openConId = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{rWire}\"><NameCon UId=\"{timerPartId}\" Name=\"R\" /><OpenCon UId=\"{openConId}\" /></Wire>");
                    }
                }

                string condDesc = !string.IsNullOrWhiteSpace(condExpr) ? $" (Koşul: {condExpr})" : "";
                summary = $"LAD Zamanlayıcı ({timerType}): {cleanInst}{condDesc}";
                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {timerType} '{cleanInst}'");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildFunctionBoxLadder(IfActionItem fnAct, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (fnAct == null || !fnAct.IsFunctionBox || !IsValidIdentifier(fnAct.TargetVar))
                    return false;

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                // Cascade Check (Tek Dalda NORM_X -> SCALE_X)
                if (fnAct.IsCascadeBox && fnAct.SecondBox != null)
                {
                    var secAct = fnAct.SecondBox;
                    var pinAccIds1 = new List<Tuple<string, int>>();
                    foreach (var pin in fnAct.FuncInputPins)
                    {
                        int accId = NextUId();
                        parts.AppendLine(FormatAccessXml(pin.Item2, accId));
                        pinAccIds1.Add(Tuple.Create(pin.Item1, accId));
                    }

                    var pinAccIds2 = new List<Tuple<string, int>>();
                    foreach (var pin in secAct.FuncInputPins)
                    {
                        int accId = NextUId();
                        parts.AppendLine(FormatAccessXml(pin.Item2, accId));
                        pinAccIds2.Add(Tuple.Create(pin.Item1, accId));
                    }

                    int casTargetAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(secAct.TargetVar, casTargetAccId));

                    int fnPart1Id = NextUId();
                    parts.AppendLine($"          <Part Name=\"{fnAct.FuncPartName}\" UId=\"{fnPart1Id}\">");
                    if (fnAct.FuncCardinality > 0)
                        parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{fnAct.FuncCardinality}</TemplateValue>");
                    if (!string.IsNullOrEmpty(fnAct.FuncSrcType))
                        parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{fnAct.FuncSrcType}</TemplateValue>");
                    if (!string.IsNullOrEmpty(fnAct.FuncDestType))
                        parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{fnAct.FuncDestType}</TemplateValue>");
                    parts.AppendLine($"          </Part>");

                    int fnPart2Id = NextUId();
                    parts.AppendLine($"          <Part Name=\"{secAct.FuncPartName}\" UId=\"{fnPart2Id}\">");
                    if (secAct.FuncCardinality > 0)
                        parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{secAct.FuncCardinality}</TemplateValue>");
                    if (!string.IsNullOrEmpty(secAct.FuncSrcType))
                        parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{secAct.FuncSrcType}</TemplateValue>");
                    if (!string.IsNullOrEmpty(secAct.FuncDestType))
                        parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{secAct.FuncDestType}</TemplateValue>");
                    parts.AppendLine($"          </Part>");

                    // 1. Powerrail -> Part 1 en
                    int casEnWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{casEnWireId}\">");
                    wires.AppendLine($"            <Powerrail />");
                    wires.AppendLine($"            <NameCon UId=\"{fnPart1Id}\" Name=\"en\" />");
                    wires.AppendLine($"          </Wire>");

                    // 2. Part 1 inputs (min, value, max)
                    foreach (var pa in pinAccIds1)
                    {
                        int wId = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wId}\"><IdentCon UId=\"{pa.Item2}\" /><NameCon UId=\"{fnPart1Id}\" Name=\"{pa.Item1}\" /></Wire>");
                    }

                    // 3. Part 1 eno -> Part 2 en (CASCADE!)
                    int cascadeEnWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{cascadeEnWireId}\"><NameCon UId=\"{fnPart1Id}\" Name=\"eno\" /><NameCon UId=\"{fnPart2Id}\" Name=\"en\" /></Wire>");

                    // 4. Part 1 out -> Part 2 value (DATA CASCADE!)
                    int cascadeDataWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{cascadeDataWireId}\"><NameCon UId=\"{fnPart1Id}\" Name=\"out\" /><NameCon UId=\"{fnPart2Id}\" Name=\"value\" /></Wire>");

                    // 5. Part 2 inputs (min, max)
                    foreach (var pa in pinAccIds2)
                    {
                        int wId = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wId}\"><IdentCon UId=\"{pa.Item2}\" /><NameCon UId=\"{fnPart2Id}\" Name=\"{pa.Item1}\" /></Wire>");
                    }

                    // 6. Part 2 out -> TargetVar
                    int casOutWireId = NextUId();
                    string casOutPin = !string.IsNullOrEmpty(secAct.FuncOutputPin) ? secAct.FuncOutputPin : "out";
                    wires.AppendLine($"          <Wire UId=\"{casOutWireId}\"><IdentCon UId=\"{casTargetAccId}\" /><NameCon UId=\"{fnPart2Id}\" Name=\"{casOutPin}\" /></Wire>");

                    xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {secAct.TargetVar} := {secAct.FuncPartName}(..., {fnAct.FuncPartName}(...))");
                    return true;
                }

                var pinAccIds = new List<Tuple<string, int>>();
                foreach (var pin in fnAct.FuncInputPins)
                {
                    int accId = NextUId();
                    parts.AppendLine(FormatAccessXml(pin.Item2, accId));
                    pinAccIds.Add(Tuple.Create(pin.Item1, accId));
                }

                int targetAccId = NextUId();
                parts.AppendLine(FormatAccessXml(fnAct.TargetVar, targetAccId));

                int fnPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"{fnAct.FuncPartName}\" UId=\"{fnPartId}\">");
                if (fnAct.FuncCardinality > 0)
                {
                    parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{fnAct.FuncCardinality}</TemplateValue>");
                }
                if (!string.IsNullOrEmpty(fnAct.FuncSrcType))
                {
                    parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{fnAct.FuncSrcType}</TemplateValue>");
                }
                if (!string.IsNullOrEmpty(fnAct.FuncDestType))
                {
                    parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{fnAct.FuncDestType}</TemplateValue>");
                }
                parts.AppendLine($"          </Part>");

                int enWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{enWireId}\">");
                wires.AppendLine($"            <Powerrail />");
                wires.AppendLine($"            <NameCon UId=\"{fnPartId}\" Name=\"en\" />");
                wires.AppendLine($"          </Wire>");

                foreach (var pa in pinAccIds)
                {
                    int wId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{wId}\"><IdentCon UId=\"{pa.Item2}\" /><NameCon UId=\"{fnPartId}\" Name=\"{pa.Item1}\" /></Wire>");
                }

                int outWireId = NextUId();
                string outPin = !string.IsNullOrEmpty(fnAct.FuncOutputPin) ? fnAct.FuncOutputPin : "out";
                wires.AppendLine($"          <Wire UId=\"{outWireId}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{fnPartId}\" Name=\"{outPin}\" /></Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {fnAct.TargetVar} := {fnAct.FuncPartName}(...)");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly Regex TypeConvertRegex = new Regex(
            @"\b(?<fn>(?:(?<src>BOOL|BYTE|WORD|DWORD|LWORD|SINT|USINT|INT|UINT|DINT|UDINT|LINT|ULINT|REAL|LREAL|TIME)_TO_(?<dest>BOOL|BYTE|WORD|DWORD|LWORD|SINT|USINT|INT|UINT|DINT|UDINT|LINT|ULINT|REAL|LREAL|TIME))|(?:TO_(?<dest2>BOOL|BYTE|WORD|DWORD|LWORD|SINT|USINT|INT|UINT|DINT|UDINT|LINT|ULINT|REAL|LREAL|TIME))|(?<dest3>REAL|LREAL|DINT|INT|UDINT|UINT|SINT|USINT|WORD|DWORD))\s*\(\s*(?<arg>[^()]+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool IsIntegerType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return false;
            string norm = NormalizePlcDatatype(typeName) ?? typeName;
            return norm.Equals("Int", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("DInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("SInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("LInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("UInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("UDInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("USInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("ULInt", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("Word", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("DWord", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("Byte", StringComparison.OrdinalIgnoreCase) ||
                   norm.Equals("LWord", StringComparison.OrdinalIgnoreCase);
        }

        private string ExtractPreConversions(string expr, string targetVar, out List<PreConvertItem> preConverts)
        {
            var list = new List<PreConvertItem>();
            preConverts = list;
            if (string.IsNullOrWhiteSpace(expr)) return expr;

            string clean = StripOuterParentheses(expr).Trim();
            if (Regex.IsMatch(clean, @"^[a-zA-Z_][a-zA-Z0-9_]*\s*\([^()]+\)$"))
            {
                return expr;
            }

            string transformed = TypeConvertRegex.Replace(expr, m =>
            {
                string arg = m.Groups["arg"].Value.Trim();
                string destType = null;
                string srcType = null;

                if (m.Groups["dest"].Success && !string.IsNullOrEmpty(m.Groups["dest"].Value))
                {
                    destType = NormalizeConvertType(m.Groups["dest"].Value);
                    srcType = NormalizeConvertType(m.Groups["src"].Value);
                }
                else if (m.Groups["dest2"].Success && !string.IsNullOrEmpty(m.Groups["dest2"].Value))
                {
                    destType = NormalizeConvertType(m.Groups["dest2"].Value);
                }
                else if (m.Groups["dest3"].Success && !string.IsNullOrEmpty(m.Groups["dest3"].Value))
                {
                    destType = NormalizeConvertType(m.Groups["dest3"].Value);
                }

                if (string.IsNullOrEmpty(destType)) destType = "Real";
                if (string.IsNullOrEmpty(srcType))
                {
                    srcType = NormalizeConvertType(GetVariableBaseType(arg)) ?? "Int";
                }

                string tempVar = AllocateTempCalcVar(destType);
                if (!UseLadderDbForUndefinedTags)
                {
                    _declaredTempVars[tempVar] = destType;
                }

                list.Add(new PreConvertItem
                {
                    InputVar = arg,
                    SrcType = srcType,
                    DestType = destType,
                    TempVar = tempVar
                });

                return tempVar;
            });

            return transformed;
        }

        private static List<string> SplitCommaSeparatedDimensions(string idxVal)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(idxVal)) return list;

            var sb = new StringBuilder();
            bool inQuote = false;
            int bracketLevel = 0;

            for (int i = 0; i < idxVal.Length; i++)
            {
                char c = idxVal[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    sb.Append(c);
                }
                else if (!inQuote && c == '[')
                {
                    bracketLevel++;
                    sb.Append(c);
                }
                else if (!inQuote && c == ']')
                {
                    bracketLevel = Math.Max(0, bracketLevel - 1);
                    sb.Append(c);
                }
                else if (!inQuote && bracketLevel == 0 && c == ',')
                {
                    string item = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(item))
                        list.Add(item);
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            string lastItem = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(lastItem))
                list.Add(lastItem);

            return list;
        }

        private string ExtractIndexPreCalculations(string varExpr, ref List<PreCalcItem> preCalcs)
        {
            if (string.IsNullOrWhiteSpace(varExpr)) return varExpr;
            if (!varExpr.Contains("[") || !varExpr.Contains("]")) return varExpr;

            var localPreCalcs = preCalcs;

            // Find all [ ... ] array indices
            string res = Regex.Replace(varExpr, @"\[(?<indices>[^\]]+)\]", match =>
            {
                string inside = match.Groups["indices"].Value;
                var dims = SplitCommaSeparatedDimensions(inside);
                var newDims = new List<string>();

                foreach (var d in dims)
                {
                    string dim = d.Trim();
                    if (string.IsNullOrWhiteSpace(dim))
                    {
                        newDims.Add(dim);
                        continue;
                    }

                    // Check if dim contains arithmetic: e.g. #sutun1 + 1, #idx - 1, #a * 2
                    if (TryParseArithmeticExpression(dim, out string bLeft, out string bOp, out string bRight) &&
                        IsSimpleOperand(bLeft) && IsSimpleOperand(bRight))
                    {
                        string partOp = MapBinaryMathOpToPartName(bOp);
                        if (partOp != null)
                        {
                            string calcSrcType = ResolveNumericSrcType(bLeft, bRight);
                            if (string.IsNullOrEmpty(calcSrcType) || calcSrcType == "Variant")
                                calcSrcType = "DInt";

                            // Check if this exact calculation already exists in localPreCalcs
                            string tempVarName = null;
                            if (localPreCalcs != null)
                            {
                                var existing = localPreCalcs.FirstOrDefault(pc =>
                                    string.Equals(pc.LeftVal, bLeft, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(pc.Op, bOp, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(pc.RightVal, bRight, StringComparison.OrdinalIgnoreCase));
                                if (existing != null)
                                {
                                    tempVarName = existing.TargetVar;
                                }
                            }

                            if (tempVarName == null)
                            {
                                tempVarName = AllocateTempCalcVar(calcSrcType);
                                if (localPreCalcs == null) localPreCalcs = new List<PreCalcItem>();
                                localPreCalcs.Add(new PreCalcItem
                                {
                                    LeftVal = bLeft,
                                    Op = bOp,
                                    RightVal = bRight,
                                    PartOp = partOp,
                                    SrcType = calcSrcType,
                                    TargetVar = tempVarName
                                });
                            }

                            newDims.Add(tempVarName);
                            continue;
                        }
                    }

                    newDims.Add(dim);
                }

                return "[" + string.Join(", ", newDims) + "]";
            });

            preCalcs = localPreCalcs;
            return res;
        }

        private void EmitPreCalcBoxes(
            List<PreCalcItem> preCalcs,
            StringBuilder parts,
            StringBuilder wires,
            HashSet<string> emittedTargets,
            ref int lastPowerOutId,
            ref string lastPowerOutPin)
        {
            if (preCalcs == null || preCalcs.Count == 0) return;

            foreach (var pc in preCalcs)
            {
                if (emittedTargets != null && !emittedTargets.Add(pc.TargetVar))
                    continue;

                int leftAccId = NextUId();
                parts.AppendLine(FormatAccessXml(pc.LeftVal, leftAccId, pc.SrcType));

                int rightAccId = NextUId();
                parts.AppendLine(FormatAccessXml(pc.RightVal, rightAccId, pc.SrcType));

                int targetAccId = NextUId();
                parts.AppendLine(FormatAccessXml(pc.TargetVar, targetAccId, pc.SrcType));

                int mathPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"{pc.PartOp}\" UId=\"{mathPartId}\">");
                if (pc.PartOp == "Add" || pc.PartOp == "Mul")
                {
                    parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>");
                }
                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{pc.SrcType}</TemplateValue>");
                parts.AppendLine($"          </Part>");

                int wireEn = NextUId();
                wires.AppendLine($"          <Wire UId=\"{wireEn}\">");
                if (lastPowerOutId < 0)
                {
                    wires.AppendLine($"            <Powerrail />");
                }
                else
                {
                    wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                }
                wires.AppendLine($"            <NameCon UId=\"{mathPartId}\" Name=\"en\" />");
                wires.AppendLine($"          </Wire>");

                int wireIn1 = NextUId();
                wires.AppendLine($"          <Wire UId=\"{wireIn1}\"><IdentCon UId=\"{leftAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"in1\" /></Wire>");

                int wireIn2 = NextUId();
                wires.AppendLine($"          <Wire UId=\"{wireIn2}\"><IdentCon UId=\"{rightAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"in2\" /></Wire>");

                int wireOut = NextUId();
                wires.AppendLine($"          <Wire UId=\"{wireOut}\"><NameCon UId=\"{mathPartId}\" Name=\"out\" /><IdentCon UId=\"{targetAccId}\" /></Wire>");

                lastPowerOutId = mathPartId;
                lastPowerOutPin = "eno";
            }
        }

        private static string MapBinaryMathOpToPartName(string op)
        {
            if (string.IsNullOrWhiteSpace(op)) return null;
            string trimmed = op.Trim();
            if (trimmed == "+") return "Add";
            if (trimmed == "-") return "Sub";
            if (trimmed == "*") return "Mul";
            if (trimmed == "/" || trimmed.Equals("DIV", StringComparison.OrdinalIgnoreCase)) return "Div";
            if (trimmed.Equals("MOD", StringComparison.OrdinalIgnoreCase)) return "Mod";
            if (trimmed == "**") return "Expt";
            return null;
        }

        private bool TryBuildBinaryMathLadder(string targetVar, string leftVal, string op, string rightVal, int networkIndex, out string xml, List<PreConvertItem> preConverts = null)
        {
            xml = null;
            try
            {
                if (!IsValidIdentifier(targetVar)) return false;

                string partOp = MapBinaryMathOpToPartName(op);
                if (partOp == null) return false;

                string srcType = ResolveNumericSrcType(leftVal, rightVal, targetVar);
                if (IsIllegalMathType(srcType)) return false;

                // User rule: For DIV (or any arithmetic) involving Time or DInt (e.g. TON1.ET / 1000), force SrcType to DInt
                if (partOp == "Div" || partOp == "Mul" || partOp == "Add" || partOp == "Sub")
                {
                    bool involvesTimeOrDInt = (leftVal != null && (leftVal.IndexOf(".ET", StringComparison.OrdinalIgnoreCase) >= 0 || leftVal.IndexOf(".PT", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                              (rightVal != null && (rightVal.IndexOf(".ET", StringComparison.OrdinalIgnoreCase) >= 0 || rightVal.IndexOf(".PT", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                              string.Equals(GetVariableBaseType(leftVal), "Time", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(GetVariableBaseType(rightVal), "Time", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(GetVariableBaseType(leftVal), "DInt", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(GetVariableBaseType(rightVal), "DInt", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(GetVariableBaseType(targetVar), "Time", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(GetVariableBaseType(targetVar), "DInt", StringComparison.OrdinalIgnoreCase);

                    if (involvesTimeOrDInt && (srcType == "Int" || srcType == "SInt" || srcType == "UInt" || srcType == "USInt"))
                    {
                        srcType = "DInt";
                    }
                }

                // Implicit conversion for integer variables when arithmetic is Real/LReal
                if (string.Equals(srcType, "Real", StringComparison.OrdinalIgnoreCase) || string.Equals(srcType, "LReal", StringComparison.OrdinalIgnoreCase))
                {
                    string leftType = GetVariableBaseType(leftVal);
                    if (IsIntegerType(leftType) && IsValidIdentifier(leftVal))
                    {
                        string normLeftType = NormalizePlcDatatype(leftType) ?? "Int";
                        string tempLeft = AllocateTempCalcVar(srcType);
                        if (!UseLadderDbForUndefinedTags) _declaredTempVars[tempLeft] = srcType;
                        if (preConverts == null) preConverts = new List<PreConvertItem>();
                        preConverts.Add(new PreConvertItem { InputVar = leftVal, SrcType = normLeftType, DestType = srcType, TempVar = tempLeft });
                        leftVal = tempLeft;
                    }

                    string rightType = GetVariableBaseType(rightVal);
                    if (IsIntegerType(rightType) && IsValidIdentifier(rightVal))
                    {
                        string normRightType = NormalizePlcDatatype(rightType) ?? "Int";
                        string tempRight = AllocateTempCalcVar(srcType);
                        if (!UseLadderDbForUndefinedTags) _declaredTempVars[tempRight] = srcType;
                        if (preConverts == null) preConverts = new List<PreConvertItem>();
                        preConverts.Add(new PreConvertItem { InputVar = rightVal, SrcType = normRightType, DestType = srcType, TempVar = tempRight });
                        rightVal = tempRight;
                    }
                }

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int lastPowerOutId = -1;
                string lastPowerOutPin = "Powerrail";

                if (preConverts != null && preConverts.Count > 0)
                {
                    for (int i = 0; i < preConverts.Count; i++)
                    {
                        var pc = preConverts[i];
                        int convInAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(pc.InputVar, convInAccId, pc.SrcType));

                        int convOutAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(pc.TempVar, convOutAccId, pc.DestType));

                        int convPartId = NextUId();
                        parts.AppendLine($"          <Part Name=\"Convert\" UId=\"{convPartId}\">");
                        parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{pc.SrcType}</TemplateValue>");
                        parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{pc.DestType}</TemplateValue>");
                        parts.AppendLine($"          </Part>");

                        int wireEn = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wireEn}\">");
                        if (i == 0)
                        {
                            wires.AppendLine($"            <Powerrail />");
                        }
                        else
                        {
                            wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                        }
                        wires.AppendLine($"            <NameCon UId=\"{convPartId}\" Name=\"en\" />");
                        wires.AppendLine($"          </Wire>");

                        int wireIn = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wireIn}\"><IdentCon UId=\"{convInAccId}\" /><NameCon UId=\"{convPartId}\" Name=\"in\" /></Wire>");

                        int wireOut = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wireOut}\"><NameCon UId=\"{convPartId}\" Name=\"out\" /><IdentCon UId=\"{convOutAccId}\" /></Wire>");

                        lastPowerOutId = convPartId;
                        lastPowerOutPin = "eno";
                    }
                }

                int leftAccId = NextUId();
                parts.AppendLine(FormatAccessXml(leftVal, leftAccId, srcType));

                int rightAccId = NextUId();
                parts.AppendLine(FormatAccessXml(rightVal, rightAccId, srcType));

                int targetAccId = NextUId();
                parts.AppendLine(FormatAccessXml(targetVar, targetAccId, srcType));

                int mathPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"{partOp}\" UId=\"{mathPartId}\">");
                if (partOp == "Add" || partOp == "Mul")
                {
                    parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>");
                }
                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{srcType}</TemplateValue>");
                parts.AppendLine($"          </Part>");

                // Powerrail to en (or last convert eno to en)
                int enWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{enWireId}\">");
                if (lastPowerOutId < 0)
                {
                    wires.AppendLine($"            <Powerrail />");
                }
                else
                {
                    wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                }
                wires.AppendLine($"            <NameCon UId=\"{mathPartId}\" Name=\"en\" />");
                wires.AppendLine($"          </Wire>");

                // in1 and in2
                int in1Wire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{in1Wire}\"><IdentCon UId=\"{leftAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"in1\" /></Wire>");

                int in2Wire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{in2Wire}\"><IdentCon UId=\"{rightAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"in2\" /></Wire>");

                // out to target
                int outWire = NextUId();
                wires.AppendLine($"          <Wire UId=\"{outWire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"out\" /></Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {targetVar} := {leftVal} {op} {rightVal}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildCalculateLadder(string targetVar, string expr, int networkIndex, out string xml, List<PreConvertItem> preConverts = null)
        {
            xml = null;
            try
            {
                if (!TryPrepareCalculateEquation(targetVar, expr, out string equation, out List<string> varList, out string srcType))
                    return false;
                if (IsIllegalMathType(srcType)) return false;

                // Implicit conversion for integer variables if srcType is Real/LReal
                if (string.Equals(srcType, "Real", StringComparison.OrdinalIgnoreCase) || string.Equals(srcType, "LReal", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < varList.Count; i++)
                    {
                        string v = varList[i];
                        string vType = GetVariableBaseType(v);
                        if (IsIntegerType(vType) && IsValidIdentifier(v))
                        {
                            string normVType = NormalizePlcDatatype(vType) ?? "Int";
                            string tempV = AllocateTempCalcVar(srcType);
                            if (!UseLadderDbForUndefinedTags) _declaredTempVars[tempV] = srcType;
                            if (preConverts == null) preConverts = new List<PreConvertItem>();
                            preConverts.Add(new PreConvertItem { InputVar = v, SrcType = normVType, DestType = srcType, TempVar = tempV });
                            varList[i] = tempV;
                        }
                    }
                }

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int lastPowerOutId = -1;
                string lastPowerOutPin = "Powerrail";

                if (preConverts != null && preConverts.Count > 0)
                {
                    for (int i = 0; i < preConverts.Count; i++)
                    {
                        var pc = preConverts[i];
                        int convInAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(pc.InputVar, convInAccId, pc.SrcType));

                        int convOutAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(pc.TempVar, convOutAccId, pc.DestType));

                        int convPartId = NextUId();
                        parts.AppendLine($"          <Part Name=\"Convert\" UId=\"{convPartId}\">");
                        parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{pc.SrcType}</TemplateValue>");
                        parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{pc.DestType}</TemplateValue>");
                        parts.AppendLine($"          </Part>");

                        int wireEn = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wireEn}\">");
                        if (i == 0)
                        {
                            wires.AppendLine($"            <Powerrail />");
                        }
                        else
                        {
                            wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                        }
                        wires.AppendLine($"            <NameCon UId=\"{convPartId}\" Name=\"en\" />");
                        wires.AppendLine($"          </Wire>");

                        int wireIn = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wireIn}\"><IdentCon UId=\"{convInAccId}\" /><NameCon UId=\"{convPartId}\" Name=\"in\" /></Wire>");

                        int wireOut = NextUId();
                        wires.AppendLine($"          <Wire UId=\"{wireOut}\"><NameCon UId=\"{convPartId}\" Name=\"out\" /><IdentCon UId=\"{convOutAccId}\" /></Wire>");

                        lastPowerOutId = convPartId;
                        lastPowerOutPin = "eno";
                    }
                }

                int calcId = NextUId();
                parts.AppendLine($"          <Part Name=\"Calc\" UId=\"{calcId}\">");
                parts.AppendLine($"            <Equation>{System.Security.SecurityElement.Escape(equation)}</Equation>");
                parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{varList.Count}</TemplateValue>");
                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{srcType}</TemplateValue>");
                parts.AppendLine($"          </Part>");

                // Powerrail to en (or last convert eno to en)
                int enWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{enWireId}\">");
                if (lastPowerOutId < 0)
                {
                    wires.AppendLine($"            <Powerrail />");
                }
                else
                {
                    wires.AppendLine($"            <NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" />");
                }
                wires.AppendLine($"            <NameCon UId=\"{calcId}\" Name=\"en\" />");
                wires.AppendLine($"          </Wire>");

                // in1 .. inN
                for (int i = 0; i < varList.Count; i++)
                {
                    int inAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(varList[i], inAccId, srcType));

                    int inWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{inWireId}\"><IdentCon UId=\"{inAccId}\" /><NameCon UId=\"{calcId}\" Name=\"in{i + 1}\" /></Wire>");
                }

                // out to target
                int targetAccId = NextUId();
                parts.AppendLine(FormatAccessXml(targetVar, targetAccId, srcType));

                int outWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{outWireId}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{calcId}\" Name=\"out\" /></Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: {targetVar} Hesaplaması");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryPrepareCalculateEquation(string targetVar, string expr, out string equation, out List<string> varList, out string srcType)
        {
            equation = null;
            varList = new List<string>();
            srcType = "DInt";

            if (!IsValidIdentifier(targetVar)) return false;
            string exprWithoutBrackets = Regex.Replace(expr, @"\[[^\]]*\]", "");
            if (!Regex.IsMatch(exprWithoutBrackets, @"[\+\-\*\/]")) return false;

            // Reject if contains illegal characters (note: allow [ ] for array indexing inside identifiers)
            string stripped = Regex.Replace(expr, @"#", "");
            if (Regex.IsMatch(stripped, @"[;:=?&|<>{}\\]")) return false;

            // Reject if contains function calls like TIME_TO_DINT(...) or FUNC(...)
            // A Calculate box equation in Siemens LAD cannot execute function calls!
            if (Regex.IsMatch(expr, @"\b[a-zA-Z_][a-zA-Z0-9_]*\s*\(")) return false;

            // Match all operands (typed constants, radix constants, variables with optional array indices, and standalone numeric constants) in one left-to-right pass.
            // TIA Portal's CALCULATE (Calc) instruction requires every operand (both tags and constants) to be an INx input pin.
            var operandRegex = new Regex(
                @"(?<typed>\b(?:BOOL|BYTE|WORD|DWORD|LWORD|SINT|USINT|INT|UINT|DINT|UDINT|LINT|ULINT|REAL|LREAL)#[0-9a-fA-F_\.]+(?:\.[0-9]+)?)|(?<radix>\b(?:16|2|8)#[0-9a-fA-F_]+\b)|(?<var>#?(?:""[^""]+""|[a-zA-Z_ğüşıöçĞÜŞİÖÇ%][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]*)(?:\s*\[\s*[^\]]+\s*\])?(?:\.(?:""[^""]+""|[a-zA-Z_ğüşıöçĞÜŞİÖÇ%][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]*)(?:\s*\[\s*[^\]]+\s*\])?)*)|(?<num>\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var localVars = new List<string>();
            equation = operandRegex.Replace(expr, m =>
            {
                string v = m.Value.Trim();
                if (string.IsNullOrWhiteSpace(v)) return m.Value;
                if (v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || v.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                    return m.Value;

                int idx = localVars.IndexOf(v);
                if (idx < 0)
                {
                    localVars.Add(v);
                    idx = localVars.Count - 1;
                }
                return $"IN{idx + 1}";
            });

            varList = localVars;
            if (varList.Count < 2 || varList.Count > 32) return false;

            srcType = ResolveNumericSrcType(targetVar, expr);
            foreach (var v in varList)
            {
                srcType = ResolveNumericSrcType(srcType, v, targetVar);
            }
            if (IsIllegalMathType(srcType)) return false;
            return true;
        }

        private bool TryBuildConditionalLadder(string condExpr, List<IfActionItem> actions, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (actions == null || actions.Count == 0) return false;

                var condPreCalcs = new List<PreCalcItem>();
                var rawAst = ParseBoolExpr(condExpr, condPreCalcs);
                if (rawAst == null) return false;
                var ast = NormalizeBoolAst(rawAst, false);
                if (ast == null) return false;

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int condLastPowerOutId = -1;
                string condLastPowerOutPin = "Powerrail";

                var emittedPreCalcTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Emit condition pre-calculations (e.g. tempCalc1 := #sira + 1)
                if (condPreCalcs.Count > 0)
                {
                    EmitPreCalcBoxes(condPreCalcs, parts, wires, emittedPreCalcTargets, ref condLastPowerOutId, ref condLastPowerOutPin);
                }

                // 2. Emit action pre-calculations (e.g. index arithmetic in actions)
                foreach (var act in actions)
                {
                    if (act.PreCalcs != null && act.PreCalcs.Count > 0)
                    {
                        EmitPreCalcBoxes(act.PreCalcs, parts, wires, emittedPreCalcTargets, ref condLastPowerOutId, ref condLastPowerOutPin);
                    }
                }

                var powerrailSinks = new List<Tuple<int, string>>();
                var internalWires = new StringBuilder();

                var finalOut = BuildBoolExprNode(ast, powerrailSinks, parts, internalWires);
                if (finalOut == null || powerrailSinks.Count == 0)
                    return false;

                // Powerrail / En wire (connects to all entry points of the condition AST)
                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                if (condLastPowerOutId < 0)
                {
                    wires.AppendLine($"            <Powerrail />");
                }
                else
                {
                    wires.AppendLine($"            <NameCon UId=\"{condLastPowerOutId}\" Name=\"{condLastPowerOutPin}\" />");
                }
                foreach (var item in powerrailSinks)
                {
                    wires.AppendLine($"            <NameCon UId=\"{item.Item1}\" Name=\"{item.Item2}\" />");
                }
                wires.AppendLine($"          </Wire>");

                wires.Append(internalWires.ToString());

                // Condition Bus Wire (connects condition output to all actions)
                int condBusWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{condBusWireId}\">");
                wires.AppendLine($"            <NameCon UId=\"{finalOut.Item1}\" Name=\"{finalOut.Item2}\" />");

                var actionInternalWires = new StringBuilder();

                // Add actions
                foreach (var act in actions)
                {
                    if (act.IsReturn)
                    {
                        int retPartId = NextUId();
                        parts.AppendLine($"          <Part Name=\"Return\" UId=\"{retPartId}\" />");
                        wires.AppendLine($"            <NameCon UId=\"{retPartId}\" Name=\"in\" />");
                    }
                    else if (act.IsJump)
                    {
                        int lblAccId = NextUId();
                        parts.AppendLine($"          <Access Scope=\"Label\" UId=\"{lblAccId}\">");
                        parts.AppendLine($"            <Label Name=\"{System.Security.SecurityElement.Escape(act.JumpLabel)}\" />");
                        parts.AppendLine($"          </Access>");

                        int jmpPartId = NextUId();
                        parts.AppendLine($"          <Part Name=\"Jump\" UId=\"{jmpPartId}\" />");

                        wires.AppendLine($"            <NameCon UId=\"{jmpPartId}\" Name=\"in\" />");

                        int lblWire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{lblWire}\"><IdentCon UId=\"{lblAccId}\" /><NameCon UId=\"{jmpPartId}\" Name=\"label\" /></Wire>");
                    }
                    else if (act.IsSet)
                    {
                        int targetAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.TargetVar, targetAccId));

                        int sCoilId = NextUId();
                        parts.AppendLine($"          <Part Name=\"SCoil\" UId=\"{sCoilId}\" />");

                        wires.AppendLine($"            <NameCon UId=\"{sCoilId}\" Name=\"in\" />");

                        int opWire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{opWire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{sCoilId}\" Name=\"operand\" /></Wire>");
                    }
                    else if (act.IsReset)
                    {
                        int targetAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.TargetVar, targetAccId));

                        int rCoilId = NextUId();
                        parts.AppendLine($"          <Part Name=\"RCoil\" UId=\"{rCoilId}\" />");

                        wires.AppendLine($"            <NameCon UId=\"{rCoilId}\" Name=\"in\" />");

                        int opWire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{opWire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{rCoilId}\" Name=\"operand\" /></Wire>");
                    }
                    else if (act.IsFunctionBox)
                    {
                        if (act.IsCascadeBox && act.SecondBox != null)
                        {
                            var secAct = act.SecondBox;
                            var pinAccIds1 = new List<Tuple<string, int>>();
                            foreach (var pin in act.FuncInputPins)
                            {
                                int accId = NextUId();
                                parts.AppendLine(FormatAccessXml(pin.Item2, accId));
                                pinAccIds1.Add(Tuple.Create(pin.Item1, accId));
                            }

                            var pinAccIds2 = new List<Tuple<string, int>>();
                            foreach (var pin in secAct.FuncInputPins)
                            {
                                int accId = NextUId();
                                parts.AppendLine(FormatAccessXml(pin.Item2, accId));
                                pinAccIds2.Add(Tuple.Create(pin.Item1, accId));
                            }

                            int targetAccId = NextUId();
                            parts.AppendLine(FormatAccessXml(secAct.TargetVar, targetAccId));

                            int fnPart1Id = NextUId();
                            parts.AppendLine($"          <Part Name=\"{act.FuncPartName}\" UId=\"{fnPart1Id}\">");
                            if (act.FuncCardinality > 0)
                                parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{act.FuncCardinality}</TemplateValue>");
                            if (!string.IsNullOrEmpty(act.FuncSrcType))
                                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{act.FuncSrcType}</TemplateValue>");
                            if (!string.IsNullOrEmpty(act.FuncDestType))
                                parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{act.FuncDestType}</TemplateValue>");
                            parts.AppendLine($"          </Part>");

                            int fnPart2Id = NextUId();
                            parts.AppendLine($"          <Part Name=\"{secAct.FuncPartName}\" UId=\"{fnPart2Id}\">");
                            if (secAct.FuncCardinality > 0)
                                parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{secAct.FuncCardinality}</TemplateValue>");
                            if (!string.IsNullOrEmpty(secAct.FuncSrcType))
                                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{secAct.FuncSrcType}</TemplateValue>");
                            if (!string.IsNullOrEmpty(secAct.FuncDestType))
                                parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{secAct.FuncDestType}</TemplateValue>");
                            parts.AppendLine($"          </Part>");

                            wires.AppendLine($"            <NameCon UId=\"{fnPart1Id}\" Name=\"en\" />");

                            foreach (var pa in pinAccIds1)
                            {
                                int wId = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wId}\"><IdentCon UId=\"{pa.Item2}\" /><NameCon UId=\"{fnPart1Id}\" Name=\"{pa.Item1}\" /></Wire>");
                            }

                            int cascadeEnWireId = NextUId();
                            actionInternalWires.AppendLine($"          <Wire UId=\"{cascadeEnWireId}\"><NameCon UId=\"{fnPart1Id}\" Name=\"eno\" /><NameCon UId=\"{fnPart2Id}\" Name=\"en\" /></Wire>");

                            int cascadeDataWireId = NextUId();
                            actionInternalWires.AppendLine($"          <Wire UId=\"{cascadeDataWireId}\"><NameCon UId=\"{fnPart1Id}\" Name=\"out\" /><NameCon UId=\"{fnPart2Id}\" Name=\"value\" /></Wire>");

                            foreach (var pa in pinAccIds2)
                            {
                                int wId = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wId}\"><IdentCon UId=\"{pa.Item2}\" /><NameCon UId=\"{fnPart2Id}\" Name=\"{pa.Item1}\" /></Wire>");
                            }

                            int outWire = NextUId();
                            string outPin = !string.IsNullOrEmpty(secAct.FuncOutputPin) ? secAct.FuncOutputPin : "out";
                            actionInternalWires.AppendLine($"          <Wire UId=\"{outWire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{fnPart2Id}\" Name=\"{outPin}\" /></Wire>");
                        }
                        else
                        {
                            var pinAccIds = new List<Tuple<string, int>>();
                            foreach (var pin in act.FuncInputPins)
                            {
                                int accId = NextUId();
                                parts.AppendLine(FormatAccessXml(pin.Item2, accId));
                                pinAccIds.Add(Tuple.Create(pin.Item1, accId));
                            }

                            int targetAccId = NextUId();
                            parts.AppendLine(FormatAccessXml(act.TargetVar, targetAccId));

                            int fnPartId = NextUId();
                            parts.AppendLine($"          <Part Name=\"{act.FuncPartName}\" UId=\"{fnPartId}\">");
                            if (act.FuncCardinality > 0)
                            {
                                parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{act.FuncCardinality}</TemplateValue>");
                            }
                            if (!string.IsNullOrEmpty(act.FuncSrcType))
                            {
                                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{act.FuncSrcType}</TemplateValue>");
                            }
                            if (!string.IsNullOrEmpty(act.FuncDestType))
                            {
                                parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{act.FuncDestType}</TemplateValue>");
                            }
                            parts.AppendLine($"          </Part>");

                            wires.AppendLine($"            <NameCon UId=\"{fnPartId}\" Name=\"en\" />");

                            foreach (var pa in pinAccIds)
                            {
                                int wId = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wId}\"><IdentCon UId=\"{pa.Item2}\" /><NameCon UId=\"{fnPartId}\" Name=\"{pa.Item1}\" /></Wire>");
                            }

                            int outWire = NextUId();
                            string outPin = !string.IsNullOrEmpty(act.FuncOutputPin) ? act.FuncOutputPin : "out";
                            actionInternalWires.AppendLine($"          <Wire UId=\"{outWire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{fnPartId}\" Name=\"{outPin}\" /></Wire>");
                        }
                    }
                    else if (act.IsBinaryMath)
                    {
                        int lastPowerOutId = -1;
                        string lastPowerOutPin = null;

                        if (act.PreConverts != null && act.PreConverts.Count > 0)
                        {
                            for (int i = 0; i < act.PreConverts.Count; i++)
                            {
                                var pc = act.PreConverts[i];
                                int convInAccId = NextUId();
                                parts.AppendLine(FormatAccessXml(pc.InputVar, convInAccId, pc.SrcType));

                                int convOutAccId = NextUId();
                                parts.AppendLine(FormatAccessXml(pc.TempVar, convOutAccId, pc.DestType));

                                int convPartId = NextUId();
                                parts.AppendLine($"          <Part Name=\"Convert\" UId=\"{convPartId}\">");
                                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{pc.SrcType}</TemplateValue>");
                                parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{pc.DestType}</TemplateValue>");
                                parts.AppendLine($"          </Part>");

                                if (i == 0)
                                {
                                    wires.AppendLine($"            <NameCon UId=\"{convPartId}\" Name=\"en\" />");
                                }
                                else
                                {
                                    int wireConvEn = NextUId();
                                    actionInternalWires.AppendLine($"          <Wire UId=\"{wireConvEn}\"><NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" /><NameCon UId=\"{convPartId}\" Name=\"en\" /></Wire>");
                                }

                                int wireIn = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wireIn}\"><IdentCon UId=\"{convInAccId}\" /><NameCon UId=\"{convPartId}\" Name=\"in\" /></Wire>");

                                int wireOut = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wireOut}\"><NameCon UId=\"{convPartId}\" Name=\"out\" /><IdentCon UId=\"{convOutAccId}\" /></Wire>");

                                lastPowerOutId = convPartId;
                                lastPowerOutPin = "eno";
                            }
                        }

                        int leftAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.MathLeft, leftAccId, act.MathSrcType));

                        int rightAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.MathRight, rightAccId, act.MathSrcType));

                        int targetAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.TargetVar, targetAccId, act.MathSrcType));

                        int mathPartId = NextUId();
                        parts.AppendLine($"          <Part Name=\"{act.MathPartOp}\" UId=\"{mathPartId}\">");
                        if (act.MathPartOp == "Add" || act.MathPartOp == "Mul")
                        {
                            parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>");
                        }
                        parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{act.MathSrcType}</TemplateValue>");
                        parts.AppendLine($"          </Part>");

                        if (lastPowerOutId < 0)
                        {
                            wires.AppendLine($"            <NameCon UId=\"{mathPartId}\" Name=\"en\" />");
                        }
                        else
                        {
                            int wireMathEn = NextUId();
                            actionInternalWires.AppendLine($"          <Wire UId=\"{wireMathEn}\"><NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" /><NameCon UId=\"{mathPartId}\" Name=\"en\" /></Wire>");
                        }

                        int in1Wire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{in1Wire}\"><IdentCon UId=\"{leftAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"in1\" /></Wire>");

                        int in2Wire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{in2Wire}\"><IdentCon UId=\"{rightAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"in2\" /></Wire>");

                        int outWire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{outWire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{mathPartId}\" Name=\"out\" /></Wire>");
                    }
                    else if (act.IsCalc)
                    {
                        int lastPowerOutId = -1;
                        string lastPowerOutPin = null;

                        if (act.PreConverts != null && act.PreConverts.Count > 0)
                        {
                            for (int i = 0; i < act.PreConverts.Count; i++)
                            {
                                var pc = act.PreConverts[i];
                                int convInAccId = NextUId();
                                parts.AppendLine(FormatAccessXml(pc.InputVar, convInAccId, pc.SrcType));

                                int convOutAccId = NextUId();
                                parts.AppendLine(FormatAccessXml(pc.TempVar, convOutAccId, pc.DestType));

                                int convPartId = NextUId();
                                parts.AppendLine($"          <Part Name=\"Convert\" UId=\"{convPartId}\">");
                                parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{pc.SrcType}</TemplateValue>");
                                parts.AppendLine($"            <TemplateValue Name=\"DestType\" Type=\"Type\">{pc.DestType}</TemplateValue>");
                                parts.AppendLine($"          </Part>");

                                if (i == 0)
                                {
                                    wires.AppendLine($"            <NameCon UId=\"{convPartId}\" Name=\"en\" />");
                                }
                                else
                                {
                                    int wireConvEn = NextUId();
                                    actionInternalWires.AppendLine($"          <Wire UId=\"{wireConvEn}\"><NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" /><NameCon UId=\"{convPartId}\" Name=\"en\" /></Wire>");
                                }

                                int wireIn = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wireIn}\"><IdentCon UId=\"{convInAccId}\" /><NameCon UId=\"{convPartId}\" Name=\"in\" /></Wire>");

                                int wireOut = NextUId();
                                actionInternalWires.AppendLine($"          <Wire UId=\"{wireOut}\"><NameCon UId=\"{convPartId}\" Name=\"out\" /><IdentCon UId=\"{convOutAccId}\" /></Wire>");

                                lastPowerOutId = convPartId;
                                lastPowerOutPin = "eno";
                            }
                        }

                        int calcId = NextUId();
                        parts.AppendLine($"          <Part Name=\"Calc\" UId=\"{calcId}\">");
                        parts.AppendLine($"            <Equation>{System.Security.SecurityElement.Escape(act.CalcEquation)}</Equation>");
                        parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{act.CalcVars.Count}</TemplateValue>");
                        parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{act.MathSrcType}</TemplateValue>");
                        parts.AppendLine($"          </Part>");

                        if (lastPowerOutId < 0)
                        {
                            wires.AppendLine($"            <NameCon UId=\"{calcId}\" Name=\"en\" />");
                        }
                        else
                        {
                            int wireCalcEn = NextUId();
                            actionInternalWires.AppendLine($"          <Wire UId=\"{wireCalcEn}\"><NameCon UId=\"{lastPowerOutId}\" Name=\"{lastPowerOutPin}\" /><NameCon UId=\"{calcId}\" Name=\"en\" /></Wire>");
                        }

                        for (int i = 0; i < act.CalcVars.Count; i++)
                        {
                            int inAccId = NextUId();
                            parts.AppendLine(FormatAccessXml(act.CalcVars[i], inAccId, act.MathSrcType));

                            int inWireId = NextUId();
                            actionInternalWires.AppendLine($"          <Wire UId=\"{inWireId}\"><IdentCon UId=\"{inAccId}\" /><NameCon UId=\"{calcId}\" Name=\"in{i + 1}\" /></Wire>");
                        }

                        int targetAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.TargetVar, targetAccId, act.MathSrcType));

                        int outWireId = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{outWireId}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{calcId}\" Name=\"out\" /></Wire>");
                    }
                    else // Move
                    {
                        int srcAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.SourceValue, srcAccId));

                        int targetAccId = NextUId();
                        parts.AppendLine(FormatAccessXml(act.TargetVar, targetAccId));

                        int movePartId = NextUId();
                        parts.AppendLine($"          <Part Name=\"Move\" UId=\"{movePartId}\">");
                        parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">1</TemplateValue>");
                        parts.AppendLine($"          </Part>");

                        wires.AppendLine($"            <NameCon UId=\"{movePartId}\" Name=\"en\" />");

                        int inWire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{inWire}\"><IdentCon UId=\"{srcAccId}\" /><NameCon UId=\"{movePartId}\" Name=\"in\" /></Wire>");

                        int out1Wire = NextUId();
                        actionInternalWires.AppendLine($"          <Wire UId=\"{out1Wire}\"><IdentCon UId=\"{targetAccId}\" /><NameCon UId=\"{movePartId}\" Name=\"out1\" /></Wire>");
                    }
                }

                // Close condBusWire
                wires.AppendLine($"          </Wire>");

                // Append action-internal wires after condBusWire is closed
                wires.Append(actionInternalWires.ToString());

                string titleCond = condExpr.Length > 90 ? condExpr.Substring(0, 87) + "..." : condExpr;
                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: IF {titleCond}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<IfActionItem> ParseIfActions(string body)
        {
            var list = new List<IfActionItem>();
            string cleanBody = StripComments(body).Trim();

            var stmts = cleanBody.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in stmts)
            {
                string stmt = raw.Trim();
                if (string.IsNullOrWhiteSpace(stmt)) continue;

                if (stmt.Equals("RETURN", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new IfActionItem { IsReturn = true });
                    continue;
                }

                var gotoMatch = Regex.Match(stmt, @"^GOTO\s+([a-zA-Z_][a-zA-Z0-9_]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (gotoMatch.Success)
                {
                    list.Add(new IfActionItem { IsJump = true, JumpLabel = gotoMatch.Groups[1].Value.Trim() });
                    continue;
                }

                // S7-SCL procedure calls in IF actions: SET(x), RESET(x), INC(x), DEC(x)
                var setActionMatch = Regex.Match(stmt, @"^SET\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (setActionMatch.Success && IsValidIdentifier(setActionMatch.Groups[1].Value.Trim()))
                {
                    list.Add(new IfActionItem { TargetVar = setActionMatch.Groups[1].Value.Trim(), IsSet = true });
                    continue;
                }
                var resetActionMatch = Regex.Match(stmt, @"^RESET\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (resetActionMatch.Success && IsValidIdentifier(resetActionMatch.Groups[1].Value.Trim()))
                {
                    list.Add(new IfActionItem { TargetVar = resetActionMatch.Groups[1].Value.Trim(), IsReset = true });
                    continue;
                }
                var incActionMatch = Regex.Match(stmt, @"^INC\s*\(\s*(?<var>[^,\)]+)(?:\s*,\s*(?<step>[^\)]+))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (incActionMatch.Success)
                {
                    string incTarget = incActionMatch.Groups["var"].Value.Trim();
                    string incStep = incActionMatch.Groups["step"].Success ? incActionMatch.Groups["step"].Value.Trim() : "1";
                    if (IsValidIdentifier(incTarget) && IsSimpleOperand(incStep))
                    {
                        string incSrcType = ResolveNumericSrcType(incTarget, incStep);
                        if (IsIllegalMathType(incSrcType)) return null;

                        list.Add(new IfActionItem
                        {
                            TargetVar = incTarget,
                            IsBinaryMath = true,
                            MathLeft = incTarget,
                            MathRight = incStep,
                            MathPartOp = "Add",
                            MathSrcType = incSrcType
                        });
                        continue;
                    }
                }
                var decActionMatch = Regex.Match(stmt, @"^DEC\s*\(\s*(?<var>[^,\)]+)(?:\s*,\s*(?<step>[^\)]+))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (decActionMatch.Success)
                {
                    string decTarget = decActionMatch.Groups["var"].Value.Trim();
                    string decStep = decActionMatch.Groups["step"].Success ? decActionMatch.Groups["step"].Value.Trim() : "1";
                    if (IsValidIdentifier(decTarget) && IsSimpleOperand(decStep))
                    {
                        string decSrcType = ResolveNumericSrcType(decTarget, decStep);
                        if (IsIllegalMathType(decSrcType)) return null;

                        list.Add(new IfActionItem
                        {
                            TargetVar = decTarget,
                            IsBinaryMath = true,
                            MathLeft = decTarget,
                            MathRight = decStep,
                            MathPartOp = "Sub",
                            MathSrcType = decSrcType
                        });
                        continue;
                    }
                }

                var match = Regex.Match(stmt, @"^(.+?)\s*:=\s*(.+)$");
                if (!match.Success) return null;

                string target = match.Groups[1].Value.Trim();
                string val = StripOuterParentheses(match.Groups[2].Value.Trim());
                string unwrappedVal = UnwrapTimeConversions(val);

                List<PreCalcItem> actIndexPreCalcs = null;
                string cleanTarget = ExtractIndexPreCalculations(target, ref actIndexPreCalcs);
                string cleanVal = ExtractIndexPreCalculations(val, ref actIndexPreCalcs);
                string cleanUnwrapped = ExtractIndexPreCalculations(unwrappedVal, ref actIndexPreCalcs);

                if (!IsValidIdentifier(cleanTarget)) return null;

                bool isTargetBool = string.Equals(GetVariableBaseType(cleanTarget), "Bool", StringComparison.OrdinalIgnoreCase);

                if (cleanVal.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                    cleanVal.Equals("BOOL#TRUE", StringComparison.OrdinalIgnoreCase) ||
                    cleanVal.Equals("BOOL#1", StringComparison.OrdinalIgnoreCase) ||
                    (isTargetBool && cleanVal == "1"))
                {
                    list.Add(new IfActionItem { TargetVar = cleanTarget, IsSet = true, PreCalcs = actIndexPreCalcs ?? new List<PreCalcItem>() });
                }
                else if (cleanVal.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                         cleanVal.Equals("BOOL#FALSE", StringComparison.OrdinalIgnoreCase) ||
                         cleanVal.Equals("BOOL#0", StringComparison.OrdinalIgnoreCase) ||
                         (isTargetBool && cleanVal == "0"))
                {
                    list.Add(new IfActionItem { TargetVar = cleanTarget, IsReset = true, PreCalcs = actIndexPreCalcs ?? new List<PreCalcItem>() });
                }
                else if (TryParseFunctionBoxCall(cleanTarget, cleanVal, out IfActionItem fnAct))
                {
                    if (actIndexPreCalcs != null && actIndexPreCalcs.Count > 0)
                        fnAct.PreCalcs.AddRange(actIndexPreCalcs);
                    list.Add(fnAct);
                }
                else if (IsSimpleOperand(cleanVal) || IsSimpleOperand(cleanUnwrapped))
                {
                    list.Add(new IfActionItem {
                        TargetVar = cleanTarget,
                        SourceValue = IsSimpleOperand(cleanVal) ? cleanVal : cleanUnwrapped,
                        PreCalcs = actIndexPreCalcs ?? new List<PreCalcItem>()
                    });
                }
                else if (cleanVal.StartsWith("-") && !IsConstantLiteral(cleanVal) && IsSimpleOperand(StripOuterParentheses(cleanVal.Substring(1).Trim())))
                {
                    string inner = StripOuterParentheses(cleanVal.Substring(1).Trim());
                    string unarySrcType = ResolveNumericSrcType("0", inner, cleanTarget);
                    if (IsIllegalMathType(unarySrcType)) return null;

                    list.Add(new IfActionItem
                    {
                        TargetVar = cleanTarget,
                        IsBinaryMath = true,
                        MathLeft = "0",
                        MathRight = inner,
                        MathPartOp = "Sub",
                        MathSrcType = unarySrcType,
                        PreCalcs = actIndexPreCalcs ?? new List<PreCalcItem>()
                    });
                }
                else
                {
                    string actVal = cleanVal;
                    string actUnwrapped = cleanUnwrapped;
                    List<PreConvertItem> actPreConverts = null;
                    if (TypeConvertRegex.IsMatch(cleanVal) && Regex.IsMatch(cleanVal, @"[\+\-\*\/]"))
                    {
                        actVal = ExtractPreConversions(cleanVal, cleanTarget, out actPreConverts);
                        actUnwrapped = UnwrapTimeConversions(actVal);
                    }

                    if ((TryParseArithmeticExpression(actVal, out string mLeft, out string mOp, out string mRight) ||
                         TryParseArithmeticExpression(actUnwrapped, out mLeft, out mOp, out mRight)) &&
                        IsSimpleOperand(mLeft) && IsSimpleOperand(mRight))
                    {
                        string partOp = MapBinaryMathOpToPartName(mOp);
                        if (partOp == null) return null;
                        string mathSrcType = ResolveNumericSrcType(mLeft, mRight, cleanTarget);
                        if (IsIllegalMathType(mathSrcType)) return null;

                        if (partOp == "Div" || partOp == "Mul" || partOp == "Add" || partOp == "Sub")
                        {
                            bool involvesTimeOrDInt = (mLeft != null && (mLeft.IndexOf(".ET", StringComparison.OrdinalIgnoreCase) >= 0 || mLeft.IndexOf(".PT", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                                      (mRight != null && (mRight.IndexOf(".ET", StringComparison.OrdinalIgnoreCase) >= 0 || mRight.IndexOf(".PT", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                                      string.Equals(GetVariableBaseType(mLeft), "Time", StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(GetVariableBaseType(mRight), "Time", StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(GetVariableBaseType(mLeft), "DInt", StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(GetVariableBaseType(mRight), "DInt", StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(GetVariableBaseType(cleanTarget), "Time", StringComparison.OrdinalIgnoreCase) ||
                                                      string.Equals(GetVariableBaseType(cleanTarget), "DInt", StringComparison.OrdinalIgnoreCase);

                            if (involvesTimeOrDInt && (mathSrcType == "Int" || mathSrcType == "SInt" || mathSrcType == "UInt" || mathSrcType == "USInt"))
                            {
                                mathSrcType = "DInt";
                            }
                        }

                        // Implicit convert check
                        if (string.Equals(mathSrcType, "Real", StringComparison.OrdinalIgnoreCase) || string.Equals(mathSrcType, "LReal", StringComparison.OrdinalIgnoreCase))
                        {
                            string leftType = GetVariableBaseType(mLeft);
                            if (IsIntegerType(leftType) && IsValidIdentifier(mLeft))
                            {
                                string normLeftType = NormalizePlcDatatype(leftType) ?? "Int";
                                string tempLeft = AllocateTempCalcVar(mathSrcType);
                                if (!UseLadderDbForUndefinedTags) _declaredTempVars[tempLeft] = mathSrcType;
                                if (actPreConverts == null) actPreConverts = new List<PreConvertItem>();
                                actPreConverts.Add(new PreConvertItem { InputVar = mLeft, SrcType = normLeftType, DestType = mathSrcType, TempVar = tempLeft });
                                mLeft = tempLeft;
                            }

                            string rightType = GetVariableBaseType(mRight);
                            if (IsIntegerType(rightType) && IsValidIdentifier(mRight))
                            {
                                string normRightType = NormalizePlcDatatype(rightType) ?? "Int";
                                string tempRight = AllocateTempCalcVar(mathSrcType);
                                if (!UseLadderDbForUndefinedTags) _declaredTempVars[tempRight] = mathSrcType;
                                if (actPreConverts == null) actPreConverts = new List<PreConvertItem>();
                                actPreConverts.Add(new PreConvertItem { InputVar = mRight, SrcType = normRightType, DestType = mathSrcType, TempVar = tempRight });
                                mRight = tempRight;
                            }
                        }

                        list.Add(new IfActionItem
                        {
                            TargetVar = cleanTarget,
                            IsBinaryMath = true,
                            MathLeft = mLeft,
                            MathRight = mRight,
                            MathPartOp = partOp,
                            MathSrcType = mathSrcType,
                            PreConverts = actPreConverts ?? new List<PreConvertItem>(),
                            PreCalcs = actIndexPreCalcs ?? new List<PreCalcItem>()
                        });
                    }
                    else if (TryPrepareCalculateEquation(cleanTarget, actVal, out string eq, out List<string> calcVars, out string calcSrcType))
                    {
                        // Implicit convert check for calc
                        if (string.Equals(calcSrcType, "Real", StringComparison.OrdinalIgnoreCase) || string.Equals(calcSrcType, "LReal", StringComparison.OrdinalIgnoreCase))
                        {
                            for (int i = 0; i < calcVars.Count; i++)
                            {
                                string v = calcVars[i];
                                string vType = GetVariableBaseType(v);
                                if (IsIntegerType(vType) && IsValidIdentifier(v))
                                {
                                    string normVType = NormalizePlcDatatype(vType) ?? "Int";
                                    string tempV = AllocateTempCalcVar(calcSrcType);
                                    if (!UseLadderDbForUndefinedTags) _declaredTempVars[tempV] = calcSrcType;
                                    if (actPreConverts == null) actPreConverts = new List<PreConvertItem>();
                                    actPreConverts.Add(new PreConvertItem { InputVar = v, SrcType = normVType, DestType = calcSrcType, TempVar = tempV });
                                    calcVars[i] = tempV;
                                }
                            }
                        }

                        list.Add(new IfActionItem
                        {
                            TargetVar = cleanTarget,
                            IsCalc = true,
                            CalcEquation = eq,
                            CalcVars = calcVars,
                            MathSrcType = calcSrcType,
                            PreConverts = actPreConverts ?? new List<PreConvertItem>(),
                            PreCalcs = actIndexPreCalcs ?? new List<PreCalcItem>()
                        });
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return list;
        }

        private bool TryBuildLabelLadder(string labelName, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (string.IsNullOrWhiteSpace(labelName)) return false;

                string tempVar = $"tempLabel_{labelName}";
                _declaredTempVars[tempVar] = "Bool";

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int targetAccId = NextUId();
                parts.AppendLine(FormatAccessXml($"#{tempVar}", targetAccId));

                int coilId = NextUId();
                parts.AppendLine($"          <Part Name=\"Coil\" UId=\"{coilId}\" />");

                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                wires.AppendLine($"            <Powerrail />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                int opWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{opWireId}\">");
                wires.AppendLine($"            <IdentCon UId=\"{targetAccId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{coilId}\" Name=\"operand\" />");
                wires.AppendLine($"          </Wire>");

                string baseXml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: Etiket ({labelName}:)");
                xml = InjectLabelIntoFlgNet(baseXml, labelName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildUnconditionalJumpLadder(string labelName, int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                if (string.IsNullOrWhiteSpace(labelName)) return false;

                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int lblAccId = NextUId();
                parts.AppendLine($"          <Access Scope=\"Label\" UId=\"{lblAccId}\">");
                parts.AppendLine($"            <Label Name=\"{System.Security.SecurityElement.Escape(labelName)}\" />");
                parts.AppendLine($"          </Access>");

                int jmpPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"Jump\" UId=\"{jmpPartId}\" />");

                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                wires.AppendLine($"            <Powerrail />");
                wires.AppendLine($"            <NameCon UId=\"{jmpPartId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                int lblWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{lblWireId}\">");
                wires.AppendLine($"            <IdentCon UId=\"{lblAccId}\" />");
                wires.AppendLine($"            <NameCon UId=\"{jmpPartId}\" Name=\"label\" />");
                wires.AppendLine($"          </Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: GOTO {labelName}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildUnconditionalReturnLadder(int networkIndex, out string xml)
        {
            xml = null;
            try
            {
                int cuId = NextUId();
                var parts = new StringBuilder();
                var wires = new StringBuilder();

                int retPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"Return\" UId=\"{retPartId}\" />");

                int powerWireId = NextUId();
                wires.AppendLine($"          <Wire UId=\"{powerWireId}\">");
                wires.AppendLine($"            <Powerrail />");
                wires.AppendLine($"            <NameCon UId=\"{retPartId}\" Name=\"in\" />");
                wires.AppendLine($"          </Wire>");

                xml = WrapCompileUnit(cuId, "LAD", parts.ToString(), wires.ToString(), $"Network {networkIndex}: RETURN");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string InjectLabelIntoFlgNet(string compileUnitXml, string labelName)
        {
            if (string.IsNullOrWhiteSpace(compileUnitXml) || string.IsNullOrWhiteSpace(labelName))
                return compileUnitXml;

            int lblDeclId = NextUId();
            string labelsBlock = $@"              <Labels>
                <LabelDeclaration UId=""{lblDeclId}"">
                  <Label Name=""{System.Security.SecurityElement.Escape(labelName)}"" />
                </LabelDeclaration>
              </Labels>
              <Parts>";

            return compileUnitXml.Replace("              <Parts>", labelsBlock);
        }

        private string GenerateSclCompileUnit(SclStatementInfo stmt, int networkIndex)
        {
            int cuId = NextUId();
            var tokens = TokenizeSclCode(stmt.RawSclCode);
            string networkSourceContent = $@"<StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/{_structuredTextVersion}"">
{tokens}
            </StructuredText>";

            var sb = new StringBuilder();
            sb.AppendLine($"      <SW.Blocks.CompileUnit ID=\"{cuId}\" CompositionName=\"CompileUnits\">");
            sb.AppendLine("        <AttributeList>");
            sb.AppendLine("          <NetworkSource>");
            sb.AppendLine($"            {networkSourceContent}");
            sb.AppendLine("          </NetworkSource>");
            sb.AppendLine("          <ProgrammingLanguage>SCL</ProgrammingLanguage>");
            sb.AppendLine("        </AttributeList>");
            sb.AppendLine("        <ObjectList>");
            sb.AppendLine($"          <MultilingualText ID=\"{NextUId()}\" CompositionName=\"Title\">");
            sb.AppendLine("            <ObjectList>");
            sb.AppendLine($"              <MultilingualTextItem ID=\"{NextUId()}\" CompositionName=\"Items\">");
            sb.AppendLine("                <AttributeList>");
            sb.AppendLine("                  <Culture>en-US</Culture>");
            sb.AppendLine($"                  <Text>Network {networkIndex}: (SCL Network)</Text>");
            sb.AppendLine("                </AttributeList>");
            sb.AppendLine("              </MultilingualTextItem>");
            sb.AppendLine("            </ObjectList>");
            sb.AppendLine("          </MultilingualText>");
            sb.AppendLine("        </ObjectList>");
            sb.AppendLine("      </SW.Blocks.CompileUnit>");

            return sb.ToString();
        }

        /// <summary>
        /// Converts raw SCL code into 100% compliant SimaticML StructuredText XML elements with unique UIds.
        /// Supports quoted and unquoted multi-part symbols ("DB"."Member"[#i]), array indexing, GOTO labels, and label declarations.
        /// </summary>
        /// <summary>
        /// Converts raw SCL code into 100% compliant SimaticML StructuredText XML elements with unique UIds.
        /// Supports quoted and unquoted multi-part symbols ("DB"."Member"[#i]), array indexing, GOTO labels, label declarations,
        /// and calls (FCs, FBs with instances, timers) with formal and nameless parameters.
        /// </summary>
        public string TokenizeSclCode(string code)
        {
            var sb = new StringBuilder();

            // 1. Check if the entire code is a standalone procedure call or assignment call: [target :=] Callee(args...);
            string noComments = StripComments(code).Trim();
            if (TryTokenizeCall(noComments, sb, "              "))
            {
                return sb.ToString();
            }

            // 2. Scan the code for embedded procedure calls (e.g. TON1.TON(...) or POKE_BOOL(...) inside IF/loops/etc.)
            // Uses .NET regex balancing group to match balanced parentheses in argument list
            var callRegex = new Regex(
                @"(?:(?<target>#?(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\[[^\]\r\n]+\])?(?:\.#?(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\[[^\]\r\n]+\])?)*)\s*:=\s*)?(?<callee>#?(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\.(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*))*)\s*\((?<args>(?>[^()']+|'[^']*'|\((?<DEPTH>)|\)(?<-DEPTH>))*(?(DEPTH)(?!)))\)\s*;",
                RegexOptions.CultureInvariant);

            var matches = callRegex.Matches(code);
            var validCallMatches = new List<Match>();
            foreach (Match cm in matches)
            {
                string callee = cm.Groups["callee"].Value.Trim();
                // Check not a language keyword like IF, WHILE, FOR, CASE, etc.
                if (SclKeywords.Contains(callee)) continue;
                validCallMatches.Add(cm);
            }

            if (validCallMatches.Count > 0)
            {
                int lastPos = 0;
                foreach (var cm in validCallMatches)
                {
                    if (cm.Index > lastPos)
                    {
                        string nonCallCode = code.Substring(lastPos, cm.Index - lastPos);
                        TokenizeRegularSclCode(nonCallCode, sb);
                    }

                    string target = cm.Groups["target"].Success ? cm.Groups["target"].Value.Trim() : null;
                    string callee = cm.Groups["callee"].Value.Trim();
                    string args = cm.Groups["args"].Value.Trim();

                    AppendTokenizedCall(sb, target, callee, args, "              ");
                    lastPos = cm.Index + cm.Length;
                }

                if (lastPos < code.Length)
                {
                    string remainingCode = code.Substring(lastPos);
                    TokenizeRegularSclCode(remainingCode, sb);
                }

                return sb.ToString();
            }

            // 3. If no embedded calls found, tokenize everything with regular tokenizer
            TokenizeRegularSclCode(code, sb);
            return sb.ToString();
        }

        private void TokenizeRegularSclCode(string code, StringBuilder sb)
        {
            if (string.IsNullOrEmpty(code)) return;

            var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // Matches comments, whitespace, numeric literals (radix 16#, 2#, 8# evaluated before decimal), string literals, compound variable/member symbols (including quoted parts and Turkish characters), operators (including :=, =>), and single identifiers/keywords
            var tokenRegex = new Regex(
                @"(?<comment>//.*$)|(?<ws>[ \t]+)|(?<num>\b16#[0-9a-fA-F]+(_[0-9a-fA-F]+)*\b|\b2#[01]+(_[01]+)*\b|\b8#[0-7]+(_[0-7]+)*\b|\b\d+(\.\d+)?([eE][+-]?\d+)?\b)|(?<str>'(?:''|[^'])*')|(?<ident>#?(?:\""[^\""]+\""|[a-zA-Z_ğüşıöçĞÜŞİÖÇ][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ]*)(?:\.(?:\""[^\""]+\""|[a-zA-Z_ğüşıöçĞÜŞİÖÇ][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ]*))*)|(?<op>:=|=>|<=|>=|<>|\.\.|\.|\+|-|\*|/|=|<|>|\(|\)|\[|\]|,|;|:|&)",
                RegexOptions.Compiled);

            int parenDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (line.Length > 0)
                    {
                        sb.AppendLine($"              <Blank Num=\"{line.Length}\" UId=\"{NextUId()}\" />");
                    }
                    else
                    {
                        sb.AppendLine($"              <NewLine Num=\"1\" UId=\"{NextUId()}\" />");
                    }
                    continue;
                }

                var matches = tokenRegex.Matches(line);
                string lastKeyword = null;
                bool firstNonWsTokenOnLine = true;

                for (int mi = 0; mi < matches.Count; mi++)
                {
                    Match m = matches[mi];
                    if (m.Groups["comment"].Success)
                    {
                        string cText = m.Groups["comment"].Value.Substring(2);
                        sb.AppendLine($"              <LineComment Inserted=\"false\" NoClosingBracket=\"false\" UId=\"{NextUId()}\">");
                        sb.AppendLine($"                <Text UId=\"{NextUId()}\">{System.Security.SecurityElement.Escape(cText)}</Text>");
                        sb.AppendLine($"              </LineComment>");
                    }
                    else if (m.Groups["ws"].Success)
                    {
                        int len = m.Groups["ws"].Value.Length;
                        sb.AppendLine($"              <Blank Num=\"{len}\" UId=\"{NextUId()}\" />");
                    }
                    else if (m.Groups["num"].Success)
                    {
                        firstNonWsTokenOnLine = false;
                        lastKeyword = null;
                        string numVal = m.Groups["num"].Value;
                        sb.AppendLine($"              <Access Scope=\"LiteralConstant\" UId=\"{NextUId()}\">");
                        sb.AppendLine($"                <Constant UId=\"{NextUId()}\">");
                        sb.AppendLine($"                  <ConstantValue UId=\"{NextUId()}\">{numVal}</ConstantValue>");
                        sb.AppendLine($"                </Constant>");
                        sb.AppendLine($"              </Access>");
                    }
                    else if (m.Groups["str"].Success)
                    {
                        firstNonWsTokenOnLine = false;
                        lastKeyword = null;
                        string strVal = m.Groups["str"].Value;
                        sb.AppendLine($"              <Access Scope=\"LiteralConstant\" UId=\"{NextUId()}\">");
                        sb.AppendLine($"                <Constant UId=\"{NextUId()}\">");
                        sb.AppendLine($"                  <ConstantValue UId=\"{NextUId()}\">{System.Security.SecurityElement.Escape(strVal)}</ConstantValue>");
                        sb.AppendLine($"                </Constant>");
                        sb.AppendLine($"              </Access>");
                    }
                    else if (m.Groups["op"].Success)
                    {
                        firstNonWsTokenOnLine = false;
                        lastKeyword = null;
                        string opVal = m.Groups["op"].Value;
                        if (opVal == "(") parenDepth++;
                        else if (opVal == ")") parenDepth = Math.Max(0, parenDepth - 1);

                        sb.AppendLine($"              <Token Text=\"{System.Security.SecurityElement.Escape(opVal)}\" UId=\"{NextUId()}\" />");
                    }
                    else if (m.Groups["ident"].Success)
                    {
                        string identVal = m.Groups["ident"].Value;

                        // Check if next non-whitespace token is ':' (Label declaration e.g. SON:)
                        bool isLabelDeclaration = false;
                        if (firstNonWsTokenOnLine && !identVal.StartsWith("#") && !identVal.Contains(".") && !identVal.Contains("["))
                        {
                            for (int nmi = mi + 1; nmi < matches.Count; nmi++)
                            {
                                if (matches[nmi].Groups["ws"].Success) continue;
                                if (matches[nmi].Groups["op"].Success && matches[nmi].Groups["op"].Value == ":")
                                    isLabelDeclaration = true;
                                break;
                            }
                        }

                        firstNonWsTokenOnLine = false;

                        // Check if keyword
                        if (SclKeywords.Contains(identVal))
                        {
                            lastKeyword = identVal.ToUpperInvariant();
                            sb.AppendLine($"              <Token Text=\"{lastKeyword}\" UId=\"{NextUId()}\" />");
                        }
                        // Check if boolean literal
                        else if (SclBooleans.Contains(identVal))
                        {
                            lastKeyword = null;
                            sb.AppendLine($"              <Access Scope=\"LiteralConstant\" UId=\"{NextUId()}\">");
                            sb.AppendLine($"                <Constant UId=\"{NextUId()}\">");
                            sb.AppendLine($"                  <ConstantValue UId=\"{NextUId()}\">{identVal.ToUpperInvariant()}</ConstantValue>");
                            sb.AppendLine($"                </Constant>");
                            sb.AppendLine($"              </Access>");
                        }
                        // Check if Label target after GOTO or Label declaration before ':'
                        else if (lastKeyword == "GOTO" || isLabelDeclaration)
                        {
                            lastKeyword = null;
                            string lblName = identVal.TrimStart('#').Trim('\"');
                            sb.AppendLine($"              <Access Scope=\"Label\" UId=\"{NextUId()}\">");
                            sb.AppendLine($"                <Label Name=\"{System.Security.SecurityElement.Escape(lblName)}\" UId=\"{NextUId()}\" />");
                            sb.AppendLine($"              </Access>");
                        }
                        // Variable or Member access (with support for quoted parts and array indices)
                        else
                        {
                            lastKeyword = null;
                            AppendStructuredTextSymbolAccess(sb, identVal, "              ");
                        }
                    }
                }

                if (i < lines.Length - 1)
                {
                    sb.AppendLine($"              <NewLine Num=\"1\" UId=\"{NextUId()}\" />");
                }
            }
        }

        private bool TryTokenizeCall(string code, StringBuilder sb, string indent)
        {
            string trimmed = code.Trim();
            // Match: [Target :=] Callee(args...);
            var m = Regex.Match(trimmed, @"^(?:(?<target>#?(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\[[^\]\r\n]+\])?(?:\.#?(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\[[^\]\r\n]+\])?)*)\s*:=\s*)?(?<callee>#?(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\.(?:\""[^\""\r\n]+\""|[a-zA-Z_][a-zA-Z0-9_]*))*)\s*\((?<args>(?>[^()']+|'[^']*'|\((?<DEPTH>)|\)(?<-DEPTH>))*(?(DEPTH)(?!)))\)\s*;?$", RegexOptions.CultureInvariant);
            if (!m.Success) return false;

            string callee = m.Groups["callee"].Value.Trim();
            // Don't treat keywords like IF, WHILE, etc. as function calls
            if (SclKeywords.Contains(callee)) return false;

            string target = m.Groups["target"].Success ? m.Groups["target"].Value.Trim() : null;
            string argsRaw = m.Groups["args"].Value.Trim();

            AppendTokenizedCall(sb, target, callee, argsRaw, indent);
            return true;
        }

        private void AppendTokenizedCall(StringBuilder sb, string target, string callee, string argsRaw, string indent)
        {
            // 1. If there is a target variable assignment (e.g. #ret := PEEK_BOOL(...))
            if (!string.IsNullOrEmpty(target))
            {
                AppendStructuredTextSymbolAccess(sb, target, indent);
                sb.AppendLine($"{indent}<Blank Num=\"1\" UId=\"{NextUId()}\" />");
                sb.AppendLine($"{indent}<Token Text=\":=\" UId=\"{NextUId()}\" />");
                sb.AppendLine($"{indent}<Blank Num=\"1\" UId=\"{NextUId()}\" />");
            }

            // 2. Determine BlockType, Name, and Instance
            string fnName;
            string blockType;
            string instStr = null;

            bool hasInArg = Regex.IsMatch(argsRaw, @"\bIN\s*(?::=|=>)", RegexOptions.IgnoreCase);
            bool hasPtArg = Regex.IsMatch(argsRaw, @"\bPT\s*(?::=|=>)", RegexOptions.IgnoreCase);
            bool hasRArg = Regex.IsMatch(argsRaw, @"\bR\s*(?::=|=>)", RegexOptions.IgnoreCase);

            int lastDot = callee.LastIndexOf('.');
            string afterLastDot = lastDot > 0 ? callee.Substring(lastDot + 1).Trim().Trim('\"', '#').ToUpperInvariant() : "";

            if (afterLastDot == "TON" || afterLastDot == "TOF" || afterLastDot == "TP" || afterLastDot == "TONR")
            {
                instStr = callee.Substring(0, lastDot).Trim();
                fnName = afterLastDot;
                blockType = "FB";
            }
            else if (hasInArg && hasPtArg)
            {
                // Timer instance call like "TIMERDB"."T_CiftElFark"(IN := ..., PT := ...)
                instStr = callee;
                if (hasRArg || callee.IndexOf("TONR", StringComparison.OrdinalIgnoreCase) >= 0)
                    fnName = "TONR";
                else if (callee.IndexOf("TOF", StringComparison.OrdinalIgnoreCase) >= 0)
                    fnName = "TOF";
                else if (callee.IndexOf("TP", StringComparison.OrdinalIgnoreCase) >= 0)
                    fnName = "TP";
                else
                    fnName = "TON";
                blockType = "FB";
            }
            else if (lastDot > 0)
            {
                instStr = callee.Substring(0, lastDot).Trim();
                fnName = callee.Substring(lastDot + 1).Trim();
                blockType = "FB";
            }
            else
            {
                fnName = callee.TrimStart('#').Trim('\"');
                if (fnName.Equals("TON", StringComparison.OrdinalIgnoreCase) ||
                    fnName.Equals("TOF", StringComparison.OrdinalIgnoreCase) ||
                    fnName.Equals("TP", StringComparison.OrdinalIgnoreCase) ||
                    fnName.Equals("TONR", StringComparison.OrdinalIgnoreCase))
                {
                    blockType = "FB";
                }
                else
                {
                    blockType = "FC";
                }
            }

            // CRITICAL: Strip any double-quotes, quotes or hash from fnName so CallInfo Name="..." is never invalid
            fnName = fnName.Trim().Trim('\"', '#', ' ').Replace("\"", "").Replace("&quot;", "");
            if (string.IsNullOrEmpty(fnName)) fnName = "BlockCall";

            // 3. <Access Scope="Call"> <Instruction Name="..." or <CallInfo Name="..." BlockType="...">
            bool isInstruction = SclBuiltInInstructions.Contains(fnName) ||
                                 fnName.IndexOf("_TO_", StringComparison.OrdinalIgnoreCase) > 0;
            int accId = NextUId();
            int callInfoId = NextUId();
            sb.AppendLine($"{indent}<Access Scope=\"Call\" UId=\"{accId}\">");
            if (isInstruction)
            {
                sb.AppendLine($"{indent}  <Instruction Name=\"{System.Security.SecurityElement.Escape(fnName)}\" UId=\"{callInfoId}\">");
            }
            else
            {
                sb.AppendLine($"{indent}  <CallInfo Name=\"{System.Security.SecurityElement.Escape(fnName)}\" BlockType=\"{blockType}\" UId=\"{callInfoId}\">");
            }

            // Instance element for FB calls
            if (!string.IsNullOrEmpty(instStr))
            {
                string instScope = instStr.StartsWith("#") ? "LocalVariable" : "GlobalVariable";
                string cleanInst = instStr.TrimStart('#');
                var compParts = cleanInst.Split('.');

                sb.AppendLine($"{indent}    <Instance Scope=\"{instScope}\" UId=\"{NextUId()}\">");
                for (int ci = 0; ci < compParts.Length; ci++)
                {
                    string partName = compParts[ci].Trim('\"').Trim();
                    sb.AppendLine($"{indent}      <Component Name=\"{System.Security.SecurityElement.Escape(partName)}\" UId=\"{NextUId()}\" />");
                    if (ci < compParts.Length - 1)
                    {
                        sb.AppendLine($"{indent}      <Token Text=\".\" UId=\"{NextUId()}\" />");
                    }
                }
                sb.AppendLine($"{indent}    </Instance>");
            }

            sb.AppendLine($"{indent}    <Token Text=\"(\" UId=\"{NextUId()}\" />");

            var argList = SplitCallArguments(argsRaw);
            for (int ai = 0; ai < argList.Count; ai++)
            {
                string arg = argList[ai].Trim();

                // Strip trailing line comments if present
                int cIdx = arg.IndexOf("//");
                if (cIdx >= 0) arg = arg.Substring(0, cIdx).Trim();

                var pMatch = Regex.Match(arg, @"^(?<pname>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<pop>:=|=>)\s*(?<pval>[\s\S]+)$");
                if (pMatch.Success)
                {
                    string pName = pMatch.Groups["pname"].Value.Trim();
                    string pOp = pMatch.Groups["pop"].Value.Trim();
                    string pVal = pMatch.Groups["pval"].Value.Trim();

                    sb.AppendLine($"{indent}    <Parameter Name=\"{System.Security.SecurityElement.Escape(pName)}\" UId=\"{NextUId()}\">");
                    sb.AppendLine($"{indent}      <Blank Num=\"1\" UId=\"{NextUId()}\" />");
                    sb.AppendLine($"{indent}      <Token Text=\"{pOp}\" UId=\"{NextUId()}\" />");
                    sb.AppendLine($"{indent}      <Blank Num=\"1\" UId=\"{NextUId()}\" />");
                    AppendStructuredTextValue(sb, pVal, indent + "      ");
                    sb.AppendLine($"{indent}    </Parameter>");
                }
                else
                {
                    sb.AppendLine($"{indent}    <NamelessParameter UId=\"{NextUId()}\">");
                    AppendStructuredTextValue(sb, arg, indent + "      ");
                    sb.AppendLine($"{indent}    </NamelessParameter>");
                }

                if (ai < argList.Count - 1)
                {
                    sb.AppendLine($"{indent}    <Token Text=\",\" UId=\"{NextUId()}\" />");
                    sb.AppendLine($"{indent}    <NewLine Num=\"1\" UId=\"{NextUId()}\" />");
                    sb.AppendLine($"{indent}    <Blank Num=\"18\" UId=\"{NextUId()}\" />");
                }
            }

            sb.AppendLine($"{indent}    <Token Text=\")\" UId=\"{NextUId()}\" />");
            if (isInstruction)
            {
                sb.AppendLine($"{indent}  </Instruction>");
            }
            else
            {
                sb.AppendLine($"{indent}  </CallInfo>");
            }
            sb.AppendLine($"{indent}</Access>");
            sb.AppendLine($"{indent}<Token Text=\";\" UId=\"{NextUId()}\" />");
        }

        private static List<string> SplitCallArguments(string argsRaw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(argsRaw)) return list;

            var cur = new StringBuilder();
            int pDepth = 0;
            int bDepth = 0;
            bool inStr = false;

            for (int i = 0; i < argsRaw.Length; i++)
            {
                char c = argsRaw[i];
                if (c == '\'')
                {
                    inStr = !inStr;
                    cur.Append(c);
                }
                else if (inStr)
                {
                    cur.Append(c);
                }
                else if (c == '(')
                {
                    pDepth++;
                    cur.Append(c);
                }
                else if (c == ')')
                {
                    pDepth = Math.Max(0, pDepth - 1);
                    cur.Append(c);
                }
                else if (c == '[')
                {
                    bDepth++;
                    cur.Append(c);
                }
                else if (c == ']')
                {
                    bDepth = Math.Max(0, bDepth - 1);
                    cur.Append(c);
                }
                else if (c == ',' && pDepth == 0 && bDepth == 0)
                {
                    string item = cur.ToString().Trim();
                    if (!string.IsNullOrEmpty(item)) list.Add(item);
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }

            string last = cur.ToString().Trim();
            if (!string.IsNullOrEmpty(last)) list.Add(last);
            return list;
        }

        private void AppendStructuredTextValue(StringBuilder sb, string val, string indent)
        {
            string trimmed = val.Trim();

            // Radix literals: 16#84, 2#01, 8#77, or numeric literals
            if (Regex.IsMatch(trimmed, @"^(?:16#[0-9a-fA-F]+(_[0-9a-fA-F]+)*|2#[01]+(_[01]+)*|8#[0-7]+(_[0-7]+)*|\b-?\d+(\.\d+)?([eE][+-]?\d+)?\b)$"))
            {
                sb.AppendLine($"{indent}<Access Scope=\"LiteralConstant\" UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}  <Constant UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}    <ConstantValue UId=\"{NextUId()}\">{trimmed}</ConstantValue>");
                sb.AppendLine($"{indent}  </Constant>");
                sb.AppendLine($"{indent}</Access>");
                return;
            }

            // Typed constants: T#10s, TIME#500ms, TOD#12:00:00, DATE#2024-01-01, etc.
            if (Regex.IsMatch(trimmed, @"^(?:T|TIME|LT|LTIME|TOD|LTOD|DATE|DT|LDT|DTL|BYTE|WORD|DWORD|LWORD|SINT|INT|DINT|LINT|USINT|UINT|UDINT|ULINT|REAL|LREAL)#", RegexOptions.IgnoreCase))
            {
                sb.AppendLine($"{indent}<Access Scope=\"TypedConstant\" UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}  <Constant UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}    <ConstantValue UId=\"{NextUId()}\">{System.Security.SecurityElement.Escape(trimmed)}</ConstantValue>");
                sb.AppendLine($"{indent}  </Constant>");
                sb.AppendLine($"{indent}</Access>");
                return;
            }

            // String literals
            if (trimmed.StartsWith("'") && trimmed.EndsWith("'"))
            {
                sb.AppendLine($"{indent}<Access Scope=\"LiteralConstant\" UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}  <Constant UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}    <ConstantValue UId=\"{NextUId()}\">{System.Security.SecurityElement.Escape(trimmed)}</ConstantValue>");
                sb.AppendLine($"{indent}  </Constant>");
                sb.AppendLine($"{indent}</Access>");
                return;
            }

            // Booleans
            if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{indent}<Access Scope=\"LiteralConstant\" UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}  <Constant UId=\"{NextUId()}\">");
                sb.AppendLine($"{indent}    <ConstantValue UId=\"{NextUId()}\">{trimmed.ToUpperInvariant()}</ConstantValue>");
                sb.AppendLine($"{indent}  </Constant>");
                sb.AppendLine($"{indent}</Access>");
                return;
            }

            // Variable / Member / Symbol access (e.g. #j, #i, "DB".Tag, Tag)
            if (IsValidIdentifier(trimmed) || trimmed.StartsWith("#") || trimmed.Contains(".") || trimmed.Contains("["))
            {
                AppendStructuredTextSymbolAccess(sb, trimmed, indent);
                return;
            }

            // Fallback: tokenize general expression
            string tokensXml = TokenizeSclCode(trimmed);
            sb.Append(tokensXml);
        }

        private void AppendStructuredTextSymbolAccess(StringBuilder sb, string identVal, string indent)
        {
            if (string.IsNullOrWhiteSpace(identVal)) return;
            string trimmed = identVal.Trim();

            // If this contains array bracket(s), e.g. "DB".arr[#idx] or arr[1, 2]
            int bracketStart = trimmed.IndexOf('[');
            if (bracketStart >= 0)
            {
                int bracketEnd = trimmed.LastIndexOf(']');
                string baseVar = trimmed.Substring(0, bracketStart).Trim();
                string indexContent = bracketEnd > bracketStart ? trimmed.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim() : "";

                // Emit base variable symbol access
                AppendStructuredTextSymbolAccess(sb, baseVar, indent);

                // Emit [
                sb.AppendLine($"{indent}<Token Text=\"[\" UId=\"{NextUId()}\" />");

                // Emit index tokens
                if (!string.IsNullOrWhiteSpace(indexContent))
                {
                    TokenizeRegularSclCode(indexContent, sb);
                }

                // Emit ]
                sb.AppendLine($"{indent}<Token Text=\"]\" UId=\"{NextUId()}\" />");
                return;
            }

            string scope = trimmed.StartsWith("#") ? "LocalVariable" : "GlobalVariable";
            string clean = trimmed.StartsWith("#") ? trimmed.Substring(1).Trim() : trimmed;

            var compMatches = Regex.Matches(clean, @"(?:""(?<name>[^""]+)""|(?<name>[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]+))");
            int accId = NextUId();
            int symId = NextUId();

            sb.AppendLine($"{indent}<Access Scope=\"{scope}\" UId=\"{accId}\">");
            sb.AppendLine($"{indent}  <Symbol UId=\"{symId}\">");

            int compIdx = 0;

            // If this is a single-component undefined global tag mapped to LADDERDB, prefix with LADDERDB
            if (scope == "GlobalVariable" && UseLadderDbForUndefinedTags && compMatches.Count == 1)
            {
                string firstComp = compMatches[0].Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(firstComp) &&
                    !string.Equals(firstComp, LadderDbName, StringComparison.OrdinalIgnoreCase) &&
                    LastGeneratedLadderDbTags.ContainsKey(firstComp))
                {
                    sb.AppendLine($"{indent}    <Component Name=\"{System.Security.SecurityElement.Escape(LadderDbName)}\" UId=\"{NextUId()}\" />");
                    compIdx++;
                }
            }

            foreach (Match cm in compMatches)
            {
                if (string.IsNullOrWhiteSpace(cm.Value)) continue;
                string compName = cm.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(compName)) continue;

                if (compIdx > 0)
                {
                    sb.AppendLine($"{indent}    <Token Text=\".\" UId=\"{NextUId()}\" />");
                }
                compIdx++;

                sb.AppendLine($"{indent}    <Component Name=\"{System.Security.SecurityElement.Escape(compName)}\" UId=\"{NextUId()}\" />");
            }

            sb.AppendLine($"{indent}  </Symbol>");
            sb.AppendLine($"{indent}</Access>");
        }

        private string WrapCompileUnit(int cuId, string lang, string parts, string wires, string title)
        {
            int cmtId1 = NextUId();
            int cmtId2 = NextUId();
            int titleId1 = NextUId();
            int titleId2 = NextUId();

            string effectiveTitle = title;
            if (!string.IsNullOrWhiteSpace(_currentCustomTitle) &&
                !Regex.IsMatch(_currentCustomTitle.Trim(), @"^Network\s+\d+\s*$", RegexOptions.IgnoreCase))
            {
                effectiveTitle = _currentCustomTitle.Trim();
            }

            return $@"      <SW.Blocks.CompileUnit ID=""{cuId}"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/{_flgNetVersion}"">
              <Parts>
{parts.TrimEnd()}
              </Parts>
              <Wires>
{wires.TrimEnd()}
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>{lang}</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""{cmtId1}"" CompositionName=""Comment"">
            <ObjectList>
              <MultilingualTextItem ID=""{cmtId2}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{titleId1}"" CompositionName=""Title"">
            <ObjectList>
              <MultilingualTextItem ID=""{titleId2}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text>{System.Security.SecurityElement.Escape(effectiveTitle)}</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>";
        }

        private string FormatAccessXml(string varName, int accessId, string expectedType = null)
        {
            string clean = StripOuterParentheses(varName).Trim();

            // 1. Check if boolean constant
            if (clean.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || clean.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                return $@"          <Access Scope=""LiteralConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantType>Bool</ConstantType>
              <ConstantValue>{clean.ToUpperInvariant()}</ConstantValue>
            </Constant>
          </Access>";
            }

            if (string.Equals(expectedType, "Bool", StringComparison.OrdinalIgnoreCase))
            {
                if (clean == "1")
                {
                    return $@"          <Access Scope=""LiteralConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantType>Bool</ConstantType>
              <ConstantValue>TRUE</ConstantValue>
            </Constant>
          </Access>";
                }
                if (clean == "0")
                {
                    return $@"          <Access Scope=""LiteralConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantType>Bool</ConstantType>
              <ConstantValue>FALSE</ConstantValue>
            </Constant>
          </Access>";
                }
            }

            // 2. Check if numeric constant (integer or real)
            if (double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                string constType = !string.IsNullOrEmpty(expectedType) ? expectedType : (clean.Contains(".") ? "Real" : "Int");
                if (string.Equals(constType, "Time", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(constType, "LTime", StringComparison.OrdinalIgnoreCase))
                {
                    string timeVal = clean == "0" ? "T#0ms" : $"T#{clean}ms";
                    return $@"          <Access Scope=""TypedConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantValue>{timeVal}</ConstantValue>
            </Constant>
          </Access>";
                }

                return $@"          <Access Scope=""LiteralConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantType>{constType}</ConstantType>
              <ConstantValue>{clean}</ConstantValue>
            </Constant>
          </Access>";
            }

            // 3. Char or String literal e.g. '0' or 'TEXT'
            if (clean.StartsWith("'") && clean.EndsWith("'") && clean.Length >= 2)
            {
                string strVal = clean.Substring(1, clean.Length - 2);
                string litType = !string.IsNullOrEmpty(expectedType) ? expectedType : (strVal.Length <= 1 ? "Char" : "String");
                return $@"          <Access Scope=""LiteralConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantType>{litType}</ConstantType>
              <ConstantValue>'{System.Security.SecurityElement.Escape(strVal)}'</ConstantValue>
            </Constant>
          </Access>";
            }

            // 3-B. Typed or Radix constant e.g. DINT#50000, REAL#3.5, INT#10, WSTRING#',', 16#FF, 2#1010, T#5s (Slides 20 & 29)
            if (TryParseTypedOrRadixConstant(clean, out string typedConstType, out string typedConstVal, out string typedScope))
            {
                string finalType = !string.IsNullOrEmpty(expectedType) ? expectedType : typedConstType;
                if (string.Equals(typedScope, "TypedConstant", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(finalType, "Time", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(finalType, "LTime", StringComparison.OrdinalIgnoreCase))
                {
                    return $@"          <Access Scope=""TypedConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantValue>{System.Security.SecurityElement.Escape(typedConstVal)}</ConstantValue>
            </Constant>
          </Access>";
                }

                return $@"          <Access Scope=""LiteralConstant"" UId=""{accessId}"">
            <Constant>
              <ConstantType>{finalType}</ConstantType>
              <ConstantValue>{System.Security.SecurityElement.Escape(typedConstVal)}</ConstantValue>
            </Constant>
          </Access>";
            }

            // 4. Variable access (Global or Local)
            string scope = clean.StartsWith("#") ? "LocalVariable" : "GlobalVariable";
            string tagBody = clean.StartsWith("#") ? clean.Substring(1).Trim() : clean;

            // Extract components using regex (matches "quoted name" or unquoted identifier, with optional array index)
            var compMatches = Regex.Matches(tagBody, @"(?:""(?<name>[^""]+)""|(?<name>[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]+))(?:\s*\[\s*(?<idx>[^\]]+)\s*\])?");
            var sb = new StringBuilder();
            sb.AppendLine($@"          <Access Scope=""{scope}"" UId=""{accessId}"">");
            sb.AppendLine($@"            <Symbol>");

            // If this is a single-component undefined global tag mapped to LADDERDB, prefix with LADDERDB
            if (scope == "GlobalVariable" && UseLadderDbForUndefinedTags && compMatches.Count == 1)
            {
                string firstComp = compMatches[0].Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(firstComp) &&
                    !string.Equals(firstComp, LadderDbName, StringComparison.OrdinalIgnoreCase) &&
                    LastGeneratedLadderDbTags.ContainsKey(firstComp))
                {
                    sb.AppendLine($@"              <Component Name=""{System.Security.SecurityElement.Escape(LadderDbName)}"" />");
                }
            }

            foreach (Match m in compMatches)
            {
                if (string.IsNullOrWhiteSpace(m.Value)) continue;
                string compName = m.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(compName))
                {
                    // Map FC Return Value assigned via #<BlockName> to #<ReturnMemberName> (Ret_Val) in LAD (Slides 7 & 8)
                    if (scope == "LocalVariable" && compMatches.Count == 1 && !m.Groups["idx"].Success)
                    {
                        if (!string.IsNullOrEmpty(_blockReturnAlias) &&
                            !string.IsNullOrEmpty(_returnMemberName) &&
                            string.Equals(compName, _blockReturnAlias, StringComparison.OrdinalIgnoreCase) &&
                            !_localInterfaceNames.Contains(compName))
                        {
                            compName = _returnMemberName;
                        }
                        else if (!_localInterfaceNames.Contains(compName) && !_declaredTempVars.ContainsKey(compName))
                        {
                            // Ensure any undeclared local variable (such as a loop counter #X or #Y) is declared in Temp
                            string inferredLocalType = _interfaceVarTypes.TryGetValue(compName, out string lt) ? lt : "DInt";
                            _declaredTempVars[compName] = inferredLocalType;
                        }
                    }

                    if (m.Groups["idx"].Success)
                    {
                        string idxVal = m.Groups["idx"].Value.Trim();
                        sb.AppendLine($@"              <Component Name=""{System.Security.SecurityElement.Escape(compName)}"" AccessModifier=""Array"">");

                        var dims = SplitCommaSeparatedDimensions(idxVal);
                        foreach (var rawDim in dims)
                        {
                            string dim = rawDim.Trim();
                            if (long.TryParse(dim, out _) || int.TryParse(dim, out _))
                            {
                                sb.AppendLine($@"                <Access Scope=""LiteralConstant"">");
                                sb.AppendLine($@"                  <Constant>");
                                sb.AppendLine($@"                    <ConstantType>DInt</ConstantType>");
                                sb.AppendLine($@"                    <ConstantValue>{dim}</ConstantValue>");
                                sb.AppendLine($@"                  </Constant>");
                                sb.AppendLine($@"                </Access>");
                            }
                            else if (TryParseTypedOrRadixConstant(dim, out string tConstType, out string tConstVal, out string tConstScope))
                            {
                                sb.AppendLine($@"                <Access Scope=""{tConstScope}"">");
                                sb.AppendLine($@"                  <Constant>");
                                if (!string.IsNullOrEmpty(tConstType))
                                {
                                    sb.AppendLine($@"                    <ConstantType>{tConstType}</ConstantType>");
                                }
                                sb.AppendLine($@"                    <ConstantValue>{System.Security.SecurityElement.Escape(tConstVal)}</ConstantValue>");
                                sb.AppendLine($@"                  </Constant>");
                                sb.AppendLine($@"                </Access>");
                            }
                            else
                            {
                                bool isLocal = dim.StartsWith("#");
                                string idxScope = isLocal ? "LocalVariable" : "GlobalVariable";
                                string dimBody = isLocal ? dim.Substring(1).Trim() : dim.Trim();

                                var subComps = Regex.Matches(dimBody, @"(?:""(?<cn>[^""]+)""|(?<cn>[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]+))");
                                string firstComp = subComps.Count > 0 ? subComps[0].Groups["cn"].Value : dimBody.Trim('\"');

                                if (isLocal && !_localInterfaceNames.Contains(firstComp) && !_declaredTempVars.ContainsKey(firstComp))
                                {
                                    string idxType = _interfaceVarTypes.TryGetValue(firstComp, out string it) ? it : "DInt";
                                    _declaredTempVars[firstComp] = idxType;
                                }

                                sb.AppendLine($@"                <Access Scope=""{idxScope}"">");
                                sb.AppendLine($@"                  <Symbol>");
                                if (!isLocal && UseLadderDbForUndefinedTags && subComps.Count == 1 &&
                                    !string.Equals(firstComp, LadderDbName, StringComparison.OrdinalIgnoreCase) &&
                                    LastGeneratedLadderDbTags.ContainsKey(firstComp))
                                {
                                    sb.AppendLine($@"                    <Component Name=""{System.Security.SecurityElement.Escape(LadderDbName)}"" />");
                                }

                                if (subComps.Count > 0)
                                {
                                    foreach (Match sc in subComps)
                                    {
                                        if (sc.Success && !string.IsNullOrWhiteSpace(sc.Groups["cn"].Value))
                                        {
                                            sb.AppendLine($@"                    <Component Name=""{System.Security.SecurityElement.Escape(sc.Groups["cn"].Value)}"" />");
                                        }
                                    }
                                }
                                else if (!string.IsNullOrWhiteSpace(dimBody))
                                {
                                    sb.AppendLine($@"                    <Component Name=""{System.Security.SecurityElement.Escape(dimBody.Trim('\"'))}"" />");
                                }

                                sb.AppendLine($@"                  </Symbol>");
                                sb.AppendLine($@"                </Access>");
                            }
                        }
                        sb.AppendLine($@"              </Component>");
                    }
                    else
                    {
                        sb.AppendLine($@"              <Component Name=""{System.Security.SecurityElement.Escape(compName)}"" />");
                    }
                }
            }
            sb.AppendLine($@"            </Symbol>");
            sb.AppendLine($@"          </Access>");
            return sb.ToString();
        }

        private void ExtractInterfaceVarTypes(ParsedSclBlock sclBlock)
        {
            if (sclBlock == null) return;

            // 1. Load external variable types from PLC Tag Tables and Global DBs
            if (sclBlock.ExternalVarTypes != null)
            {
                foreach (var kvp in sclBlock.ExternalVarTypes)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                        continue;

                    TryRecordFixedArrayBounds(kvp.Key.Trim(), kvp.Value);

                    string norm = NormalizePlcDatatype(kvp.Value);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        _interfaceVarTypes[kvp.Key.Trim()] = norm;
                    }
                }
            }

            // 2. Load local block <Interface> members (including nested Struct members and Return section)
            try
            {
                XElement ifaceElem = sclBlock.OriginalAttributeList?.Element("Interface");
                if (ifaceElem == null && !string.IsNullOrWhiteSpace(sclBlock.InterfaceXml))
                {
                    ifaceElem = XElement.Parse(sclBlock.InterfaceXml);
                }
                if (ifaceElem != null)
                {
                    ExtractLocalInterfaceMembersRecursive(ifaceElem, "");

                    // Check Return section for FC return member (usually Ret_Val) so #<BlockName> maps to it (Slides 7 & 8)
                    var returnSection = ifaceElem.Descendants()
                        .FirstOrDefault(d => d.Name.LocalName == "Section" &&
                                             string.Equals((string)d.Attribute("Name"), "Return", StringComparison.OrdinalIgnoreCase));
                    var retMember = returnSection?.Elements().FirstOrDefault(e => e.Name.LocalName == "Member");
                    if (retMember != null)
                    {
                        string retName = retMember.Attribute("Name")?.Value;
                        string retDt = NormalizePlcDatatype(retMember.Attribute("Datatype")?.Value);
                        if (!string.IsNullOrWhiteSpace(retName) && !string.Equals(retDt, "Void", StringComparison.OrdinalIgnoreCase))
                        {
                            _returnMemberName = retName.Trim();
                            if (!string.IsNullOrWhiteSpace(sclBlock.BlockName))
                            {
                                _blockReturnAlias = sclBlock.BlockName.Trim();
                                if (!string.IsNullOrEmpty(retDt) && !_interfaceVarTypes.ContainsKey(_blockReturnAlias))
                                {
                                    _interfaceVarTypes[_blockReturnAlias] = retDt;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Infer Bool type from explicit TRUE / FALSE usage in statements if not already in dictionary
            if (sclBlock.Statements != null)
            {
                foreach (var st in sclBlock.Statements)
                {
                    if (string.IsNullOrWhiteSpace(st?.RawSclCode)) continue;
                    var boolAssignMatches = Regex.Matches(st.RawSclCode, @"(#?(?:""[^""]+""|[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]+)(?:\s*\[\s*[^\]]+\s*\])?(?:\.(?:""[^""]+""|[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]+)(?:\s*\[\s*[^\]]+\s*\])?)*)\s*(?::=|=|<>)\s*(?:TRUE|FALSE|BOOL#TRUE|BOOL#FALSE|BOOL#1|BOOL#0)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    foreach (Match bm in boolAssignMatches)
                    {
                        string key = NormalizeVarLookupKey(bm.Groups[1].Value);
                        if (!string.IsNullOrEmpty(key) && !_interfaceVarTypes.ContainsKey(key))
                        {
                            _interfaceVarTypes[key] = "Bool";
                        }
                    }
                }
            }
        }

        private void TryRecordFixedArrayBounds(string key, string rawDatatype)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(rawDatatype))
                return;
            var m = Regex.Match(rawDatatype, @"Array\s*\[\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success)
            {
                _arrayBounds[key] = Tuple.Create(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            }
        }

        private void ExtractLocalInterfaceMembersRecursive(XElement container, string prefix)
        {
            if (container == null) return;
            foreach (var elem in container.Elements())
            {
                if (elem.Name.LocalName == "Member")
                {
                    string name = elem.Attribute("Name")?.Value;
                    string dt = elem.Attribute("Datatype")?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        if (string.IsNullOrEmpty(prefix))
                        {
                            _localInterfaceNames.Add(name.Trim());
                        }
                        string fullPath = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
                        TryRecordFixedArrayBounds(fullPath, dt);
                        TryRecordFixedArrayBounds(name, dt);
                        string normType = NormalizePlcDatatype(dt);
                        if (!string.IsNullOrEmpty(normType))
                        {
                            _interfaceVarTypes[fullPath] = normType;
                            if (!_interfaceVarTypes.ContainsKey(name))
                                _interfaceVarTypes[name] = normType;
                        }
                        ExtractLocalInterfaceMembersRecursive(elem, fullPath);
                    }
                }
                else
                {
                    ExtractLocalInterfaceMembersRecursive(elem, prefix);
                }
            }
        }

        private string AllocateTempCalcVar(string srcType)
        {
            string normType = NormalizePlcDatatype(srcType) ?? "Real";
            while (true)
            {
                string candidate = $"tempCalc{++_tempVarIndex}";
                if (_localInterfaceNames.Contains(candidate) || _declaredTempVars.ContainsKey(candidate))
                {
                    continue;
                }

                _declaredTempVars[candidate] = normType;
                _interfaceVarTypes[candidate] = normType;
                _interfaceVarTypes[$"#{candidate}"] = normType;
                return $"#{candidate}";
            }
        }

        private void ScanAndInferUndefinedGlobalTags(ParsedSclBlock sclBlock)
        {
            if (sclBlock?.Statements == null) return;

            var undefinedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arrayMaxIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var tokenRegex = new Regex(
                @"(?<comment>//.*$)|(?<str>'(?:''|[^'])*')|(?<ident>#?(?:\""[^\""]+\""|[a-zA-Z_ğüşıöçĞÜŞİÖÇ][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]*)(?:\s*\[\s*[^\]]+\s*\])?(?:\.(?:\""[^\""]+\""|[a-zA-Z_ğüşıöçĞÜŞİÖÇ][a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]*)(?:\s*\[\s*[^\]]+\s*\])?)*)|(?<op>:=|<=|>=|<>|:)",
                RegexOptions.Multiline);

            foreach (var stmt in sclBlock.Statements)
            {
                if (string.IsNullOrWhiteSpace(stmt?.RawSclCode)) continue;
                string code = StripComments(stmt.RawSclCode);
                var matches = tokenRegex.Matches(code);

                string prevIdent = null;
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    if (!m.Groups["ident"].Success) continue;

                    string rawIdent = m.Groups["ident"].Value.Trim();
                    if (string.IsNullOrWhiteSpace(rawIdent)) continue;

                    // Skip if local variable (#...)
                    if (rawIdent.StartsWith("#"))
                    {
                        prevIdent = rawIdent;
                        continue;
                    }

                    // Check if label declaration (immediately followed by ':' and not ':=')
                    if (i + 1 < matches.Count && matches[i + 1].Groups["op"].Success && matches[i + 1].Groups["op"].Value == ":")
                    {
                        prevIdent = rawIdent;
                        continue;
                    }

                    // Skip if preceded by GOTO
                    if (string.Equals(prevIdent, "GOTO", StringComparison.OrdinalIgnoreCase))
                    {
                        prevIdent = rawIdent;
                        continue;
                    }

                    prevIdent = rawIdent;

                    // Parse components
                    var compMatches = Regex.Matches(rawIdent, @"(?:""(?<name>[^""]+)""|(?<name>[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]+))(?:\s*\[\s*(?<idx>[^\]]+)\s*\])?");
                    if (compMatches.Count == 0) continue;

                    if (compMatches.Count == 2 &&
                        string.Equals(compMatches[0].Groups["name"].Value, LadderDbName, StringComparison.OrdinalIgnoreCase))
                    {
                        string memberName = compMatches[1].Groups["name"].Value;
                        if (!string.IsNullOrWhiteSpace(memberName))
                        {
                            undefinedTags.Add(memberName);
                        }
                        continue;
                    }

                    // Only standalone (1-component) global tags can be undefined tags
                    if (compMatches.Count != 1) continue;

                    string tagName = compMatches[0].Groups["name"].Value.Trim();
                    if (string.IsNullOrWhiteSpace(tagName)) continue;

                    // Skip keywords, booleans, jump labels, numeric/hex/time constants, direct PLC addresses
                    if (SclKeywords.Contains(tagName) || SclBooleans.Contains(tagName) || _jumpTargetLabels.Contains(tagName))
                        continue;
                    if (double.TryParse(tagName, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                        continue;
                    if (Regex.IsMatch(tagName, @"^(?:16#|2#|8#|T#|TIME#|D#|TOD#|%[IQMDB])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        continue;
                    if (_localInterfaceNames.Contains(tagName))
                        continue;
                    // Skip function/procedure calls: e.g. POKE_BOOL(...)
                    if (Regex.IsMatch(code, $@"\b{Regex.Escape(tagName)}\s*\("))
                        continue;
                    // Skip formal parameter assignments inside call parentheses: e.g. (..., area := ...)
                    if (Regex.IsMatch(code, $@"\(\s*([^()]*?\b)?{Regex.Escape(tagName)}\s*(?::=|=>)"))
                        continue;

                    bool isUndefined = tagName.StartsWith("tempCalc", StringComparison.OrdinalIgnoreCase) ||
                                       (sclBlock.HasScannedPlcSymbols &&
                                        sclBlock.DefinedGlobalSymbols != null &&
                                        !sclBlock.DefinedGlobalSymbols.Contains(tagName));

                    if (isUndefined)
                    {
                        undefinedTags.Add(tagName);
                        if (compMatches[0].Groups["idx"].Success)
                        {
                            string idxStr = compMatches[0].Groups["idx"].Value.Trim();
                            int idxVal = 10;
                            if (int.TryParse(idxStr, out int parsedIdx) && parsedIdx >= 0)
                                idxVal = Math.Max(parsedIdx, 1);

                            if (!arrayMaxIndices.TryGetValue(tagName, out int curMax) || idxVal > curMax)
                                arrayMaxIndices[tagName] = idxVal;
                        }
                    }
                }
            }

            foreach (string tagName in undefinedTags)
            {
                string inferredBaseType = InferUndefinedTagType(tagName, sclBlock);
                string finalDatatype = inferredBaseType;

                if (arrayMaxIndices.TryGetValue(tagName, out int maxIdx))
                {
                    finalDatatype = $"Array[0..{maxIdx}] of {inferredBaseType}";
                }

                LastGeneratedLadderDbTags[tagName] = finalDatatype;
                _interfaceVarTypes[tagName] = inferredBaseType;
                _interfaceVarTypes[$"{LadderDbName}.{tagName}"] = inferredBaseType;
            }
        }

        private string InferUndefinedTagType(string tagName, ParsedSclBlock sclBlock)
        {
            if (_existingLadderDbTypes.TryGetValue(tagName, out string existingDbType) && !string.IsNullOrWhiteSpace(existingDbType))
            {
                return NormalizePlcDatatype(existingDbType) ?? existingDbType;
            }

            if (_interfaceVarTypes.TryGetValue(tagName, out string knownType) && !string.IsNullOrWhiteSpace(knownType))
            {
                return knownType;
            }

            string escaped = Regex.Escape(tagName);
            string tagPattern = $@"(?:""{escaped}""|\b{escaped}\b)(?:\s*\[\s*[^\]]+\s*\])?";

            bool sawIntegerComparisonOrAssign = false;

            if (sclBlock?.Statements != null)
            {
                foreach (var st in sclBlock.Statements)
                {
                    if (string.IsNullOrWhiteSpace(st?.RawSclCode)) continue;
                    string code = StripComments(st.RawSclCode);

                    // 1. Check assignments: Target := RHS;
                    foreach (Match am in Regex.Matches(code, @"([^\r\n;:=]+?)\s*:=\s*([^\r\n;]+)"))
                    {
                        string lhs = am.Groups[1].Value.Trim();
                        string rhs = am.Groups[2].Value.Trim();

                        if (Regex.IsMatch(lhs, $@"^{tagPattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            if (rhs.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                                rhs.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                                Regex.IsMatch(rhs, @"\b(AND|OR|XOR|NOT)\b|<=|>=|<>|<|>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                            {
                                return "Bool";
                            }
                            if (rhs.StartsWith("'") && rhs.EndsWith("'"))
                                return "String";
                            if (IsFloatingPointLiteral(rhs))
                                return "Real";

                            string rhsType = GetVariableBaseType(rhs);
                            if (!string.IsNullOrEmpty(rhsType))
                                return rhsType;

                            if (long.TryParse(rhs, out _))
                                sawIntegerComparisonOrAssign = true;
                        }
                        else if (Regex.IsMatch(rhs, tagPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            string lhsType = GetVariableBaseType(lhs);
                            if (!string.IsNullOrEmpty(lhsType))
                                return lhsType;
                        }
                    }

                    // 2. Check comparisons: Left Op Right
                    foreach (Match cm in Regex.Matches(code, @"([^\r\n;:=<>()]+?)\s*(<=|>=|<>|<|>|=)\s*([^\r\n;:=<>()]+)"))
                    {
                        string l = cm.Groups[1].Value.Trim();
                        string r = cm.Groups[3].Value.Trim();

                        // Strip leading IF / AND / OR / NOT
                        l = Regex.Replace(l, @"^(?:IF|ELSIF|AND|OR|XOR|NOT)\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
                        r = Regex.Replace(r, @"\s+(?:THEN|AND|OR|XOR).*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

                        if (Regex.IsMatch(l, $@"^{tagPattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            if (r.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || r.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                                return "Bool";
                            if (IsFloatingPointLiteral(r))
                                return "Real";
                            string rType = GetVariableBaseType(r);
                            if (!string.IsNullOrEmpty(rType))
                                return rType;
                            if (long.TryParse(r, out _))
                                sawIntegerComparisonOrAssign = true;
                        }
                        else if (Regex.IsMatch(r, $@"^{tagPattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            if (l.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || l.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                                return "Bool";
                            if (IsFloatingPointLiteral(l))
                                return "Real";
                            string lType = GetVariableBaseType(l);
                            if (!string.IsNullOrEmpty(lType))
                                return lType;
                            if (long.TryParse(l, out _))
                                sawIntegerComparisonOrAssign = true;
                        }
                    }

                    // 3. Check if used as a standalone boolean condition in IF ... THEN
                    var ifMatch = Regex.Match(code, @"\bIF\s+([\s\S]+?)\s+THEN\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (ifMatch.Success)
                    {
                        string cond = ifMatch.Groups[1].Value;
                        if (Regex.IsMatch(cond, tagPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                            !Regex.IsMatch(cond, $@"{tagPattern}\s*(?:<=|>=|<>|<|>|=|\+|\-|\*|/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                            !Regex.IsMatch(cond, $@"(?:<=|>=|<>|<|>|=|\+|\-|\*|/)\s*{tagPattern}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            return "Bool";
                        }
                    }
                }
            }

            if (sawIntegerComparisonOrAssign)
                return "Int";

            if (tagName.IndexOf("Real", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tagName.IndexOf("Calc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tagName.IndexOf("Deger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tagName.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Real";
            }

            return "Real";
        }

        private static string NormalizeVarLookupKey(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return null;
            string clean = StripOuterParentheses(expr).Trim().TrimStart('#');
            var compMatches = Regex.Matches(clean, @"(?:""(?<name>[^""]+)""|(?<name>[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ\-]+))(?:\s*\[\s*(?<idx>[^\]]+)\s*\])?");
            var names = new List<string>();
            foreach (Match m in compMatches)
            {
                string n = m.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(n))
                    names.Add(n);
            }
            return names.Count > 0 ? string.Join(".", names) : null;
        }

        private static string NormalizePlcDatatype(string rawDatatype)
        {
            if (string.IsNullOrWhiteSpace(rawDatatype)) return null;
            string dt = rawDatatype.Trim();

            // Check "Array[...] of <Type>"
            var arrMatch = Regex.Match(dt, @"\bof\s+""?([a-zA-Z0-9_]+)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (arrMatch.Success)
            {
                dt = arrMatch.Groups[1].Value.Trim();
            }

            dt = dt.Trim('\"');
            if (dt.StartsWith("STRING", StringComparison.OrdinalIgnoreCase))
                return "String";
            if (dt.StartsWith("WSTRING", StringComparison.OrdinalIgnoreCase))
                return "WString";

            switch (dt.ToUpperInvariant())
            {
                case "BOOL": return "Bool";
                case "CHAR": return "Char";
                case "WCHAR": return "WChar";
                case "REAL": return "Real";
                case "LREAL": return "LReal";
                case "INT": return "Int";
                case "DINT": return "DInt";
                case "SINT": return "SInt";
                case "USINT": return "USInt";
                case "UINT": return "UInt";
                case "UDINT": return "UDInt";
                case "LINT": return "LInt";
                case "ULINT": return "ULInt";
                case "BYTE": return "Byte";
                case "WORD": return "Word";
                case "DWORD": return "DWord";
                case "TIME": return "Time";
                default: return null;
            }
        }

        private string GetVariableBaseType(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return null;
            string clean = StripOuterParentheses(expr).Trim();

            if (clean.StartsWith("'") && clean.EndsWith("'") && clean.Length >= 2)
            {
                string inner = clean.Substring(1, clean.Length - 2);
                return inner.Length <= 1 ? "Char" : "String";
            }

            if (TryParseTypedOrRadixConstant(clean, out string tcType, out _, out _))
            {
                return tcType;
            }

            if (IsFloatingPointLiteral(clean))
            {
                return "Real";
            }

            if (TryParseArithmeticExpression(clean, out string aLeft, out _, out string aRight))
            {
                return GetVariableBaseType(aLeft) ?? GetVariableBaseType(aRight);
            }

            // Direct PLC bit address e.g. %Q0.0, %I0.1, %M10.2
            if (Regex.IsMatch(clean, @"^%[IQM]\d+\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return "Bool";

            if (clean.StartsWith("#"))
                clean = clean.Substring(1).Trim();

            var compMatches = Regex.Matches(clean, @"(?:""(?<name>[^""]+)""|(?<name>[a-zA-Z0-9_ğüşıöçĞÜŞİÖÇ%]+))(?:\s*\[\s*(?<idx>[^\]]+)\s*\])?");
            if (compMatches.Count == 0) return null;

            var compNames = new List<string>();
            foreach (Match m in compMatches)
            {
                string n = m.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(n))
                    compNames.Add(n);
            }
            if (compNames.Count == 0) return null;

            // 1. Try full component path e.g. "RECETE.ON SIRA LED"
            string fullPath = string.Join(".", compNames);
            if (_interfaceVarTypes.TryGetValue(fullPath, out string fullType))
                return ExtractArrayElementType(fullType);

            // 2. Try leaf component name e.g. "ON SIRA LED"
            string leafName = compNames[compNames.Count - 1];
            if (_interfaceVarTypes.TryGetValue(leafName, out string leafType))
                return ExtractArrayElementType(leafType);

            // Timer standard properties (Q, IN, ET, PT)
            if (string.Equals(leafName, "Q", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, "IN", StringComparison.OrdinalIgnoreCase))
                return "Bool";
            if (string.Equals(leafName, "ET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, "PT", StringComparison.OrdinalIgnoreCase))
                return "Time";

            // 3. For single-component or array access, try root component name
            if (compNames.Count == 1 && _interfaceVarTypes.TryGetValue(compNames[0], out string rootType))
                return ExtractArrayElementType(rootType);

            return null;
        }

        private static string ExtractArrayElementType(string typeStr)
        {
            if (string.IsNullOrWhiteSpace(typeStr)) return typeStr;
            string trimmed = typeStr.Trim();
            if (trimmed.StartsWith("Array", StringComparison.OrdinalIgnoreCase))
            {
                int ofIdx = trimmed.LastIndexOf(" of ", StringComparison.OrdinalIgnoreCase);
                if (ofIdx >= 0)
                {
                    return trimmed.Substring(ofIdx + 4).Trim();
                }
            }
            return trimmed;
        }

        private static bool IsFloatingPointLiteral(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return false;
            return Regex.IsMatch(val.Trim(), @"^-?\d+\.\d+([eE][+-]?\d+)?$");
        }

        /// <summary>
        /// Resolves the common numeric SrcType for arithmetic or comparison instructions,
        /// implementing the implicit type promotion hierarchy from Slide 21 of the SCL guide:
        /// LReal &gt; Real &gt; LInt/ULInt &gt; DInt/UDInt &gt; Int/UInt &gt; SInt/USInt
        /// (with SInt + USInt promoting to Int).
        /// </summary>
        private string ResolveNumericSrcType(string left, string right, string target = null)
        {
            string tTarget = GetVariableBaseType(target);
            string tLeft = GetVariableBaseType(left);
            string tRight = GetVariableBaseType(right);

            bool hasTimeOrDInt = (tTarget != null && (tTarget.Equals("Time", StringComparison.OrdinalIgnoreCase) || tTarget.Equals("DInt", StringComparison.OrdinalIgnoreCase))) ||
                                 (tLeft != null && (tLeft.Equals("Time", StringComparison.OrdinalIgnoreCase) || tLeft.Equals("DInt", StringComparison.OrdinalIgnoreCase))) ||
                                 (tRight != null && (tRight.Equals("Time", StringComparison.OrdinalIgnoreCase) || tRight.Equals("DInt", StringComparison.OrdinalIgnoreCase))) ||
                                 (left != null && (left.IndexOf(".ET", StringComparison.OrdinalIgnoreCase) >= 0 || left.IndexOf(".PT", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                 (right != null && (right.IndexOf(".ET", StringComparison.OrdinalIgnoreCase) >= 0 || right.IndexOf(".PT", StringComparison.OrdinalIgnoreCase) >= 0));

            // If target is Int/SInt/UInt/USInt, constrain arithmetic to target's type if operands fit (and no Time/DInt involved)
            if (!hasTimeOrDInt && !string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(tTarget) && !IsIllegalMathType(tTarget))
            {
                if (string.Equals(tTarget, "Int", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tTarget, "SInt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tTarget, "UInt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tTarget, "USInt", StringComparison.OrdinalIgnoreCase))
                {
                    return tTarget;
                }
            }

            string[] candidates = { tTarget, tLeft, tRight };
            if (candidates.Any(c => string.Equals(c, "LReal", StringComparison.OrdinalIgnoreCase)))
                return "LReal";
            if (candidates.Any(c => string.Equals(c, "Real", StringComparison.OrdinalIgnoreCase)))
                return "Real";
            if (candidates.Any(c => string.Equals(c, "Char", StringComparison.OrdinalIgnoreCase)))
                return "Char";
            if (candidates.Any(c => string.Equals(c, "String", StringComparison.OrdinalIgnoreCase)))
                return "String";
            if (candidates.Any(c => string.Equals(c, "WString", StringComparison.OrdinalIgnoreCase)))
                return "WString";

            // If Time or DInt is present, always promote to DInt (Slide 21 / 32-bit LAD requirement)
            if (hasTimeOrDInt || candidates.Any(c => string.Equals(c, "Time", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "DInt", StringComparison.OrdinalIgnoreCase)))
                return "DInt";

            // Map Time / LTime to their underlying 32-bit / 64-bit integer representation in math instructions
            if (candidates.Any(c => string.Equals(c, "LTime", StringComparison.OrdinalIgnoreCase)))
                return "LInt";
            if (candidates.Any(c => string.Equals(c, "Time", StringComparison.OrdinalIgnoreCase)))
                return "DInt";

            // Integer hierarchy promotion (Slide 21)
            if (candidates.Any(c => string.Equals(c, "LInt", StringComparison.OrdinalIgnoreCase)))
                return "LInt";
            if (candidates.Any(c => string.Equals(c, "ULInt", StringComparison.OrdinalIgnoreCase)))
                return "ULInt";
            if (candidates.Any(c => string.Equals(c, "DInt", StringComparison.OrdinalIgnoreCase)))
                return "DInt";
            if (candidates.Any(c => string.Equals(c, "UDInt", StringComparison.OrdinalIgnoreCase)))
                return "UDInt";
            if (candidates.Any(c => string.Equals(c, "Int", StringComparison.OrdinalIgnoreCase)))
                return "Int";
            if (candidates.Any(c => string.Equals(c, "UInt", StringComparison.OrdinalIgnoreCase)))
                return "UInt";

            bool hasSInt = candidates.Any(c => string.Equals(c, "SInt", StringComparison.OrdinalIgnoreCase));
            bool hasUSInt = candidates.Any(c => string.Equals(c, "USInt", StringComparison.OrdinalIgnoreCase));
            if (hasSInt && hasUSInt)
                return "Int"; // Slide 21: SInt + USInt -> Int
            if (hasSInt)
                return "SInt";
            if (hasUSInt)
                return "USInt";

            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && !string.Equals(c, "Bool", StringComparison.OrdinalIgnoreCase))
                    return c;
            }

            // Fallback heuristic when variables are not in local block Interface (e.g. Global DB tags)
            string combined = $"{left} {right} {target}";
            bool looksLikeReal = IsFloatingPointLiteral(left) ||
                                 IsFloatingPointLiteral(right) ||
                                 IsFloatingPointLiteral(target) ||
                                 combined.IndexOf("Real", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 combined.IndexOf("Deger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 combined.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 combined.IndexOf("Muhendislik", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 combined.IndexOf("HAM", StringComparison.OrdinalIgnoreCase) >= 0;

            return looksLikeReal ? "Real" : "DInt";
        }

        private abstract class BoolExprNode { }

        private class BoolAndNode : BoolExprNode
        {
            public List<BoolExprNode> Children { get; set; } = new List<BoolExprNode>();
        }

        private class BoolOrNode : BoolExprNode
        {
            public List<BoolExprNode> Children { get; set; } = new List<BoolExprNode>();
        }

        private class BoolNotNode : BoolExprNode
        {
            public BoolExprNode Inner { get; set; }
        }

        private class BoolLeafNode : BoolExprNode
        {
            public ConditionFactor Factor { get; set; }
        }

        private static bool IsIdentBoundaryChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '#';
        }

        private List<string> SplitTopLevelOr(string expr)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(expr)) return list;

            int parenDepth = 0;
            bool inDoubleQuote = false;
            bool inSingleQuote = false;
            int start = 0;

            for (int i = 0; i < expr.Length; i++)
            {
                char ch = expr[i];
                if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
                else if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
                else if (!inDoubleQuote && !inSingleQuote)
                {
                    if (ch == '(') parenDepth++;
                    else if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0)
                    {
                        if (i + 2 <= expr.Length &&
                            (i == 0 || !IsIdentBoundaryChar(expr[i - 1])) &&
                            expr.Substring(i, 2).Equals("OR", StringComparison.OrdinalIgnoreCase) &&
                            (i + 2 == expr.Length || !IsIdentBoundaryChar(expr[i + 2])))
                        {
                            string part = expr.Substring(start, i - start).Trim();
                            if (!string.IsNullOrWhiteSpace(part))
                                list.Add(part);
                            i += 2;
                            start = i;
                        }
                    }
                }
            }

            if (start < expr.Length)
            {
                string part = expr.Substring(start).Trim();
                if (!string.IsNullOrWhiteSpace(part))
                    list.Add(part);
            }

            return list.Count > 0 ? list : new List<string> { expr.Trim() };
        }

        private List<string> SplitTopLevelAnd(string expr)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(expr)) return tokens;

            int parenDepth = 0;
            bool inDoubleQuote = false;
            bool inSingleQuote = false;
            int start = 0;

            for (int i = 0; i < expr.Length; i++)
            {
                char ch = expr[i];
                if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
                else if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
                else if (!inDoubleQuote && !inSingleQuote)
                {
                    if (ch == '(') parenDepth++;
                    else if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0)
                    {
                        if (i + 3 <= expr.Length &&
                            (i == 0 || !IsIdentBoundaryChar(expr[i - 1])) &&
                            expr.Substring(i, 3).Equals("AND", StringComparison.OrdinalIgnoreCase) &&
                            (i + 3 == expr.Length || !IsIdentBoundaryChar(expr[i + 3])))
                        {
                            string part = expr.Substring(start, i - start).Trim();
                            if (!string.IsNullOrWhiteSpace(part)) tokens.Add(part);
                            i += 3;
                            start = i;
                        }
                        else if (ch == '&')
                        {
                            string part = expr.Substring(start, i - start).Trim();
                            if (!string.IsNullOrWhiteSpace(part)) tokens.Add(part);
                            start = i + 1;
                        }
                    }
                }
            }

            if (start < expr.Length)
            {
                string part = expr.Substring(start).Trim();
                if (!string.IsNullOrWhiteSpace(part)) tokens.Add(part);
            }

            return tokens.Count > 0 ? tokens : new List<string> { expr.Trim() };
        }

        private List<string> SplitTopLevelXor(string expr)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(expr)) return list;

            int parenDepth = 0;
            bool inDoubleQuote = false;
            bool inSingleQuote = false;
            int start = 0;

            for (int i = 0; i < expr.Length; i++)
            {
                char ch = expr[i];
                if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
                else if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
                else if (!inDoubleQuote && !inSingleQuote)
                {
                    if (ch == '(') parenDepth++;
                    else if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0)
                    {
                        if (i + 3 <= expr.Length &&
                            (i == 0 || !IsIdentBoundaryChar(expr[i - 1])) &&
                            expr.Substring(i, 3).Equals("XOR", StringComparison.OrdinalIgnoreCase) &&
                            (i + 3 == expr.Length || !IsIdentBoundaryChar(expr[i + 3])))
                        {
                            string part = expr.Substring(start, i - start).Trim();
                            if (!string.IsNullOrWhiteSpace(part))
                                list.Add(part);
                            i += 3;
                            start = i;
                        }
                    }
                }
            }

            if (start < expr.Length)
            {
                string part = expr.Substring(start).Trim();
                if (!string.IsNullOrWhiteSpace(part))
                    list.Add(part);
            }

            return list.Count > 0 ? list : new List<string> { expr.Trim() };
        }

        /// <summary>
        /// Recursively parses an arbitrary SCL boolean/comparison expression (supporting nested parentheses,
        /// OR, XOR, AND / &amp;, NOT / ~, comparisons, and boolean literals) into a BoolExprNode AST.
        /// </summary>
        private BoolExprNode ParseBoolExpr(string expr, List<PreCalcItem> condPreCalcs = null)
        {
            if (string.IsNullOrWhiteSpace(expr)) return null;
            string clean = StripOuterParentheses(expr).Trim();
            if (string.IsNullOrWhiteSpace(clean)) return null;

            // 1. Lowest precedence: Top-level OR
            var orParts = SplitTopLevelOr(clean);
            if (orParts.Count > 1)
            {
                var orNode = new BoolOrNode();
                foreach (var p in orParts)
                {
                    var child = ParseBoolExpr(p, condPreCalcs);
                    if (child == null) return null;
                    if (child is BoolOrNode childOr)
                        orNode.Children.AddRange(childOr.Children);
                    else
                        orNode.Children.Add(child);
                }
                return orNode;
            }

            // 2. Next precedence: Top-level XOR -> A XOR B === (A AND NOT B) OR (NOT A AND B)
            var xorParts = SplitTopLevelXor(clean);
            if (xorParts.Count > 1)
            {
                var current = ParseBoolExpr(xorParts[0], condPreCalcs);
                if (current == null) return null;
                for (int i = 1; i < xorParts.Count; i++)
                {
                    var next = ParseBoolExpr(xorParts[i], condPreCalcs);
                    if (next == null) return null;

                    var branch1 = new BoolAndNode();
                    branch1.Children.Add(current);
                    branch1.Children.Add(new BoolNotNode { Inner = next });

                    var branch2 = new BoolAndNode();
                    branch2.Children.Add(new BoolNotNode { Inner = current });
                    branch2.Children.Add(next);

                    var xorOr = new BoolOrNode();
                    xorOr.Children.Add(branch1);
                    xorOr.Children.Add(branch2);
                    current = xorOr;
                }
                return current;
            }

            // 2. Next precedence: Top-level AND / &
            var andParts = SplitTopLevelAnd(clean);
            if (andParts.Count > 1)
            {
                var andNode = new BoolAndNode();
                foreach (var p in andParts)
                {
                    var child = ParseBoolExpr(p, condPreCalcs);
                    if (child == null) return null;
                    if (child is BoolAndNode childAnd)
                        andNode.Children.AddRange(childAnd.Children);
                    else
                        andNode.Children.Add(child);
                }
                return andNode;
            }

            // 3. Unary prefix NOT or ~
            if (clean.StartsWith("~"))
            {
                string rest = clean.Substring(1).Trim();
                var inner = ParseBoolExpr(rest, condPreCalcs);
                return inner != null ? new BoolNotNode { Inner = inner } : null;
            }
            if (clean.Length > 3 &&
                clean.StartsWith("NOT", StringComparison.OrdinalIgnoreCase) &&
                !IsIdentBoundaryChar(clean[3]))
            {
                string rest = clean.Substring(3).Trim();
                var inner = ParseBoolExpr(rest, condPreCalcs);
                return inner != null ? new BoolNotNode { Inner = inner } : null;
            }

            // 4. Leaf: Comparison or Contact
            return ParseBoolLeaf(clean, condPreCalcs);
        }

        private BoolExprNode ParseBoolLeaf(string clean, List<PreCalcItem> condPreCalcs = null)
        {
            if (string.IsNullOrWhiteSpace(clean)) return null;

            if (TryFindComparisonOperator(clean, out string left, out string op, out string right))
            {
                // First, extract any array index pre-calculations (e.g. arr[i + 1] -> arr[#tempCalc])
                left = ExtractIndexPreCalculations(left, ref condPreCalcs);
                right = ExtractIndexPreCalculations(right, ref condPreCalcs);

                // Check if left is arithmetic: e.g. #sira + 1 or #uretimadet - 1
                if (!IsValidIdentifier(left) && !IsConstantLiteral(left))
                {
                    if (TryParseArithmeticExpression(left, out string aLeft, out string aOp, out string aRight) &&
                        IsSimpleOperand(aLeft) && IsSimpleOperand(aRight))
                    {
                        string mathPartOp = MapBinaryMathOpToPartName(aOp);
                        if (mathPartOp != null)
                        {
                            string mathSrcType = ResolveNumericSrcType(aLeft, aRight);
                            if (string.IsNullOrEmpty(mathSrcType) || mathSrcType == "Variant")
                                mathSrcType = "DInt";

                            string tempVarName = null;
                            if (condPreCalcs != null)
                            {
                                var existing = condPreCalcs.FirstOrDefault(pc =>
                                    string.Equals(pc.LeftVal, aLeft, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(pc.Op, aOp, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(pc.RightVal, aRight, StringComparison.OrdinalIgnoreCase));
                                if (existing != null)
                                {
                                    tempVarName = existing.TargetVar;
                                }
                            }

                            if (tempVarName == null)
                            {
                                tempVarName = AllocateTempCalcVar(mathSrcType);
                                if (condPreCalcs == null) condPreCalcs = new List<PreCalcItem>();
                                condPreCalcs.Add(new PreCalcItem
                                {
                                    LeftVal = aLeft,
                                    Op = aOp,
                                    RightVal = aRight,
                                    PartOp = mathPartOp,
                                    SrcType = mathSrcType,
                                    TargetVar = tempVarName
                                });
                            }

                            left = tempVarName;
                        }
                    }
                }

                // Check if right is arithmetic
                if (!IsValidIdentifier(right) && !IsConstantLiteral(right))
                {
                    if (TryParseArithmeticExpression(right, out string aLeft, out string aOp, out string aRight) &&
                        IsSimpleOperand(aLeft) && IsSimpleOperand(aRight))
                    {
                        string mathPartOp = MapBinaryMathOpToPartName(aOp);
                        if (mathPartOp != null)
                        {
                            string mathSrcType = ResolveNumericSrcType(aLeft, aRight);
                            if (string.IsNullOrEmpty(mathSrcType) || mathSrcType == "Variant")
                                mathSrcType = "DInt";

                            string tempVarName = null;
                            if (condPreCalcs != null)
                            {
                                var existing = condPreCalcs.FirstOrDefault(pc =>
                                    string.Equals(pc.LeftVal, aLeft, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(pc.Op, aOp, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(pc.RightVal, aRight, StringComparison.OrdinalIgnoreCase));
                                if (existing != null)
                                {
                                    tempVarName = existing.TargetVar;
                                }
                            }

                            if (tempVarName == null)
                            {
                                tempVarName = AllocateTempCalcVar(mathSrcType);
                                if (condPreCalcs == null) condPreCalcs = new List<PreCalcItem>();
                                condPreCalcs.Add(new PreCalcItem
                                {
                                    LeftVal = aLeft,
                                    Op = aOp,
                                    RightVal = aRight,
                                    PartOp = mathPartOp,
                                    SrcType = mathSrcType,
                                    TargetVar = tempVarName
                                });
                            }

                            right = tempVarName;
                        }
                    }
                }

                // Check boolean comparison: Tag = TRUE, Tag = FALSE, Tag <> TRUE, Tag <> FALSE
                if (op == "=" || op == "<>")
                {
                    bool rightIsBoolLit = right.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                                          right.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                    bool leftIsBoolLit = left.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                                         left.Equals("FALSE", StringComparison.OrdinalIgnoreCase);

                    if (rightIsBoolLit && IsValidIdentifier(left))
                    {
                        bool isTrue = right.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        bool isNegated = (op == "=") ? !isTrue : isTrue;
                        return new BoolLeafNode
                        {
                            Factor = new ConditionFactor
                            {
                                IsComparison = false,
                                VariableName = left,
                                IsNegated = isNegated
                            }
                        };
                    }
                    if (leftIsBoolLit && IsValidIdentifier(right))
                    {
                        bool isTrue = left.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        bool isNegated = (op == "=") ? !isTrue : isTrue;
                        return new BoolLeafNode
                        {
                            Factor = new ConditionFactor
                            {
                                IsComparison = false,
                                VariableName = right,
                                IsNegated = isNegated
                            }
                        };
                    }

                    // Also check if variable is known to be Bool in Interface and compared against 0 or 1
                    if (IsValidIdentifier(left) && (right == "0" || right == "1") &&
                        string.Equals(GetVariableBaseType(left), "Bool", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isOne = (right == "1");
                        bool isNegated = (op == "=") ? !isOne : isOne;
                        return new BoolLeafNode
                        {
                            Factor = new ConditionFactor
                            {
                                IsComparison = false,
                                VariableName = left,
                                IsNegated = isNegated
                            }
                        };
                    }
                    if (IsValidIdentifier(right) && (left == "0" || left == "1") &&
                        string.Equals(GetVariableBaseType(right), "Bool", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isOne = (left == "1");
                        bool isNegated = (op == "=") ? !isOne : isOne;
                        return new BoolLeafNode
                        {
                            Factor = new ConditionFactor
                            {
                                IsComparison = false,
                                VariableName = right,
                                IsNegated = isNegated
                            }
                        };
                    }
                }

                string partOp = op == "<" ? "Lt" :
                                op == ">" ? "Gt" :
                                op == "<=" ? "Le" :
                                op == ">=" ? "Ge" :
                                op == "<>" ? "Ne" : "Eq";

                string srcType = ResolveNumericSrcType(left, right);

                if (!IsValidIdentifier(left) && !IsConstantLiteral(left))
                {
                    return null;
                }

                if (!IsValidIdentifier(right) && !IsConstantLiteral(right))
                {
                    return null;
                }

                return new BoolLeafNode
                {
                    Factor = new ConditionFactor
                    {
                        IsComparison = true,
                        CompLeft = left,
                        CompRight = right,
                        CompOp = partOp,
                        CompType = srcType
                    }
                };
            }

            clean = ExtractIndexPreCalculations(clean, ref condPreCalcs);
            if (!IsValidIdentifier(clean))
                return null;

            return new BoolLeafNode
            {
                Factor = new ConditionFactor
                {
                    IsComparison = false,
                    VariableName = clean,
                    IsNegated = false
                }
            };
        }

        /// <summary>
        /// Pushes NOT operators down to the leaves using De Morgan's Laws (Negation Normal Form)
        /// and flattens nested AND/OR nodes.
        /// </summary>
        private BoolExprNode NormalizeBoolAst(BoolExprNode node, bool negate)
        {
            if (node == null) return null;

            if (node is BoolNotNode notNode)
            {
                return NormalizeBoolAst(notNode.Inner, !negate);
            }

            if (node is BoolAndNode andNode)
            {
                if (!negate)
                {
                    var res = new BoolAndNode();
                    foreach (var c in andNode.Children)
                    {
                        var normChild = NormalizeBoolAst(c, false);
                        if (normChild == null) return null;
                        if (normChild is BoolAndNode subAnd)
                            res.Children.AddRange(subAnd.Children);
                        else
                            res.Children.Add(normChild);
                    }
                    return res.Children.Count == 1 ? res.Children[0] : res;
                }
                else
                {
                    // De Morgan: NOT (A AND B) => (NOT A) OR (NOT B)
                    var res = new BoolOrNode();
                    foreach (var c in andNode.Children)
                    {
                        var normChild = NormalizeBoolAst(c, true);
                        if (normChild == null) return null;
                        if (normChild is BoolOrNode subOr)
                            res.Children.AddRange(subOr.Children);
                        else
                            res.Children.Add(normChild);
                    }
                    return res.Children.Count == 1 ? res.Children[0] : res;
                }
            }

            if (node is BoolOrNode orNode)
            {
                if (!negate)
                {
                    var res = new BoolOrNode();
                    foreach (var c in orNode.Children)
                    {
                        var normChild = NormalizeBoolAst(c, false);
                        if (normChild == null) return null;
                        if (normChild is BoolOrNode subOr)
                            res.Children.AddRange(subOr.Children);
                        else
                            res.Children.Add(normChild);
                    }
                    return res.Children.Count == 1 ? res.Children[0] : res;
                }
                else
                {
                    // De Morgan: NOT (A OR B) => (NOT A) AND (NOT B)
                    var res = new BoolAndNode();
                    foreach (var c in orNode.Children)
                    {
                        var normChild = NormalizeBoolAst(c, true);
                        if (normChild == null) return null;
                        if (normChild is BoolAndNode subAnd)
                            res.Children.AddRange(subAnd.Children);
                        else
                            res.Children.Add(normChild);
                    }
                    return res.Children.Count == 1 ? res.Children[0] : res;
                }
            }

            if (node is BoolLeafNode leaf)
            {
                var f = leaf.Factor;
                return new BoolLeafNode
                {
                    Factor = new ConditionFactor
                    {
                        IsComparison = f.IsComparison,
                        VariableName = f.VariableName,
                        IsNegated = !f.IsComparison ? (negate ? !f.IsNegated : f.IsNegated) : f.IsNegated,
                        CompLeft = f.CompLeft,
                        CompRight = f.CompRight,
                        CompOp = f.IsComparison ? (negate ? InvertComparisonPartOp(f.CompOp) : f.CompOp) : f.CompOp,
                        CompType = f.CompType
                    }
                };
            }

            return null;
        }

        private static string InvertComparisonPartOp(string partOp)
        {
            switch (partOp)
            {
                case "Eq": return "Ne";
                case "Ne": return "Eq";
                case "Lt": return "Ge";
                case "Ge": return "Lt";
                case "Gt": return "Le";
                case "Le": return "Gt";
                default: return partOp;
            }
        }

        /// <summary>
        /// Recursively synthesizes a Series-Parallel SimaticML FlgNet circuit from a normalized BoolExprNode AST.
        /// Any left-most entry point of the sub-circuit adds its (UId, PinName) to entrySinks,
        /// and the method returns the single output (UId, OutPinName) of the sub-circuit.
        /// </summary>
        private Tuple<int, string> BuildBoolExprNode(
            BoolExprNode node,
            List<Tuple<int, string>> entrySinks,
            StringBuilder parts,
            StringBuilder wires)
        {
            if (node == null) return null;

            if (node is BoolAndNode andNode)
            {
                if (andNode.Children.Count == 0) return null;
                if (andNode.Children.Count == 1)
                    return BuildBoolExprNode(andNode.Children[0], entrySinks, parts, wires);

                var currentOut = BuildBoolExprNode(andNode.Children[0], entrySinks, parts, wires);
                if (currentOut == null) return null;

                for (int i = 1; i < andNode.Children.Count; i++)
                {
                    var nextStageSinks = new List<Tuple<int, string>>();
                    var nextOut = BuildBoolExprNode(andNode.Children[i], nextStageSinks, parts, wires);
                    if (nextOut == null || nextStageSinks.Count == 0)
                        return null;

                    int seriesWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{seriesWireId}\">");
                    wires.AppendLine($"            <NameCon UId=\"{currentOut.Item1}\" Name=\"{currentOut.Item2}\" />");
                    foreach (var sink in nextStageSinks)
                    {
                        wires.AppendLine($"            <NameCon UId=\"{sink.Item1}\" Name=\"{sink.Item2}\" />");
                    }
                    wires.AppendLine($"          </Wire>");

                    currentOut = nextOut;
                }

                return currentOut;
            }

            if (node is BoolOrNode orNode)
            {
                if (orNode.Children.Count == 0) return null;
                if (orNode.Children.Count == 1)
                    return BuildBoolExprNode(orNode.Children[0], entrySinks, parts, wires);

                var branchOuts = new List<Tuple<int, string>>();
                foreach (var child in orNode.Children)
                {
                    var childOut = BuildBoolExprNode(child, entrySinks, parts, wires);
                    if (childOut == null) return null;
                    branchOuts.Add(childOut);
                }

                int orPartId = NextUId();
                parts.AppendLine($"          <Part Name=\"O\" UId=\"{orPartId}\">");
                parts.AppendLine($"            <TemplateValue Name=\"Card\" Type=\"Cardinality\">{branchOuts.Count}</TemplateValue>");
                parts.AppendLine($"          </Part>");

                for (int i = 0; i < branchOuts.Count; i++)
                {
                    int inWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{inWireId}\">");
                    wires.AppendLine($"            <NameCon UId=\"{branchOuts[i].Item1}\" Name=\"{branchOuts[i].Item2}\" />");
                    wires.AppendLine($"            <NameCon UId=\"{orPartId}\" Name=\"in{i + 1}\" />");
                    wires.AppendLine($"          </Wire>");
                }

                return Tuple.Create(orPartId, "out");
            }

            if (node is BoolLeafNode leaf)
            {
                var factor = leaf.Factor;

                if (factor.IsComparison)
                {
                    int leftAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(factor.CompLeft, leftAccId, factor.CompType));

                    int rightAccId = NextUId();
                    parts.AppendLine(FormatAccessXml(factor.CompRight, rightAccId, factor.CompType));

                    int cmpPartId = NextUId();
                    parts.AppendLine($"          <Part Name=\"{factor.CompOp}\" UId=\"{cmpPartId}\">");
                    parts.AppendLine($"            <TemplateValue Name=\"SrcType\" Type=\"Type\">{factor.CompType}</TemplateValue>");
                    parts.AppendLine($"          </Part>");

                    int cmpIn1Wire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{cmpIn1Wire}\"><IdentCon UId=\"{leftAccId}\" /><NameCon UId=\"{cmpPartId}\" Name=\"in1\" /></Wire>");

                    int cmpIn2Wire = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{cmpIn2Wire}\"><IdentCon UId=\"{rightAccId}\" /><NameCon UId=\"{cmpPartId}\" Name=\"in2\" /></Wire>");

                    entrySinks.Add(Tuple.Create(cmpPartId, "pre"));
                    return Tuple.Create(cmpPartId, "out");
                }
                else
                {
                    if (!IsValidIdentifier(factor.VariableName)) return null;

                    int accessId = NextUId();
                    parts.AppendLine(FormatAccessXml(factor.VariableName, accessId));

                    int contactId = NextUId();
                    if (factor.IsNegated)
                    {
                        parts.AppendLine($"          <Part Name=\"Contact\" UId=\"{contactId}\">");
                        parts.AppendLine($"            <Negated Name=\"operand\" />");
                        parts.AppendLine($"          </Part>");
                    }
                    else
                    {
                        parts.AppendLine($"          <Part Name=\"Contact\" UId=\"{contactId}\" />");
                    }

                    int opWireId = NextUId();
                    wires.AppendLine($"          <Wire UId=\"{opWireId}\"><IdentCon UId=\"{accessId}\" /><NameCon UId=\"{contactId}\" Name=\"operand\" /></Wire>");

                    entrySinks.Add(Tuple.Create(contactId, "in"));
                    return Tuple.Create(contactId, "out");
                }
            }

            return null;
        }

        private static string StripOuterParentheses(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return expr;
            string trimmed = expr.Trim();
            while (trimmed.StartsWith("(") && trimmed.EndsWith(")"))
            {
                int depth = 0;
                bool closesBeforeEnd = false;
                for (int i = 0; i < trimmed.Length - 1; i++)
                {
                    char c = trimmed[i];
                    if (c == '"')
                    {
                        i++;
                        while (i < trimmed.Length - 1 && trimmed[i] != '"') i++;
                    }
                    else if (c == '\'')
                    {
                        i++;
                        while (i < trimmed.Length - 1 && trimmed[i] != '\'') i++;
                    }
                    else if (c == '(')
                    {
                        depth++;
                    }
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closesBeforeEnd = true;
                            break;
                        }
                    }
                }

                if (!closesBeforeEnd && depth == 1)
                {
                    trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
                }
                else
                {
                    break;
                }
            }
            return trimmed;
        }

        private static bool TryFindComparisonOperator(string expr, out string left, out string op, out string right)
        {
            left = null;
            op = null;
            right = null;
            if (string.IsNullOrWhiteSpace(expr)) return false;

            string clean = StripOuterParentheses(expr);
            int parenDepth = 0;
            bool inDoubleQuote = false;
            bool inSingleQuote = false;

            for (int i = 0; i < clean.Length; i++)
            {
                char ch = clean[i];
                if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
                else if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
                else if (!inDoubleQuote && !inSingleQuote)
                {
                    if (ch == '(' || ch == '[') parenDepth++;
                    else if (ch == ')' || ch == ']') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0)
                    {
                        // Check 2-character operators
                        if (i + 1 < clean.Length)
                        {
                            string two = clean.Substring(i, 2);
                            if (two == "<=" || two == ">=" || two == "<>")
                            {
                                left = StripOuterParentheses(clean.Substring(0, i));
                                op = two;
                                right = StripOuterParentheses(clean.Substring(i + 2));
                                return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right);
                            }
                        }

                        // Check 1-character operators
                        if (ch == '<' || ch == '>')
                        {
                            left = StripOuterParentheses(clean.Substring(0, i));
                            op = ch.ToString();
                            right = StripOuterParentheses(clean.Substring(i + 1));
                            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right);
                        }
                        else if (ch == '=')
                        {
                            if (i > 0 && clean[i - 1] == ':') continue; // skip :=
                            left = StripOuterParentheses(clean.Substring(0, i));
                            op = "=";
                            right = StripOuterParentheses(clean.Substring(i + 1));
                            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right);
                        }
                    }
                }
            }

            return false;
        }

        private bool TryParseArithmeticExpression(string expr, out string left, out string op, out string right)
        {
            left = null;
            op = null;
            right = null;
            if (string.IsNullOrWhiteSpace(expr)) return false;

            string clean = StripOuterParentheses(expr);
            if (string.IsNullOrWhiteSpace(clean)) return false;

            // Scan for top-level binary + or - (right to left for left-associativity)
            int parenDepth = 0;
            bool inDoubleQuote = false;
            bool inSingleQuote = false;

            for (int i = clean.Length - 1; i >= 0; i--)
            {
                char ch = clean[i];
                if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
                else if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
                else if (!inDoubleQuote && !inSingleQuote)
                {
                    if (ch == ')' || ch == ']') parenDepth++;
                    else if (ch == '(' || ch == '[') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0)
                    {
                        if (ch == '+' || ch == '-')
                        {
                            if (ch == '-')
                            {
                                int prevIdx = i - 1;
                                while (prevIdx >= 0 && char.IsWhiteSpace(clean[prevIdx])) prevIdx--;
                                if (prevIdx < 0) continue; // unary at start
                                char prevChar = clean[prevIdx];
                                if (prevChar == '+' || prevChar == '-' || prevChar == '*' || prevChar == '/' || prevChar == '(' || prevChar == '=')
                                    continue; // unary after operator
                            }

                            string l = StripOuterParentheses(clean.Substring(0, i));
                            string r = StripOuterParentheses(clean.Substring(i + 1));

                            if (IsValidOperand(l) && IsValidOperand(r))
                            {
                                left = l;
                                op = ch.ToString();
                                right = r;
                                return true;
                            }
                        }
                    }
                }
            }

            // Scan for top-level binary *, /, MOD, or ** (right to left)
            parenDepth = 0;
            inDoubleQuote = false;
            inSingleQuote = false;

            for (int i = clean.Length - 1; i >= 0; i--)
            {
                char ch = clean[i];
                if (ch == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
                else if (ch == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
                else if (!inDoubleQuote && !inSingleQuote)
                {
                    if (ch == ')' || ch == ']') parenDepth++;
                    else if (ch == '(' || ch == '[') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0)
                    {
                        // Check 2-char **
                        if (ch == '*' && i > 0 && clean[i - 1] == '*')
                        {
                            string l = StripOuterParentheses(clean.Substring(0, i - 1));
                            string r = StripOuterParentheses(clean.Substring(i + 1));
                            if (IsValidOperand(l) && IsValidOperand(r))
                            {
                                left = l;
                                op = "**";
                                right = r;
                                return true;
                            }
                            i--;
                            continue;
                        }

                        // Check 3-char MOD keyword (Slide 22)
                        if (i >= 2 &&
                            clean.Substring(i - 2, 3).Equals("MOD", StringComparison.OrdinalIgnoreCase) &&
                            (i - 2 == 0 || !IsIdentBoundaryChar(clean[i - 3])) &&
                            (i + 1 == clean.Length || !IsIdentBoundaryChar(clean[i + 1])))
                        {
                            string l = StripOuterParentheses(clean.Substring(0, i - 2));
                            string r = StripOuterParentheses(clean.Substring(i + 1));
                            if (IsValidOperand(l) && IsValidOperand(r))
                            {
                                left = l;
                                op = "MOD";
                                right = r;
                                return true;
                            }
                            i -= 2;
                            continue;
                        }

                        // Check 3-char DIV keyword (s7sclapb.chm scl00178.htm)
                        if (i >= 2 &&
                            clean.Substring(i - 2, 3).Equals("DIV", StringComparison.OrdinalIgnoreCase) &&
                            (i - 2 == 0 || !IsIdentBoundaryChar(clean[i - 3])) &&
                            (i + 1 == clean.Length || !IsIdentBoundaryChar(clean[i + 1])))
                        {
                            string l = StripOuterParentheses(clean.Substring(0, i - 2));
                            string r = StripOuterParentheses(clean.Substring(i + 1));
                            if (IsValidOperand(l) && IsValidOperand(r))
                            {
                                left = l;
                                op = "DIV";
                                right = r;
                                return true;
                            }
                            i -= 2;
                            continue;
                        }

                        if (ch == '*' || ch == '/')
                        {
                            string l = StripOuterParentheses(clean.Substring(0, i));
                            string r = StripOuterParentheses(clean.Substring(i + 1));

                            if (IsValidOperand(l) && IsValidOperand(r))
                            {
                                left = l;
                                op = ch.ToString();
                                right = r;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool IsValidOperand(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string clean = StripOuterParentheses(s);
            if (IsConstantLiteral(clean))
                return true;
            if (IsValidIdentifier(clean))
                return true;
            return TryParseArithmeticExpression(clean, out _, out _, out _);
        }

        private void EnsureTempVariablesDeclared(XElement interfaceElem, Dictionary<string, string> tempVars)
        {
            if (interfaceElem == null || tempVars == null || tempVars.Count == 0) return;

            var tempSection = interfaceElem.Descendants()
                .FirstOrDefault(d => d.Name.LocalName == "Section" && (string)d.Attribute("Name") == "Temp");

            if (tempSection == null)
            {
                var sectionsElem = interfaceElem.Descendants().FirstOrDefault(d => d.Name.LocalName == "Sections");
                if (sectionsElem != null)
                {
                    XNamespace ns = sectionsElem.Name.Namespace;
                    tempSection = new XElement(ns + "Section", new XAttribute("Name", "Temp"));
                    sectionsElem.Add(tempSection);
                }
                else
                {
                    XNamespace ns = $"http://www.siemens.com/automation/Openness/SW/Interface/{_interfaceVersion}";
                    tempSection = new XElement(ns + "Section", new XAttribute("Name", "Temp"));
                    var sections = new XElement(ns + "Sections", tempSection);
                    interfaceElem.Add(sections);
                }
            }

            XNamespace memberNs = tempSection.Name.Namespace;
            foreach (var kvp in tempVars)
            {
                string varName = kvp.Key;
                string dataType = kvp.Value ?? "Real";

                bool exists = tempSection.Elements()
                    .Any(e => e.Name.LocalName == "Member" && string.Equals((string)e.Attribute("Name"), varName, StringComparison.OrdinalIgnoreCase));

                if (!exists)
                {
                    tempSection.Add(new XElement(memberNs + "Member",
                        new XAttribute("Name", varName),
                        new XAttribute("Datatype", dataType),
                        new XAttribute("Accessibility", "Public")));
                }
            }
        }

        private string StripComments(string code)
        {
            var noBlockComments = Regex.Replace(code, @"\(\*.*?\*\)", "", RegexOptions.Singleline);
            var noLineComments = Regex.Replace(noBlockComments, @"//.*$", "", RegexOptions.Multiline);
            var noRegions = Regex.Replace(noLineComments, @"^\s*(?:REGION\b[^\r\n]*|END_REGION\b[^\r\n]*)", "", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return noRegions;
        }

        private class ConditionFactor
        {
            public bool IsComparison { get; set; }
            public string VariableName { get; set; }
            public bool IsNegated { get; set; }
            public string CompLeft { get; set; }
            public string CompRight { get; set; }
            public string CompOp { get; set; }
            public string CompType { get; set; } = "Real";
        }

        private class PreConvertItem
        {
            public string InputVar { get; set; }
            public string SrcType { get; set; }
            public string DestType { get; set; }
            public string TempVar { get; set; }
        }

        private class PreCalcItem
        {
            public string LeftVal { get; set; }
            public string Op { get; set; }
            public string RightVal { get; set; }
            public string PartOp { get; set; }
            public string SrcType { get; set; }
            public string TargetVar { get; set; }
        }

        private class IfActionItem
        {
            public string TargetVar { get; set; }
            public bool IsSet { get; set; }
            public bool IsReset { get; set; }
            public bool IsReturn { get; set; }
            public bool IsJump { get; set; }
            public string JumpLabel { get; set; }
            public string SourceValue { get; set; }
            public bool IsBinaryMath { get; set; }
            public string MathLeft { get; set; }
            public string MathRight { get; set; }
            public string MathPartOp { get; set; }
            public string MathSrcType { get; set; }
            public bool IsCalc { get; set; }
            public string CalcEquation { get; set; }
            public List<string> CalcVars { get; set; }
            public bool IsFunctionBox { get; set; }
            public string FuncPartName { get; set; }
            public string FuncSrcType { get; set; }
            public string FuncDestType { get; set; }
            public int FuncCardinality { get; set; }
            public List<Tuple<string, string>> FuncInputPins { get; set; } = new List<Tuple<string, string>>();
            public string FuncOutputPin { get; set; } = "out";
            public bool IsCascadeBox { get; set; }
            public IfActionItem SecondBox { get; set; }
            public List<PreConvertItem> PreConverts { get; set; } = new List<PreConvertItem>();
            public List<PreCalcItem> PreCalcs { get; set; } = new List<PreCalcItem>();
        }

        /// <summary>
        /// Builds a SimaticML LAD CompileUnit (<SW.Blocks.CompileUnit>) that calls an FB or FC
        /// inside Main [OB1] (or another block) with wired Input/Output/InOut parameters.
        /// </summary>
        public string BuildLadBlockCallCompileUnitXml(
            string callBlockName,
            string callBlockType,
            string instanceDbName,
            List<BlockInterfaceMember> interfaceMembers,
            Dictionary<string, string> parameterBindings,
            string tiaVersion = "V20",
            int startObjectId = 200,
            string networkTitle = null)
        {
            string flgVer = "v5";
            if (!string.IsNullOrWhiteSpace(tiaVersion))
            {
                if (tiaVersion.StartsWith("V14", StringComparison.OrdinalIgnoreCase)) flgVer = "v1";
                else if (tiaVersion.StartsWith("V15.1", StringComparison.OrdinalIgnoreCase) || tiaVersion.StartsWith("V15_1", StringComparison.OrdinalIgnoreCase)) flgVer = "v3";
                else if (tiaVersion.StartsWith("V15", StringComparison.OrdinalIgnoreCase)) flgVer = "v2";
                else if (tiaVersion.StartsWith("V16", StringComparison.OrdinalIgnoreCase) || tiaVersion.StartsWith("V17", StringComparison.OrdinalIgnoreCase)) flgVer = "v4";
            }

            int uid = 20;
            int NextCallUid() => ++uid;

            var partsSb = new StringBuilder();
            var paramsSb = new StringBuilder();
            var wiresSb = new StringBuilder();

            string bType = string.Equals(callBlockType, "FB", StringComparison.OrdinalIgnoreCase) ? "FB" : "FC";
            var callParams = (interfaceMembers ?? new List<BlockInterfaceMember>())
                .Where(m => string.Equals(m.Section, "Input", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(m.Section, "Output", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(m.Section, "InOut", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var paramAccessUids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in callParams)
            {
                string boundVal = null;
                if (parameterBindings != null && parameterBindings.TryGetValue(p.Name, out string b) && !string.IsNullOrWhiteSpace(b))
                {
                    boundVal = b.Trim();
                }

                if (!string.IsNullOrWhiteSpace(boundVal))
                {
                    int accUid = NextCallUid();
                    paramAccessUids[p.Name] = accUid;

                    string cleanTag = boundVal.Trim('\"', ' ');
                    if (cleanTag.Contains("."))
                    {
                        var comps = cleanTag.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                        partsSb.AppendLine($"              <Access Scope=\"GlobalVariable\" UId=\"{accUid}\">");
                        partsSb.AppendLine("                <Symbol>");
                        foreach (var c in comps)
                        {
                            partsSb.AppendLine($"                  <Component Name=\"{System.Security.SecurityElement.Escape(c.Trim('\"', ' '))}\" />");
                        }
                        partsSb.AppendLine("                </Symbol>");
                        partsSb.AppendLine("              </Access>");
                    }
                    else if (IsConstantLiteral(boundVal))
                    {
                        string cType = NormalizePlcDatatype(p.DataType) ?? "Bool";
                        string cVal = boundVal;
                        if (string.Equals(cType, "Bool", StringComparison.OrdinalIgnoreCase))
                            cVal = boundVal.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || boundVal == "1" ? "TRUE" : "FALSE";

                        partsSb.AppendLine($"              <Access Scope=\"LiteralConstant\" UId=\"{accUid}\">");
                        partsSb.AppendLine("                <Constant>");
                        partsSb.AppendLine($"                  <ConstantType>{System.Security.SecurityElement.Escape(cType)}</ConstantType>");
                        partsSb.AppendLine($"                  <ConstantValue>{System.Security.SecurityElement.Escape(cVal)}</ConstantValue>");
                        partsSb.AppendLine("                </Constant>");
                        partsSb.AppendLine("              </Access>");
                    }
                    else
                    {
                        partsSb.AppendLine($"              <Access Scope=\"GlobalVariable\" UId=\"{accUid}\">");
                        partsSb.AppendLine("                <Symbol>");
                        partsSb.AppendLine($"                  <Component Name=\"{System.Security.SecurityElement.Escape(cleanTag)}\" />");
                        partsSb.AppendLine("                </Symbol>");
                        partsSb.AppendLine("              </Access>");
                    }
                }

                string secName = string.Equals(p.Section, "Output", StringComparison.OrdinalIgnoreCase) ? "Output" :
                                 (string.Equals(p.Section, "InOut", StringComparison.OrdinalIgnoreCase) ? "InOut" : "Input");
                string pType = NormalizePlcDatatype(p.DataType) ?? p.DataType ?? "Bool";
                paramsSb.AppendLine($"                  <Parameter Name=\"{System.Security.SecurityElement.Escape(p.Name)}\" Section=\"{secName}\" Type=\"{System.Security.SecurityElement.Escape(pType)}\" />");
            }

            int callUid = NextCallUid();
            partsSb.AppendLine($"              <Call UId=\"{callUid}\">");
            partsSb.AppendLine($"                <CallInfo Name=\"{System.Security.SecurityElement.Escape(callBlockName)}\" BlockType=\"{bType}\">");
            if (bType == "FB" && !string.IsNullOrWhiteSpace(instanceDbName))
            {
                int instUid = NextCallUid();
                partsSb.AppendLine($"                  <Instance Scope=\"GlobalVariable\" UId=\"{instUid}\">");
                partsSb.AppendLine($"                    <Component Name=\"{System.Security.SecurityElement.Escape(instanceDbName.Trim('\"', ' '))}\" />");
                partsSb.AppendLine("                  </Instance>");
            }
            if (paramsSb.Length > 0)
            {
                partsSb.Append(paramsSb.ToString());
            }
            partsSb.AppendLine("                </CallInfo>");
            partsSb.AppendLine("              </Call>");

            // Powerrail -> Call.en
            int enWireUid = NextCallUid();
            wiresSb.AppendLine($"              <Wire UId=\"{enWireUid}\">");
            wiresSb.AppendLine("                <Powerrail />");
            wiresSb.AppendLine($"                <NameCon UId=\"{callUid}\" Name=\"en\" />");
            wiresSb.AppendLine("              </Wire>");

            foreach (var p in callParams)
            {
                int wUid = NextCallUid();
                if (paramAccessUids.TryGetValue(p.Name, out int accUid))
                {
                    wiresSb.AppendLine($"              <Wire UId=\"{wUid}\">");
                    if (string.Equals(p.Section, "Output", StringComparison.OrdinalIgnoreCase))
                    {
                        wiresSb.AppendLine($"                <NameCon UId=\"{callUid}\" Name=\"{System.Security.SecurityElement.Escape(p.Name)}\" />");
                        wiresSb.AppendLine($"                <IdentCon UId=\"{accUid}\" />");
                    }
                    else
                    {
                        wiresSb.AppendLine($"                <IdentCon UId=\"{accUid}\" />");
                        wiresSb.AppendLine($"                <NameCon UId=\"{callUid}\" Name=\"{System.Security.SecurityElement.Escape(p.Name)}\" />");
                    }
                    wiresSb.AppendLine("              </Wire>");
                }
                else
                {
                    int openUid = NextCallUid();
                    wiresSb.AppendLine($"              <Wire UId=\"{wUid}\">");
                    wiresSb.AppendLine($"                <NameCon UId=\"{callUid}\" Name=\"{System.Security.SecurityElement.Escape(p.Name)}\" />");
                    wiresSb.AppendLine($"                <OpenCon UId=\"{openUid}\" />");
                    wiresSb.AppendLine("              </Wire>");
                }
            }

            int cuId = startObjectId;
            int cmtId1 = startObjectId + 1;
            int cmtId2 = startObjectId + 2;
            int titleId1 = startObjectId + 3;
            int titleId2 = startObjectId + 4;
            string safeTitle = System.Security.SecurityElement.Escape(networkTitle ?? $"{callBlockName} Çağrısı");

            return $@"      <SW.Blocks.CompileUnit ID=""{cuId}"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/{flgVer}"">
              <Parts>
{partsSb.ToString().TrimEnd()}
              </Parts>
              <Wires>
{wiresSb.ToString().TrimEnd()}
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""{cmtId1}"" CompositionName=""Comment"">
            <ObjectList>
              <MultilingualTextItem ID=""{cmtId2}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{titleId1}"" CompositionName=""Title"">
            <ObjectList>
              <MultilingualTextItem ID=""{titleId2}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text>{safeTitle}</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>";
        }
    }
}


