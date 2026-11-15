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
    public class ParsedSclBlock
    {
        public string BlockName { get; set; }
        public string BlockType { get; set; } // "FC" or "FB"
        public int? BlockNumber { get; set; }
        public string InterfaceXml { get; set; }
        public string HeaderVersion { get; set; } = "0.1";
        public string MemoryLayout { get; set; } = "Optimized";
        public List<SclStatementInfo> Statements { get; set; } = new List<SclStatementInfo>();
        public string OriginalSclCode { get; set; }
        public string FullSclCode { get; set; }
        public XElement OriginalAttributeList { get; set; }
        public string EngineeringVersion { get; set; } = "V20";
        public Dictionary<string, string> ExternalVarTypes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DefinedGlobalSymbols { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ExistingLadderDbTypes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool HasScannedPlcSymbols { get; set; } = false;
    }

    public static class SclCodeParser
    {
        /// <summary>
        /// Parses an exported SimaticML XML file of an SCL block into interface and individual statements.
        /// </summary>
        public static ParsedSclBlock ParseBlockXml(string xmlPath)
        {
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("SimaticML XML dosyası bulunamadı.", xmlPath);

            var doc = XDocument.Load(xmlPath);
            var root = doc.Root;

            var blockElem = root?.Elements().FirstOrDefault(e =>
                e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));

            if (blockElem == null)
                throw new InvalidOperationException("Geçerli bir SW.Blocks elementi XML içerisinde bulunamadı.");

            string localName = blockElem.Name.LocalName;
            string bType = "FC";
            if (localName.EndsWith(".FB", StringComparison.OrdinalIgnoreCase)) bType = "FB";
            else if (localName.EndsWith(".FC", StringComparison.OrdinalIgnoreCase)) bType = "FC";

            var attrList = blockElem.Element("AttributeList");
            string bName = attrList?.Element("Name")?.Value ?? Path.GetFileNameWithoutExtension(xmlPath);
            int? bNum = null;
            if (int.TryParse(attrList?.Element("Number")?.Value, out int nVal) && nVal > 0)
                bNum = nVal;

            string headerVersion = attrList?.Element("HeaderVersion")?.Value ?? "0.1";
            string memLayout = attrList?.Element("MemoryLayout")?.Value ?? "Optimized";

            var ifaceElem = attrList?.Element("Interface");
            string ifaceXml = ifaceElem != null ? ifaceElem.ToString() : "<Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\"><Section Name=\"Temp\" /></Sections></Interface>";

            // Reconstruct SCL code from compile units / StructuredText
            var compileUnits = blockElem.Descendants().Where(d => d.Name.LocalName.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase)).ToList();
            var statementsList = new List<SclStatementInfo>();
            var fullCodeSb = new StringBuilder();
            var origCodeSb = new StringBuilder();

            // Reconstruct Interface text for display
            string ifaceDisplay = FormatInterfaceToScl(ifaceElem);
            if (!string.IsNullOrWhiteSpace(ifaceDisplay))
            {
                fullCodeSb.AppendLine(ifaceDisplay);
                fullCodeSb.AppendLine("BEGIN");

                origCodeSb.AppendLine(ifaceDisplay);
                origCodeSb.AppendLine("BEGIN");
            }

            int stIndex = 1;
            if (compileUnits.Count > 0)
            {
                foreach (var cu in compileUnits)
                {
                    string cuTitle = cu.Descendants().FirstOrDefault(d => d.Name.LocalName.EndsWith("Title", StringComparison.OrdinalIgnoreCase))
                        ?.Descendants().FirstOrDefault(d => d.Name.LocalName.EndsWith("Text", StringComparison.OrdinalIgnoreCase))?.Value;

                    var stElem = cu.Descendants().FirstOrDefault(d => d.Name.LocalName.EndsWith("StructuredText", StringComparison.OrdinalIgnoreCase));
                    if (stElem != null)
                    {
                        string sclChunk = ReconstructStructuredText(stElem);
                        if (!string.IsNullOrWhiteSpace(sclChunk))
                        {
                            if (!string.IsNullOrWhiteSpace(cuTitle))
                            {
                                origCodeSb.AppendLine($"// Network: {cuTitle}");
                            }
                            origCodeSb.AppendLine(sclChunk.Trim());

                            var splitStmts = SplitSclStatements(sclChunk);
                            foreach (var rawStmt in splitStmts)
                            {
                                var expanded = ExpandIfBranches(rawStmt);
                                foreach (var stmt in expanded)
                                {
                                    var info = new SclStatementInfo
                                    {
                                        Index = stIndex++,
                                        Title = !string.IsNullOrWhiteSpace(cuTitle) ? cuTitle : $"Network {stIndex - 1}",
                                        RawSclCode = stmt.Trim(),
                                        OriginalStructuredTextXml = null
                                    };
                                    statementsList.Add(info);
                                    fullCodeSb.AppendLine(stmt.Trim());
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                // Fallback: StructuredText elements directly under block
                var stElements = blockElem.Descendants().Where(d => d.Name.LocalName.EndsWith("StructuredText", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var stElem in stElements)
                {
                    string sclChunk = ReconstructStructuredText(stElem);
                    if (!string.IsNullOrWhiteSpace(sclChunk))
                    {
                        origCodeSb.AppendLine(sclChunk.Trim());

                        var splitStmts = SplitSclStatements(sclChunk);
                        foreach (var rawStmt in splitStmts)
                        {
                            var expanded = ExpandIfBranches(rawStmt);
                            foreach (var stmt in expanded)
                            {
                                var info = new SclStatementInfo
                                {
                                    Index = stIndex++,
                                    Title = $"Network {stIndex - 1}",
                                    RawSclCode = stmt.Trim(),
                                    OriginalStructuredTextXml = null
                                };
                                statementsList.Add(info);
                                fullCodeSb.AppendLine(stmt.Trim());
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(ifaceDisplay))
            {
                string endKw = bType == "FB" ? "END_FUNCTION_BLOCK" : "END_FUNCTION";
                fullCodeSb.AppendLine(endKw);
                origCodeSb.AppendLine(endKw);
            }

            string engVersion = root?.Element("Engineering")?.Attribute("version")?.Value ?? "V20";

            return new ParsedSclBlock
            {
                BlockName = bName,
                BlockType = bType,
                BlockNumber = bNum,
                InterfaceXml = ifaceXml,
                HeaderVersion = headerVersion,
                MemoryLayout = memLayout,
                Statements = statementsList,
                OriginalSclCode = origCodeSb.ToString(),
                FullSclCode = fullCodeSb.ToString(),
                OriginalAttributeList = attrList != null ? new XElement(attrList) : null,
                EngineeringVersion = engVersion
            };
        }

        private static int _loopCounter = 0;
        private static int _caseCounter = 0;

        /// <summary>
        /// Splits a block of SCL code into individual statements while keeping compound blocks intact.
        /// Supports multi-line (* ... *) comments, REGION / END_REGION blocks, and multi-line parenthesized expressions.
        /// </summary>
        public static List<string> SplitSclStatements(string sclCode)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(sclCode)) return results;

            var lines = sclCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var currentBuffer = new StringBuilder();
            int ifDepth = 0;
            int caseDepth = 0;
            int loopDepth = 0;
            int parenDepth = 0;
            bool inBlockComment = false;

            foreach (var rawLine in lines)
            {
                string line = rawLine;
                string trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    if (currentBuffer.Length > 0)
                        currentBuffer.AppendLine(line);
                    continue;
                }

                // Extract code outside of //, (* ... *), '...', and "..." while updating inBlockComment and parenDepth
                string codeOnly = ExtractCodeFromLine(trimmed, ref inBlockComment, ref parenDepth);
                string cleanCode = codeOnly.Trim();

                // Pure comment line
                if (string.IsNullOrEmpty(cleanCode))
                {
                    currentBuffer.AppendLine(line);
                    continue;
                }

                // Handle REGION / END_REGION (Slide 13)
                if (Regex.IsMatch(cleanCode, @"^REGION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    string regionTitle = cleanCode.Length > 6 ? cleanCode.Substring(6).Trim() : "";
                    currentBuffer.AppendLine($"// [REGION] {regionTitle}");
                    continue;
                }
                if (Regex.IsMatch(cleanCode, @"^END_REGION\b;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue;
                }

                currentBuffer.AppendLine(line);
                string upper = cleanCode.ToUpperInvariant();

                // IF tracking (count all occurrences on the line)
                int ifOpens = Regex.Matches(upper, @"\bIF\b").Count;
                int ifCloses = Regex.Matches(upper, @"\bEND_IF\b").Count;
                ifDepth = Math.Max(0, ifDepth + ifOpens - ifCloses);

                // CASE tracking
                int caseOpens = Regex.Matches(upper, @"\bCASE\b").Count;
                int caseCloses = Regex.Matches(upper, @"\bEND_CASE\b").Count;
                caseDepth = Math.Max(0, caseDepth + caseOpens - caseCloses);

                // FOR / WHILE / REPEAT tracking (note: UNTIL does not close a loop, END_REPEAT closes REPEAT)
                int loopOpens = Regex.Matches(upper, @"\b(FOR|WHILE|REPEAT)\b").Count;
                int loopCloses = Regex.Matches(upper, @"\b(END_FOR|END_WHILE|END_REPEAT)\b").Count;
                loopDepth = Math.Max(0, loopDepth + loopOpens - loopCloses);

                // If not inside any compound statement or unclosed parentheses, check for terminating semicolon
                if (ifDepth == 0 && caseDepth == 0 && loopDepth == 0 && parenDepth == 0 && upper.EndsWith(";"))
                {
                    string stmt = currentBuffer.ToString().Trim();
                    if (!string.IsNullOrEmpty(stmt) && !string.IsNullOrWhiteSpace(StripSclComments(stmt)))
                    {
                        results.Add(stmt);
                    }
                    currentBuffer.Clear();
                }
            }

            if (currentBuffer.Length > 0)
            {
                string rem = currentBuffer.ToString().Trim();
                if (!string.IsNullOrEmpty(rem) && !string.IsNullOrWhiteSpace(StripSclComments(rem)))
                    results.Add(rem);
            }

            return results;
        }

        private static string ExtractCodeFromLine(string line, ref bool inBlockComment, ref int parenDepth)
        {
            if (string.IsNullOrEmpty(line)) return "";
            var sb = new StringBuilder();
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int i = 0;
            int len = line.Length;

            while (i < len)
            {
                if (inBlockComment)
                {
                    int closeIdx = line.IndexOf("*)", i, StringComparison.Ordinal);
                    if (closeIdx < 0)
                    {
                        return sb.ToString();
                    }
                    inBlockComment = false;
                    i = closeIdx + 2;
                    sb.Append(' ');
                    continue;
                }

                char ch = line[i];

                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (ch == '/' && i + 1 < len && line[i + 1] == '/')
                    {
                        break; // Rest of line is // comment
                    }
                    if (ch == '(' && i + 1 < len && line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i += 2;
                        continue;
                    }
                }

                if (ch == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    sb.Append(' ');
                    i++;
                    continue;
                }
                if (ch == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    sb.Append(' ');
                    i++;
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (ch == '(') parenDepth++;
                    else if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);
                    sb.Append(ch);
                }
                else
                {
                    sb.Append(' ');
                }

                i++;
            }

            return sb.ToString();
        }

        private class ParsedIfBranch
        {
            public string Kind { get; set; } // "IF", "ELSIF", "ELSE"
            public string Condition { get; set; }
            public string Body { get; set; }
        }

        private class ParsedCaseBranch
        {
            public string LabelSpec { get; set; }
            public string Condition { get; set; }
            public string Body { get; set; }
        }

        /// <summary>
        /// Recursively flattens compound and nested IF / ELSIF / ELSE, CASE ... OF, and FOR / WHILE / REPEAT
        /// control structures into Ladder-convertible statements.
        /// </summary>
        public static List<string> ExpandIfBranches(string stmt)
        {
            if (string.IsNullOrWhiteSpace(stmt))
                return new List<string>();

            string clean = stmt.Trim();

            // Extract leading comment lines so they aren't lost
            var leadingComments = new StringBuilder();
            string remaining = clean;
            while (true)
            {
                var commMatch = Regex.Match(remaining, @"^\s*(//[^\r\n]*|\(\*[\s\S]*?\*\))\s*(?:[\r\n]+|$)");
                if (commMatch.Success && commMatch.Length > 0)
                {
                    leadingComments.AppendLine(commMatch.Groups[1].Value.Trim());
                    remaining = remaining.Substring(commMatch.Length).Trim();
                }
                else
                {
                    break;
                }
            }

            string noComments = StripSclComments(remaining).Trim();
            if (string.IsNullOrWhiteSpace(noComments))
                return new List<string>();

            List<string> expandedResults = null;

            // 1. Top-level CASE ... OF ... END_CASE; (Slide 24)
            if (Regex.IsMatch(noComments, @"^CASE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (TryExpandCaseBlock(noComments, out List<string> caseExpanded) && caseExpanded.Count > 0)
                {
                    expandedResults = caseExpanded;
                }
            }
            // 2. Top-level FOR ... TO ... [BY ...] DO ... END_FOR; (Slides 2, 25, 27, 30)
            else if (Regex.IsMatch(noComments, @"^FOR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (TryExpandForLoop(noComments, out List<string> forExpanded) && forExpanded.Count > 0)
                {
                    expandedResults = forExpanded;
                }
            }
            // 3. Top-level WHILE ... DO ... END_WHILE; (Slides 25, 26, 27)
            else if (Regex.IsMatch(noComments, @"^WHILE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (TryExpandWhileLoop(noComments, out List<string> whileExpanded) && whileExpanded.Count > 0)
                {
                    expandedResults = whileExpanded;
                }
            }
            // 4. Top-level REPEAT ... UNTIL ... END_REPEAT; (Slides 25, 26, 27)
            else if (Regex.IsMatch(noComments, @"^REPEAT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (TryExpandRepeatLoop(noComments, out List<string> repeatExpanded) && repeatExpanded.Count > 0)
                {
                    expandedResults = repeatExpanded;
                }
            }
            // 5. Top-level IF ... THEN ... [ELSIF ...] [ELSE ...] END_IF; (Slides 23, 27)
            else if (Regex.IsMatch(noComments, @"^IF\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (TryParseTopLevelIfBlock(noComments, out List<ParsedIfBranch> branches) && branches.Count > 0)
                {
                    expandedResults = ExpandParsedIfBranches(branches);
                }
            }

            if (expandedResults == null || expandedResults.Count == 0)
                return new List<string> { stmt };

            if (leadingComments.Length > 0)
            {
                expandedResults[0] = leadingComments.ToString().TrimEnd() + "\r\n" + expandedResults[0];
            }

            return expandedResults;
        }

        private static List<string> ExpandParsedIfBranches(List<ParsedIfBranch> branches)
        {
            var expandedResults = new List<string>();
            var negatedPriorConditions = new List<string>();

            foreach (var branch in branches)
            {
                string effectiveCond;
                if (branch.Kind == "IF" || branch.Kind == "ELSIF")
                {
                    if (negatedPriorConditions.Count == 0)
                    {
                        effectiveCond = branch.Condition.Trim();
                    }
                    else
                    {
                        effectiveCond = string.Join(" AND ", negatedPriorConditions) + $" AND ({branch.Condition.Trim()})";
                    }
                    negatedPriorConditions.Add($"NOT ({branch.Condition.Trim()})");
                }
                else // ELSE
                {
                    if (negatedPriorConditions.Count == 0)
                        continue;
                    effectiveCond = string.Join(" AND ", negatedPriorConditions);
                }

                var subStatements = SplitSclStatements(branch.Body);
                var plainActionBuffer = new StringBuilder();

                void FlushPlainActions()
                {
                    string actionsText = plainActionBuffer.ToString().Trim();
                    if (!string.IsNullOrEmpty(actionsText))
                    {
                        string indented = string.Join("\r\n    ",
                            actionsText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(l => l.Trim()));
                        expandedResults.Add($"IF {effectiveCond} THEN\r\n    {indented}\r\nEND_IF;");
                        plainActionBuffer.Clear();
                    }
                }

                foreach (var subStmt in subStatements)
                {
                    string subClean = StripSclComments(subStmt).Trim();
                    if (string.IsNullOrEmpty(subClean))
                        continue;

                    if (Regex.IsMatch(subClean, @"^(?:IF|CASE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        FlushPlainActions();

                        var nestedExpanded = ExpandIfBranches(subClean);
                        foreach (var nestedItem in nestedExpanded)
                        {
                            string nestedClean = StripSclComments(nestedItem).Trim();
                            if (TryParseTopLevelIfBlock(nestedClean, out List<ParsedIfBranch> nestedBranches) &&
                                nestedBranches.Count == 1 &&
                                nestedBranches[0].Kind == "IF")
                            {
                                string innerCond = nestedBranches[0].Condition.Trim();
                                string innerBody = nestedBranches[0].Body.Trim();
                                string indentedInner = string.Join("\r\n    ",
                                    innerBody.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                             .Select(l => l.Trim()));
                                expandedResults.Add($"IF ({effectiveCond}) AND ({innerCond}) THEN\r\n    {indentedInner}\r\nEND_IF;");
                            }
                            else if (Regex.IsMatch(nestedClean, @"^[a-zA-Z_][a-zA-Z0-9_]*\s*:\s*;?$"))
                            {
                                // Label from CASE state-machine expansion
                                expandedResults.Add(nestedClean);
                            }
                            else
                            {
                                expandedResults.Add($"IF {effectiveCond} THEN\r\n    {nestedItem.Trim()}\r\nEND_IF;");
                            }
                        }
                    }
                    else if (Regex.IsMatch(subClean, @"^(?:FOR|WHILE|REPEAT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        FlushPlainActions();

                        var loopExpanded = ExpandIfBranches(subClean);
                        if (loopExpanded.Count > 0)
                        {
                            string skipLbl = $"IF_SKIP_{++_loopCounter}";
                            expandedResults.Add($"IF NOT ({effectiveCond}) THEN\r\n    GOTO {skipLbl};\r\nEND_IF;");
                            expandedResults.AddRange(loopExpanded);
                            expandedResults.Add($"{skipLbl}:;");
                        }
                    }
                    else
                    {
                        if (IsSimpleBooleanAction(subClean))
                        {
                            plainActionBuffer.AppendLine(subClean);
                        }
                        else
                        {
                            FlushPlainActions();
                            expandedResults.Add($"IF {effectiveCond} THEN\r\n    {subClean}\r\nEND_IF;");
                        }
                    }
                }

                FlushPlainActions();
            }

            return expandedResults;
        }

        private static bool IsSimpleBooleanAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            string clean = StripSclComments(action).Trim().TrimEnd(';');

            if (Regex.IsMatch(clean, @"^(?:RETURN|GOTO\s+[a-zA-Z_][a-zA-Z0-9_]*|SET\s*\([^\)]+\)|RESET\s*\([^\)]+\))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            var assign = Regex.Match(clean, @"^[#a-zA-Z_][a-zA-Z0-9_\.]*\s*:=\s*([\s\S]+)$");
            if (assign.Success)
            {
                string val = assign.Groups[1].Value.Trim();
                if (val.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("BOOL#TRUE", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("BOOL#FALSE", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("BOOL#1", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("BOOL#0", StringComparison.OrdinalIgnoreCase) ||
                    val == "1" || val == "0")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryExpandCaseBlock(string code, out List<string> results)
        {
            results = new List<string>();
            if (!TryParseTopLevelCaseBlock(code, out string selector, out List<ParsedCaseBranch> branches, out string elseBody))
                return false;
            if (branches.Count == 0)
                return false;

            // Check if selector variable is modified inside any branch body (state machine pattern)
            string escapedSelector = Regex.Escape(selector.Trim());
            bool selectorModified = branches.Any(b =>
                Regex.IsMatch(b.Body ?? "", $@"{escapedSelector}\s*:=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

            if (!selectorModified)
            {
                // Build equivalent IF / ELSIF / ELSE block and expand it cleanly
                var ifBranches = new List<ParsedIfBranch>();
                for (int i = 0; i < branches.Count; i++)
                {
                    ifBranches.Add(new ParsedIfBranch
                    {
                        Kind = i == 0 ? "IF" : "ELSIF",
                        Condition = branches[i].Condition,
                        Body = branches[i].Body
                    });
                }
                if (!string.IsNullOrWhiteSpace(elseBody))
                {
                    ifBranches.Add(new ParsedIfBranch
                    {
                        Kind = "ELSE",
                        Condition = null,
                        Body = elseBody
                    });
                }

                results = ExpandParsedIfBranches(ifBranches);
                return results.Count > 0;
            }
            else
            {
                // Selector is modified in branches (state machine): jump to CASE_END_n after each matching branch
                string caseEndLbl = $"CASE_END_{++_caseCounter}";
                for (int i = 0; i < branches.Count; i++)
                {
                    string bBody = branches[i].Body.Trim();
                    string bodyWithJump = bBody + $"\r\nGOTO {caseEndLbl};";
                    string ifStmt = $"IF {branches[i].Condition} THEN\r\n    {bodyWithJump}\r\nEND_IF;";
                    results.AddRange(ExpandIfBranches(ifStmt));
                }

                if (!string.IsNullOrWhiteSpace(elseBody))
                {
                    var elseStmts = SplitSclStatements(elseBody);
                    foreach (var es in elseStmts)
                    {
                        results.AddRange(ExpandIfBranches(es));
                    }
                }

                results.Add($"{caseEndLbl}:;");
                return results.Count > 0;
            }
        }

        private static bool TryParseTopLevelCaseBlock(string code, out string selector, out List<ParsedCaseBranch> branches, out string elseBody)
        {
            selector = null;
            branches = new List<ParsedCaseBranch>();
            elseBody = null;

            if (string.IsNullOrWhiteSpace(code)) return false;
            string trimmed = code.Trim();
            if (!MatchKeywordAt(trimmed, 0, "CASE")) return false;

            int i = 4; // Past "CASE"
            int len = trimmed.Length;
            int parenDepth = 0;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            // 1. Read selector up to top-level "OF"
            int selectorStart = i;
            int ofIndex = -1;
            while (i < len)
            {
                char ch = trimmed[i];
                if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; i++; continue; }
                if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; i++; continue; }
                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (ch == '(') { parenDepth++; i++; continue; }
                    if (ch == ')') { parenDepth = Math.Max(0, parenDepth - 1); i++; continue; }
                    if (parenDepth == 0 && MatchKeywordAt(trimmed, i, "OF"))
                    {
                        ofIndex = i;
                        break;
                    }
                }
                i++;
            }

            if (ofIndex < 0) return false;
            selector = trimmed.Substring(selectorStart, ofIndex - selectorStart).Trim();
            if (string.IsNullOrWhiteSpace(selector)) return false;

            i = ofIndex + 2; // Past "OF"
            int bodyRegionStart = i;

            // 2. Scan inside CASE .. END_CASE
            int nestedCaseDepth = 0;
            int nestedIfDepth = 0;
            int nestedLoopDepth = 0;
            parenDepth = 0;
            inSingleQuote = false;
            inDoubleQuote = false;

            string currentLabelSpec = null;
            int currentBranchBodyStart = -1;
            int lastTopLevelBoundary = i; // Position after OF or after the last top-level ';'
            int elseBodyStart = -1;

            while (i < len)
            {
                char ch = trimmed[i];
                if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; i++; continue; }
                if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; i++; continue; }

                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (ch == '(') { parenDepth++; i++; continue; }
                    if (ch == ')') { parenDepth = Math.Max(0, parenDepth - 1); i++; continue; }

                    if (parenDepth == 0)
                    {
                        if (MatchKeywordAt(trimmed, i, "IF")) { nestedIfDepth++; i += 2; continue; }
                        if (MatchKeywordAt(trimmed, i, "END_IF")) { nestedIfDepth = Math.Max(0, nestedIfDepth - 1); i += 6; continue; }
                        if (MatchKeywordAt(trimmed, i, "FOR")) { nestedLoopDepth++; i += 3; continue; }
                        if (MatchKeywordAt(trimmed, i, "WHILE")) { nestedLoopDepth++; i += 5; continue; }
                        if (MatchKeywordAt(trimmed, i, "REPEAT")) { nestedLoopDepth++; i += 6; continue; }
                        if (MatchKeywordAt(trimmed, i, "END_FOR")) { nestedLoopDepth = Math.Max(0, nestedLoopDepth - 1); i += 7; continue; }
                        if (MatchKeywordAt(trimmed, i, "END_WHILE")) { nestedLoopDepth = Math.Max(0, nestedLoopDepth - 1); i += 9; continue; }
                        if (MatchKeywordAt(trimmed, i, "END_REPEAT")) { nestedLoopDepth = Math.Max(0, nestedLoopDepth - 1); i += 10; continue; }
                        if (MatchKeywordAt(trimmed, i, "CASE")) { nestedCaseDepth++; i += 4; continue; }

                        if (MatchKeywordAt(trimmed, i, "END_CASE"))
                        {
                            if (nestedCaseDepth > 0)
                            {
                                nestedCaseDepth--;
                                i += 8;
                                continue;
                            }

                            // Closing top-level END_CASE
                            if (elseBodyStart >= 0)
                            {
                                elseBody = trimmed.Substring(elseBodyStart, i - elseBodyStart).Trim();
                            }
                            else if (currentLabelSpec != null && currentBranchBodyStart >= 0)
                            {
                                string bBody = trimmed.Substring(currentBranchBodyStart, i - currentBranchBodyStart).Trim();
                                string cond = BuildCaseBranchCondition(selector, currentLabelSpec);
                                if (string.IsNullOrEmpty(cond)) return false;
                                branches.Add(new ParsedCaseBranch
                                {
                                    LabelSpec = currentLabelSpec,
                                    Condition = cond,
                                    Body = bBody
                                });
                            }

                            i += 8; // Past "END_CASE"
                            string tail = trimmed.Substring(i).Trim();
                            return (tail == "" || tail == ";") && branches.Count > 0;
                        }

                        if (nestedCaseDepth == 0 && nestedIfDepth == 0 && nestedLoopDepth == 0)
                        {
                            if (elseBodyStart < 0 && MatchKeywordAt(trimmed, i, "ELSE"))
                            {
                                if (currentLabelSpec != null && currentBranchBodyStart >= 0)
                                {
                                    string bBody = trimmed.Substring(currentBranchBodyStart, i - currentBranchBodyStart).Trim();
                                    string cond = BuildCaseBranchCondition(selector, currentLabelSpec);
                                    if (string.IsNullOrEmpty(cond)) return false;
                                    branches.Add(new ParsedCaseBranch
                                    {
                                        LabelSpec = currentLabelSpec,
                                        Condition = cond,
                                        Body = bBody
                                    });
                                    currentLabelSpec = null;
                                }

                                i += 4; // Past "ELSE"
                                // Optional ':' after ELSE in some SCL dialects
                                while (i < len && char.IsWhiteSpace(trimmed[i])) i++;
                                if (i < len && trimmed[i] == ':' && (i + 1 >= len || trimmed[i + 1] != '=')) i++;
                                elseBodyStart = i;
                                continue;
                            }

                            if (elseBodyStart < 0 && ch == ';')
                            {
                                lastTopLevelBoundary = i + 1;
                                i++;
                                continue;
                            }

                            // Check for branch colon ':' (not ':=')
                            if (elseBodyStart < 0 && ch == ':' && (i + 1 >= len || trimmed[i + 1] != '='))
                            {
                                string candidateLabelSpec = trimmed.Substring(lastTopLevelBoundary, i - lastTopLevelBoundary).Trim();
                                if (!string.IsNullOrWhiteSpace(candidateLabelSpec))
                                {
                                    if (currentLabelSpec != null && currentBranchBodyStart >= 0)
                                    {
                                        int prevBodyEnd = lastTopLevelBoundary;
                                        if (prevBodyEnd > currentBranchBodyStart)
                                        {
                                            string prevBody = trimmed.Substring(currentBranchBodyStart, prevBodyEnd - currentBranchBodyStart).Trim();
                                            string prevCond = BuildCaseBranchCondition(selector, currentLabelSpec);
                                            if (string.IsNullOrEmpty(prevCond)) return false;
                                            branches.Add(new ParsedCaseBranch
                                            {
                                                LabelSpec = currentLabelSpec,
                                                Condition = prevCond,
                                                Body = prevBody
                                            });
                                        }
                                    }

                                    currentLabelSpec = candidateLabelSpec;
                                    currentBranchBodyStart = i + 1;
                                    lastTopLevelBoundary = i + 1;
                                    i++;
                                    continue;
                                }
                            }
                        }
                    }
                }

                i++;
            }

            return false;
        }

        private static string BuildCaseBranchCondition(string selector, string labelSpec)
        {
            if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(labelSpec))
                return null;

            var parts = new List<string>();
            int parenDepth = 0;
            bool inSingle = false;
            bool inDouble = false;
            int start = 0;

            for (int i = 0; i < labelSpec.Length; i++)
            {
                char c = labelSpec[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (!inSingle && !inDouble)
                {
                    if (c == '(') parenDepth++;
                    else if (c == ')') parenDepth = Math.Max(0, parenDepth - 1);
                    else if (parenDepth == 0 && c == ',')
                    {
                        parts.Add(labelSpec.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
            }
            if (start < labelSpec.Length)
                parts.Add(labelSpec.Substring(start).Trim());

            var conds = new List<string>();
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var rangeMatch = Regex.Match(p, @"^(.+?)\s*\.\.\s*(.+)$");
                if (rangeMatch.Success)
                {
                    string low = rangeMatch.Groups[1].Value.Trim();
                    string high = rangeMatch.Groups[2].Value.Trim();
                    conds.Add($"(({selector} >= {low}) AND ({selector} <= {high}))");
                }
                else
                {
                    conds.Add($"({selector} = {p})");
                }
            }

            if (conds.Count == 0) return null;
            return conds.Count == 1 ? conds[0] : "(" + string.Join(" OR ", conds) + ")";
        }

        private static bool TryExpandForLoop(string code, out List<string> results)
        {
            results = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) return false;
            string trimmed = code.Trim();
            if (!MatchKeywordAt(trimmed, 0, "FOR")) return false;

            // Match FOR <var> := <start> TO <end> [BY <step>] DO <body> END_FOR;
            var headerMatch = Regex.Match(
                trimmed,
                @"^FOR\s+(.+?)\s*:=\s*(.+?)\s+TO\s+(.+?)(?:\s+BY\s+(.+?))?\s+DO\b([\s\S]+)\bEND_FOR\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!headerMatch.Success) return false;

            string loopVar = headerMatch.Groups[1].Value.Trim();
            string startExpr = headerMatch.Groups[2].Value.Trim();
            string endExpr = headerMatch.Groups[3].Value.Trim();
            string stepExpr = headerMatch.Groups[4].Success ? headerMatch.Groups[4].Value.Trim() : "1";
            string body = headerMatch.Groups[5].Value.Trim();

            if (string.IsNullOrWhiteSpace(loopVar) || string.IsNullOrWhiteSpace(startExpr) || string.IsNullOrWhiteSpace(endExpr))
                return false;

            int loopId = ++_loopCounter;
            string startLbl = $"LOOP_START_{loopId}";
            string nextLbl = $"LOOP_NEXT_{loopId}";
            string exitLbl = $"LOOP_EXIT_{loopId}";

            string cleanStep = stepExpr.Trim('(', ')', ' ');
            bool isNegativeStep = cleanStep.StartsWith("-");
            string cmpOp = isNegativeStep ? "<" : ">";
            string absStep = isNegativeStep ? cleanStep.Substring(1).Trim() : cleanStep;
            string stepOp = isNegativeStep ? "-" : "+";

            string rewrittenBody = RewriteLoopControlKeywords(body, nextLbl, exitLbl, out bool usedContinue, out _);

            // 1. Initialize loop counter: #i := startExpr;
            results.Add($"{loopVar} := {startExpr};");

            // 2. Loop start label
            results.Add($"{startLbl}:;");

            // 3. Loop termination check: IF #i > endExpr THEN GOTO LOOP_EXIT_n; END_IF;
            results.Add($"IF {loopVar} {cmpOp} ({endExpr}) THEN\r\n    GOTO {exitLbl};\r\nEND_IF;");

            // 4. Loop body statements (recursively expanded)
            var bodyStmts = SplitSclStatements(rewrittenBody);
            foreach (var bs in bodyStmts)
            {
                results.AddRange(ExpandIfBranches(bs));
            }

            // 5. Optional CONTINUE target label before step increment
            if (usedContinue)
            {
                results.Add($"{nextLbl}:;");
            }

            // 6. Step increment: #i := #i + step;
            results.Add($"{loopVar} := {loopVar} {stepOp} {absStep};");

            // 7. Jump back to loop start
            results.Add($"GOTO {startLbl};");

            // 8. Loop exit label
            results.Add($"{exitLbl}:;");

            return true;
        }

        private static bool TryExpandWhileLoop(string code, out List<string> results)
        {
            results = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) return false;
            string trimmed = code.Trim();
            if (!MatchKeywordAt(trimmed, 0, "WHILE")) return false;

            var match = Regex.Match(
                trimmed,
                @"^WHILE\s+([\s\S]+?)\s+DO\b([\s\S]+)\bEND_WHILE\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!match.Success) return false;

            string cond = match.Groups[1].Value.Trim();
            string body = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(cond)) return false;

            int loopId = ++_loopCounter;
            string startLbl = $"LOOP_START_{loopId}";
            string exitLbl = $"LOOP_EXIT_{loopId}";

            string rewrittenBody = RewriteLoopControlKeywords(body, startLbl, exitLbl, out _, out _);

            results.Add($"{startLbl}:;");
            results.Add($"IF NOT ({cond}) THEN\r\n    GOTO {exitLbl};\r\nEND_IF;");

            var bodyStmts = SplitSclStatements(rewrittenBody);
            foreach (var bs in bodyStmts)
            {
                results.AddRange(ExpandIfBranches(bs));
            }

            results.Add($"GOTO {startLbl};");
            results.Add($"{exitLbl}:;");

            return true;
        }

        private static bool TryExpandRepeatLoop(string code, out List<string> results)
        {
            results = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) return false;
            string trimmed = code.Trim();
            if (!MatchKeywordAt(trimmed, 0, "REPEAT")) return false;

            // Find the top-level UNTIL before the final END_REPEAT
            int i = 6; // Past "REPEAT"
            int len = trimmed.Length;
            int nestedRepeatDepth = 0;
            bool inSingle = false;
            bool inDouble = false;
            int untilIdx = -1;
            int endRepeatIdx = -1;

            while (i < len)
            {
                char ch = trimmed[i];
                if (ch == '\'' && !inDouble) { inSingle = !inSingle; i++; continue; }
                if (ch == '"' && !inSingle) { inDouble = !inDouble; i++; continue; }
                if (!inSingle && !inDouble)
                {
                    if (MatchKeywordAt(trimmed, i, "REPEAT")) { nestedRepeatDepth++; i += 6; continue; }
                    if (MatchKeywordAt(trimmed, i, "END_REPEAT"))
                    {
                        if (nestedRepeatDepth > 0)
                        {
                            nestedRepeatDepth--;
                            i += 10;
                            continue;
                        }
                        endRepeatIdx = i;
                        break;
                    }
                    if (nestedRepeatDepth == 0 && MatchKeywordAt(trimmed, i, "UNTIL"))
                    {
                        untilIdx = i;
                    }
                }
                i++;
            }

            if (untilIdx < 0 || endRepeatIdx < 0 || untilIdx >= endRepeatIdx)
                return false;

            string body = trimmed.Substring(6, untilIdx - 6).Trim();
            string cond = trimmed.Substring(untilIdx + 5, endRepeatIdx - (untilIdx + 5)).Trim().TrimEnd(';').Trim();
            if (string.IsNullOrWhiteSpace(cond)) return false;

            int loopId = ++_loopCounter;
            string startLbl = $"LOOP_START_{loopId}";
            string nextLbl = $"LOOP_NEXT_{loopId}";
            string exitLbl = $"LOOP_EXIT_{loopId}";

            string rewrittenBody = RewriteLoopControlKeywords(body, nextLbl, exitLbl, out bool usedContinue, out bool usedExit);

            results.Add($"{startLbl}:;");

            var bodyStmts = SplitSclStatements(rewrittenBody);
            foreach (var bs in bodyStmts)
            {
                results.AddRange(ExpandIfBranches(bs));
            }

            if (usedContinue)
            {
                results.Add($"{nextLbl}:;");
            }

            results.Add($"IF NOT ({cond}) THEN\r\n    GOTO {startLbl};\r\nEND_IF;");

            if (usedExit)
            {
                results.Add($"{exitLbl}:;");
            }

            return true;
        }

        /// <summary>
        /// Replaces CONTINUE and EXIT statements at the current loop depth (ignoring any inner nested FOR/WHILE/REPEAT loops)
        /// with GOTO &lt;continueLabel&gt; and GOTO &lt;exitLabel&gt;.
        /// </summary>
        private static string RewriteLoopControlKeywords(string body, string continueLabel, string exitLabel, out bool usedContinue, out bool usedExit)
        {
            usedContinue = false;
            usedExit = false;
            if (string.IsNullOrWhiteSpace(body)) return body;

            var sb = new StringBuilder();
            int i = 0;
            int len = body.Length;
            int innerLoopDepth = 0;
            bool inSingle = false;
            bool inDouble = false;

            while (i < len)
            {
                char ch = body[i];
                if (ch == '\'' && !inDouble) { inSingle = !inSingle; sb.Append(ch); i++; continue; }
                if (ch == '"' && !inSingle) { inDouble = !inDouble; sb.Append(ch); i++; continue; }

                if (!inSingle && !inDouble)
                {
                    if (MatchKeywordAt(body, i, "FOR")) { innerLoopDepth++; sb.Append("FOR"); i += 3; continue; }
                    if (MatchKeywordAt(body, i, "WHILE")) { innerLoopDepth++; sb.Append("WHILE"); i += 5; continue; }
                    if (MatchKeywordAt(body, i, "REPEAT")) { innerLoopDepth++; sb.Append("REPEAT"); i += 6; continue; }
                    if (MatchKeywordAt(body, i, "END_FOR")) { innerLoopDepth = Math.Max(0, innerLoopDepth - 1); sb.Append("END_FOR"); i += 7; continue; }
                    if (MatchKeywordAt(body, i, "END_WHILE")) { innerLoopDepth = Math.Max(0, innerLoopDepth - 1); sb.Append("END_WHILE"); i += 9; continue; }
                    if (MatchKeywordAt(body, i, "END_REPEAT")) { innerLoopDepth = Math.Max(0, innerLoopDepth - 1); sb.Append("END_REPEAT"); i += 10; continue; }

                    if (innerLoopDepth == 0)
                    {
                        if (MatchKeywordAt(body, i, "CONTINUE"))
                        {
                            usedContinue = true;
                            sb.Append($"GOTO {continueLabel}");
                            i += 8;
                            continue;
                        }
                        if (MatchKeywordAt(body, i, "EXIT"))
                        {
                            usedExit = true;
                            sb.Append($"GOTO {exitLabel}");
                            i += 4;
                            continue;
                        }
                    }
                }

                sb.Append(ch);
                i++;
            }

            return sb.ToString();
        }

        private static string StripSclComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string noBlock = Regex.Replace(code, @"\(\*[\s\S]*?\*\)", "");
            string noLine = Regex.Replace(noBlock, @"//.*$", "", RegexOptions.Multiline);
            string noRegions = Regex.Replace(noLine, @"^\s*(?:REGION\b[^\r\n]*|END_REGION\b[^\r\n]*)", "", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return noRegions;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool MatchKeywordAt(string text, int index, string keyword)
        {
            if (index < 0 || index + keyword.Length > text.Length)
                return false;
            if (index > 0 && IsWordChar(text[index - 1]))
                return false;
            if (index + keyword.Length < text.Length && IsWordChar(text[index + keyword.Length]))
                return false;
            return string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static bool TryParseTopLevelIfBlock(string code, out List<ParsedIfBranch> branches)
        {
            branches = new List<ParsedIfBranch>();
            if (string.IsNullOrWhiteSpace(code)) return false;

            string trimmed = code.Trim();
            if (!MatchKeywordAt(trimmed, 0, "IF"))
                return false;

            int i = 2; // Past "IF"
            int len = trimmed.Length;
            int parenDepth = 0;
            int nestedIfDepth = 0;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            string currentKind = "IF";
            string currentCondition = null;
            bool readingCondition = true;
            int segmentStart = i;

            while (i < len)
            {
                char ch = trimmed[i];

                if (ch == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    i++;
                    continue;
                }
                if (ch == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    i++;
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        i++;
                        continue;
                    }
                    if (ch == ')')
                    {
                        parenDepth = Math.Max(0, parenDepth - 1);
                        i++;
                        continue;
                    }

                    if (parenDepth == 0)
                    {
                        if (readingCondition)
                        {
                            if (MatchKeywordAt(trimmed, i, "THEN"))
                            {
                                currentCondition = trimmed.Substring(segmentStart, i - segmentStart).Trim();
                                if (string.IsNullOrWhiteSpace(currentCondition))
                                    return false;
                                i += 4; // Past "THEN"
                                segmentStart = i;
                                readingCondition = false;
                                continue;
                            }
                        }
                        else
                        {
                            if (MatchKeywordAt(trimmed, i, "IF"))
                            {
                                nestedIfDepth++;
                                i += 2;
                                continue;
                            }

                            if (MatchKeywordAt(trimmed, i, "END_IF"))
                            {
                                if (nestedIfDepth > 0)
                                {
                                    nestedIfDepth--;
                                    i += 6;
                                    continue;
                                }
                                else
                                {
                                    // End of top-level IF
                                    string body = trimmed.Substring(segmentStart, i - segmentStart).Trim();
                                    branches.Add(new ParsedIfBranch
                                    {
                                        Kind = currentKind,
                                        Condition = currentCondition,
                                        Body = body
                                    });

                                    i += 6; // Past "END_IF"
                                    string tail = trimmed.Substring(i).Trim();
                                    if (tail == "" || tail == ";")
                                        return true;
                                    return false; // Trailing statements after END_IF;
                                }
                            }

                            if (nestedIfDepth == 0)
                            {
                                if (MatchKeywordAt(trimmed, i, "ELSIF"))
                                {
                                    if (currentKind == "ELSE")
                                        return false;

                                    string body = trimmed.Substring(segmentStart, i - segmentStart).Trim();
                                    branches.Add(new ParsedIfBranch
                                    {
                                        Kind = currentKind,
                                        Condition = currentCondition,
                                        Body = body
                                    });

                                    currentKind = "ELSIF";
                                    currentCondition = null;
                                    i += 5; // Past "ELSIF"
                                    segmentStart = i;
                                    readingCondition = true;
                                    continue;
                                }

                                if (MatchKeywordAt(trimmed, i, "ELSE"))
                                {
                                    if (currentKind == "ELSE")
                                        return false;

                                    string body = trimmed.Substring(segmentStart, i - segmentStart).Trim();
                                    branches.Add(new ParsedIfBranch
                                    {
                                        Kind = currentKind,
                                        Condition = currentCondition,
                                        Body = body
                                    });

                                    currentKind = "ELSE";
                                    currentCondition = null;
                                    i += 4; // Past "ELSE"
                                    segmentStart = i;
                                    readingCondition = false;
                                    continue;
                                }
                            }
                        }
                    }
                }

                i++;
            }

            return false;
        }

        public static List<string> SplitOrBranches(string expr)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(expr)) return list;

            var tokens = new List<string>();
            int parenDepth = 0;
            int start = 0;

            for (int i = 0; i < expr.Length; i++)
            {
                if (expr[i] == '(') parenDepth++;
                else if (expr[i] == ')') parenDepth = Math.Max(0, parenDepth - 1);
                else if (parenDepth == 0)
                {
                    if (i + 4 <= expr.Length &&
                        (i == 0 || char.IsWhiteSpace(expr[i - 1])) &&
                        expr.Substring(i, 2).Equals("OR", StringComparison.OrdinalIgnoreCase) &&
                        (i + 2 == expr.Length || char.IsWhiteSpace(expr[i + 2])))
                    {
                        tokens.Add(expr.Substring(start, i - start).Trim());
                        i += 2;
                        start = i;
                    }
                }
            }
            if (start < expr.Length)
            {
                tokens.Add(expr.Substring(start).Trim());
            }

            foreach (var t in tokens)
            {
                string tr = t.Trim(' ', '(', ')');
                if (!string.IsNullOrWhiteSpace(tr))
                    list.Add(tr);
            }
            return list.Count > 0 ? list : new List<string> { expr.Trim() };
        }

        public static string FormatInterfaceToScl(XElement ifaceElem)
        {
            if (ifaceElem == null) return "";
            var sb = new StringBuilder();
            var sections = ifaceElem.Descendants().Where(d => d.Name.LocalName == "Section").ToList();

            foreach (var sec in sections)
            {
                string secName = sec.Attribute("Name")?.Value ?? "";
                var members = sec.Elements().Where(e => e.Name.LocalName == "Member").ToList();
                if (members.Count == 0) continue;

                string sclSec = MapSectionToSclKeyword(secName);
                if (string.IsNullOrEmpty(sclSec)) continue;

                sb.AppendLine($"   {sclSec}");
                foreach (var m in members)
                {
                    string mName = m.Attribute("Name")?.Value ?? "";
                    string mType = m.Attribute("Datatype")?.Value ?? "Void";
                    sb.AppendLine($"      {mName} : {mType};");
                }
                sb.AppendLine("   END_VAR");
            }

            return sb.ToString().TrimEnd();
        }

        private static string MapSectionToSclKeyword(string secName)
        {
            switch (secName)
            {
                case "Input": return "VAR_INPUT";
                case "Output": return "VAR_OUTPUT";
                case "InOut": return "VAR_IN_OUT";
                case "Static": return "VAR";
                case "Temp": return "VAR_TEMP";
                case "Constant": return "VAR CONSTANT";
                case "Return": return "VAR_OUTPUT // Return";
                default: return null;
            }
        }

        public static string ReconstructStructuredText(XElement stElem)
        {
            if (stElem == null) return "";
            var sb = new StringBuilder();
            ReconstructRecursive(stElem, sb);
            return sb.ToString();
        }

        private static void ReconstructRecursive(XElement container, StringBuilder sb)
        {
            if (container == null) return;
            foreach (var elem in container.Elements())
            {
                string ln = elem.Name.LocalName;
                if (ln.Equals("NewLine", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                }
                else if (ln.Equals("Blank", StringComparison.OrdinalIgnoreCase))
                {
                    int num = 1;
                    string numAttr = elem.Attribute("Num")?.Value ?? elem.Attribute("num")?.Value;
                    if (!string.IsNullOrEmpty(numAttr))
                        int.TryParse(numAttr, out num);
                    sb.Append(new string(' ', Math.Max(1, num)));
                }
                else if (ln.Equals("Token", StringComparison.OrdinalIgnoreCase))
                {
                    string txt = elem.Attribute("Text")?.Value ?? elem.Attribute("text")?.Value ?? "";
                    sb.Append(txt);
                }
                else if (ln.Equals("Text", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(elem.Value);
                }
                else if (ln.Equals("Label", StringComparison.OrdinalIgnoreCase))
                {
                    string lblName = elem.Attribute("Name")?.Value ?? elem.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(lblName))
                        sb.Append(lblName);
                    if (elem.HasElements)
                        ReconstructRecursive(elem, sb);
                }
                else if (ln.Equals("CallInfo", StringComparison.OrdinalIgnoreCase))
                {
                    // If CallInfo has no <Instance> child, output the block/instruction name from Name attribute
                    bool hasInstance = elem.Elements().Any(e => e.Name.LocalName.Equals("Instance", StringComparison.OrdinalIgnoreCase));
                    if (!hasInstance)
                    {
                        string callName = elem.Attribute("Name")?.Value ?? elem.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(callName))
                        {
                            string formattedCallName = (callName.Contains(" ") || callName.Contains("-")) ? $"\"{callName}\"" : callName;
                            sb.Append(formattedCallName);
                        }
                    }
                    if (elem.HasElements)
                        ReconstructRecursive(elem, sb);
                }
                else if (ln.Equals("Parameter", StringComparison.OrdinalIgnoreCase))
                {
                    string pName = elem.Attribute("Name")?.Value ?? elem.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(pName))
                    {
                        var firstTok = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("Token", StringComparison.OrdinalIgnoreCase));
                        string firstTokText = firstTok?.Attribute("Text")?.Value ?? firstTok?.Attribute("text")?.Value;
                        if (!string.Equals(firstTokText, pName, StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append(pName);
                            bool hasAssignToken = elem.Descendants().Any(d =>
                                d.Name.LocalName.Equals("Token", StringComparison.OrdinalIgnoreCase) &&
                                (d.Attribute("Text")?.Value == ":=" || d.Attribute("Text")?.Value == "=>" ||
                                 d.Attribute("text")?.Value == ":=" || d.Attribute("text")?.Value == "=>"));
                            if (!hasAssignToken)
                            {
                                sb.Append(" := ");
                            }
                        }
                    }
                    if (elem.HasElements)
                        ReconstructRecursive(elem, sb);
                }
                else if (ln.Equals("Instruction", StringComparison.OrdinalIgnoreCase))
                {
                    string instName = elem.Attribute("Name")?.Value ?? elem.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(instName))
                    {
                        sb.Append(instName);
                    }
                    if (elem.HasElements)
                        ReconstructRecursive(elem, sb);
                }
                else if (ln.Equals("Access", StringComparison.OrdinalIgnoreCase))
                {
                    string scope = elem.Attribute("Scope")?.Value ?? elem.Attribute("scope")?.Value ?? "";
                    if (scope.Equals("Call", StringComparison.OrdinalIgnoreCase))
                    {
                        if (elem.HasElements)
                            ReconstructRecursive(elem, sb);
                        continue;
                    }

                    var labelElem = elem.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Label", StringComparison.OrdinalIgnoreCase));
                    if (labelElem != null)
                    {
                        string lblName = labelElem.Attribute("Name")?.Value ?? labelElem.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(lblName))
                            sb.Append(lblName);
                        if (labelElem.HasElements)
                            ReconstructRecursive(labelElem, sb);
                        foreach (var other in elem.Elements().Where(e => e != labelElem))
                        {
                            if (other.Name.LocalName.Equals("Token", StringComparison.OrdinalIgnoreCase))
                                sb.Append(other.Attribute("Text")?.Value ?? other.Attribute("text")?.Value ?? "");
                        }
                        continue;
                    }

                    var predefElem = elem.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("PredefinedVariable", StringComparison.OrdinalIgnoreCase));
                    if (predefElem != null)
                    {
                        string predefName = predefElem.Attribute("Name")?.Value ?? predefElem.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(predefName))
                            sb.Append(predefName);
                        continue;
                    }

                    var symElem = elem.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Symbol", StringComparison.OrdinalIgnoreCase));
                    var compElements = (symElem != null ? symElem.Elements() : elem.Elements())
                        .Where(d => d.Name.LocalName.Equals("Component", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (compElements.Count > 0)
                    {
                        bool isLocal = string.Equals(scope, "LocalVariable", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(scope, "LocalConstant", StringComparison.OrdinalIgnoreCase);
                        var formattedComps = new List<string>();

                        foreach (var c in compElements)
                        {
                            string cName = c.Attribute("Name")?.Value ?? c.Attribute("name")?.Value;
                            if (string.IsNullOrEmpty(cName)) continue;

                            string idxStr = "";
                            if (c.HasElements)
                            {
                                var idxSb = new StringBuilder();
                                ReconstructRecursive(c, idxSb);
                                string rawIdx = idxSb.ToString().Trim();
                                if (!string.IsNullOrEmpty(rawIdx))
                                {
                                    idxStr = rawIdx.StartsWith("[") ? rawIdx : $"[{rawIdx}]";
                                }
                            }

                            string formattedName;
                            if (isLocal)
                            {
                                formattedName = (cName.Contains(" ") || cName.Contains("-")) ? $"\"{cName}\"" : cName;
                            }
                            else
                            {
                                formattedName = $"\"{cName}\"";
                            }

                            formattedComps.Add(formattedName + idxStr);
                        }

                        string prefix = isLocal ? "#" : "";
                        sb.Append(prefix + string.Join(".", formattedComps));
                    }
                    else
                    {
                        var constElem = elem.Elements().FirstOrDefault(d => d.Name.LocalName.Equals("Constant", StringComparison.OrdinalIgnoreCase));
                        string constVal = constElem?.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("ConstantValue", StringComparison.OrdinalIgnoreCase))?.Value;
                        if (!string.IsNullOrEmpty(constVal))
                        {
                            sb.Append(constVal);
                        }
                        else if (elem.HasElements)
                        {
                            ReconstructRecursive(elem, sb);
                        }
                    }
                }
                else if (ln.Equals("LineComment", StringComparison.OrdinalIgnoreCase) || ln.Equals("Comment", StringComparison.OrdinalIgnoreCase))
                {
                    string txt = elem.Descendants().FirstOrDefault(d => d.Name.LocalName.Equals("Text", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                    sb.Append($"// {txt}");
                }
                else if (elem.HasElements)
                {
                    ReconstructRecursive(elem, sb);
                }
            }
        }

        /// <summary>
        /// Builds a ParsedSclBlock directly from interface members, SCL statements/networks, and global tag/DB definitions
        /// so SclToLadConverter can convert generated or user-written SCL directly into SimaticML LAD/SCL XML.
        /// </summary>
        public static ParsedSclBlock BuildParsedBlockFromSpec(
            string blockName,
            string blockType,
            int? blockNumber,
            List<BlockInterfaceMember> interfaceMembers,
            List<Tuple<string, string>> titledStatements,
            Dictionary<string, string> externalVarTypes,
            HashSet<string> definedGlobalSymbols,
            string tiaVersion = "V20",
            bool forceSclNetworks = false)
        {
            string ifaceVer = "v5";
            if (!string.IsNullOrWhiteSpace(tiaVersion))
            {
                if (tiaVersion.StartsWith("V14", StringComparison.OrdinalIgnoreCase)) ifaceVer = "v2";
                else if (tiaVersion.StartsWith("V15", StringComparison.OrdinalIgnoreCase)) ifaceVer = "v3";
                else if (tiaVersion.StartsWith("V16", StringComparison.OrdinalIgnoreCase)) ifaceVer = "v4";
            }

            string bType = string.IsNullOrWhiteSpace(blockType) ? "FC" : blockType.Trim().ToUpperInvariant();
            var ifaceSb = new StringBuilder();
            ifaceSb.AppendLine($"<Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/{ifaceVer}\">");

            if (bType == "OB")
            {
                ifaceSb.AppendLine("  <Section Name=\"Input\">");
                ifaceSb.AppendLine("    <Member Name=\"Initial_Call\" Datatype=\"Bool\" Accessibility=\"Public\" Informative=\"true\" />");
                ifaceSb.AppendLine("    <Member Name=\"Remanence\" Datatype=\"Bool\" Accessibility=\"Public\" Informative=\"true\" />");
                ifaceSb.AppendLine("  </Section>");
                ifaceSb.AppendLine("  <Section Name=\"Temp\">");
                if (interfaceMembers != null)
                {
                    foreach (var m in interfaceMembers.Where(x => string.Equals(x.Section, "Temp", StringComparison.OrdinalIgnoreCase)))
                    {
                        ifaceSb.AppendLine($"    <Member Name=\"{System.Security.SecurityElement.Escape(m.Name)}\" Datatype=\"{System.Security.SecurityElement.Escape(m.DataType ?? "Bool")}\" />");
                    }
                }
                ifaceSb.AppendLine("  </Section>");
                ifaceSb.AppendLine("  <Section Name=\"Constant\" />");
            }
            else
            {
                string[] sections = bType == "FB"
                    ? new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" }
                    : new[] { "Input", "Output", "InOut", "Temp", "Constant", "Return" };

                foreach (var sec in sections)
                {
                    if (sec == "Return" && bType == "FC")
                    {
                        var retMem = interfaceMembers?.FirstOrDefault(x => string.Equals(x.Section, "Return", StringComparison.OrdinalIgnoreCase));
                        string retType = retMem?.DataType ?? "Void";
                        ifaceSb.AppendLine("  <Section Name=\"Return\">");
                        ifaceSb.AppendLine($"    <Member Name=\"Ret_Val\" Datatype=\"{System.Security.SecurityElement.Escape(retType)}\" Accessibility=\"Public\" />");
                        ifaceSb.AppendLine("  </Section>");
                        continue;
                    }

                    var secMembers = interfaceMembers?
                        .Where(x => string.Equals(x.Section, sec, StringComparison.OrdinalIgnoreCase))
                        .ToList() ?? new List<BlockInterfaceMember>();

                    if (secMembers.Count == 0)
                    {
                        ifaceSb.AppendLine($"  <Section Name=\"{sec}\" />");
                    }
                    else
                    {
                        ifaceSb.AppendLine($"  <Section Name=\"{sec}\">");
                        foreach (var m in secMembers)
                        {
                            string accAttr = (sec == "Temp" || sec == "Constant") ? "" : " Accessibility=\"Public\"";
                            ifaceSb.AppendLine($"    <Member Name=\"{System.Security.SecurityElement.Escape(m.Name)}\" Datatype=\"{System.Security.SecurityElement.Escape(m.DataType ?? "Bool")}\"{accAttr} />");
                        }
                        ifaceSb.AppendLine("  </Section>");
                    }
                }
            }
            ifaceSb.Append("</Sections></Interface>");

            var stmtList = new List<SclStatementInfo>();
            var fullSclSb = new StringBuilder();
            int idx = 1;

            if (titledStatements != null)
            {
                foreach (var pair in titledStatements)
                {
                    string title = pair.Item1;
                    string rawCode = pair.Item2;
                    if (string.IsNullOrWhiteSpace(rawCode)) continue;

                    var allExpanded = new List<string>();
                    var splitStmts = SplitSclStatements(rawCode);
                    foreach (var s in splitStmts)
                    {
                        var expanded = ExpandIfBranches(s);
                        foreach (var exp in expanded)
                        {
                            if (!string.IsNullOrWhiteSpace(exp))
                                allExpanded.Add(exp.Trim());
                        }
                    }

                    for (int subIdx = 0; subIdx < allExpanded.Count; subIdx++)
                    {
                        string exp = allExpanded[subIdx];
                        string effectiveTitle = !string.IsNullOrWhiteSpace(title) ? title : $"Network {idx}";
                        if (allExpanded.Count > 1 && !string.IsNullOrWhiteSpace(title))
                        {
                            var targetMatch = Regex.Match(exp, @"^(?:IF\s+[\s\S]+?\s+THEN\s+)?(#?(?:""[^""]+""|[a-zA-Z0-9_]+)(?:\.(?:""[^""]+""|[a-zA-Z0-9_]+))*)\s*:=", RegexOptions.IgnoreCase);
                            if (targetMatch.Success)
                            {
                                effectiveTitle = $"{title} [{targetMatch.Groups[1].Value.Trim()}]";
                            }
                            else
                            {
                                effectiveTitle = $"{title} (#{subIdx + 1})";
                            }
                        }

                        stmtList.Add(new SclStatementInfo
                        {
                            Index = idx,
                            Title = effectiveTitle,
                            RawSclCode = exp,
                            KeepAsScl = forceSclNetworks
                        });
                        fullSclSb.AppendLine($"// {effectiveTitle}");
                        fullSclSb.AppendLine(exp);
                        fullSclSb.AppendLine();
                        idx++;
                    }
                }
            }

            var parsed = new ParsedSclBlock
            {
                BlockName = blockName,
                BlockType = bType,
                BlockNumber = blockNumber,
                InterfaceXml = ifaceSb.ToString(),
                HeaderVersion = "0.1",
                MemoryLayout = "Optimized",
                Statements = stmtList,
                OriginalSclCode = fullSclSb.ToString(),
                FullSclCode = fullSclSb.ToString(),
                EngineeringVersion = tiaVersion ?? "V20",
                HasScannedPlcSymbols = true
            };

            if (externalVarTypes != null)
            {
                foreach (var kv in externalVarTypes)
                    parsed.ExternalVarTypes[kv.Key] = kv.Value;
            }

            if (definedGlobalSymbols != null)
            {
                foreach (var sym in definedGlobalSymbols)
                    parsed.DefinedGlobalSymbols.Add(sym);
            }

            return parsed;
        }
    }
}


