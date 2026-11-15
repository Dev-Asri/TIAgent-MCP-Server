using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using TIAgent.Models;

namespace TIAgent.Services
{
    /// <summary>Kurulu bir GSDML dosyasından okunan DAP / modül / alt modül bilgisi.</summary>
    public class GsdItemInfo
    {
        public string Kind { get; set; }           // DAP, M, SM
        public string Id { get; set; }
        public string Name { get; set; }
        public string TypeIdentifier { get; set; }
    }

    public class GsdFileInfo
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public List<GsdItemInfo> Items { get; set; } = new List<GsdItemInfo>();
    }

    /// <summary>
    /// Donanım (Devices &amp; Networks) ve Technology Object yönetimi.
    /// Tüm Siemens.Engineering erişimi reflection ile yapılır (derleme zamanında Siemens DLL bağımlılığı yoktur).
    /// </summary>
    public partial class TiaOpennessService
    {
        private readonly Dictionary<string, GsdFileInfo> _gsdCache = new Dictionary<string, GsdFileInfo>(StringComparer.OrdinalIgnoreCase);
        private DateTime _gsdCacheTime = DateTime.MinValue;

        // ════════════════════════════════════════════════════════════════════
        //  PUBLIC API
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Openness thread'i dışından çağrılabilir: yalnızca donanım / TO silme.</summary>
        public bool DeleteHardwareAndTechnologyObjects(
            TiaPlcItem plc,
            IEnumerable<string> devicesToDelete,
            IEnumerable<string> technologyObjectsToDelete,
            Action<string, string, bool> stepCallback,
            out string summary)
        {
            if (plc?.RawPlcSoftware == null)
                throw new ArgumentNullException(nameof(plc), "Hedef PLC bağlı değil.");

            string localSummary = "";
            bool ok = RunOnOpennessThreadAsync(() =>
            {
                int count = DeleteHardwareInternal(plc, devicesToDelete, technologyObjectsToDelete, stepCallback);
                localSummary = count > 0 ? $"{count} donanım/TO öğesi silindi" : "Silinecek donanım/TO bulunamadı";
                return count > 0;
            }).GetAwaiter().GetResult();
            summary = localSummary;
            return ok;
        }

        /// <summary>Modül takma denemesi ve teşhisi.</summary>
        public object ProbeModulePlugging(TiaPlcItem plc, string keyword)
        {
            if (plc?.RawDevice == null) return new { error = "PLC yok" };

            return RunOnOpennessThreadAsync<object>(() =>
            {
                var logs = new List<string>();
                var device = plc.RawDevice;
                var allItems = CollectAllDeviceItemsRecursive(device);

                var candidates = new List<string>
                {
                    "OrderNumber:6ES7 223-1BL32-0XB0",
                    "OrderNumber:6ES7 223-1BL32-0XB0/V2.0",
                    "OrderNumber:6ES7 223-1BL30-0XB0",
                    "OrderNumber:6ES7 223-1PH32-0XB0",
                    "OrderNumber:6ES7 223-1PH30-0XB0",
                    "OrderNumber:6ES7 221-1BF32-0XB0",
                    "OrderNumber:6ES7 222-1BF32-0XB0"
                };

                var containers = new List<Tuple<string, object>> { Tuple.Create("Device", device) };
                foreach (var di in allItems)
                {
                    string dName = GetPropString(di, "Name");
                    int dPos = GetPropInt(di, "PositionNumber");
                    string dTid = GetPropString(di, "TypeIdentifier");
                    containers.Add(Tuple.Create($"{dName} (pos={dPos}, tid={dTid})", (object)di));
                }

                foreach (var c in containers)
                {
                    var type = c.Item2.GetType();
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()))
                        .Where(m => m.Name == "CanPlugNew" || m.Name == "PlugNew")
                        .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                        .Distinct()
                        .ToList();

                    if (methods.Count > 0)
                    {
                        logs.Add($"Container [{c.Item1}] has methods: {string.Join(" | ", methods)}");

                        foreach (var tid in candidates)
                        {
                            for (int pos = 1; pos <= 10; pos++)
                            {
                                try
                                {
                                    bool can = Convert.ToBoolean(InvokeMember(c.Item2, "CanPlugNew", tid, "SM_Test", pos));
                                    if (can)
                                    {
                                        logs.Add($"SUCCESS CAN: [{c.Item1}] can plug {tid} at pos={pos}");
                                        try
                                        {
                                            object plugged = InvokeMember(c.Item2, "PlugNew", tid, "SM_Test", pos);
                                            logs.Add($"SUCCESS PLUGGED: {plugged} at pos={pos}");
                                            return new { ok = true, logs };
                                        }
                                        catch (Exception pEx)
                                        {
                                            logs.Add($"FAILED PLUG: {pEx.Message}");
                                        }
                                    }
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }
                    }
                }

                return new { ok = false, logs };
            }).GetAwaiter().GetResult();
        }

        /// <summary>Projedeki cihazları, modülleri, IP adreslerini ve Technology Object'leri listeler.</summary>
        public object GetHardwareOverview(TiaPlcItem plc)
        {
            if (plc?.RawPlcSoftware == null)
                return new { ok = false, error = "TIA Portal'a bağlı PLC yok." };

            return RunOnOpennessThreadAsync<object>(() =>
            {
                object project = GetProjectFromPlc(plc);
                var devices = new List<object>();
                foreach (var dev in CollectAllDevicesInProject(project))
                {
                    var items = new List<object>();
                    var ips = new List<string>();
                    foreach (var di in CollectAllDeviceItemsRecursive(dev))
                    {
                        string tid = GetPropString(di, "TypeIdentifier");
                        items.Add(new
                        {
                            name = GetPropString(di, "Name"),
                            position = GetPropInt(di, "PositionNumber"),
                            typeIdentifier = tid,
                            addresses = DescribeAddresses(di)
                        });
                        object ni = GetNetworkInterface(di);
                        if (ni != null)
                        {
                            foreach (var node in AsEnumerable(GetPropValue(ni, "Nodes")))
                            {
                                string ip = SafeGetAttribute(node, "Address")?.ToString();
                                if (!string.IsNullOrWhiteSpace(ip)) ips.Add(ip);
                            }
                        }
                    }
                    devices.Add(new
                    {
                        name = GetPropString(dev, "Name"),
                        typeIdentifier = GetPropString(dev, "TypeIdentifier"),
                        isTargetPlc = ReferenceEquals(dev, plc.RawDevice) || Equals(dev, plc.RawDevice),
                        ipAddresses = ips,
                        items = items
                    });
                }

                var tos = new List<object>();
                foreach (var to in AsEnumerable(GetTechnologyObjectsComposition(plc.RawPlcSoftware)))
                {
                    tos.Add(new
                    {
                        name = GetPropString(to, "Name"),
                        type = GetPropString(to, "TypeIdentifier"),
                        number = GetPropInt(to, "Number")
                    });
                }

                return new { ok = true, plc = plc.Name, devices, technologyObjects = tos };
            }).GetAwaiter().GetResult();
        }

        /// <summary>TIA Portal'a (ve açık projeye) yüklü GSDML dosyalarını ve modüllerini listeler.</summary>
        public object ListInstalledGsd(TiaPlcItem plc, string keyword)
        {
            object project = null;
            try
            {
                if (plc != null)
                    project = RunOnOpennessThreadAsync(() => GetProjectFromPlc(plc)).GetAwaiter().GetResult();
            }
            catch { }

            string projectPath = null;
            try
            {
                if (project != null)
                    projectPath = RunOnOpennessThreadAsync(() => GetPropString(project, "Path")).GetAwaiter().GetResult();
            }
            catch { }

            var files = ScanInstalledGsdmlFiles(projectPath, forceRefresh: true);
            string kw = NormalizeKey(keyword);
            var result = files
                .Where(f => string.IsNullOrEmpty(kw) || NormalizeKey(f.FileName).Contains(kw) || f.Items.Any(i => NormalizeKey(i.Name).Contains(kw)))
                .Select(f => new
                {
                    file = f.FileName,
                    path = f.FullPath,
                    daps = f.Items.Where(i => i.Kind == "DAP").Select(i => new { i.Name, i.TypeIdentifier }).ToList(),
                    modules = f.Items.Where(i => i.Kind != "DAP").Select(i => new { i.Kind, i.Name, i.TypeIdentifier }).ToList()
                })
                .ToList();
            return new { ok = true, count = result.Count, searchedFolders = GetGsdSearchFolders(projectPath), files = result };
        }

        // ════════════════════════════════════════════════════════════════════
        //  PAKET İÇİ UYGULAMA (Openness thread içinde çağrılır)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Paketteki donanım ve Technology Object talimatlarını uygular. Openness thread'i içinde çağrılmalıdır.
        /// Her öğe denenir; hatalar toplanıp raporlanır.
        /// </summary>
        internal bool ApplyHardwareAndTechnologyInternal(TiaPlcItem plc, TiaPackage pkg, Action<string, string, bool> stepCallback, out string hwErrorSummary)
        {
            hwErrorSummary = null;
            if (pkg == null || !pkg.HasHardwareContent()) return true;

            var hwErrors = new List<string>();

            object project = GetProjectFromPlc(plc);
            if (project == null)
            {
                hwErrorSummary = "Proje nesnesine ulaşılamadı; donanım talimatları atlandı.";
                stepCallback?.Invoke("donanım", hwErrorSummary, false);
                return false;
            }

            // 1. Silmeler
            DeleteHardwareInternal(plc, pkg.DevicesToDelete, pkg.TechnologyObjectsToDelete, stepCallback);

            // 2. PLC IP / PROFINET arayüzü
            object plcNi = FindEthernetInterface(plc.RawDevice, out _);
            if (!string.IsNullOrWhiteSpace(pkg.PlcIpAddress))
            {
                try
                {
                    object plcNode = AsEnumerable(GetPropValue(plcNi, "Nodes")).FirstOrDefault();
                    if (plcNode == null) throw new InvalidOperationException("PLC PROFINET arayüzü bulunamadı.");
                    SafeSetAttribute(plcNode, "Address", pkg.PlcIpAddress.Trim());
                    stepCallback?.Invoke("donanım", $"PLC IP adresi ayarlandı: {plc.Name} → {pkg.PlcIpAddress}", true);
                }
                catch (Exception ex)
                {
                    string err = $"PLC IP ayarlanamadı: {Unwrap(ex).Message}";
                    hwErrors.Add(err);
                    stepCallback?.Invoke("donanım", err, false);
                }
            }

            // 3. PLC rack modülleri
            if (pkg.PlcModules != null)
            {
                foreach (var mod in pkg.PlcModules)
                {
                    try
                    {
                        object item = PlugModuleInternal(plc.RawDevice, mod, null);
                        stepCallback?.Invoke("donanım", $"PLC modülü takıldı: {mod.Name} ({GetPropString(item, "TypeIdentifier")}, Slot {GetPropInt(item, "PositionNumber")})", true);
                    }
                    catch (Exception ex)
                    {
                        string err = $"PLC modülü takılamadı: {mod?.Name ?? "modül"} – {Unwrap(ex).Message}";
                        hwErrors.Add(err);
                        stepCallback?.Invoke("donanım", err, false);
                    }
                }
            }

            // 4. PROFINET cihazlar
            if (pkg.HardwareDevices != null)
            {
                foreach (var devEntry in pkg.HardwareDevices)
                {
                    try
                    {
                        CreateOrUpdateDeviceInternal(project, plc, plcNi, devEntry, stepCallback);
                    }
                    catch (Exception ex)
                    {
                        string err = $"Cihaz eklenemedi: {devEntry?.Name} – {Unwrap(ex).Message}";
                        hwErrors.Add(err);
                        stepCallback?.Invoke("donanım", err, false);
                    }
                }
            }

            // 5. Technology Objects
            if (pkg.TechnologyObjects != null)
            {
                foreach (var toEntry in pkg.TechnologyObjects)
                {
                    try
                    {
                        CreateOrUpdateTechnologyObjectInternal(project, plc, toEntry, stepCallback);
                    }
                    catch (Exception ex)
                    {
                        string err = $"Technology Object oluşturulamadı: {toEntry?.Name} – {Unwrap(ex).Message}";
                        hwErrors.Add(err);
                        stepCallback?.Invoke("TO", err, false);
                    }
                }
            }

            // 6. Donanım derlemesi (CPU device item)
            try
            {
                object cpuItem = CollectAllDeviceItemsRecursive(plc.RawDevice).FirstOrDefault(di => GetSoftwareFromDeviceItem(di) != null);
                if (cpuItem != null)
                {
                    bool hwOk = CompileBlock(cpuItem, out string hwMsg);
                    stepCallback?.Invoke("derleme", $"Donanım derlemesi ({plc.Name}): {hwMsg}", hwOk);
                }
            }
            catch (Exception ex)
            {
                stepCallback?.Invoke("derleme", $"Donanım derlemesi yapılamadı: {Unwrap(ex).Message}", false);
            }

            if (hwErrors.Count > 0)
            {
                hwErrorSummary = string.Join("; ", hwErrors);
                return false;
            }

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  CİHAZ OLUŞTURMA
        // ════════════════════════════════════════════════════════════════════

        private void CreateOrUpdateDeviceInternal(object project, TiaPlcItem plc, object plcNi, HardwareDeviceEntry entry, Action<string, string, bool> stepCallback)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                throw new ArgumentException("Cihaz adı (Name) boş.");

            string name = entry.Name.Trim();
            object device = FindDeviceByName(project, name);

            if (device != null && IsSameObject(device, plc.RawDevice))
                throw new InvalidOperationException($"'{name}' hedef PLC'nin kendisi; değiştirilemez.");

            if (device != null && entry.ReplaceIfExists)
            {
                InvokeMember(device, "Delete");
                stepCallback?.Invoke("donanım", $"Mevcut cihaz silindi (yeniden oluşturulacak): {name}", true);
                device = null;
            }

            if (device == null)
            {
                // Kontrol: Bu öğe aslında bir PLC Rack Modülü (SM, SB, CM) mü?
                // Ajanlar (OpenCode, Cursor vb.) PLC rack genişleme modüllerini bazen yanlışlıkla HardwareDevices altına koyabilir.
                if (IsPlcRackModuleCandidate(entry))
                {
                    try
                    {
                        var mod = new HardwareModuleEntry
                        {
                            Name = entry.Name,
                            TypeIdentifier = !string.IsNullOrWhiteSpace(entry.TypeIdentifier) ? entry.TypeIdentifier : entry.Name,
                            AlternativeTypeIdentifiers = entry.AlternativeTypeIdentifiers,
                            GsdModuleKeyword = entry.GsdKeyword,
                            Comment = entry.Comment
                        };
                        object plugged = PlugModuleInternal(plc.RawDevice, mod, null);
                        if (plugged != null)
                        {
                            stepCallback?.Invoke("donanım", $"PLC modülü takıldı: {mod.Name} ({GetPropString(plugged, "TypeIdentifier")}, Slot {GetPropInt(plugged, "PositionNumber")})", true);

                            // Varsa alt modülleri de tak
                            if (entry.Modules != null && entry.Modules.Count > 0)
                            {
                                foreach (var subMod in entry.Modules)
                                {
                                    try { PlugModuleInternal(plc.RawDevice, subMod, null); } catch { }
                                }
                            }
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"PLC modülü olarak takılamadı: {Unwrap(ex).Message}");
                    }
                }

                var candidates = BuildDeviceTypeIdentifierCandidates(project, entry);
                if (candidates.Count == 0)
                    throw new InvalidOperationException("TypeIdentifier verilmedi ve GsdKeyword ile yüklü GSDML dosyası bulunamadı. " +
                                                        "TIA Portal'da 'Options > Manage general station description files (GSD)' ile GSDML yükleyin.");

                object devicesComp = GetPropValue(project, "Devices");
                string deviceName = string.IsNullOrWhiteSpace(entry.DeviceName) ? name : entry.DeviceName.Trim();
                var errors = new List<string>();

                foreach (var tid in candidates)
                {
                    foreach (var dn in new[] { deviceName, null })
                    {
                        try
                        {
                            device = InvokeMember(devicesComp, "CreateWithItem", tid, name, dn);
                            if (device != null)
                            {
                                stepCallback?.Invoke("donanım", $"Cihaz eklendi: {name} [{tid}]", true);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{tid}: {Unwrap(ex).Message}");
                        }
                    }
                    if (device != null) break;
                }

                if (device == null)
                {
                    if (IsPlcRackModuleCandidate(entry))
                    {
                        try
                        {
                            var mod = new HardwareModuleEntry
                            {
                                Name = entry.Name,
                                TypeIdentifier = !string.IsNullOrWhiteSpace(entry.TypeIdentifier) ? entry.TypeIdentifier : entry.Name,
                                AlternativeTypeIdentifiers = entry.AlternativeTypeIdentifiers,
                                GsdModuleKeyword = entry.GsdKeyword,
                                Comment = entry.Comment
                            };
                            object plugged = PlugModuleInternal(plc.RawDevice, mod, null);
                            if (plugged != null)
                            {
                                stepCallback?.Invoke("donanım", $"PLC modülü takıldı: {mod.Name} ({GetPropString(plugged, "TypeIdentifier")}, Slot {GetPropInt(plugged, "PositionNumber")})", true);
                                return;
                            }
                        }
                        catch { }
                    }

                    throw new InvalidOperationException("Katalogda bulunamadı. Denenenler → " + string.Join(" | ", errors.Distinct().Take(6)));
                }
            }
            else
            {
                stepCallback?.Invoke("donanım", $"Cihaz zaten mevcut, ayarları güncelleniyor: {name}", true);
            }

            // ── Ağ / IP / IO-System ──
            object devNi = FindEthernetInterface(device, out _);
            if (devNi != null)
            {
                object devNode = AsEnumerable(GetPropValue(devNi, "Nodes")).FirstOrDefault();
                if (devNode != null)
                {
                    if (!string.IsNullOrWhiteSpace(entry.IpAddress))
                    {
                        try { SafeSetAttribute(devNode, "IpProtocolSelection", "Project"); } catch { }
                        SafeSetAttribute(devNode, "Address", entry.IpAddress.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(entry.SubnetMask))
                    {
                        try { SafeSetAttribute(devNode, "SubnetMask", entry.SubnetMask.Trim()); } catch { }
                    }

                    string pnName = !string.IsNullOrWhiteSpace(entry.PnDeviceName) ? entry.PnDeviceName.Trim() : name.ToLowerInvariant();
                    try { SafeSetAttribute(devNode, "PnDeviceNameAutoGeneration", false); } catch { }
                    try { SafeSetAttribute(devNode, "PnDeviceName", pnName); } catch { }

                    if (entry.ConnectToPlcIoSystem && plcNi != null)
                    {
                        ConnectDeviceToPlcIoSystem(plcNi, devNi, devNode);
                        stepCallback?.Invoke("donanım", $"{name}: IP {entry.IpAddress ?? "(değişmedi)"}, PN adı '{pnName}', PLC PROFINET IO-System'e bağlandı", true);
                    }
                    else
                    {
                        stepCallback?.Invoke("donanım", $"{name}: IP {entry.IpAddress ?? "(değişmedi)"}, PN adı '{pnName}'", true);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.IpAddress))
            {
                stepCallback?.Invoke("donanım", $"{name}: PROFINET arayüzü bulunamadı, IP atanamadı.", false);
            }

            // ── Modüller / Telegramlar ──
            if (entry.Modules != null)
            {
                GsdFileInfo gsd = FindGsdFileForDevice(project, device);
                foreach (var mod in entry.Modules)
                {
                    try
                    {
                        object item = PlugModuleInternal(device, mod, gsd);
                        stepCallback?.Invoke("donanım", $"{name}: modül takıldı → {GetPropString(item, "Name")} ({DescribeAddressesShort(item)})", true);
                    }
                    catch (Exception ex)
                    {
                        stepCallback?.Invoke("donanım", $"{name}: modül takılamadı ({mod}) – {Unwrap(ex).Message}", false);
                    }
                }
            }
        }

        private List<string> BuildDeviceTypeIdentifierCandidates(object project, HardwareDeviceEntry entry)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.TypeIdentifier)) list.Add(entry.TypeIdentifier.Trim());
            if (entry.AlternativeTypeIdentifiers != null)
                list.AddRange(entry.AlternativeTypeIdentifiers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

            if (!string.IsNullOrWhiteSpace(entry.GsdKeyword))
            {
                string kw = NormalizeKey(entry.GsdKeyword);
                string dapKw = NormalizeKey(entry.GsdDapKeyword);
                var files = ScanInstalledGsdmlFiles(GetPropString(project, "Path"), forceRefresh: false)
                    .Where(f => NormalizeKey(f.FileName).Contains(kw) || f.Items.Any(i => i.Kind == "DAP" && NormalizeKey(i.Name).Contains(kw)))
                    .OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase) // en yeni tarihli GSDML önce
                    .ToList();

                foreach (var f in files)
                {
                    var daps = f.Items.Where(i => i.Kind == "DAP").ToList();
                    if (!string.IsNullOrEmpty(dapKw))
                        daps = daps.OrderByDescending(d => NormalizeKey(d.Name).Contains(dapKw) || NormalizeKey(d.Id).Contains(dapKw)).ToList();
                    else
                        daps.Reverse(); // GSDML'de en yeni firmware DAP genelde sondadır
                    foreach (var d in daps)
                    {
                        list.Add(d.TypeIdentifier);
                        list.Add($"GSD:{f.FileName.ToUpperInvariant()}/DAP/{d.Id}");
                    }
                }
            }
            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool IsPlcRackModuleCandidate(HardwareDeviceEntry entry)
        {
            if (entry == null) return false;
            // Eğer açıkça IP adresi verilmişse, muhtemelen harici bir PROFINET istasyonudur (sürücü vb.)
            if (!string.IsNullOrWhiteSpace(entry.IpAddress)) return false;

            string s = $"{entry.Name} {entry.DeviceName} {entry.TypeIdentifier} {entry.GsdKeyword} {entry.Comment}".ToLowerInvariant();

            // Siemens Standart Sinyal / İletişim modülleri (SM / SB / CM) veya I/O anahtar kelimeleri
            bool hasSmPattern = s.Contains("sm_") || s.Contains("sm ") || s.Contains("sm12") || s.Contains("sm15") ||
                                s.Contains("sb_") || s.Contains("sb ") || s.Contains("sb12") ||
                                s.Contains("cm_") || s.Contains("cm ") || s.Contains("cm12") || s.Contains("cm15");

            bool hasIoPattern = s.Contains("di16") || s.Contains("dq16") || s.Contains("di 16") || s.Contains("dq 16") ||
                                s.Contains("16di") || s.Contains("16dq") || s.Contains("16 di") || s.Contains("16 dq") ||
                                s.Contains("16i") || s.Contains("16q") || s.Contains("16 i") || s.Contains("16 q") ||
                                s.Contains("16 in") || s.Contains("16 out") || s.Contains("16in") || s.Contains("16out") ||
                                s.Contains("8di") || s.Contains("8dq") || s.Contains("8 di") || s.Contains("8 dq") ||
                                s.Contains("8i") || s.Contains("8q") || s.Contains("8 in") || s.Contains("8 out") ||
                                s.Contains("di8") || s.Contains("dq8") || s.Contains("di 8") || s.Contains("dq 8") ||
                                s.Contains("analog") || s.Contains("ai4") || s.Contains("aq2") || s.Contains("ai8") ||
                                s.Contains("modul") || s.Contains("module");

            bool hasMlfbPattern = s.Contains("6es7 22") || s.Contains("6es722") ||
                                  s.Contains("6es7 23") || s.Contains("6es723") ||
                                  s.Contains("6es7 24") || s.Contains("6es724") ||
                                  s.Contains("6es7 52") || s.Contains("6es752") ||
                                  s.Contains("6es7 53") || s.Contains("6es753") ||
                                  s.Contains("6es7 54") || s.Contains("6es754");

            return hasSmPattern || hasIoPattern || hasMlfbPattern;
        }

        private void ConnectDeviceToPlcIoSystem(object plcNi, object devNi, object devNode)
        {
            object plcNode = AsEnumerable(GetPropValue(plcNi, "Nodes")).FirstOrDefault();
            if (plcNode == null) throw new InvalidOperationException("PLC PROFINET düğümü bulunamadı.");

            object subnet = GetPropValue(plcNode, "ConnectedSubnet");
            if (subnet == null)
                subnet = InvokeMember(plcNode, "CreateAndConnectSubnet", "PN/IE_1");

            object devSubnet = GetPropValue(devNode, "ConnectedSubnet");
            if (devSubnet == null)
                InvokeMember(devNode, "ConnectToSubnet", subnet);

            object ioCtrl = AsEnumerable(GetPropValue(plcNi, "IoControllers")).FirstOrDefault();
            if (ioCtrl == null) return; // PLC IO-Controller değil

            object ioSystem = GetPropValue(ioCtrl, "IoSystem");
            if (ioSystem == null)
                ioSystem = InvokeMember(ioCtrl, "CreateIoSystem", "PROFINET IO-System");

            object connector = AsEnumerable(GetPropValue(devNi, "IoConnectors")).FirstOrDefault();
            if (connector == null) return;
            object connected = GetPropValue(connector, "ConnectedToIoSystem");
            if (connected == null)
                InvokeMember(connector, "ConnectToIoSystem", ioSystem);
        }

        // ════════════════════════════════════════════════════════════════════
        //  MODÜL TAKMA
        // ════════════════════════════════════════════════════════════════════

        private object PlugModuleInternal(object device, HardwareModuleEntry mod, GsdFileInfo gsd)
        {
            if (mod == null) throw new ArgumentNullException(nameof(mod));
            string modName = string.IsNullOrWhiteSpace(mod.Name) ? null : mod.Name.Trim();

            var allItems = CollectAllDeviceItemsRecursive(device);

            // Aynı isimde modül zaten varsa tekrar takma, sadece adres güncelle
            if (modName != null)
            {
                var existing = allItems.FirstOrDefault(di => string.Equals(GetPropString(di, "Name"), modName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    ApplyModuleAddresses(existing, mod);
                    return existing;
                }
            }

            var candidates = BuildModuleTypeIdentifierCandidates(device, mod, gsd);
            if (candidates.Count == 0)
                throw new InvalidOperationException("Modül için TypeIdentifier/GsdModuleKeyword çözülemedi.");

            // Takılabilecek konteynerler: Rack (System:Rack) öncelikli, ardından DeviceItem'lar ve Device
            var containers = new List<object>();
            var rackItem = allItems.FirstOrDefault(di => (GetPropString(di, "TypeIdentifier") ?? "").IndexOf("Rack", StringComparison.OrdinalIgnoreCase) >= 0);
            if (rackItem != null) containers.Add(rackItem);

            var cpuItem = allItems.FirstOrDefault(di => di != rackItem && GetSoftwareFromDeviceItem(di) != null);
            if (cpuItem != null) containers.Add(cpuItem);

            containers.Add(device);
            foreach (var di in allItems)
            {
                if (!containers.Contains(di)) containers.Add(di);
            }

            if (!string.IsNullOrWhiteSpace(mod.ParentItemName))
            {
                var filtered = containers.Where(c => string.Equals(GetPropString(c, "Name"), mod.ParentItemName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count > 0) containers = filtered;
            }

            List<int> positions;
            if (mod.Position.HasValue && mod.Position.Value > 0)
            {
                positions = new List<int> { mod.Position.Value };
            }
            else
            {
                // Boş slotları tara: S7-1200 SM slotları 2..8, S7-1500 slotları 2..31, SB slotu 101, CM slotları 102..104
                positions = new List<int> { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 101, 102, 103, 1, 0 };
            }

            // Siemens TIA Portal isim kuralı: İsim içinde '/' veya özel karakterler bulunamaz
            string plugName = string.IsNullOrWhiteSpace(modName) ? "SM_Module" : System.Text.RegularExpressions.Regex.Replace(modName, @"[^\w\-]", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(plugName)) plugName = "SM_Module";

            var errors = new List<string>();
            foreach (var tid in candidates)
            {
                foreach (var cont in containers)
                {
                    foreach (int pos in positions)
                    {
                        bool can;
                        try
                        {
                            can = Convert.ToBoolean(InvokeMember(cont, "CanPlugNew", tid, plugName, pos));
                        }
                        catch (MissingMethodException) { break; }
                        catch (Exception ex) { errors.Add(Unwrap(ex).Message); break; }

                        if (!can) continue;
                        object item = InvokeMember(cont, "PlugNew", tid, plugName, pos);
                        if (item != null)
                        {
                            ApplyModuleAddresses(item, mod);
                            return item;
                        }
                    }
                }
            }

            throw new InvalidOperationException("Uygun slot bulunamadı veya katalogda yok. Denenen: " + string.Join(", ", candidates.Take(4)) +
                                                (errors.Count > 0 ? " | " + errors.Distinct().First() : ""));
        }

        private List<string> BuildModuleTypeIdentifierCandidates(object device, HardwareModuleEntry mod, GsdFileInfo gsd)
        {
            var candidates = new List<string>();

            // 1. Doğrudan verilen TypeIdentifier ve alternatifler
            if (!string.IsNullOrWhiteSpace(mod.TypeIdentifier))
            {
                candidates.Add(mod.TypeIdentifier.Trim());
            }
            if (mod.AlternativeTypeIdentifiers != null)
            {
                candidates.AddRange(mod.AlternativeTypeIdentifiers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            }

            // 2. GSD modülü ise
            if (!string.IsNullOrWhiteSpace(mod.GsdModuleKeyword) && gsd != null)
            {
                foreach (var it in FindGsdItemsByKeyword(gsd, mod.GsdModuleKeyword))
                {
                    candidates.Add(it.TypeIdentifier);
                    candidates.Add($"GSD:{gsd.FileName.ToUpperInvariant()}/{it.Kind}/{it.Id}");
                }
            }

            // 3. Siemens Standart PLC Modülleri (SM / SB / CM) Doğal Dil ve MLFB Çözümlemesi
            string devTid = GetPropString(device, "TypeIdentifier") ?? "";
            string devName = GetPropString(device, "Name") ?? "";
            bool isS71200 = devTid.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            devTid.IndexOf("6ES7 21", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            devName.IndexOf("1200", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isS71500 = devTid.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            devTid.IndexOf("6ES7 51", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            devName.IndexOf("1500", StringComparison.OrdinalIgnoreCase) >= 0;

            string searchKey = $"{mod.TypeIdentifier} {mod.Name} {mod.GsdModuleKeyword} {mod.Comment}".ToLowerInvariant();

            var baseMlfbList = new List<string>();

            // MLFB regex eşleşmesi (ör. 6ES7 223-1BL32-0XB0 veya 6ES7523-1BL00-0AA0)
            var mlfbMatch = System.Text.RegularExpressions.Regex.Match(searchKey, @"6ES7\s*([0-9A-Z]{3})\s*-\s*([0-9A-Z]{5})\s*-\s*([0-9A-Z]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mlfbMatch.Success)
            {
                baseMlfbList.Add($"6ES7 {mlfbMatch.Groups[1].Value.ToUpperInvariant()}-{mlfbMatch.Groups[2].Value.ToUpperInvariant()}-{mlfbMatch.Groups[3].Value.ToUpperInvariant()}");
            }

            // 16DI / 16DQ (16I 16Q / 16 in 16 out) Modülleri
            if (searchKey.Contains("16i") || searchKey.Contains("16di") || searchKey.Contains("16 i") || searchKey.Contains("16 di") ||
                searchKey.Contains("di 16") || searchKey.Contains("di16") || searchKey.Contains("sm 1223") || searchKey.Contains("sm1223") ||
                searchKey.Contains("16q") || searchKey.Contains("16dq") || searchKey.Contains("dq 16") || searchKey.Contains("dq16") ||
                searchKey.Contains("16 in") || searchKey.Contains("16 out") || searchKey.Contains("16in") || searchKey.Contains("16out") ||
                searchKey.Contains("16 giris") || searchKey.Contains("16 cikis"))
            {
                bool isCombined = searchKey.Contains("16i 16q") || searchKey.Contains("16i/16q") || searchKey.Contains("16di 16dq") ||
                                  searchKey.Contains("16di/16dq") || searchKey.Contains("di 16/dq 16") || searchKey.Contains("di16/dq16") ||
                                  searchKey.Contains("di 16 / dq 16") || searchKey.Contains("di16_dq16") || searchKey.Contains("sm 1223") ||
                                  searchKey.Contains("sm1223") ||
                                  (searchKey.Contains("16 in") && searchKey.Contains("16 out")) ||
                                  (searchKey.Contains("16in") && searchKey.Contains("16out")) ||
                                  (searchKey.Contains("16 giris") && searchKey.Contains("16 cikis")) ||
                                  (searchKey.Contains("16") && (searchKey.Contains("i") || searchKey.Contains("di") || searchKey.Contains("in") || searchKey.Contains("giris")) && (searchKey.Contains("q") || searchKey.Contains("dq") || searchKey.Contains("out") || searchKey.Contains("cikis")));

                if (isCombined)
                {
                    if (isS71200 || !isS71500)
                    {
                        baseMlfbList.Add("6ES7 223-1BL32-0XB0"); // SM 1223 DI 16/DQ 16 x 24VDC Transistor
                        baseMlfbList.Add("6ES7 223-1PH32-0XB0"); // SM 1223 DI 16/DQ 16 x 24VDC Relay
                        baseMlfbList.Add("6ES7 223-1BL30-0XB0");
                        baseMlfbList.Add("6ES7 223-1PH30-0XB0");
                    }
                    if (isS71500 || !isS71200)
                    {
                        baseMlfbList.Add("6ES7 523-1BL00-0AA0"); // DI 16/DQ 16 x24VDC BA
                    }
                }
                else if (searchKey.Contains("16i") || searchKey.Contains("16di") || searchKey.Contains("di 16") || searchKey.Contains("di16") || searchKey.Contains("sm 1221"))
                {
                    // Sadece 16DI
                    if (isS71200 || !isS71500)
                    {
                        baseMlfbList.Add("6ES7 221-1BL32-0XB0"); // SM 1221 DI 16 x 24VDC
                        baseMlfbList.Add("6ES7 221-1BL30-0XB0");
                    }
                    if (isS71500 || !isS71200)
                    {
                        baseMlfbList.Add("6ES7 521-1BH00-0AB0"); // DI 16x24VDC HF
                        baseMlfbList.Add("6ES7 521-1BH10-0AA0"); // DI 16x24VDC BA
                    }
                }
                else if (searchKey.Contains("16q") || searchKey.Contains("16dq") || searchKey.Contains("dq 16") || searchKey.Contains("dq16") || searchKey.Contains("sm 1222"))
                {
                    // Sadece 16DQ
                    if (isS71200 || !isS71500)
                    {
                        baseMlfbList.Add("6ES7 222-1BL32-0XB0"); // SM 1222 DQ 16 x 24VDC
                        baseMlfbList.Add("6ES7 222-1HH32-0XB0"); // SM 1222 DQ 16 x Relay
                    }
                    if (isS71500 || !isS71200)
                    {
                        baseMlfbList.Add("6ES7 522-1BH01-0AB0");
                        baseMlfbList.Add("6ES7 522-1BH10-0AA0");
                    }
                }
            }

            // 8DI / 8DQ Modülleri
            if (searchKey.Contains("8i") || searchKey.Contains("8di") || searchKey.Contains("di 8") || searchKey.Contains("di8"))
            {
                if (searchKey.Contains("8q") || searchKey.Contains("8dq") || searchKey.Contains("dq 8") || searchKey.Contains("dq8"))
                {
                    if (isS71200 || !isS71500)
                    {
                        baseMlfbList.Add("6ES7 223-1BH32-0XB0"); // SM 1223 DI 8/DQ 8 x 24VDC
                        baseMlfbList.Add("6ES7 223-1PH32-0XB0");
                    }
                }
                else
                {
                    if (isS71200 || !isS71500)
                    {
                        baseMlfbList.Add("6ES7 221-1BF32-0XB0"); // SM 1221 DI 8 x 24VDC
                    }
                }
            }

            // Sadece 8DQ
            if ((searchKey.Contains("8q") || searchKey.Contains("8dq") || searchKey.Contains("dq 8") || searchKey.Contains("dq8")) &&
                !searchKey.Contains("di") && !searchKey.Contains("8i"))
            {
                if (isS71200 || !isS71500)
                {
                    baseMlfbList.Add("6ES7 222-1BF32-0XB0"); // SM 1222 DQ 8 x 24VDC
                    baseMlfbList.Add("6ES7 222-1HF32-0XB0"); // SM 1222 DQ 8 x Relay
                }
            }

            // Analog Modüller (AI / AQ)
            if (searchKey.Contains("analog") || searchKey.Contains("ai") || searchKey.Contains("aq") || searchKey.Contains("sm 1231") || searchKey.Contains("sm 1232") || searchKey.Contains("sm 1234"))
            {
                if (isS71200 || !isS71500)
                {
                    if (searchKey.Contains("4ai") || searchKey.Contains("ai 4") || searchKey.Contains("1231"))
                        baseMlfbList.Add("6ES7 231-4HD32-0XB0"); // SM 1231 AI 4
                    if (searchKey.Contains("8ai") || searchKey.Contains("ai 8"))
                        baseMlfbList.Add("6ES7 231-4HF32-0XB0"); // SM 1231 AI 8
                    if (searchKey.Contains("2aq") || searchKey.Contains("aq 2") || searchKey.Contains("1232"))
                        baseMlfbList.Add("6ES7 232-4HB32-0XB0"); // SM 1232 AQ 2
                    if (searchKey.Contains("4aq") || searchKey.Contains("aq 4"))
                        baseMlfbList.Add("6ES7 232-4HD32-0XB0"); // SM 1232 AQ 4
                    if (searchKey.Contains("1234") || (searchKey.Contains("ai") && searchKey.Contains("aq")))
                        baseMlfbList.Add("6ES7 234-4HE32-0XB0"); // SM 1234 AI 4 / AQ 2
                }
            }

            // 4. Her MLFB için Siemens Openness versiyon türevlerini oluştur
            foreach (var mlfb in baseMlfbList.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add($"OrderNumber:{mlfb}/V2.0");
                candidates.Add($"OrderNumber:{mlfb}/V2.1");
                candidates.Add($"OrderNumber:{mlfb}/V2.2");
                candidates.Add($"OrderNumber:{mlfb}/V1.0");
                candidates.Add($"OrderNumber:{mlfb}/V3.0");
                candidates.Add($"OrderNumber:{mlfb}/V4.0");
                candidates.Add($"OrderNumber:{mlfb}");
            }

            return candidates.Distinct(StringComparer.Ordinal).ToList();
        }

        private void ApplyModuleAddresses(object item, HardwareModuleEntry mod)
        {
            if (!mod.InputStartAddress.HasValue && !mod.OutputStartAddress.HasValue) return;
            foreach (var di in new[] { item }.Concat(CollectAllDeviceItemsRecursive(item)))
            {
                foreach (var addr in AsEnumerable(GetPropValue(di, "Addresses")))
                {
                    string ioType = GetPropString(addr, "IoType");
                    try
                    {
                        if (mod.InputStartAddress.HasValue && ioType.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                            SetPropValue(addr, "StartAddress", mod.InputStartAddress.Value);
                        else if (mod.OutputStartAddress.HasValue && ioType.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
                            SetPropValue(addr, "StartAddress", mod.OutputStartAddress.Value);
                    }
                    catch { }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  TECHNOLOGY OBJECTS
        // ════════════════════════════════════════════════════════════════════

        private object GetTechnologyObjectsComposition(object plcSoftware)
        {
            object group = GetPropValue(plcSoftware, "TechnologicalObjectGroup");
            return GetPropValue(group, "TechnologicalObjects");
        }

        private void CreateOrUpdateTechnologyObjectInternal(object project, TiaPlcItem plc, TechnologyObjectEntry entry, Action<string, string, bool> stepCallback)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                throw new ArgumentException("Technology Object adı boş.");
            string name = entry.Name.Trim();
            string type = string.IsNullOrWhiteSpace(entry.Type) ? "TO_PositioningAxis" : entry.Type.Trim();

            object toComp = GetTechnologyObjectsComposition(plc.RawPlcSoftware);
            if (toComp == null)
                throw new InvalidOperationException("Bu PLC Technology Object desteklemiyor (TechnologicalObjectGroup yok).");

            object to = SafeFind(toComp, name);
            if (to != null && entry.ReplaceIfExists)
            {
                InvokeMember(to, "Delete");
                to = null;
            }

            if (to == null)
            {
                var versions = new List<string>();
                if (!string.IsNullOrWhiteSpace(entry.Version)) versions.Add(entry.Version.Trim());
                versions.AddRange(GetTechnologyObjectVersionCandidates(type));

                var errors = new List<string>();
                foreach (var vs in versions.Distinct())
                {
                    if (!Version.TryParse(vs.Contains('.') ? vs : vs + ".0", out Version ver)) continue;
                    try
                    {
                        to = InvokeMember(toComp, "Create", name, type, ver);
                        if (to != null)
                        {
                            stepCallback?.Invoke("TO", $"Technology Object oluşturuldu: {name} [{type} V{ver}]", true);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"V{ver}: {Unwrap(ex).Message}");
                    }
                }
                if (to == null)
                    throw new InvalidOperationException($"{type} oluşturulamadı. " + string.Join(" | ", errors.Distinct().Take(3)));
            }
            else
            {
                stepCallback?.Invoke("TO", $"Technology Object zaten mevcut, güncelleniyor: {name}", true);
            }

            // Sürücü / telegram bağlantısı
            if (!string.IsNullOrWhiteSpace(entry.DriveDeviceName))
            {
                try
                {
                    string connected = ConnectAxisToDrive(project, to, entry);
                    stepCallback?.Invoke("TO", $"{name} → {entry.DriveDeviceName} sürücüsüne bağlandı ({connected})", true);
                }
                catch (Exception ex)
                {
                    stepCallback?.Invoke("TO", $"{name}: sürücü bağlantısı yapılamadı – {Unwrap(ex).Message} (TIA Portal'da 'Hardware interface' ekranından elle seçilebilir)", false);
                }
            }

            // Parametreler
            if (entry.Parameters != null && entry.Parameters.Count > 0)
            {
                int okCount = 0;
                var failed = new List<string>();
                foreach (var kv in entry.Parameters)
                {
                    try
                    {
                        if (SetTechnologyParameter(to, kv.Key, kv.Value)) okCount++;
                        else failed.Add(kv.Key);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{kv.Key} ({Unwrap(ex).Message})");
                    }
                }
                stepCallback?.Invoke("TO", $"{name}: {okCount}/{entry.Parameters.Count} parametre ayarlandı" +
                                           (failed.Count > 0 ? $" – ayarlanamayan: {string.Join(", ", failed.Take(5))}" : ""), failed.Count == 0);
            }
        }

        private static IEnumerable<string> GetTechnologyObjectVersionCandidates(string type)
        {
            string t = (type ?? "").ToLowerInvariant();
            if (t.StartsWith("pid_compact")) return new[] { "3.0", "2.4", "2.3", "2.2", "2.1", "2.0", "1.2", "1.1", "1.0" };
            if (t.StartsWith("pid_3step")) return new[] { "3.0", "2.4", "2.3", "2.2", "2.1", "2.0", "1.1", "1.0" };
            if (t.StartsWith("pid_temp")) return new[] { "1.2", "1.1", "1.0" };
            if (t.StartsWith("high_speed_counter") || t.StartsWith("hsc")) return new[] { "1.0" };
            return new[] { "9.0", "8.0", "7.0", "6.0", "5.0", "4.0", "3.0", "2.0", "1.0" };
        }

        private bool SetTechnologyParameter(object to, string paramName, string rawValue)
        {
            object parameters = GetPropValue(to, "Parameters");
            object p = SafeFind(parameters, paramName);
            if (p == null) return false;

            object current = GetPropValue(p, "Value");
            object converted = ConvertParameterValue(rawValue, current?.GetType());
            PropertyInfo pi = p.GetType().GetProperty("Value");
            if (pi == null || !pi.CanWrite) return false;
            pi.SetValue(p, converted, null);
            return true;
        }

        private static object ConvertParameterValue(string raw, Type targetType)
        {
            if (raw == null) return null;
            string s = raw.Trim();
            if (targetType == null || targetType == typeof(string)) 
            {
                if (bool.TryParse(s, out bool bb)) return bb;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ii)) return ii;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double dd)) return dd;
                return s;
            }
            if (targetType == typeof(bool))
            {
                if (s == "1") return true;
                if (s == "0") return false;
                return bool.Parse(s);
            }
            if (targetType.IsEnum) return Enum.Parse(targetType, s, true);
            return Convert.ChangeType(s.Replace(',', '.'), targetType, CultureInfo.InvariantCulture);
        }

        private string ConnectAxisToDrive(object project, object to, TechnologyObjectEntry entry)
        {
            object driveDevice = FindDeviceByName(project, entry.DriveDeviceName.Trim());
            if (driveDevice == null) throw new InvalidOperationException($"'{entry.DriveDeviceName}' cihazı projede bulunamadı.");

            object telegramItem = FindTelegramItem(driveDevice, entry.TelegramModuleKeyword);
            if (telegramItem == null) throw new InvalidOperationException("Sürücüde telegram modülü bulunamadı (önce cihaza telegram takın).");

            Type providerType = FindEngineeringType("Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider");
            object provider = providerType != null ? InvokeGetService(to, providerType) : null;
            if (provider == null) throw new InvalidOperationException("AxisHardwareConnectionProvider servisi bu TO/TIA sürümünde yok.");

            var done = new List<string>();
            object actor = GetPropValue(provider, "ActorInterface");
            if (actor != null)
            {
                InvokeMember(actor, "Connect", telegramItem);
                done.Add("Actor");
            }

            object sensors = GetPropValue(provider, "SensorInterface");
            object sensor0 = AsEnumerable(sensors).FirstOrDefault() ?? (sensors != null && !(sensors is IEnumerable) ? sensors : null);
            if (sensor0 != null)
            {
                try { InvokeMember(sensor0, "Connect", telegramItem); done.Add("Encoder"); } catch { }
            }

            return $"{GetPropString(telegramItem, "Name")} · {string.Join("+", done)}";
        }

        private object FindTelegramItem(object driveDevice, string keyword)
        {
            var items = CollectAllDeviceItemsRecursive(driveDevice);
            string kw = NormalizeKey(keyword);
            if (!string.IsNullOrEmpty(kw))
            {
                var hit = items.FirstOrDefault(di => NormalizeKey(GetPropString(di, "Name")).Contains(kw) || NormalizeKey(GetPropString(di, "TypeIdentifier")).Contains(kw));
                if (hit != null) return hit;
            }
            var tel = items.FirstOrDefault(di => GetPropString(di, "Name").IndexOf("telegram", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 GetPropString(di, "Name").IndexOf("telegramm", StringComparison.OrdinalIgnoreCase) >= 0);
            if (tel != null) return tel;

            // Hem giriş hem çıkış adresi olan ilk alt modül
            return items.FirstOrDefault(di =>
            {
                var addrs = AsEnumerable(GetPropValue(di, "Addresses")).ToList();
                return addrs.Any(a => GetPropString(a, "IoType").IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0) &&
                       addrs.Any(a => GetPropString(a, "IoType").IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0);
            });
        }

        // ════════════════════════════════════════════════════════════════════
        //  SİLME
        // ════════════════════════════════════════════════════════════════════

        private int DeleteHardwareInternal(TiaPlcItem plc, IEnumerable<string> devices, IEnumerable<string> tos, Action<string, string, bool> stepCallback)
        {
            int count = 0;
            object project = GetProjectFromPlc(plc);

            if (tos != null)
            {
                object toComp = GetTechnologyObjectsComposition(plc.RawPlcSoftware);
                foreach (var raw in tos.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
                {
                    try
                    {
                        var targets = raw == "*"
                            ? AsEnumerable(toComp).ToList()
                            : new List<object> { SafeFind(toComp, raw) }.Where(o => o != null).ToList();
                        if (targets.Count == 0)
                        {
                            stepCallback?.Invoke("silme", $"Technology Object bulunamadı: {raw}", false);
                            continue;
                        }
                        foreach (var t in targets)
                        {
                            string n = GetPropString(t, "Name");
                            InvokeMember(t, "Delete");
                            count++;
                            stepCallback?.Invoke("silme", $"Technology Object silindi: {n}", true);
                        }
                    }
                    catch (Exception ex)
                    {
                        stepCallback?.Invoke("silme", $"Technology Object silinemedi: {raw} – {Unwrap(ex).Message}", false);
                    }
                }
            }

            if (devices != null && project != null)
            {
                foreach (var raw in devices.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
                {
                    try
                    {
                        if (raw == "*")
                        {
                            // Hedef PLC hariç tüm cihazlar
                            foreach (var dev in CollectAllDevicesInProject(project).Where(d => !IsSameObject(d, plc.RawDevice)).ToList())
                            {
                                string n = GetPropString(dev, "Name");
                                InvokeMember(dev, "Delete");
                                count++;
                                stepCallback?.Invoke("silme", $"Cihaz silindi: {n}", true);
                            }
                            continue;
                        }

                        object device = FindDeviceByName(project, raw);
                        if (device != null)
                        {
                            if (IsSameObject(device, plc.RawDevice))
                            {
                                // PLC'nin kendisi silinmez; aynı isimde modül varsa onu sil
                                object sub = FindPluggedModule(plc.RawDevice, raw);
                                if (sub == null)
                                {
                                    stepCallback?.Invoke("silme", $"'{raw}' hedef PLC'nin kendisi, silinmedi.", false);
                                    continue;
                                }
                                InvokeMember(sub, "Delete");
                                count++;
                                stepCallback?.Invoke("silme", $"PLC modülü silindi: {raw}", true);
                                continue;
                            }
                            InvokeMember(device, "Delete");
                            count++;
                            stepCallback?.Invoke("silme", $"Cihaz silindi: {raw}", true);
                            continue;
                        }

                        // Cihaz değilse: herhangi bir cihazdaki modül
                        object module = null;
                        foreach (var dev in CollectAllDevicesInProject(project))
                        {
                            module = FindPluggedModule(dev, raw);
                            if (module != null) break;
                        }
                        if (module != null)
                        {
                            InvokeMember(module, "Delete");
                            count++;
                            stepCallback?.Invoke("silme", $"Modül silindi: {raw}", true);
                        }
                        else
                        {
                            stepCallback?.Invoke("silme", $"Cihaz/modül bulunamadı: {raw}", false);
                        }
                    }
                    catch (Exception ex)
                    {
                        stepCallback?.Invoke("silme", $"Cihaz/modül silinemedi: {raw} – {Unwrap(ex).Message}", false);
                    }
                }
            }
            return count;
        }

        private object FindPluggedModule(object device, string name)
        {
            return CollectAllDeviceItemsRecursive(device)
                .FirstOrDefault(di => string.Equals(GetPropString(di, "Name"), name, StringComparison.OrdinalIgnoreCase) &&
                                      GetSoftwareFromDeviceItem(di) == null);
        }

        // ════════════════════════════════════════════════════════════════════
        //  GSDML TARAMA
        // ════════════════════════════════════════════════════════════════════

        private List<string> GetGsdSearchFolders(string projectPath)
        {
            var folders = new List<string>();
            string verKey = _lockedVersion?.VersionKey ?? "V20";
            string verNum = new string(verKey.Where(c => char.IsDigit(c) || c == '_' || c == '.').ToArray()).Trim('_', '.');
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            foreach (var v in new[] { verKey, "V" + verNum })
            {
                folders.Add(Path.Combine(programData, "Siemens", "Automation", "Portal " + v, "data", "Hwcn", "Custom"));
                folders.Add(Path.Combine(programData, "Siemens", "Automation", "Portal " + v));
            }
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                try
                {
                    string projDir = File.Exists(projectPath) ? Path.GetDirectoryName(projectPath) : projectPath;
                    if (!string.IsNullOrWhiteSpace(projDir))
                        folders.Add(Path.Combine(projDir, "AdditionalFiles"));
                }
                catch { }
            }
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }

        public List<GsdFileInfo> ScanInstalledGsdmlFiles(string projectPath, bool forceRefresh)
        {
            lock (_gsdCache)
            {
                if (!forceRefresh && _gsdCache.Count > 0 && (DateTime.Now - _gsdCacheTime).TotalMinutes < 10)
                    return _gsdCache.Values.ToList();

                _gsdCache.Clear();
                foreach (var folder in GetGsdSearchFolders(projectPath))
                {
                    foreach (var file in SafeEnumerateFiles(folder, "GSDML-*.xml", 8))
                    {
                        string fn = Path.GetFileName(file);
                        if (_gsdCache.ContainsKey(fn)) continue;
                        var info = ParseGsdmlFile(file);
                        if (info != null) _gsdCache[fn] = info;
                    }
                }
                _gsdCacheTime = DateTime.Now;
                return _gsdCache.Values.ToList();
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern, int maxDepth)
        {
            var stack = new Stack<Tuple<string, int>>();
            stack.Push(Tuple.Create(root, 0));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                string[] files = null;
                try { files = Directory.GetFiles(cur.Item1, pattern); } catch { }
                if (files != null) foreach (var f in files) yield return f;

                if (cur.Item2 >= maxDepth) continue;
                string[] dirs = null;
                try { dirs = Directory.GetDirectories(cur.Item1); } catch { }
                if (dirs != null) foreach (var d in dirs) stack.Push(Tuple.Create(d, cur.Item2 + 1));
            }
        }

        private static GsdFileInfo ParseGsdmlFile(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var primary = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PrimaryLanguage");
                if (primary != null)
                {
                    foreach (var t in primary.Elements().Where(e => e.Name.LocalName == "Text"))
                    {
                        string id = (string)t.Attribute("TextId");
                        if (id != null && !texts.ContainsKey(id)) texts[id] = (string)t.Attribute("Value") ?? "";
                    }
                }

                string fileName = Path.GetFileName(path);
                var info = new GsdFileInfo { FileName = fileName, FullPath = path };

                void AddItems(string elementName, string kind)
                {
                    foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == elementName))
                    {
                        string id = (string)el.Attribute("ID");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var nameEl = el.Elements().FirstOrDefault(e => e.Name.LocalName == "ModuleInfo")?
                                       .Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                        string textId = (string)nameEl?.Attribute("TextId");
                        string name = textId != null && texts.TryGetValue(textId, out var n) ? n : (textId ?? id);
                        info.Items.Add(new GsdItemInfo
                        {
                            Kind = kind,
                            Id = id,
                            Name = name,
                            TypeIdentifier = $"GSD:{fileName}/{kind}/{id}"
                        });
                    }
                }

                AddItems("DeviceAccessPointItem", "DAP");
                AddItems("ModuleItem", "M");
                AddItems("SubmoduleItem", "SM");
                return info;
            }
            catch
            {
                return null;
            }
        }

        private GsdFileInfo FindGsdFileForDevice(object project, object device)
        {
            var tids = new List<string> { GetPropString(device, "TypeIdentifier") };
            tids.AddRange(CollectAllDeviceItemsRecursive(device).Select(di => GetPropString(di, "TypeIdentifier")));
            string gsdTid = tids.FirstOrDefault(t => t.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase));
            if (gsdTid == null) return null;

            string fileName = gsdTid.Substring(4).Split('/')[0];
            var files = ScanInstalledGsdmlFiles(GetPropString(project, "Path"), forceRefresh: false);
            return files.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<GsdItemInfo> FindGsdItemsByKeyword(GsdFileInfo gsd, string keyword)
        {
            string kw = NormalizeKey(keyword);
            if (string.IsNullOrEmpty(kw)) return Enumerable.Empty<GsdItemInfo>();

            return gsd.Items
                .Where(i => i.Kind != "DAP")
                .Select(i => new { Item = i, Key = NormalizeKey(i.Name), IdKey = NormalizeKey(i.Id) })
                .Where(x => KeywordMatches(x.Key, kw) || KeywordMatches(x.IdKey, kw))
                .OrderBy(x => x.Key.Length)
                .Select(x => x.Item);
        }

        /// <summary>"telegram3" anahtarı "standardtelegram3,pzd59" ile eşleşir ama "telegram30" ile eşleşmez.</summary>
        private static bool KeywordMatches(string haystack, string kw)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            int idx = haystack.IndexOf(kw, StringComparison.Ordinal);
            while (idx >= 0)
            {
                int after = idx + kw.Length;
                bool endsWithDigit = char.IsDigit(kw[kw.Length - 1]);
                if (!endsWithDigit || after >= haystack.Length || !char.IsDigit(haystack[after]))
                    return true;
                idx = haystack.IndexOf(kw, idx + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return new string(s.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-').ToArray());
        }

        // ════════════════════════════════════════════════════════════════════
        //  YARDIMCILAR
        // ════════════════════════════════════════════════════════════════════

        private object GetProjectFromPlc(TiaPlcItem plc)
        {
            object cur = plc?.RawDevice ?? plc?.RawPlcSoftware;
            for (int i = 0; i < 30 && cur != null; i++)
            {
                if (cur.GetType().FullName == "Siemens.Engineering.Project") return cur;
                cur = GetPropValue(cur, "Parent");
            }
            return null;
        }

        private object FindDeviceByName(object project, string name)
        {
            if (project == null || string.IsNullOrWhiteSpace(name)) return null;
            var devices = CollectAllDevicesInProject(project);
            var byDev = devices.FirstOrDefault(d => string.Equals(GetPropString(d, "Name"), name, StringComparison.OrdinalIgnoreCase));
            if (byDev != null) return byDev;

            // GSD cihazlarda isim genelde head device item üzerindedir
            foreach (var d in devices)
            {
                var items = GetPropValue(d, "DeviceItems") as IEnumerable;
                if (items == null) continue;
                foreach (var di in items)
                {
                    if (string.Equals(GetPropString(di, "Name"), name, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
            return null;
        }

        private object GetNetworkInterface(object deviceItem)
        {
            Type niType = FindEngineeringType("Siemens.Engineering.HW.Features.NetworkInterface");
            return niType != null ? InvokeGetService(deviceItem, niType) : null;
        }

        private object FindEthernetInterface(object device, out object ownerItem)
        {
            ownerItem = null;
            if (device == null) return null;
            foreach (var di in CollectAllDeviceItemsRecursive(device))
            {
                object ni = GetNetworkInterface(di);
                if (ni == null) continue;
                string itype = GetPropString(ni, "InterfaceType");
                if (string.IsNullOrEmpty(itype) || itype.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (AsEnumerable(GetPropValue(ni, "Nodes")).Any())
                    {
                        ownerItem = di;
                        return ni;
                    }
                }
            }
            return null;
        }

        private List<object> DescribeAddresses(object deviceItem)
        {
            var list = new List<object>();
            foreach (var a in AsEnumerable(GetPropValue(deviceItem, "Addresses")))
            {
                list.Add(new
                {
                    ioType = GetPropString(a, "IoType"),
                    start = GetPropInt(a, "StartAddress"),
                    length = GetPropInt(a, "Length")
                });
            }
            return list;
        }

        private string DescribeAddressesShort(object item)
        {
            var parts = new List<string>();
            foreach (var di in new[] { item }.Concat(CollectAllDeviceItemsRecursive(item)))
            {
                foreach (var a in AsEnumerable(GetPropValue(di, "Addresses")))
                {
                    string io = GetPropString(a, "IoType");
                    string prefix = io.StartsWith("In", StringComparison.OrdinalIgnoreCase) ? "I" : io.StartsWith("Out", StringComparison.OrdinalIgnoreCase) ? "Q" : io;
                    int start = GetPropInt(a, "StartAddress");
                    int len = GetPropInt(a, "Length") / 8;
                    parts.Add(len > 1 ? $"{prefix}{start}..{start + len - 1}" : $"{prefix}{start}");
                }
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "adres yok";
        }

        private static bool IsSameObject(object a, object b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            try { return a.Equals(b); } catch { return false; }
        }

        private static IEnumerable<object> AsEnumerable(object maybeCollection)
        {
            if (maybeCollection is IEnumerable en && !(maybeCollection is string))
            {
                foreach (var o in en) yield return o;
            }
        }

        private static object SafeFind(object composition, string name)
        {
            if (composition == null) return null;
            try
            {
                var m = composition.GetType().GetMethod("Find", new[] { typeof(string) });
                if (m != null) return m.Invoke(composition, new object[] { name });
            }
            catch { }
            return AsEnumerable(composition).FirstOrDefault(o => string.Equals(GetPropString(o, "Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static object SafeGetAttribute(object obj, string attr)
        {
            try { return InvokeMember(obj, "GetAttribute", attr); } catch { return null; }
        }

        private static void SafeSetAttribute(object obj, string attr, object value)
        {
            // Enum değerli attribute'larda string → enum dönüşümünü dene
            if (value is string sv)
            {
                object cur = SafeGetAttribute(obj, attr);
                if (cur != null && cur.GetType().IsEnum)
                    value = Enum.Parse(cur.GetType(), sv, true);
            }
            InvokeMember(obj, "SetAttribute", attr, value);
        }

        private static bool SetPropValue(object obj, string propName, object value)
        {
            PropertyInfo pi = obj?.GetType().GetProperty(propName);
            if (pi == null || !pi.CanWrite) return false;
            object v = value;
            if (value != null && !pi.PropertyType.IsInstanceOfType(value))
                v = Convert.ChangeType(value, Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType, CultureInfo.InvariantCulture);
            pi.SetValue(obj, v, null);
            return true;
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex?.GetBaseException() ?? ex;
        }

        /// <summary>Reflection ile tip uyumlu public metodu bulup çağırır (arayüz metodları dahil).</summary>
        private static object InvokeMember(object target, string methodName, params object[] args)
        {
            if (target == null) throw new InvalidOperationException($"{methodName}: hedef nesne bulunamadı.");
            args = args ?? new object[0];

            var type = target.GetType();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()))
                .Where(m => m.Name == methodName && !m.IsGenericMethodDefinition && m.GetParameters().Length == args.Length)
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                var conv = new object[args.Length];
                bool match = true;
                for (int i = 0; i < args.Length && match; i++)
                {
                    object a = args[i];
                    Type pt = ps[i].ParameterType;
                    if (a == null)
                    {
                        if (pt.IsValueType && Nullable.GetUnderlyingType(pt) == null) match = false;
                        else conv[i] = null;
                    }
                    else if (pt.IsInstanceOfType(a)) conv[i] = a;
                    else if (pt.IsEnum && a is string es)
                    {
                        try { conv[i] = Enum.Parse(pt, es, true); } catch { match = false; }
                    }
                    else if (pt == typeof(object)) conv[i] = a;
                    else match = false;
                }
                if (!match) continue;

                try
                {
                    return m.Invoke(target, conv);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw;
                }
            }

            throw new MissingMethodException(type.FullName, methodName);
        }
    }
}

