using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;
using System.Web.Script.Serialization;
using TIAgent.Models;

namespace TIAgent.Services
{
    public partial class TiaOpennessService : IDisposable
    {
        private readonly Action<string> _log;
        private readonly Action<string> _errorLog;

        private readonly BlockingCollection<OpennessWorkItem> _workQueue = new BlockingCollection<OpennessWorkItem>();
        private readonly Thread _opennessThread;
        private int _opennessThreadId;
        private bool _disposed;

        private TiaVersionInfo _lockedVersion;
        private Assembly _engAssembly;
        private Assembly _hmiAssembly;
        private object _currentTiaPortal;
        private int _connectedProcessId;

        public TiaVersionInfo LockedVersion => _lockedVersion;
        public bool IsVersionLocked => _lockedVersion != null && _engAssembly != null;
        public bool IsConnected => _currentTiaPortal != null;

        private class OpennessWorkItem
        {
            public Func<object> Work { get; set; }
            public TaskCompletionSource<object> Tcs { get; set; }
        }

        public TiaOpennessService(Action<string> log = null, Action<string> errorLog = null)
        {
            _log = log ?? (_ => { });
            _errorLog = errorLog ?? (_ => { });

            _opennessThread = new Thread(OpennessThreadMain)
            {
                Name = "OpennessThread",
                IsBackground = true
            };
            _opennessThread.SetApartmentState(ApartmentState.STA);
            _opennessThread.Start();
        }

        private void OpennessThreadMain()
        {
            _opennessThreadId = Thread.CurrentThread.ManagedThreadId;

            foreach (var item in _workQueue.GetConsumingEnumerable())
            {
                try
                {
                    object result = item.Work();
                    item.Tcs.TrySetResult(result);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    item.Tcs.TrySetException(tie.InnerException);
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
            }
        }

        public Task<T> RunOnOpennessThreadAsync<T>(Func<T> func)
        {
            if (Thread.CurrentThread.ManagedThreadId == _opennessThreadId)
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    var tcsErr = new TaskCompletionSource<T>();
                    tcsErr.SetException(ex);
                    return tcsErr.Task;
                }
            }

            var tcs = new TaskCompletionSource<object>();
            _workQueue.Add(new OpennessWorkItem
            {
                Work = () => func(),
                Tcs = tcs
            });

            return tcs.Task.ContinueWith(t => (T)t.Result, TaskContinuationOptions.ExecuteSynchronously);
        }

        public void RunOnOpennessThread(Action action)
        {
            RunOnOpennessThreadAsync<object>(() =>
            {
                action();
                return null;
            }).GetAwaiter().GetResult();
        }

        public List<TiaVersionInfo> GetInstalledAndLibVersions()
        {
            var list = new List<TiaVersionInfo>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            var versionDefs = new[]
            {
                new { Key = "V21", Display = "TIA Portal V21", VerPrefix = "21.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V21" },
                new { Key = "V20", Display = "TIA Portal V20", VerPrefix = "20.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V20" },
                new { Key = "V19", Display = "TIA Portal V19", VerPrefix = "19.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V19" },
                new { Key = "V18", Display = "TIA Portal V18", VerPrefix = "18.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V18" },
                new { Key = "V17", Display = "TIA Portal V17", VerPrefix = "17.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V17" },
                new { Key = "V16", Display = "TIA Portal V16", VerPrefix = "16.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V16" },
                new { Key = "V15.1", Display = "TIA Portal V15.1", VerPrefix = "15.1", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V15_1" },
                new { Key = "V15", Display = "TIA Portal V15", VerPrefix = "15.0", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V15" },
                new { Key = "V14 SP1", Display = "TIA Portal V14 SP1", VerPrefix = "14.0.1", ProgFolder = @"C:\Program Files\Siemens\Automation\Portal V14" }
            };

            foreach (var def in versionDefs)
            {
                string resolvedDll = null;
                string resolvedFolder = null;
                string resolvedHmi = null;
                bool fromInstalledTia = false;

                // 1. Program Files (check both Portal executable and PublicAPI DLL)
                string portalExe = Path.Combine(def.ProgFolder, "Bin", "Siemens.Automation.Portal.exe");
                if (File.Exists(portalExe))
                {
                    string[] pfCandidates =
                    {
                        Path.Combine(def.ProgFolder, "PublicAPI", def.Key, "Siemens.Engineering.dll"),
                        Path.Combine(def.ProgFolder, "PublicAPI", def.Key, "net48", "Siemens.Engineering.Base.dll")
                    };

                    foreach (var pf in pfCandidates)
                    {
                        if (File.Exists(pf))
                        {
                            resolvedDll = pf;
                            resolvedFolder = Path.GetDirectoryName(pf);
                            fromInstalledTia = true;
                            break;
                        }
                    }
                }

                // 2. Windows Registry fallback (if TIA Portal is installed on a different drive/path)
                if (resolvedDll == null)
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (var opennessKey = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness"))
                        {
                            if (opennessKey != null)
                            {
                                foreach (var subName in opennessKey.GetSubKeyNames())
                                {
                                    if (!subName.StartsWith(def.VerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                                    using (var verKey = opennessKey.OpenSubKey(subName + @"\PublicAPI"))
                                    {
                                        if (verKey == null) continue;
                                        foreach (var valName in verKey.GetValueNames())
                                        {
                                            if (!valName.StartsWith(def.VerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                                            string regPath = verKey.GetValue(valName) as string;
                                            if (!string.IsNullOrWhiteSpace(regPath) && File.Exists(regPath))
                                            {
                                                resolvedDll = regPath;
                                                resolvedFolder = Path.GetDirectoryName(regPath);
                                                fromInstalledTia = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (resolvedDll != null) break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Only include versions that are actually installed on this PC
                if (!fromInstalledTia || string.IsNullOrWhiteSpace(resolvedDll))
                    continue;

                list.Add(new TiaVersionInfo
                {
                    VersionKey = def.Key,
                    DisplayName = def.Display,
                    AssemblyVersionPrefix = def.VerPrefix,
                    PortalInstallFolder = def.ProgFolder,
                    ResolvedFolder = resolvedFolder,
                    EngineeringDllPath = resolvedDll,
                    HmiDllPath = resolvedHmi,
                    IsInstalledInProgramFiles = true
                });
            }

            return list;
        }

        public void EnsureVersionLoaded(TiaVersionInfo version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));

            if (_lockedVersion != null && _engAssembly != null)
            {
                if (!string.Equals(_lockedVersion.VersionKey, version.VersionKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Oturum zaten {_lockedVersion.DisplayName} için kilitli. Sürüm değiştirmek için uygulamayı yeniden başlatın.");
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(version.EngineeringDllPath) || !File.Exists(version.EngineeringDllPath))
            {
                throw new FileNotFoundException(
                    $"{version.DisplayName} için Openness API DLL bulunamadı. Aranan yol: {version.ResolvedFolder}");
            }

            _lockedVersion = version;
            _engAssembly = Assembly.LoadFrom(version.EngineeringDllPath);

            if (!string.IsNullOrWhiteSpace(version.HmiDllPath) && File.Exists(version.HmiDllPath))
            {
                try { _hmiAssembly = Assembly.LoadFrom(version.HmiDllPath); } catch { }
            }

            // Setup AssemblyResolve for dependencies
            AppDomain.CurrentDomain.AssemblyResolve += (s, args) =>
            {
                string asmName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(_lockedVersion?.ResolvedFolder)) return null;

                string candidate = Path.Combine(_lockedVersion.ResolvedFolder, asmName + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);

                return null;
            };

            _log($"API Kütüphanesi yüklendi: {version.DisplayName} ({Path.GetFileName(version.EngineeringDllPath)})");
        }

        public List<TiaProcessItem> GetProcessList(TiaVersionInfo version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));

            // Avoid locking the Openness Assembly in memory if no TIA Portal process for this version is currently running
            if (!IsVersionLocked)
            {
                bool anyMatchingProcess = false;
                try
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName("Siemens.Automation.Portal");
                    foreach (var p in procs)
                    {
                        try
                        {
                            string path = p.MainModule?.FileName ?? "";
                            if (!string.IsNullOrEmpty(version.PortalInstallFolder) &&
                                path.StartsWith(version.PortalInstallFolder, StringComparison.OrdinalIgnoreCase))
                            {
                                anyMatchingProcess = true;
                                break;
                            }
                            if (path.IndexOf(version.VersionKey.Replace(".", "_"), StringComparison.OrdinalIgnoreCase) >= 0 ||
                                path.IndexOf(version.VersionKey, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                anyMatchingProcess = true;
                                break;
                            }
                        }
                        catch
                        {
                            // If MainModule is inaccessible, fall back to loading Openness DLL
                            anyMatchingProcess = true;
                            break;
                        }
                    }
                }
                catch
                {
                    anyMatchingProcess = true;
                }

                if (!anyMatchingProcess)
                {
                    return new List<TiaProcessItem>();
                }
            }

            EnsureVersionLoaded(version);

            return RunOnOpennessThreadAsync(() =>
            {
                var result = new List<TiaProcessItem>();
                Type tiaPortalType = FindEngineeringType("Siemens.Engineering.TiaPortal");
                if (tiaPortalType == null)
                    throw new InvalidOperationException("Siemens.Engineering.TiaPortal tipi bulunamadı.");

                MethodInfo getProcessesMethod = tiaPortalType.GetMethod("GetProcesses", Type.EmptyTypes);
                if (getProcessesMethod == null)
                    throw new InvalidOperationException("TiaPortal.GetProcesses() metodu bulunamadı.");

                var processes = getProcessesMethod.Invoke(null, null) as IEnumerable;
                if (processes == null) return result;

                foreach (var p in processes)
                {
                    int pid = GetPropInt(p, "Id");
                    string mode = GetPropString(p, "Mode");
                    string projPath = GetPropString(p, "ProjectPath");
                    string projName = !string.IsNullOrEmpty(projPath) ? Path.GetFileNameWithoutExtension(projPath) : "";

                    result.Add(new TiaProcessItem
                    {
                        ProcessId = pid,
                        Mode = mode,
                        ProjectPath = projPath,
                        ProjectName = projName,
                        VersionName = version.DisplayName,
                        RawProcess = p
                    });
                }

                return result;
            }).GetAwaiter().GetResult();
        }

        public TiaProjectItem ConnectToProcess(TiaProcessItem processItem)
        {
            if (processItem?.RawProcess == null)
                throw new ArgumentNullException(nameof(processItem));

            return RunOnOpennessThreadAsync(() =>
            {
                if (_currentTiaPortal != null && _connectedProcessId == processItem.ProcessId)
                {
                    try
                    {
                        var existingProj = GetCurrentProjectFromPortal(_currentTiaPortal);
                        if (existingProj != null)
                        {
                            return existingProj;
                        }
                    }
                    catch
                    {
                        try { (_currentTiaPortal as IDisposable)?.Dispose(); } catch { }
                        _currentTiaPortal = null;
                        _connectedProcessId = 0;
                    }
                }
                else if (_currentTiaPortal != null)
                {
                    try { (_currentTiaPortal as IDisposable)?.Dispose(); } catch { }
                    _currentTiaPortal = null;
                    _connectedProcessId = 0;
                }

                _log($"TIA Portal (PID: {processItem.ProcessId}) sürecine bağlanılıyor...");

                MethodInfo attachMethod = processItem.RawProcess.GetType().GetMethod("Attach", Type.EmptyTypes);
                if (attachMethod == null)
                    throw new InvalidOperationException("TiaPortalProcess.Attach() metodu bulunamadı.");

                _currentTiaPortal = attachMethod.Invoke(processItem.RawProcess, null);
                _connectedProcessId = processItem.ProcessId;
                _log("Bağlantı kuruldu. Açık proje taranıyor...");

                return GetCurrentProjectFromPortal(_currentTiaPortal);
            }).GetAwaiter().GetResult();
        }

        public void Disconnect()
        {
            if (_currentTiaPortal == null) return;

            RunOnOpennessThreadAsync(() =>
            {
                if (_currentTiaPortal != null)
                {
                    try
                    {
                        if (_currentTiaPortal is IDisposable disp)
                        {
                            disp.Dispose();
                        }
                        else
                        {
                            var dispMethod = _currentTiaPortal.GetType().GetMethod("Dispose", Type.EmptyTypes);
                            dispMethod?.Invoke(_currentTiaPortal, null);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Bağlantı kapatılırken uyarı: {ex.Message}");
                    }
                    finally
                    {
                        _currentTiaPortal = null;
                    }
                }
                _log("TIA Portal bağlantısı kapatıldı.");
                return (object)null;
            }).GetAwaiter().GetResult();
        }

        public TiaProjectItem OpenProjectFile(TiaVersionInfo version, string projectFilePath)
        {
            EnsureVersionLoaded(version);
            if (!File.Exists(projectFilePath))
                throw new FileNotFoundException("Proje dosyası bulunamadı.", projectFilePath);

            return RunOnOpennessThreadAsync(() =>
            {
                string fullProjectPath = Path.GetFullPath(projectFilePath);

                // 1. Check if this project is ALREADY open in an active TIA Portal instance
                try
                {
                    var processes = GetProcessList(version);
                    var alreadyOpen = processes.FirstOrDefault(p =>
                        !string.IsNullOrWhiteSpace(p.ProjectPath) &&
                        string.Equals(Path.GetFullPath(p.ProjectPath), fullProjectPath, StringComparison.OrdinalIgnoreCase));

                    if (alreadyOpen != null)
                    {
                        _log($"'{Path.GetFileName(projectFilePath)}' projesi zaten açık (PID: {alreadyOpen.ProcessId}). Doğrudan bağlanılıyor...");
                        MethodInfo attachMethod = alreadyOpen.RawProcess.GetType().GetMethod("Attach", Type.EmptyTypes);
                        if (attachMethod != null)
                        {
                            _currentTiaPortal = attachMethod.Invoke(alreadyOpen.RawProcess, null);
                            var proj = GetCurrentProjectFromPortal(_currentTiaPortal);
                            if (proj != null) return proj;
                        }
                    }

                    // 2. Check if there is an idle (empty) TIA Portal instance running
                    var idleProc = processes.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.ProjectPath) || string.IsNullOrWhiteSpace(p.ProjectName));
                    if (idleProc != null)
                    {
                        _log($"Boşta çalışan TIA Portal (PID: {idleProc.ProcessId}) bulundu. Proje bu oturumda açılıyor...");
                        MethodInfo attachMethod = idleProc.RawProcess.GetType().GetMethod("Attach", Type.EmptyTypes);
                        if (attachMethod != null)
                        {
                            _currentTiaPortal = attachMethod.Invoke(idleProc.RawProcess, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Çalışan süreçler kontrol edilirken bilgi: {ex.Message}");
                }

                if (_currentTiaPortal == null)
                {
                    _log($"TIA Portal başlatılıyor ({version.DisplayName}) ve kapalı proje açılıyor: {Path.GetFileName(projectFilePath)}...");

                    Type tiaPortalType = FindEngineeringType("Siemens.Engineering.TiaPortal");
                    Type tiaPortalModeType = FindEngineeringType("Siemens.Engineering.TiaPortalMode");

                    object modeWithGui = Enum.Parse(tiaPortalModeType, "WithUserInterface");
                    ConstructorInfo ctor = tiaPortalType.GetConstructor(new[] { tiaPortalModeType });
                    _currentTiaPortal = ctor.Invoke(new[] { modeWithGui });
                }

                object projectsComp = GetPropValue(_currentTiaPortal, "Projects");
                if (projectsComp == null)
                    throw new InvalidOperationException("TIA Portal Projects koleksiyonu bulunamadı.");

                MethodInfo openMethod = projectsComp.GetType().GetMethod("Open", new[] { typeof(FileInfo) })
                    ?? projectsComp.GetType().GetMethods().FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(FileInfo));

                if (openMethod == null)
                    throw new InvalidOperationException("TIA Portal Projects.Open(FileInfo) metodu bulunamadı.");

                object rawProj = openMethod.Invoke(projectsComp, new object[] { new FileInfo(projectFilePath) });
                _log($"Proje başarıyla açıldı: {Path.GetFileName(projectFilePath)}");

                return new TiaProjectItem
                {
                    Name = GetPropString(rawProj, "Name"),
                    FilePath = projectFilePath,
                    RawProjectObject = rawProj
                };
            }).GetAwaiter().GetResult();
        }

        private TiaProjectItem GetCurrentProjectFromPortal(object portal)
        {
            object projectsComp = GetPropValue(portal, "Projects");
            if (projectsComp == null) return null;

            var projList = projectsComp as IEnumerable;
            if (projList == null) return null;

            foreach (var p in projList)
            {
                string name = GetPropString(p, "Name");
                string path = GetPropString(p, "Path");
                return new TiaProjectItem
                {
                    Name = name,
                    FilePath = path,
                    RawProjectObject = p
                };
            }

            return null;
        }

        public List<TiaPlcItem> GetPlcList(TiaProjectItem project)
        {
            if (project?.RawProjectObject == null) return new List<TiaPlcItem>();

            return RunOnOpennessThreadAsync(() =>
            {
                var localPlcs = new List<TiaPlcItem>();
                var allDevices = CollectAllDevicesInProject(project.RawProjectObject);

                foreach (var dev in allDevices)
                {
                    string devName = GetPropString(dev, "Name");
                    var allDevItems = CollectAllDeviceItemsRecursive(dev);

                    foreach (var di in allDevItems)
                    {
                        object sw = GetSoftwareFromDeviceItem(di);
                        if (sw == null) continue;

                        string swTypeName = sw.GetType().Name;
                        if (swTypeName.IndexOf("PlcSoftware", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string plcName = GetPropString(sw, "Name");
                            if (string.IsNullOrWhiteSpace(plcName))
                                plcName = GetPropString(di, "Name");
                            if (string.IsNullOrWhiteSpace(plcName))
                                plcName = devName;

                            localPlcs.Add(new TiaPlcItem
                            {
                                Name = plcName,
                                DeviceName = devName,
                                TypeName = GetPropString(di, "TypeIdentifier"),
                                RawDevice = dev,
                                RawPlcSoftware = sw
                            });
                        }
                    }
                }

                _log($"Proje '{project.Name}': {localPlcs.Count} adet PLC istasyonu bulundu.");
                return localPlcs;
            }).GetAwaiter().GetResult();
        }

        private List<object> CollectAllDevicesInProject(object projectObj)
        {
            var list = new List<object>();
            if (projectObj == null) return list;

            var directDevs = GetPropValue(projectObj, "Devices") as IEnumerable;
            if (directDevs != null)
            {
                foreach (var d in directDevs) list.Add(d);
            }

            var ungrouped = GetPropValue(projectObj, "UngroupedDevicesGroup");
            if (ungrouped != null)
            {
                CollectDevicesFromGroupRecursive(ungrouped, list);
            }

            var devGroups = GetPropValue(projectObj, "DeviceGroups") as IEnumerable;
            if (devGroups != null)
            {
                foreach (var g in devGroups)
                    CollectDevicesFromGroupRecursive(g, list);
            }

            return list;
        }

        private void CollectDevicesFromGroupRecursive(object groupObj, List<object> list)
        {
            if (groupObj == null) return;
            var devs = GetPropValue(groupObj, "Devices") as IEnumerable;
            if (devs != null)
            {
                foreach (var d in devs)
                {
                    if (!list.Contains(d))
                        list.Add(d);
                }
            }

            var subGroups = GetPropValue(groupObj, "Groups") as IEnumerable;
            if (subGroups != null)
            {
                foreach (var sg in subGroups)
                    CollectDevicesFromGroupRecursive(sg, list);
            }
        }

        private List<object> CollectAllDeviceItemsRecursive(object parentDeviceOrItem)
        {
            var list = new List<object>();
            var items = GetPropValue(parentDeviceOrItem, "DeviceItems") as IEnumerable;
            if (items == null) return list;

            foreach (var di in items)
            {
                list.Add(di);
                list.AddRange(CollectAllDeviceItemsRecursive(di));
            }
            return list;
        }

        private object GetSoftwareFromDeviceItem(object deviceItem)
        {
            if (deviceItem == null) return null;
            Type swContainerType = FindEngineeringType("Siemens.Engineering.HW.Features.SoftwareContainer");
            if (swContainerType == null) return null;

            object swContainer = InvokeGetService(deviceItem, swContainerType);
            return GetPropValue(swContainer, "Software");
        }

        private object InvokeGetService(object engObject, Type serviceType)
        {
            if (engObject == null || serviceType == null) return null;
            try
            {
                MethodInfo getSvcMethod = engObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getSvcMethod != null)
                {
                    var generic = getSvcMethod.MakeGenericMethod(serviceType);
                    return generic.Invoke(engObject, null);
                }
            }
            catch { }
            return null;
        }

        private object _cachedPlcSoftwareForTags;
        private readonly Dictionary<string, string> _cachedPlcTagTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cachedDefinedGlobalSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _cachedLadderDbTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _cachedDbBlocks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _exportedDbNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<SclBlockItem> ScanSclBlocks(TiaPlcItem plc)
        {
            if (plc?.RawPlcSoftware == null) return new List<SclBlockItem>();

            return RunOnOpennessThreadAsync(() =>
            {
                _cachedPlcSoftwareForTags = null;
                _cachedPlcTagTypes.Clear();
                _cachedDefinedGlobalSymbols.Clear();
                _cachedLadderDbTypes.Clear();
                _cachedDbBlocks.Clear();
                _exportedDbNames.Clear();

                var results = new List<SclBlockItem>();
                object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                if (blockGroup == null) return results;

                ScanBlocksRecursive(blockGroup, "", plc.RawPlcSoftware, results);
                _log($"SCL blok taraması tamamlandı: {results.Count} adet FC / FB SCL fonksiyonu bulundu.");
                return results;
            }).GetAwaiter().GetResult();
        }

        private void ScanBlocksRecursive(object currentGroup, string groupPath, object plcSw, List<SclBlockItem> results)
        {
            string groupName = GetPropString(currentGroup, "Name");
            string currentPath = string.IsNullOrEmpty(groupPath) ? groupName : $"{groupPath}/{groupName}";

            // Scan blocks in this group
            object blocksComp = GetPropValue(currentGroup, "Blocks");
            if (blocksComp is IEnumerable blocksEnum)
            {
                foreach (var blk in blocksEnum)
                {
                    string typeName = blk.GetType().Name;
                    string lang = GetPropValue(blk, "ProgrammingLanguage")?.ToString() ?? "";
                    string name = GetPropString(blk, "Name");

                    if (!string.IsNullOrEmpty(name))
                    {
                        _cachedDefinedGlobalSymbols.Add(name);
                    }

                    // Cache DB blocks (GlobalDB, InstanceDB, ArrayDB, DataBlock) so we can resolve DB member datatypes
                    if (!string.IsNullOrEmpty(name) &&
                        (typeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         string.Equals(lang, "DB", StringComparison.OrdinalIgnoreCase)))
                    {
                        _cachedDbBlocks[name] = blk;
                    }

                    // Filter: Only FC or FB and ProgrammingLanguage == SCL
                    bool isFcOrFb = string.Equals(typeName, "FC", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(typeName, "FB", StringComparison.OrdinalIgnoreCase) ||
                                   typeName.IndexOf("Function", StringComparison.OrdinalIgnoreCase) >= 0;

                    bool isScl = string.Equals(lang, "SCL", StringComparison.OrdinalIgnoreCase);

                    if (isFcOrFb && isScl)
                    {
                        int num = GetPropInt(blk, "Number");
                        string bType = typeName.Contains("FB") ? "FB" : "FC";

                        results.Add(new SclBlockItem
                        {
                            Name = name,
                            BlockType = bType,
                            BlockNumber = num > 0 ? (int?)num : null,
                            ProgrammingLanguage = "SCL",
                            GroupPath = currentPath,
                            RawBlockObject = blk,
                            RawPlcSoftware = plcSw,
                            RawParentGroup = currentGroup,
                            Status = "Hazır"
                        });
                    }
                }
            }

            // Subgroups
            object subGroupsComp = GetPropValue(currentGroup, "Groups");
            if (subGroupsComp is IEnumerable subGroupsEnum)
            {
                foreach (var sub in subGroupsEnum)
                {
                    ScanBlocksRecursive(sub, currentPath, plcSw, results);
                }
            }
        }

        public ParsedSclBlock LoadSclCodeForBlock(SclBlockItem blockItem)
        {
            if (blockItem?.RawBlockObject == null)
                throw new ArgumentNullException(nameof(blockItem));

            return RunOnOpennessThreadAsync(() =>
            {
                string tempXml = Path.Combine(Path.GetTempPath(), $"Scl2Lad_{Guid.NewGuid():N}.xml");
                try
                {
                    ExportBlockToXml(blockItem.RawBlockObject, tempXml);
                    var parsed = SclCodeParser.ParseBlockXml(tempXml);

                    // Populate external variable types from PLC Tag Tables and referenced Data Blocks
                    try
                    {
                        PopulateExternalVarTypes(blockItem.RawPlcSoftware, parsed);
                    }
                    catch { }

                    blockItem.OriginalSclSourceCode = parsed.OriginalSclCode;
                    blockItem.SclSourceCode = parsed.FullSclCode;
                    blockItem.Statements = parsed.Statements;
                    blockItem.LineCount = string.IsNullOrEmpty(parsed.OriginalSclCode) ? 0 : parsed.OriginalSclCode.Split('\n').Length;
                    blockItem.Status = "Kod Yüklendi";

                    _log($"'{blockItem.Name}' kodları okundu ({blockItem.Statements.Count} network/ifade).");
                    return parsed;
                }
                finally
                {
                    if (File.Exists(tempXml))
                    {
                        try { File.Delete(tempXml); } catch { }
                    }
                }
            }).GetAwaiter().GetResult();
        }

        private void PopulateExternalVarTypes(object plcSoftware, ParsedSclBlock parsed)
        {
            if (plcSoftware == null || parsed == null) return;

            // 1. Scan PLC Tag Tables once per PlcSoftware instance
            if (!ReferenceEquals(_cachedPlcSoftwareForTags, plcSoftware))
            {
                _cachedPlcSoftwareForTags = plcSoftware;
                _cachedPlcTagTypes.Clear();
                _cachedDefinedGlobalSymbols.Clear();
                _cachedLadderDbTypes.Clear();

                object tagTableGroup = GetPropValue(plcSoftware, "TagTableGroup");
                if (tagTableGroup != null)
                {
                    ScanTagTableGroupRecursive(tagTableGroup, _cachedPlcTagTypes);
                }

                // Ensure DB & code blocks are indexed
                object blockGroup = GetPropValue(plcSoftware, "BlockGroup");
                if (blockGroup != null)
                {
                    IndexDbBlocksRecursive(blockGroup);
                }

                // If LADDERDB already exists in the PLC, read its existing members once
                if (_cachedDbBlocks.TryGetValue("LADDERDB", out object ladderDbObj) && ladderDbObj != null)
                {
                    ExportAndReadDbInterface("LADDERDB", ladderDbObj, _cachedLadderDbTypes);
                }
            }

            // 2. For any referenced Global DB ("DBName"."MemberName") in the SCL code, export its Interface once
            if (!string.IsNullOrEmpty(parsed.FullSclCode))
            {
                var dbMatches = Regex.Matches(parsed.FullSclCode, @"""([^""]+)""\s*\.");
                foreach (Match m in dbMatches)
                {
                    string dbName = m.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(dbName) || _exportedDbNames.Contains(dbName))
                        continue;

                    _exportedDbNames.Add(dbName);
                    if (_cachedDbBlocks.TryGetValue(dbName, out object dbObj) && dbObj != null)
                    {
                        ExportAndReadDbInterface(dbName, dbObj, null);
                    }
                }
            }

            foreach (var kvp in _cachedPlcTagTypes)
            {
                parsed.ExternalVarTypes[kvp.Key] = kvp.Value;
            }

            foreach (var sym in _cachedDefinedGlobalSymbols)
            {
                parsed.DefinedGlobalSymbols.Add(sym);
            }

            foreach (var kvp in _cachedLadderDbTypes)
            {
                parsed.ExistingLadderDbTypes[kvp.Key] = kvp.Value;
            }

            parsed.HasScannedPlcSymbols = true;
        }

        private void ExportAndReadDbInterface(string dbName, object dbObj, Dictionary<string, string> directMemberDict)
        {
            if (dbObj == null) return;
            string tempDbXml = Path.Combine(Path.GetTempPath(), $"Scl2Lad_DB_{Guid.NewGuid():N}.xml");
            try
            {
                ExportBlockToXml(dbObj, tempDbXml);
                if (File.Exists(tempDbXml))
                {
                    var dbDoc = XDocument.Load(tempDbXml);
                    var ifaceElem = dbDoc.Descendants().FirstOrDefault(d => d.Name.LocalName == "Interface");
                    if (ifaceElem != null)
                    {
                        ExtractXmlInterfaceMembersRecursive(ifaceElem, dbName, _cachedPlcTagTypes);
                        if (directMemberDict != null)
                        {
                            foreach (var m in ifaceElem.Descendants().Where(d => d.Name.LocalName == "Member"))
                            {
                                string mName = m.Attribute("Name")?.Value;
                                string mType = m.Attribute("Datatype")?.Value;
                                if (!string.IsNullOrWhiteSpace(mName) && !string.IsNullOrWhiteSpace(mType))
                                {
                                    directMemberDict[mName] = mType;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (File.Exists(tempDbXml))
                {
                    try { File.Delete(tempDbXml); } catch { }
                }
            }
        }

        private void ScanTagTableGroupRecursive(object groupObj, Dictionary<string, string> tagDict)
        {
            if (groupObj == null) return;

            if (GetPropValue(groupObj, "TagTables") is IEnumerable tables)
            {
                foreach (var tbl in tables)
                {
                    if (GetPropValue(tbl, "Tags") is IEnumerable tags)
                    {
                        foreach (var tag in tags)
                        {
                            string name = GetPropString(tag, "Name");
                            string dt = GetPropString(tag, "DataTypeName");
                            string addr = GetPropString(tag, "LogicalAddress");

                            if (!string.IsNullOrEmpty(name))
                            {
                                _cachedDefinedGlobalSymbols.Add(name);
                            }

                            if (string.IsNullOrEmpty(dt) && !string.IsNullOrEmpty(addr) &&
                                Regex.IsMatch(addr.Trim(), @"^%[IQM]\d+\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                            {
                                dt = "Bool";
                            }

                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dt))
                            {
                                tagDict[name] = dt;
                            }
                        }
                    }

                    if (GetPropValue(tbl, "UserConstants") is IEnumerable consts)
                    {
                        foreach (var c in consts)
                        {
                            string name = GetPropString(c, "Name");
                            string dt = GetPropString(c, "DataTypeName");
                            if (!string.IsNullOrEmpty(name))
                            {
                                _cachedDefinedGlobalSymbols.Add(name);
                                if (!string.IsNullOrEmpty(dt))
                                    tagDict[name] = dt;
                            }
                        }
                    }

                    if (GetPropValue(tbl, "SystemConstants") is IEnumerable sysConsts)
                    {
                        foreach (var sc in sysConsts)
                        {
                            string name = GetPropString(sc, "Name");
                            if (!string.IsNullOrEmpty(name))
                            {
                                _cachedDefinedGlobalSymbols.Add(name);
                            }
                        }
                    }
                }
            }

            if (GetPropValue(groupObj, "Groups") is IEnumerable subGroups)
            {
                foreach (var sg in subGroups)
                {
                    ScanTagTableGroupRecursive(sg, tagDict);
                }
            }
        }

        private void IndexDbBlocksRecursive(object groupObj)
        {
            if (groupObj == null) return;

            if (GetPropValue(groupObj, "Blocks") is IEnumerable blocks)
            {
                foreach (var blk in blocks)
                {
                    string typeName = blk.GetType().Name;
                    string lang = GetPropValue(blk, "ProgrammingLanguage")?.ToString() ?? "";
                    string name = GetPropString(blk, "Name");

                    if (!string.IsNullOrEmpty(name))
                    {
                        _cachedDefinedGlobalSymbols.Add(name);
                    }

                    if (!string.IsNullOrEmpty(name) &&
                        (typeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         string.Equals(lang, "DB", StringComparison.OrdinalIgnoreCase)))
                    {
                        _cachedDbBlocks[name] = blk;
                    }
                }
            }

            if (GetPropValue(groupObj, "Groups") is IEnumerable subGroups)
            {
                foreach (var sg in subGroups)
                {
                    IndexDbBlocksRecursive(sg);
                }
            }
        }

        private static void ExtractXmlInterfaceMembersRecursive(XElement container, string prefix, Dictionary<string, string> targetDict)
        {
            if (container == null) return;

            foreach (var elem in container.Elements())
            {
                if (elem.Name.LocalName == "Member")
                {
                    string mName = elem.Attribute("Name")?.Value;
                    string mType = elem.Attribute("Datatype")?.Value;
                    if (!string.IsNullOrEmpty(mName))
                    {
                        string fullPath = string.IsNullOrEmpty(prefix) ? mName : $"{prefix}.{mName}";
                        if (!string.IsNullOrEmpty(mType))
                        {
                            targetDict[fullPath] = mType;
                            if (!targetDict.ContainsKey(mName))
                                targetDict[mName] = mType;
                        }
                        ExtractXmlInterfaceMembersRecursive(elem, fullPath, targetDict);
                    }
                }
                else
                {
                    ExtractXmlInterfaceMembersRecursive(elem, prefix, targetDict);
                }
            }
        }

        public void ExportBlockToXml(object block, string exportXmlPath)
        {
            Type exportOptionsType = FindEngineeringType("Siemens.Engineering.ExportOptions");
            object optVal = Enum.Parse(exportOptionsType, "WithDefaults");

            MethodInfo exportMethod = block.GetType().GetMethod("Export", new[] { typeof(FileInfo), exportOptionsType });
            if (exportMethod == null)
                throw new InvalidOperationException("PlcBlock.Export metodu bulunamadı.");

            exportMethod.Invoke(block, new object[] { new FileInfo(exportXmlPath), optVal });
        }

        /// <summary>
        /// Ensures that a Global Data Block named "LADDERDB" exists in the PLC project and contains all
        /// specified undefined/helper tags with their inferred data types.
        /// </summary>
        public void EnsureLadderDbUpdated(object plcSoftware, Dictionary<string, string> tagsToDefine, string tiaVersion = "V20", string dbName = "LADDERDB")
        {
            if (plcSoftware == null || tagsToDefine == null || tagsToDefine.Count == 0)
                return;

            var activeDbTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool isDefaultLadderDb = string.Equals(dbName, "LADDERDB", StringComparison.OrdinalIgnoreCase);

            foreach (var kv in tagsToDefine)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    activeDbTags[kv.Key] = kv.Value;
                    if (isDefaultLadderDb && !_cachedLadderDbTypes.ContainsKey(kv.Key))
                    {
                        _cachedLadderDbTypes[kv.Key] = kv.Value;
                    }
                    _cachedPlcTagTypes[$"{dbName}.{kv.Key}"] = kv.Value;
                }
            }
            if (isDefaultLadderDb)
            {
                foreach (var kv in _cachedLadderDbTypes)
                {
                    if (!activeDbTags.ContainsKey(kv.Key))
                        activeDbTags[kv.Key] = kv.Value;
                }
            }
            _cachedDefinedGlobalSymbols.Add(dbName);

            RunOnOpennessThread(() =>
            {
                object blockGroup = GetPropValue(plcSoftware, "BlockGroup");
                if (blockGroup == null)
                    throw new InvalidOperationException("PLC BlockGroup bulunamadı.");

                object blocksComp = GetPropValue(blockGroup, "Blocks");
                if (blocksComp == null)
                    throw new InvalidOperationException("PLC BlockGroup.Blocks koleksiyonu bulunamadı.");

                // Refresh DB index to check if LADDERDB already exists
                IndexDbBlocksRecursive(blockGroup);

                Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                MethodInfo importMethod = blocksComp.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
                object optOverride = importOptionsType != null ? Enum.Parse(importOptionsType, "Override") : null;

                void CompileLadderDbInternal()
                {
                    IndexDbBlocksRecursive(blockGroup);
                    try
                    {
                        object ladderDbBlock = null;
                        if (!_cachedDbBlocks.TryGetValue(dbName, out ladderDbBlock) || ladderDbBlock == null)
                        {
                            MethodInfo findMethod = blocksComp.GetType().GetMethod("Find", new[] { typeof(string) });
                            ladderDbBlock = findMethod?.Invoke(blocksComp, new object[] { dbName });
                        }

                        if (ladderDbBlock != null)
                        {
                            _log($"'{dbName}' veri bloğu derleniyor (TIA Portal sembol ve tag eşlemesi)...");
                            CompileBlock(ladderDbBlock, out _);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"'{dbName}' derlenirken uyarı: {ex.Message}");
                    }
                }

                // Tier 1: Update existing LADDERDB by exporting its XML, adding missing members, and re-importing
                if (_cachedDbBlocks.TryGetValue(dbName, out object existingLadderDb) && existingLadderDb != null)
                {
                    string tempExport = Path.Combine(Path.GetTempPath(), $"LadderDb_Export_{Guid.NewGuid():N}.xml");
                    try
                    {
                        ExportBlockToXml(existingLadderDb, tempExport);
                        if (File.Exists(tempExport))
                        {
                            var xdoc = XDocument.Load(tempExport);
                            var staticSection = xdoc.Descendants()
                                .FirstOrDefault(e => e.Name.LocalName == "Section" &&
                                                     string.Equals(e.Attribute("Name")?.Value, "Static", StringComparison.OrdinalIgnoreCase));

                            if (staticSection != null)
                            {
                                XNamespace ns = staticSection.Name.Namespace;
                                int addedCount = 0;

                                foreach (var kv in activeDbTags)
                                {
                                    bool exists = staticSection.Elements()
                                        .Any(m => m.Name.LocalName == "Member" &&
                                                  string.Equals(m.Attribute("Name")?.Value, kv.Key, StringComparison.OrdinalIgnoreCase));

                                    if (!exists)
                                    {
                                        string normType = NormalizeDbDatatypeForXml(kv.Value);
                                        var memElem = new XElement(ns + "Member",
                                            new XAttribute("Name", kv.Key),
                                            new XAttribute("Datatype", normType));
                                        if (string.Equals(normType, "IEC_TIMER", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(normType, "TON_TIME", StringComparison.OrdinalIgnoreCase))
                                        {
                                            memElem.Add(new XAttribute("Version", "1.0"));
                                        }
                                        staticSection.Add(memElem);
                                        addedCount++;
                                    }
                                }

                                if (addedCount > 0 && importMethod != null)
                                {
                                    xdoc.Save(tempExport);
                                    InvokeBlocksImport(importMethod, blocksComp, tempExport, optOverride);
                                    _log($"'{dbName}' veri bloğu güncellendi ({addedCount} yeni tag eklendi).");
                                }
                                else
                                {
                                    _log($"'{dbName}' veri bloğunda tüm taglar zaten tanımlı.");
                                }

                                CompileLadderDbInternal();
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Mevcut '{dbName}' XML üzerinden güncellenirken uyarı: {ex.Message}. Alternatif yöntem deneniyor...");
                    }
                    finally
                    {
                        if (File.Exists(tempExport))
                        {
                            try { File.Delete(tempExport); } catch { }
                        }
                    }
                }

                // Tier 2A: Clone an existing GlobalDB in the project as a version-perfect XML template
                object templateDb = _cachedDbBlocks
                    .FirstOrDefault(kv => !string.Equals(kv.Key, dbName, StringComparison.OrdinalIgnoreCase) &&
                                          kv.Value != null &&
                                          (kv.Value.GetType().Name.IndexOf("GlobalDB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           kv.Value.GetType().Name.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0)).Value;

                if (templateDb != null && importMethod != null)
                {
                    string tempTemplate = Path.Combine(Path.GetTempPath(), $"LadderDb_Template_{Guid.NewGuid():N}.xml");
                    try
                    {
                        ExportBlockToXml(templateDb, tempTemplate);
                        if (File.Exists(tempTemplate))
                        {
                            var xdoc = XDocument.Load(tempTemplate);
                            var dbElement = xdoc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
                            var attrList = dbElement?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                            var staticSection = attrList?.Descendants()
                                .FirstOrDefault(e => e.Name.LocalName == "Section" &&
                                                     string.Equals(e.Attribute("Name")?.Value, "Static", StringComparison.OrdinalIgnoreCase));

                            if (dbElement != null && attrList != null && staticSection != null)
                            {
                                var nameElem = attrList.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                                if (nameElem != null) nameElem.Value = dbName;
                                else attrList.Add(new XElement("Name", dbName));

                                var autoNumElem = attrList.Elements().FirstOrDefault(e => e.Name.LocalName == "AutoNumber");
                                if (autoNumElem != null) autoNumElem.Value = "true";
                                else attrList.AddFirst(new XElement("AutoNumber", "true"));

                                attrList.Elements().Where(e => e.Name.LocalName == "Number").Remove();

                                XNamespace ns = staticSection.Name.Namespace;
                                staticSection.RemoveNodes();
                                foreach (var kv in activeDbTags)
                                {
                                    string normType = NormalizeDbDatatypeForXml(kv.Value);
                                    var memElem = new XElement(ns + "Member",
                                        new XAttribute("Name", kv.Key),
                                        new XAttribute("Datatype", normType));
                                    if (string.Equals(normType, "IEC_TIMER", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(normType, "TON_TIME", StringComparison.OrdinalIgnoreCase))
                                    {
                                        memElem.Add(new XAttribute("Version", "1.0"));
                                    }
                                    staticSection.Add(memElem);
                                }

                                var objList = dbElement.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
                                objList?.RemoveNodes();

                                xdoc.Save(tempTemplate);
                                InvokeBlocksImport(importMethod, blocksComp, tempTemplate, optOverride);
                                _log($"'{dbName}' Global DB oluşturuldu ve {activeDbTags.Count} tag tanımlandı.");
                                CompileLadderDbInternal();
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Şablon DB üzerinden '{dbName}' oluşturulamadı ({ex.Message}), doğrudan XML/kaynak yöntemi deneniyor...");
                    }
                    finally
                    {
                        if (File.Exists(tempTemplate))
                        {
                            try { File.Delete(tempTemplate); } catch { }
                        }
                    }
                }

                // Tier 2B: Direct SW.Blocks.GlobalDB SimaticML XML import
                if (importMethod != null)
                {
                    string tempDirectXml = Path.Combine(Path.GetTempPath(), $"LadderDb_Direct_{Guid.NewGuid():N}.xml");
                    try
                    {
                        string directXml = BuildGlobalDbSimaticMlXml(dbName, activeDbTags, tiaVersion);
                        File.WriteAllText(tempDirectXml, directXml, Encoding.UTF8);
                        InvokeBlocksImport(importMethod, blocksComp, tempDirectXml, optOverride);
                        _log($"'{dbName}' Global DB doğrudan oluşturuldu ve {activeDbTags.Count} tag tanımlandı.");
                        CompileLadderDbInternal();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log($"Doğrudan XML ile '{dbName}' oluşturulamadı ({ex.Message}), ExternalSource yöntemi deneniyor...");
                    }
                    finally
                    {
                        if (File.Exists(tempDirectXml))
                        {
                            try { File.Delete(tempDirectXml); } catch { }
                        }
                    }
                }

                // Tier 3: Fallback via PlcExternalSourceGroup (.db text source)
                CreateGlobalDbViaExternalSource(plcSoftware, dbName, activeDbTags);
                CompileLadderDbInternal();
            });
        }

        private static void InvokeBlocksImport(MethodInfo importMethod, object blocksComp, string xmlFilePath, object optOverride)
        {
            try
            {
                importMethod.Invoke(blocksComp, new object[] { new FileInfo(xmlFilePath), optOverride });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }

        private static string NormalizeDbDatatypeForXml(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return "Real";
            string t = dataType.Trim();
            if (t.Equals("BOOL", StringComparison.OrdinalIgnoreCase)) return "Bool";
            if (t.Equals("INT", StringComparison.OrdinalIgnoreCase)) return "Int";
            if (t.Equals("DINT", StringComparison.OrdinalIgnoreCase)) return "DInt";
            if (t.Equals("REAL", StringComparison.OrdinalIgnoreCase)) return "Real";
            if (t.Equals("LREAL", StringComparison.OrdinalIgnoreCase)) return "LReal";
            if (t.Equals("WORD", StringComparison.OrdinalIgnoreCase)) return "Word";
            if (t.Equals("DWORD", StringComparison.OrdinalIgnoreCase)) return "DWord";
            if (t.Equals("BYTE", StringComparison.OrdinalIgnoreCase)) return "Byte";
            if (t.Equals("TIME", StringComparison.OrdinalIgnoreCase)) return "Time";
            if (t.Equals("STRING", StringComparison.OrdinalIgnoreCase)) return "String";
            return t;
        }

        private static string BuildGlobalDbSimaticMlXml(string dbName, Dictionary<string, string> tags, string tiaVersion)
        {
            string ver = string.IsNullOrWhiteSpace(tiaVersion) ? "V20" : tiaVersion.Trim();
            if (!ver.StartsWith("V", StringComparison.OrdinalIgnoreCase)) ver = "V" + ver;

            string ifaceVer = "v5";
            bool includeNamespace = true;
            if (ver.StartsWith("V14", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v2"; includeNamespace = false; }
            else if (ver.StartsWith("V15", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v3"; includeNamespace = false; }
            else if (ver.StartsWith("V16", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v4"; includeNamespace = true; }
            else if (ver.StartsWith("V17", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v5"; includeNamespace = true; }

            var sb = new StringBuilder();
            sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
            sb.AppendLine("<Document>");
            sb.AppendLine($@"  <Engineering version=""{ver}"" />");
            sb.AppendLine(@"  <SW.Blocks.GlobalDB ID=""0"">");
            sb.AppendLine("    <AttributeList>");
            sb.AppendLine("      <AutoNumber>true</AutoNumber>");
            sb.AppendLine("      <HeaderAuthor>TiaSCL2LAD</HeaderAuthor>");
            sb.AppendLine("      <HeaderVersion>0.1</HeaderVersion>");
            sb.AppendLine($@"      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/{ifaceVer}"">");
            sb.AppendLine(@"  <Section Name=""Static"">");
            foreach (var kv in tags)
            {
                string safeName = System.Security.SecurityElement.Escape(kv.Key);
                string normType = NormalizeDbDatatypeForXml(kv.Value);
                string safeType = System.Security.SecurityElement.Escape(normType);
                string verAttr = (string.Equals(normType, "IEC_TIMER", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(normType, "TON_TIME", StringComparison.OrdinalIgnoreCase))
                    ? @" Version=""1.0"""
                    : "";
                sb.AppendLine($@"    <Member Name=""{safeName}"" Datatype=""{safeType}""{verAttr} />");
            }
            sb.AppendLine("  </Section>");
            sb.AppendLine("</Sections></Interface>");
            sb.AppendLine("      <MemoryLayout>Optimized</MemoryLayout>");
            sb.AppendLine($"      <Name>{System.Security.SecurityElement.Escape(dbName)}</Name>");
            if (includeNamespace)
            {
                sb.AppendLine("      <Namespace />");
            }
            sb.AppendLine("      <ProgrammingLanguage>DB</ProgrammingLanguage>");
            sb.AppendLine("    </AttributeList>");
            sb.AppendLine("    <ObjectList />");
            sb.AppendLine("  </SW.Blocks.GlobalDB>");
            sb.AppendLine("</Document>");
            return sb.ToString();
        }

        private void CreateGlobalDbViaExternalSource(object plcSoftware, string dbName, Dictionary<string, string> tags)
        {
            object extGroup = GetPropValue(plcSoftware, "ExternalSourceGroup");
            object extSources = GetPropValue(extGroup, "ExternalSources");
            if (extSources == null)
                throw new InvalidOperationException("PLC ExternalSourceGroup.ExternalSources bulunamadı.");

            string tempDbFile = Path.Combine(Path.GetTempPath(), $"{dbName}_{Guid.NewGuid():N}.db");
            object createdExtSource = null;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"DATA_BLOCK \"{dbName}\"");
                sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                sb.AppendLine("VERSION : 0.1");
                sb.AppendLine("NON_RETAIN");
                sb.AppendLine("   VAR ");
                foreach (var kv in tags)
                {
                    sb.AppendLine($"      \"{kv.Key}\" : {NormalizeDbDatatypeForXml(kv.Value)};");
                }
                sb.AppendLine("   END_VAR");
                sb.AppendLine();
                sb.AppendLine("BEGIN");
                sb.AppendLine("END_DATA_BLOCK");

                File.WriteAllText(tempDbFile, sb.ToString(), Encoding.UTF8);

                string sourceName = $"TmpSrc_{Guid.NewGuid():N}";
                MethodInfo createFromFile = extSources.GetType().GetMethod("CreateFromFile", new[] { typeof(string), typeof(string) });
                if (createFromFile == null)
                    throw new InvalidOperationException("ExternalSources.CreateFromFile metodu bulunamadı.");

                createdExtSource = createFromFile.Invoke(extSources, new object[] { sourceName, tempDbFile });
                if (createdExtSource != null)
                {
                    MethodInfo genMethod = createdExtSource.GetType().GetMethods()
                        .FirstOrDefault(m => m.Name == "GenerateBlocksFromSource" && m.GetParameters().Length == 0);
                    if (genMethod != null)
                    {
                        genMethod.Invoke(createdExtSource, null);
                    }
                    else
                    {
                        Type genOptType = FindEngineeringType("Siemens.Engineering.SW.ExternalSources.GenerateBlockOption");
                        MethodInfo genMethodOpt = genOptType != null
                            ? createdExtSource.GetType().GetMethod("GenerateBlocksFromSource", new[] { genOptType })
                            : null;
                        if (genMethodOpt != null)
                        {
                            object opt = Enum.Parse(genOptType, "KeepOnError");
                            genMethodOpt.Invoke(createdExtSource, new object[] { opt });
                        }
                    }

                    _log($"'{dbName}' Global DB ExternalSource üzerinden oluşturuldu ({tags.Count} tag tanımlandı).");
                }
            }
            finally
            {
                if (createdExtSource != null)
                {
                    try { createdExtSource.GetType().GetMethod("Delete")?.Invoke(createdExtSource, null); } catch { }
                }
                if (File.Exists(tempDbFile))
                {
                    try { File.Delete(tempDbFile); } catch { }
                }
            }
        }

        public void ImportConvertedLadBlock(SclBlockItem blockItem, string simaticMlXml)
        {
            if (blockItem?.RawPlcSoftware == null)
                throw new ArgumentNullException(nameof(blockItem));

            RunOnOpennessThread(() =>
            {
                string tempXml = Path.Combine(Path.GetTempPath(), $"ConvertedLad_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempXml, simaticMlXml, Encoding.UTF8);

                    object targetGroup = blockItem.RawParentGroup;
                    if (targetGroup == null)
                    {
                        targetGroup = GetPropValue(blockItem.RawPlcSoftware, "BlockGroup");
                    }

                    object blocksComp = GetPropValue(targetGroup, "Blocks");
                    if (blocksComp == null)
                        throw new InvalidOperationException("Target BlockGroup.Blocks koleksiyonu bulunamadı.");

                    Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                    MethodInfo importMethod = blocksComp.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });

                    try
                    {
                        if (importMethod != null)
                        {
                            object optOverride = Enum.Parse(importOptionsType, "Override");
                            importMethod.Invoke(blocksComp, new object[] { new FileInfo(tempXml), optOverride });
                        }
                        else
                        {
                            MethodInfo createMethod = blocksComp.GetType().GetMethod("CreateFrom", new[] { typeof(FileInfo) });
                            createMethod.Invoke(blocksComp, new object[] { new FileInfo(tempXml) });
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        try
                        {
                            string debugXml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"FailedImport_{blockItem.Name}.xml");
                            File.WriteAllText(debugXml, simaticMlXml, Encoding.UTF8);
                            _log($"[DEBUG] Hatalı SimaticML XML dosyası kaydedildi: {debugXml}");
                        }
                        catch { }
                        throw tie.InnerException ?? tie;
                    }
                    catch (Exception)
                    {
                        try
                        {
                            string debugXml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"FailedImport_{blockItem.Name}.xml");
                            File.WriteAllText(debugXml, simaticMlXml, Encoding.UTF8);
                            _log($"[DEBUG] Hatalı SimaticML XML dosyası kaydedildi: {debugXml}");
                        }
                        catch { }
                        throw;
                    }

                    _log($"LAD bloğu projeye başarıyla eklendi / içe aktarıldı.");
                    blockItem.Status = "Dönüştürüldü ve Eklendi";

                    // Compile the imported block and LADDERDB to eliminate any "???" placeholders in TIA Portal LAD editor
                    try
                    {
                        string importedName = ExtractTargetNameFromXml(simaticMlXml) ?? blockItem.Name;
                        MethodInfo findMethod = blocksComp.GetType().GetMethod("Find", new[] { typeof(string) });
                        object importedBlock = findMethod?.Invoke(blocksComp, new object[] { importedName });
                        if (importedBlock != null)
                        {
                            _log($"'{importedName}' bloğu derleniyor...");
                            CompileBlock(importedBlock, out string cMsg);
                        }

                        // Also compile LADDERDB if present to synchronize symbols
                        if (_cachedDbBlocks.TryGetValue("LADDERDB", out object ladderDbBlock) && ladderDbBlock != null)
                        {
                            CompileBlock(ladderDbBlock, out _);
                        }
                    }
                    catch (Exception cEx)
                    {
                        _log($"İçe aktarılan blok derlenirken uyarı: {cEx.Message}");
                    }
                }
                finally
                {
                    if (File.Exists(tempXml))
                    {
                        try { File.Delete(tempXml); } catch { }
                    }
                }
            });
        }

        private static string ExtractTargetNameFromXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var m = Regex.Match(xml, @"<Name>([^<]+)</Name>");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Compiles a PLC Block (such as LADDERDB or an FB/FC) or the entire PlcSoftware using Siemens.Engineering.Compiler.ICompilable.
        /// Resolves tags and addresses so that TIA Portal ladder editor displays no "???" placeholders.
        /// </summary>
        public bool CompileBlock(object blockOrPlcSoftware, out string compileMessage)
        {
            compileMessage = null;
            if (blockOrPlcSoftware == null) return false;
            try
            {
                Type iCompilableType = FindEngineeringType("Siemens.Engineering.Compiler.ICompilable");
                if (iCompilableType == null)
                {
                    compileMessage = "ICompilable tipi bulunamadı.";
                    return false;
                }

                object compService = InvokeGetService(blockOrPlcSoftware, iCompilableType);
                if (compService == null)
                {
                    compileMessage = "ICompilable servisi alınamadı.";
                    return false;
                }

                MethodInfo compileMethod = compService.GetType().GetMethod("Compile", Type.EmptyTypes);
                if (compileMethod == null)
                {
                    compileMessage = "Compile metodu bulunamadı.";
                    return false;
                }

                object result = compileMethod.Invoke(compService, null);
                if (result != null)
                {
                    string state = GetPropValue(result, "State")?.ToString() ?? "Bilinmiyor";
                    int errors = (int)(GetPropValue(result, "ErrorCount") ?? 0);
                    int warnings = (int)(GetPropValue(result, "WarningCount") ?? 0);
                    compileMessage = $"Durum: {state} (Hata: {errors}, Uyarı: {warnings})";
                    _log($"[Derleme] {compileMessage}");

                    if (errors > 0 || warnings > 0)
                    {
                        var msgs = GetPropValue(result, "Messages") as System.Collections.IEnumerable;
                        if (msgs != null)
                        {
                            foreach (var m in msgs)
                            {
                                string desc = GetPropValue(m, "Description")?.ToString();
                                string path = GetPropValue(m, "Path")?.ToString();
                                if (!string.IsNullOrWhiteSpace(desc))
                                {
                                    _log($"  [Derleme Detayı] {path}: {desc}");
                                }
                            }
                        }
                    }

                    return errors == 0;
                }
            }
            catch (Exception ex)
            {
                compileMessage = ex.GetBaseException()?.Message ?? ex.Message;
                _log($"[Derleme Uyarısı] {compileMessage}");
            }
            return false;
        }

        public void CreateSampleSclBlock(TiaPlcItem plc, string blockName = "FC_MotorVeHatKontrol")
        {
            if (plc?.RawPlcSoftware == null)
                throw new ArgumentNullException(nameof(plc));

            RunOnOpennessThread(() =>
            {
                _log($"Test için örnek SCL fonksiyon bloğu ('{blockName}') oluşturuluyor...");

                string sampleXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V20"" />
  <DocumentInfo>
    <Created>{DateTime.UtcNow:o}</Created>
    <ExportSetting>WithDefaults</ExportSetting>
  </DocumentInfo>
  <SW.Blocks.FC ID=""0"">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor>TiaSCL2LAD</HeaderAuthor>
      <HeaderVersion>0.1</HeaderVersion>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
  <Section Name=""Input"">
    <Member Name=""I_Start_Buton"" Datatype=""Bool"" Accessibility=""Public"" />
    <Member Name=""I_Stop_Buton"" Datatype=""Bool"" Accessibility=""Public"" />
    <Member Name=""I_AcilStop_Ok"" Datatype=""Bool"" Accessibility=""Public"" />
    <Member Name=""I_Termik_Atik"" Datatype=""Bool"" Accessibility=""Public"" />
    <Member Name=""I_Hiz_Referans"" Datatype=""Int"" Accessibility=""Public"" />
  </Section>
  <Section Name=""Output"">
    <Member Name=""Q_Motor_Calis"" Datatype=""Bool"" Accessibility=""Public"" />
    <Member Name=""Q_Ariza_Lamba"" Datatype=""Bool"" Accessibility=""Public"" />
    <Member Name=""Q_Hiz_Cikis"" Datatype=""Int"" Accessibility=""Public"" />
  </Section>
  <Section Name=""Temp"">
    <Member Name=""t_Sayac"" Datatype=""Int"" />
    <Member Name=""t_Toplam"" Datatype=""DInt"" />
  </Section>
</Sections></Interface>
      <IsIECCheckEnabled>false</IsIECCheckEnabled>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>{blockName}</Name>
      <ProgrammingLanguage>SCL</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
              <LineComment><Text>Network 1: Motor Başlatma ve Durdurma (LAD'a cevrilebilir Boolean)</Text></LineComment>
              <NewLine />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""Q_Motor_Calis"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text="":="" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""I_AcilStop_Ok"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text=""AND"" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""I_Start_Buton"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text=""AND"" />
              <Blank Num=""1"" />
              <Token Text=""NOT"" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""I_Stop_Buton"" /></Symbol></Access>
              <Token Text="";"" />
              <NewLine />
              <NewLine />
              <LineComment><Text>Network 2: Hata Lambası Setleme (LAD'a cevrilebilir IF-THEN)</Text></LineComment>
              <NewLine />
              <Token Text=""IF"" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""I_Termik_Atik"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text=""THEN"" />
              <NewLine />
              <Blank Num=""4"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""Q_Ariza_Lamba"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text="":="" />
              <Blank Num=""1"" />
              <Token Text=""TRUE;"" />
              <NewLine />
              <Token Text=""END_IF;"" />
              <NewLine />
              <NewLine />
              <LineComment><Text>Network 3: Hız Değer Aktarımı (LAD'a cevrilebilir Move)</Text></LineComment>
              <NewLine />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""Q_Hiz_Cikis"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text="":="" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""I_Hiz_Referans"" /></Symbol></Access>
              <Token Text="";"" />
              <NewLine />
              <NewLine />
              <LineComment><Text>Network 4: Döngü ve Dizi İşlemleri (LAD'a cevrilemez -> SCL Network kalmalı)</Text></LineComment>
              <NewLine />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""t_Toplam"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text="":="" />
              <Blank Num=""1"" />
              <Token Text=""0;"" />
              <NewLine />
              <Token Text=""FOR"" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""t_Sayac"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text="":="" />
              <Blank Num=""1"" />
              <Token Text=""1"" />
              <Blank Num=""1"" />
              <Token Text=""TO"" />
              <Blank Num=""1"" />
              <Token Text=""10"" />
              <Blank Num=""1"" />
              <Token Text=""DO"" />
              <NewLine />
              <Blank Num=""4"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""t_Toplam"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text="":="" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""t_Toplam"" /></Symbol></Access>
              <Blank Num=""1"" />
              <Token Text=""+"" />
              <Blank Num=""1"" />
              <Access Scope=""LocalVariable""><Symbol><Component Name=""t_Sayac"" /></Symbol></Access>
              <Token Text="";"" />
              <NewLine />
              <Token Text=""END_FOR;"" />
            </StructuredText>
          </NetworkSource>
          <ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FC>
</Document>";

                string tempXml = Path.Combine(Path.GetTempPath(), $"SampleScl_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempXml, sampleXml, Encoding.UTF8);
                    object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                    object blocksComp = GetPropValue(blockGroup, "Blocks");

                    Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                    MethodInfo importMethod = blocksComp.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
                    if (importMethod != null)
                    {
                        object optOverride = Enum.Parse(importOptionsType, "Override");
                        importMethod.Invoke(blocksComp, new object[] { new FileInfo(tempXml), optOverride });
                    }
                    _log($"Örnek SCL bloğu ('{blockName}') başarıyla PLC'ye aktarıldı.");
                }
                finally
                {
                    if (File.Exists(tempXml))
                    {
                        try { File.Delete(tempXml); } catch { }
                    }
                }
            });
        }

        public Task<string> ImportSclBlockFromFileAsync(TiaPlcItem plc, string filePath)
        {
            if (plc?.RawPlcSoftware == null)
                throw new ArgumentNullException(nameof(plc), "PLC istasyonu seçilmedi.");

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("İçe aktarılacak dosya bulunamadı.", filePath);

            return RunOnOpennessThreadAsync(() =>
            {
                string fileName = Path.GetFileName(filePath);
                _log($"Dosya okunuyor: '{fileName}'...");

                string content;
                using (var reader = new StreamReader(filePath, Encoding.UTF8, true))
                {
                    content = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException("Seçilen dosyanın içi boş.");

                string trimmed = content.TrimStart();
                bool isXml = trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("<Document", StringComparison.OrdinalIgnoreCase);

                if (isXml)
                {
                    // SimaticML XML Import
                    var nameMatch = Regex.Match(content, @"<Name>([^<]+)</Name>");
                    string blockName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(filePath);

                    _log($"SimaticML XML dosyası PLC bloklarına aktarılıyor: '{blockName}'...");
                    object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                    object blocksComp = GetPropValue(blockGroup, "Blocks");
                    if (blocksComp == null)
                        throw new InvalidOperationException("PLC BlockGroup.Blocks koleksiyonuna erişilemedi.");

                    Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                    MethodInfo importMethod = blocksComp.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
                    if (importMethod == null)
                        throw new InvalidOperationException("PlcBlockComposition.Import metodu bulunamadı.");

                    object optOverride = Enum.Parse(importOptionsType, "Override");
                    importMethod.Invoke(blocksComp, new object[] { new FileInfo(filePath), optOverride });
                    _log($"SimaticML XML bloğu başarıyla projeye aktarıldı: '{blockName}'");
                    return blockName;
                }
                else
                {
                    // SCL Source File Import via ExternalSources
                    var blockMatch = Regex.Match(content, @"\b(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK)\s+""?([a-zA-Z0-9_#\s]+)""?", RegexOptions.IgnoreCase);
                    string detectedName;

                    if (blockMatch.Success)
                    {
                        detectedName = blockMatch.Groups[2].Value.Trim().Trim('"', '#', ' ');
                        _log($"SCL kaynak kodunda '{detectedName}' bloğu tespit edildi.");
                    }
                    else
                    {
                        // Bare SCL snippet without envelope -> Wrap into FUNCTION
                        string safeBlockName = Path.GetFileNameWithoutExtension(filePath);
                        safeBlockName = Regex.Replace(safeBlockName, @"[^\w]", "_");
                        if (string.IsNullOrWhiteSpace(safeBlockName) || char.IsDigit(safeBlockName[0]))
                            safeBlockName = "FC_" + safeBlockName;

                        detectedName = safeBlockName;
                        _log($"Dosyada FUNCTION zarfı bulunamadı, '{detectedName}' adıyla otomatik SCL fonksiyon zarfına alındı.");

                        var sb = new StringBuilder();
                        sb.AppendLine($"FUNCTION \"{safeBlockName}\" : Void");
                        sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                        sb.AppendLine("VERSION : 0.1");
                        sb.AppendLine("   VAR_TEMP");
                        sb.AppendLine("   END_VAR");
                        sb.AppendLine();
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(content);
                        sb.AppendLine("END_FUNCTION");
                        content = sb.ToString();
                    }

                    string tempDir = Path.Combine(Path.GetTempPath(), "TiaSclImport");
                    Directory.CreateDirectory(tempDir);
                    string safeBaseName = Regex.Replace(Path.GetFileNameWithoutExtension(filePath), @"[^\w]", "_");
                    if (string.IsNullOrWhiteSpace(safeBaseName)) safeBaseName = "SclSource";
                    string sourceNameWithExt = safeBaseName + ".scl";
                    string tempSclPath = Path.Combine(tempDir, sourceNameWithExt);

                    try
                    {
                        File.WriteAllText(tempSclPath, content, new UTF8Encoding(false));

                        object extGroup = GetPropValue(plc.RawPlcSoftware, "ExternalSourceGroup");
                        if (extGroup == null)
                            throw new InvalidOperationException("PLC ExternalSourceGroup bulunamadı.");

                        object extSources = GetPropValue(extGroup, "ExternalSources");
                        if (extSources == null)
                            throw new InvalidOperationException("PLC ExternalSources koleksiyonu bulunamadı.");

                        // Delete existing external source with same name if present
                        MethodInfo findMethod = extSources.GetType().GetMethod("Find", new[] { typeof(string) });
                        if (findMethod != null)
                        {
                            try
                            {
                                object existingWithExt = findMethod.Invoke(extSources, new object[] { sourceNameWithExt });
                                existingWithExt?.GetType().GetMethod("Delete")?.Invoke(existingWithExt, null);
                            }
                            catch { }

                            try
                            {
                                object existingBase = findMethod.Invoke(extSources, new object[] { safeBaseName });
                                existingBase?.GetType().GetMethod("Delete")?.Invoke(existingBase, null);
                            }
                            catch { }
                        }

                        // CreateFromFile
                        MethodInfo createFromFile = extSources.GetType().GetMethod("CreateFromFile", new[] { typeof(string), typeof(string) });
                        if (createFromFile == null)
                            throw new InvalidOperationException("ExternalSources.CreateFromFile metodu bulunamadı.");

                        object createdExtSource = null;
                        try
                        {
                            createdExtSource = createFromFile.Invoke(extSources, new object[] { sourceNameWithExt, tempSclPath });
                        }
                        catch
                        {
                            createdExtSource = createFromFile.Invoke(extSources, new object[] { safeBaseName, tempSclPath });
                        }

                        if (createdExtSource == null)
                            throw new InvalidOperationException("Harici SCL kaynağı (ExternalSource) oluşturulamadı.");

                        _log($"SCL kaynağından PLC blokları derlenip üretiliyor ('{sourceNameWithExt}')...");

                        // GenerateBlocksFromSource
                        object genResult = null;
                        Type genOptType = FindEngineeringType("Siemens.Engineering.SW.ExternalSources.GenerateBlockOption");
                        MethodInfo genMethodOpt = genOptType != null
                            ? createdExtSource.GetType().GetMethod("GenerateBlocksFromSource", new[] { genOptType })
                            : null;

                        if (genMethodOpt != null)
                        {
                            object opt = Enum.Parse(genOptType, "KeepOnError");
                            genResult = genMethodOpt.Invoke(createdExtSource, new object[] { opt });
                        }
                        else
                        {
                            MethodInfo genMethodNoParam = createdExtSource.GetType().GetMethod("GenerateBlocksFromSource", Type.EmptyTypes);
                            if (genMethodNoParam != null)
                            {
                                genResult = genMethodNoParam.Invoke(createdExtSource, null);
                            }
                            else
                            {
                                var anyGen = createdExtSource.GetType().GetMethods().FirstOrDefault(m => m.Name == "GenerateBlocksFromSource");
                                if (anyGen != null)
                                {
                                    var prms = anyGen.GetParameters();
                                    if (prms.Length == 0) genResult = anyGen.Invoke(createdExtSource, null);
                                    else if (prms.Length == 1 && genOptType != null && prms[0].ParameterType == genOptType)
                                    {
                                        object opt = Enum.Parse(genOptType, "KeepOnError");
                                        genResult = anyGen.Invoke(createdExtSource, new object[] { opt });
                                    }
                                }
                            }
                        }

                        if (genResult is IEnumerable genEnum)
                        {
                            foreach (var item in genEnum)
                            {
                                string genName = GetPropString(item, "Name");
                                if (!string.IsNullOrEmpty(genName))
                                {
                                    detectedName = genName;
                                    break;
                                }
                            }
                        }

                        _log($"'{detectedName}' SCL bloğu başarıyla üretildi ve PLC projesine eklendi.");
                        return detectedName;
                    }
                    finally
                    {
                        if (File.Exists(tempSclPath))
                        {
                            try { File.Delete(tempSclPath); } catch { }
                        }
                    }
                }
            });
        }

        public string ImportSclBlockFromFile(TiaPlcItem plc, string filePath)
        {
            return ImportSclBlockFromFileAsync(plc, filePath).GetAwaiter().GetResult();
        }

        // =========================================================================================
        // TIAGENT MCP SERVER — TAM PAKET AKTARIMI (TAG + GLOBAL DB + INSTANCE DB + LAD/SCL BLOK + OB1)
        // =========================================================================================

        public ProjectOverviewInfo ReadProjectOverview(TiaProjectItem project, TiaPlcItem plc)
        {
            if (plc?.RawPlcSoftware == null)
                return new ProjectOverviewInfo();

            return RunOnOpennessThreadAsync(() =>
            {
                var info = new ProjectOverviewInfo
                {
                    ProjectName = project?.Name ?? "-",
                    ProjectPath = project?.FilePath ?? "-",
                    TiaVersion = _lockedVersion?.VersionKey ?? "V20",
                    ActivePlcName = plc.Name,
                    ActivePlcType = plc.TypeName
                };

                // 1. Read Tag Tables
                object tagTableGroup = GetPropValue(plc.RawPlcSoftware, "TagTableGroup");
                if (tagTableGroup != null)
                {
                    CollectAllTagsFromGroupRecursive(tagTableGroup, info.ExistingTags);
                }

                // 2. Read Program Blocks & DBs
                object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                if (blockGroup != null)
                {
                    CollectAllBlocksRecursive(blockGroup, "", plc.RawPlcSoftware, info.ExistingProgramBlocks, info.ExistingDataBlocks);
                }

                return info;
            }).GetAwaiter().GetResult();
        }

        private void CollectAllTagsFromGroupRecursive(object groupObj, List<PlcTagEntry> targetList)
        {
            if (groupObj == null) return;
            if (GetPropValue(groupObj, "TagTables") is IEnumerable tables)
            {
                foreach (var tbl in tables)
                {
                    string tblName = GetPropString(tbl, "Name");
                    if (GetPropValue(tbl, "Tags") is IEnumerable tags)
                    {
                        foreach (var tag in tags)
                        {
                            string name = GetPropString(tag, "Name");
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

                            targetList.Add(new PlcTagEntry
                            {
                                Name = name,
                                DataType = string.IsNullOrEmpty(dt) ? "Bool" : dt,
                                LogicalAddress = addr,
                                Direction = dir,
                                TableName = tblName,
                                Comment = commentText
                            });
                        }
                    }
                }
            }

            if (GetPropValue(groupObj, "Groups") is IEnumerable subGroups)
            {
                foreach (var sg in subGroups)
                    CollectAllTagsFromGroupRecursive(sg, targetList);
            }
        }

        private void CollectAllBlocksRecursive(object currentGroup, string groupPath, object plcSw, List<SclBlockItem> progBlocks, List<string> dbNames)
        {
            string groupName = GetPropString(currentGroup, "Name");
            string currentPath = string.IsNullOrEmpty(groupPath) ? groupName : $"{groupPath}/{groupName}";

            if (GetPropValue(currentGroup, "Blocks") is IEnumerable blocksEnum)
            {
                foreach (var blk in blocksEnum)
                {
                    string typeName = blk.GetType().Name;
                    string lang = GetPropValue(blk, "ProgrammingLanguage")?.ToString() ?? "";
                    string name = GetPropString(blk, "Name");
                    int num = GetPropInt(blk, "Number");

                    if (typeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(lang, "DB", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(name) && !dbNames.Contains(name))
                            dbNames.Add(name);

                        progBlocks.Add(new SclBlockItem
                        {
                            Name = name,
                            BlockType = "DB",
                            BlockNumber = num > 0 ? (int?)num : null,
                            ProgrammingLanguage = "DB",
                            GroupPath = currentPath,
                            RawBlockObject = blk,
                            RawPlcSoftware = plcSw,
                            RawParentGroup = currentGroup,
                            Status = "DB"
                        });
                    }
                    else
                    {
                        string bType = typeName.Contains("FB") ? "FB" : (typeName.Contains("OB") ? "OB" : "FC");
                        progBlocks.Add(new SclBlockItem
                        {
                            Name = name,
                            BlockType = bType,
                            BlockNumber = num > 0 ? (int?)num : null,
                            ProgrammingLanguage = lang,
                            GroupPath = currentPath,
                            RawBlockObject = blk,
                            RawPlcSoftware = plcSw,
                            RawParentGroup = currentGroup,
                            Status = lang
                        });
                    }
                }
            }

            if (GetPropValue(currentGroup, "Groups") is IEnumerable subGroups)
            {
                foreach (var sub in subGroups)
                    CollectAllBlocksRecursive(sub, currentPath, plcSw, progBlocks, dbNames);
            }
        }

        public object EnsureBlockSubfolder(object plcSoftware, string folderPath)
        {
            object rootBlockGroup = GetPropValue(plcSoftware, "BlockGroup");
            if (rootBlockGroup == null || string.IsNullOrWhiteSpace(folderPath))
                return rootBlockGroup;

            string[] parts = folderPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            object currentGroup = rootBlockGroup;

            foreach (var rawPart in parts)
            {
                string part = rawPart.Trim();
                if (string.IsNullOrEmpty(part)) continue;

                object subGroups = GetPropValue(currentGroup, "Groups");
                if (subGroups == null) break;

                object foundGroup = null;
                MethodInfo findMethod = subGroups.GetType().GetMethod("Find", new[] { typeof(string) });
                if (findMethod != null)
                {
                    try { foundGroup = findMethod.Invoke(subGroups, new object[] { part }); } catch { }
                }

                if (foundGroup == null && subGroups is IEnumerable grpEnum)
                {
                    foreach (var g in grpEnum)
                    {
                        if (string.Equals(GetPropString(g, "Name"), part, StringComparison.OrdinalIgnoreCase))
                        {
                            foundGroup = g;
                            break;
                        }
                    }
                }

                if (foundGroup == null)
                {
                    MethodInfo createMethod = subGroups.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (createMethod != null)
                    {
                        foundGroup = createMethod.Invoke(subGroups, new object[] { part });
                        _log($"Program blocks altında '{part}' klasörü oluşturuldu.");
                    }
                }

                if (foundGroup != null)
                    currentGroup = foundGroup;
            }

            return currentGroup ?? rootBlockGroup;
        }

        public int EnsurePlcTagsCreatedInternal(object plcSoftware, string tableName, List<PlcTagEntry> tags, string tiaVersion)
        {
            if (plcSoftware == null || tags == null || tags.Count == 0) return 0;

            string safeTableName = string.IsNullOrWhiteSpace(tableName) ? "TIAgent_IO_Taglari" : tableName.Trim();
            object tagTableGroup = GetPropValue(plcSoftware, "TagTableGroup");
            object tagTables = GetPropValue(tagTableGroup, "TagTables");
            if (tagTables == null) return 0;

            // Collect all existing tag names across all tag tables to avoid duplicate symbol errors
            var existingTags = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (tagTables is IEnumerable allTablesEnum)
            {
                foreach (var t in allTablesEnum)
                {
                    if (GetPropValue(t, "Tags") is IEnumerable tTags)
                    {
                        foreach (var tg in tTags)
                        {
                            string tName = GetPropString(tg, "Name");
                            if (!string.IsNullOrEmpty(tName))
                                existingTags[tName] = tg;
                        }
                    }
                }
            }

            // Find or create target tag table
            object targetTable = null;
            MethodInfo findTable = tagTables.GetType().GetMethod("Find", new[] { typeof(string) });
            if (findTable != null)
            {
                try { targetTable = findTable.Invoke(tagTables, new object[] { safeTableName }); } catch { }
            }

            if (targetTable == null)
            {
                MethodInfo createTable = tagTables.GetType().GetMethod("Create", new[] { typeof(string) });
                if (createTable != null)
                {
                    targetTable = createTable.Invoke(tagTables, new object[] { safeTableName });
                    _log($"PLC Tag Tablosu oluşturuldu: '{safeTableName}'");
                }
            }

            if (targetTable == null) return 0;

            object tagsComposition = GetPropValue(targetTable, "Tags");
            if (tagsComposition == null) return 0;

            MethodInfo createTag3 = tagsComposition.GetType().GetMethod("Create", new[] { typeof(string), typeof(string), typeof(string) });
            MethodInfo createTag1 = tagsComposition.GetType().GetMethod("Create", new[] { typeof(string) });

            int createdCount = 0;
            foreach (var item in tags)
            {
                if (string.IsNullOrWhiteSpace(item?.Name)) continue;
                string cleanName = item.Name.Trim().Trim('\"');

                if (existingTags.TryGetValue(cleanName, out object existTagObj) && existTagObj != null)
                {
                    try
                    {
                        string targetType = NormalizeDbDatatypeForXml(item.DataType ?? "Bool");
                        string targetAddr = string.IsNullOrWhiteSpace(item.LogicalAddress) ? "%M0.0" : item.LogicalAddress.Trim();
                        string curType = GetPropString(existTagObj, "DataTypeName");
                        string curAddr = GetPropString(existTagObj, "LogicalAddress");

                        // If data type or address changed, delete the old tag so it gets re-created atomically
                        // with the new type and address to prevent Siemens Openness mismatch errors
                        if (!string.Equals(curType, targetType, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(curAddr, targetAddr, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                MethodInfo del = existTagObj.GetType().GetMethod("Delete", Type.EmptyTypes);
                                del?.Invoke(existTagObj, null);
                                existTagObj = null;
                            }
                            catch { }
                        }
                    }
                    catch { }

                    if (existTagObj != null)
                    {
                        _cachedDefinedGlobalSymbols.Add(cleanName);
                        _cachedPlcTagTypes[cleanName] = item.DataType ?? "Bool";
                        continue;
                    }
                }

                try
                {
                    string dType = NormalizeDbDatatypeForXml(item.DataType ?? "Bool");
                    string addr = string.IsNullOrWhiteSpace(item.LogicalAddress) ? "%M0.0" : item.LogicalAddress.Trim();

                    object createdTag = null;
                    if (createTag3 != null)
                    {
                        createdTag = createTag3.Invoke(tagsComposition, new object[] { cleanName, dType, addr });
                    }
                    else if (createTag1 != null)
                    {
                        createdTag = createTag1.Invoke(tagsComposition, new object[] { cleanName });
                        if (createdTag != null)
                        {
                            createdTag.GetType().GetProperty("DataTypeName")?.SetValue(createdTag, dType, null);
                            createdTag.GetType().GetProperty("LogicalAddress")?.SetValue(createdTag, addr, null);
                        }
                    }

                    if (createdTag != null && !string.IsNullOrWhiteSpace(item.Comment))
                    {
                        try
                        {
                            object cmtObj = GetPropValue(createdTag, "Comment");
                            if (GetPropValue(cmtObj, "Items") is IEnumerable cmtItems)
                            {
                                foreach (var ci in cmtItems)
                                {
                                    ci.GetType().GetProperty("Text")?.SetValue(ci, item.Comment, null);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    existingTags[cleanName] = createdTag;
                    _cachedDefinedGlobalSymbols.Add(cleanName);
                    _cachedPlcTagTypes[cleanName] = dType;
                    createdCount++;
                }
                catch (Exception ex)
                {
                    _log($"Tag '{cleanName}' oluşturulurken uyarı: {ex.GetBaseException()?.Message ?? ex.Message}");
                }
            }

            if (createdCount > 0)
            {
                _log($"'{safeTableName}' tablosuna {createdCount} yeni PLC I/O Tag eklendi.");
            }
            return createdCount;
        }

        private void EnsureInstanceDbCreatedInternal(object plcSoftware, PlcDbEntry dbEntry, object targetGroup, string tiaVersion)
        {
            if (plcSoftware == null || dbEntry == null || string.IsNullOrWhiteSpace(dbEntry.Name) || string.IsNullOrWhiteSpace(dbEntry.InstanceOfFbName))
                return;

            string dbName = dbEntry.Name.Trim().Trim('\"');
            string fbName = dbEntry.InstanceOfFbName.Trim().Trim('\"');

            object groupToUse = targetGroup ?? GetPropValue(plcSoftware, "BlockGroup");
            object blocksComp = GetPropValue(groupToUse, "Blocks");
            if (blocksComp == null) return;

            // Method 1: Try SimaticML SW.Blocks.InstanceDB XML Import
            Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
            MethodInfo importMethod = blocksComp.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
            object optOverride = importOptionsType != null ? Enum.Parse(importOptionsType, "Override") : null;

            bool created = false;
            if (importMethod != null)
            {
                string ver = string.IsNullOrWhiteSpace(tiaVersion) ? "V20" : tiaVersion.Trim();
                if (!ver.StartsWith("V", StringComparison.OrdinalIgnoreCase)) ver = "V" + ver;
                string ifaceVer = "v5";
                bool includeNs = true;
                if (ver.StartsWith("V14", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v2"; includeNs = false; }
                else if (ver.StartsWith("V15", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v3"; includeNs = false; }
                else if (ver.StartsWith("V16", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v4"; includeNs = true; }

                var sb = new StringBuilder();
                sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
                sb.AppendLine("<Document>");
                sb.AppendLine($@"  <Engineering version=""{ver}"" />");
                sb.AppendLine(@"  <SW.Blocks.InstanceDB ID=""0"">");
                sb.AppendLine("    <AttributeList>");
                sb.AppendLine("      <AutoNumber>true</AutoNumber>");
                sb.AppendLine($"      <InstanceOfName>{System.Security.SecurityElement.Escape(fbName)}</InstanceOfName>");
                sb.AppendLine("      <InstanceOfType>FB</InstanceOfType>");
                sb.AppendLine($@"      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/{ifaceVer}"">");
                sb.AppendLine(@"  <Section Name=""Input"" />");
                sb.AppendLine(@"  <Section Name=""Output"" />");
                sb.AppendLine(@"  <Section Name=""InOut"" />");
                sb.AppendLine(@"  <Section Name=""Static"" />");
                sb.AppendLine("</Sections></Interface>");
                sb.AppendLine("      <MemoryLayout>Optimized</MemoryLayout>");
                sb.AppendLine($"      <Name>{System.Security.SecurityElement.Escape(dbName)}</Name>");
                if (includeNs) sb.AppendLine("      <Namespace />");
                sb.AppendLine("      <ProgrammingLanguage>DB</ProgrammingLanguage>");
                sb.AppendLine("    </AttributeList>");
                sb.AppendLine("    <ObjectList />");
                sb.AppendLine("  </SW.Blocks.InstanceDB>");
                sb.AppendLine("</Document>");

                string tempXml = Path.Combine(Path.GetTempPath(), $"InstDb_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempXml, sb.ToString(), Encoding.UTF8);
                    InvokeBlocksImport(importMethod, blocksComp, tempXml, optOverride);
                    created = true;
                    _log($"'{dbName}' Instance DB ('{fbName}' için) oluşturuldu.");
                }
                catch
                {
                    created = false;
                }
                finally
                {
                    if (File.Exists(tempXml)) { try { File.Delete(tempXml); } catch { } }
                }
            }

            // Method 2: Fallback via ExternalSource (.db)
            if (!created)
            {
                object extGroup = GetPropValue(plcSoftware, "ExternalSourceGroup");
                object extSources = GetPropValue(extGroup, "ExternalSources");
                if (extSources != null)
                {
                    string tempDbPath = Path.Combine(Path.GetTempPath(), $"{dbName}_{Guid.NewGuid():N}.db");
                    object extSrc = null;
                    try
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"DATA_BLOCK \"{dbName}\"");
                        sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                        sb.AppendLine("VERSION : 0.1");
                        sb.AppendLine("NON_RETAIN");
                        sb.AppendLine($"\"{fbName}\"");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine("END_DATA_BLOCK");
                        File.WriteAllText(tempDbPath, sb.ToString(), new UTF8Encoding(false));

                        string srcName = $"InstSrc_{Guid.NewGuid():N}.db";
                        MethodInfo createFromFile = extSources.GetType().GetMethod("CreateFromFile", new[] { typeof(string), typeof(string) });
                        extSrc = createFromFile?.Invoke(extSources, new object[] { srcName, tempDbPath });
                        if (extSrc != null)
                        {
                            Type genOptType = FindEngineeringType("Siemens.Engineering.SW.ExternalSources.GenerateBlockOption");
                            MethodInfo genMethod = genOptType != null
                                ? extSrc.GetType().GetMethod("GenerateBlocksFromSource", new[] { genOptType })
                                : extSrc.GetType().GetMethod("GenerateBlocksFromSource", Type.EmptyTypes);

                            if (genMethod != null)
                            {
                                if (genMethod.GetParameters().Length == 1 && genOptType != null)
                                    genMethod.Invoke(extSrc, new object[] { Enum.Parse(genOptType, "KeepOnError") });
                                else
                                    genMethod.Invoke(extSrc, null);
                            }
                            _log($"'{dbName}' Instance DB ExternalSource ile oluşturuldu.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"'{dbName}' Instance DB oluşturulurken uyarı: {ex.GetBaseException()?.Message ?? ex.Message}");
                    }
                    finally
                    {
                        try { extSrc?.GetType().GetMethod("Delete")?.Invoke(extSrc, null); } catch { }
                        if (File.Exists(tempDbPath)) { try { File.Delete(tempDbPath); } catch { } }
                    }
                }
            }

            // Compile Instance DB
            try
            {
                MethodInfo findMethod = blocksComp.GetType().GetMethod("Find", new[] { typeof(string) });
                object dbObj = findMethod?.Invoke(blocksComp, new object[] { dbName });
                if (dbObj == null)
                {
                    object rootBg = GetPropValue(plcSoftware, "BlockGroup");
                    object rootBlocks = GetPropValue(rootBg, "Blocks");
                    dbObj = rootBlocks?.GetType().GetMethod("Find", new[] { typeof(string) })?.Invoke(rootBlocks, new object[] { dbName });
                }
                if (dbObj != null)
                {
                    CompileBlock(dbObj, out _);
                }
            }
            catch { }
        }

        private void EnsureMainObCallUpdatedInternal(
            object plcSoftware,
            PlcBlockEntry mainCallEntry,
            List<BlockInterfaceMember> targetBlockInterface,
            string tiaVersion,
            SclToLadConverter converter)
        {
            if (plcSoftware == null || mainCallEntry == null || string.IsNullOrWhiteSpace(mainCallEntry.CallTargetBlockName))
                return;

            object rootBlockGroup = GetPropValue(plcSoftware, "BlockGroup");
            object rootBlocks = GetPropValue(rootBlockGroup, "Blocks");
            if (rootBlocks == null) return;

            MethodInfo findMethod = rootBlocks.GetType().GetMethod("Find", new[] { typeof(string) });
            object mainOb = findMethod?.Invoke(rootBlocks, new object[] { "Main" });

            // Also search if OB1 is named something else (e.g. "OB1" or "Main_1")
            if (mainOb == null && rootBlocks is IEnumerable blkEnum)
            {
                foreach (var b in blkEnum)
                {
                    if (b.GetType().Name.Contains("OB") && GetPropInt(b, "Number") == 1)
                    {
                        mainOb = b;
                        break;
                    }
                }
            }

            Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
            MethodInfo importMethod = rootBlocks.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
            object optOverride = importOptionsType != null ? Enum.Parse(importOptionsType, "Override") : null;
            if (importMethod == null) return;

            string callBlockName = mainCallEntry.CallTargetBlockName;
            string callBlockType = mainCallEntry.CallTargetBlockType ?? "FB";
            string instDbName = mainCallEntry.CallInstanceDbName;

            if (mainOb != null)
            {
                string tempMainXml = Path.Combine(Path.GetTempPath(), $"Main_Export_{Guid.NewGuid():N}.xml");
                try
                {
                    ExportBlockToXml(mainOb, tempMainXml);
                    if (File.Exists(tempMainXml))
                    {
                        string existingXml = File.ReadAllText(tempMainXml, Encoding.UTF8);

                        // If Main already contains a CallInfo to this block, remove the old CompileUnit for this block so we refresh it cleanly
                        var xdoc = XDocument.Load(tempMainXml);
                        var obElem = xdoc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
                        var objList = obElem?.Element("ObjectList");
                        if (objList != null)
                        {
                            var existingCallUnits = objList.Elements()
                                .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit" &&
                                            e.ToString().IndexOf($"Name=\"{callBlockName}\"", StringComparison.OrdinalIgnoreCase) >= 0)
                                .ToList();

                            foreach (var oldCu in existingCallUnits)
                                oldCu.Remove();

                            int maxId = 50;
                            foreach (var desc in xdoc.Descendants())
                            {
                                string idAttr = desc.Attribute("ID")?.Value;
                                if (!string.IsNullOrEmpty(idAttr))
                                {
                                    if (int.TryParse(idAttr, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int hexVal))
                                    {
                                        if (hexVal > maxId) maxId = hexVal;
                                    }
                                    else if (int.TryParse(idAttr, out int decVal) && decVal > maxId)
                                    {
                                        maxId = decVal;
                                    }
                                }
                            }

                            string newCuXml = converter.BuildLadBlockCallCompileUnitXml(
                                callBlockName,
                                callBlockType,
                                instDbName,
                                targetBlockInterface,
                                mainCallEntry.CallParameterBindings,
                                tiaVersion,
                                maxId + 10,
                                $"T-IA Agent: {callBlockName} Çağrısı");

                            var newCuElem = XElement.Parse(newCuXml);
                            var titleElem = objList.Elements().LastOrDefault(e =>
                                e.Name.LocalName == "MultilingualText" &&
                                string.Equals(e.Attribute("CompositionName")?.Value, "Title", StringComparison.OrdinalIgnoreCase));

                            if (titleElem != null)
                                titleElem.AddBeforeSelf(newCuElem);
                            else
                                objList.Add(newCuElem);

                            xdoc.Save(tempMainXml);
                            InvokeBlocksImport(importMethod, rootBlocks, tempMainXml, optOverride);
                            _log($"Main [OB1] içerisine '{callBlockName}' blok çağrısı (LAD Network) eklendi.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Main [OB1] güncellenirken uyarı: {ex.GetBaseException()?.Message ?? ex.Message}");
                }
                finally
                {
                    if (File.Exists(tempMainXml)) { try { File.Delete(tempMainXml); } catch { } }
                }
            }
            else
            {
                // Main [OB1] does not exist yet -> Create new Main [OB1] with the call network
                string ver = string.IsNullOrWhiteSpace(tiaVersion) ? "V20" : tiaVersion.Trim();
                if (!ver.StartsWith("V", StringComparison.OrdinalIgnoreCase)) ver = "V" + ver;
                string ifaceVer = "v5";
                bool includeNs = true;
                if (ver.StartsWith("V14", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v2"; includeNs = false; }
                else if (ver.StartsWith("V15", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v3"; includeNs = false; }
                else if (ver.StartsWith("V16", StringComparison.OrdinalIgnoreCase)) { ifaceVer = "v4"; includeNs = true; }

                string cuXml = converter.BuildLadBlockCallCompileUnitXml(
                    callBlockName,
                    callBlockType,
                    instDbName,
                    targetBlockInterface,
                    mainCallEntry.CallParameterBindings,
                    tiaVersion,
                    10,
                    $"T-IA Agent: {callBlockName} Çağrısı");

                string nsLine = includeNs ? "      <Namespace />\r\n" : "";
                string newMainXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""{ver}"" />
  <SW.Blocks.OB ID=""0"">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor>TIAgent MCP Server</HeaderAuthor>
      <HeaderVersion>0.1</HeaderVersion>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/{ifaceVer}"">
  <Section Name=""Input"">
    <Member Name=""Initial_Call"" Datatype=""Bool"" Accessibility=""Public"" Informative=""true"" />
    <Member Name=""Remanence"" Datatype=""Bool"" Accessibility=""Public"" Informative=""true"" />
  </Section>
  <Section Name=""Temp"" />
  <Section Name=""Constant"" />
</Sections></Interface>
      <IsIECCheckEnabled>false</IsIECCheckEnabled>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>Main</Name>
{nsLine}      <Number>1</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SecondaryType>ProgramCycle</SecondaryType>
      <SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
{cuXml}
    </ObjectList>
  </SW.Blocks.OB>
</Document>";

                string tempMainXml = Path.Combine(Path.GetTempPath(), $"Main_New_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempMainXml, newMainXml, Encoding.UTF8);
                    InvokeBlocksImport(importMethod, rootBlocks, tempMainXml, optOverride);
                    _log($"Main [OB1] oluşturuldu ve '{callBlockName}' çağrısı eklendi.");
                }
                catch (Exception ex)
                {
                    _log($"Main [OB1] oluşturulurken uyarı: {ex.GetBaseException()?.Message ?? ex.Message}");
                }
                finally
                {
                    if (File.Exists(tempMainXml)) { try { File.Delete(tempMainXml); } catch { } }
                }
            }
        }

        public string BackupExistingItemsBeforeImport(TiaPlcItem plc, TiaPackage pkg, string backupRootDir)
        {
            if (plc?.RawPlcSoftware == null || pkg == null || string.IsNullOrWhiteSpace(backupRootDir))
                return null;

            string createdBackupDir = null;
            RunOnOpennessThread(() =>
            {
                try
                {
                    string folderName = $"{plc.Name}_{pkg.PackageName}_{DateTime.Now:yyyyMMdd_HHmmss}";
                    foreach (char c in Path.GetInvalidFileNameChars())
                        folderName = folderName.Replace(c, '_');

                    string targetDir = Path.Combine(backupRootDir, folderName);
                    Directory.CreateDirectory(targetDir);
                    createdBackupDir = targetDir;

                    object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                    object blocksComp = GetPropValue(blockGroup, "Blocks");
                    MethodInfo findBlock = blocksComp?.GetType().GetMethod("Find", new[] { typeof(string) });

                    int backedUp = 0;
                    if (findBlock != null)
                    {
                        var namesToBackup = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Main" };
                        if (pkg.Blocks != null)
                        {
                            foreach (var b in pkg.Blocks)
                                if (!string.IsNullOrWhiteSpace(b.Name)) namesToBackup.Add(b.Name);
                        }
                        if (pkg.DataBlocks != null)
                        {
                            foreach (var db in pkg.DataBlocks)
                                if (!string.IsNullOrWhiteSpace(db.Name)) namesToBackup.Add(db.Name);
                        }

                        foreach (var bName in namesToBackup)
                        {
                            try
                            {
                                object blk = findBlock.Invoke(blocksComp, new object[] { bName });
                                if (blk != null)
                                {
                                    string xmlPath = Path.Combine(targetDir, $"{bName}.xml");
                                    try
                                    {
                                        ExportBlockToXml(blk, xmlPath);
                                    }
                                    catch
                                    {
                                        CompileBlock(blk, out _);
                                        ExportBlockToXml(blk, xmlPath);
                                    }
                                    if (File.Exists(xmlPath))
                                        backedUp++;
                                }
                            }
                            catch { }
                        }
                    }

                    if (backedUp > 0)
                    {
                        _log($"Yedekleme tamamlandı ({backedUp} mevcut blok yedeklendi): {targetDir}");
                    }

                    // Kayıt manifestosu (operation.json): Zaman, işlem ve blok bazlı geri alma bilgisi
                    try
                    {
                        var backedUpNames = new List<string>();
                        foreach (var f in Directory.GetFiles(targetDir, "*.xml"))
                        {
                            backedUpNames.Add(Path.GetFileNameWithoutExtension(f));
                        }

                        var opInfo = new BackupOperationItem
                        {
                            Id = folderName,
                            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            OperationName = !string.IsNullOrWhiteSpace(pkg.Title) ? pkg.Title : pkg.PackageName,
                            PackageName = pkg.PackageName,
                            Prompt = pkg.SourcePrompt ?? "",
                            SourceChannel = pkg.SourceChannel ?? "AI",
                            PlcName = plc.Name,
                            BackupFolderPath = targetDir,
                            BackedUpBlocks = backedUpNames,
                            CreatedBlocks = pkg.Blocks?.Select(b => b.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                            CreatedDataBlocks = pkg.DataBlocks?.Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                            CreatedTags = pkg.Tags?.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>()
                        };

                        var serializer = new JavaScriptSerializer();
                        string json = serializer.Serialize(opInfo);
                        File.WriteAllText(Path.Combine(targetDir, "operation.json"), json, Encoding.UTF8);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    _log($"Yedekleme uyarısı: {ex.Message}");
                }
            });
            return createdBackupDir;
        }

        public bool RollbackBackupOperation(
            TiaPlcItem plc,
            BackupOperationItem op,
            Action<string, string, bool> stepCallback,
            out string error)
        {
            error = "";
            if (plc?.RawPlcSoftware == null)
            {
                error = "Hedef PLC bağlı değil.";
                return false;
            }
            if (op == null || !Directory.Exists(op.BackupFolderPath))
            {
                error = "Yedek klasörü bulunamadı: " + (op?.BackupFolderPath ?? "null");
                return false;
            }

            bool ok = false;
            string localError = "";
            RunOnOpennessThread(() =>
            {
                try
                {
                    stepCallback?.Invoke("geri-al", $"{op.OperationName} ({op.Timestamp}) geri alınıyor...", true);

                    object blockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                    object blocksComp = GetPropValue(blockGroup, "Blocks");
                    MethodInfo findBlock = blocksComp?.GetType().GetMethod("Find", new[] { typeof(string) });
                    Type blocksType = blocksComp?.GetType();
                    MethodInfo importMethod = blocksType?.GetMethods()
                        .FirstOrDefault(m => m.Name == "Import" && m.GetParameters().Length == 2);

                    // 1. İşlem sırasında oluşturulmuş olan yeni blokları kaldır (öncesinde var olan yedek listesinde olmayanlar)
                    var preExisting = new HashSet<string>(op.BackedUpBlocks ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    var toDelete = new List<string>();
                    if (op.CreatedBlocks != null) toDelete.AddRange(op.CreatedBlocks);
                    if (op.CreatedDataBlocks != null) toDelete.AddRange(op.CreatedDataBlocks);

                    int deletedCount = 0;
                    foreach (var blkName in toDelete.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!preExisting.Contains(blkName) && findBlock != null)
                        {
                            try
                            {
                                object existingBlk = findBlock.Invoke(blocksComp, new object[] { blkName });
                                if (existingBlk != null)
                                {
                                    MethodInfo deleteMethod = existingBlk.GetType().GetMethod("Delete");
                                    deleteMethod?.Invoke(existingBlk, null);
                                    deletedCount++;
                                    stepCallback?.Invoke("geri-al", $"Oluşturulan '{blkName}' bloğu kaldırıldı.", true);
                                }
                            }
                            catch (Exception ex)
                            {
                                stepCallback?.Invoke("geri-al", $"'{blkName}' silinirken uyarı: {ex.Message}", false);
                            }
                        }
                    }

                    // 2. Yedek klasöründeki orijinal SimaticML XML dosyalarını tekrar TIA Portal'a aktar (Override)
                    var xmlFiles = Directory.GetFiles(op.BackupFolderPath, "*.xml");
                    int restoredCount = 0;
                    Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                    importMethod = blocksComp?.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType }) ?? importMethod;
                    object optOverride = importOptionsType != null ? Enum.Parse(importOptionsType, "Override") : null;

                    foreach (var xmlFile in xmlFiles)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(xmlFile);
                        try
                        {
                            if (importMethod != null && blocksComp != null)
                            {
                                InvokeBlocksImport(importMethod, blocksComp, xmlFile, optOverride);
                                restoredCount++;
                                stepCallback?.Invoke("geri-al", $"'{fileName}' bloğu orijinal haline döndürüldü.", true);
                            }
                        }
                        catch (Exception ex)
                        {
                            stepCallback?.Invoke("geri-al", $"'{fileName}' geri yüklenirken hata: {ex.Message}", false);
                        }
                    }

                    // 3. PLC Yazılımını Derle
                    CompileBlock(plc.RawPlcSoftware, out string compileSummary);
                    stepCallback?.Invoke("geri-al", $"İşlem geri alındı ({restoredCount} blok restore edildi, {deletedCount} yeni blok temizlendi). Derleme: {compileSummary}", true);

                    op.IsRolledBack = true;
                    try
                    {
                        string manifestPath = Path.Combine(op.BackupFolderPath, "operation.json");
                        var serializer = new JavaScriptSerializer();
                        string json = serializer.Serialize(op);
                        File.WriteAllText(manifestPath, json, Encoding.UTF8);
                    }
                    catch { }

                    ok = true;
                }
                catch (Exception ex)
                {
                    localError = ex.Message;
                    stepCallback?.Invoke("geri-al", $"Geri alma hatası: {ex.Message}", false);
                    ok = false;
                }
            });

            error = localError;
            return ok;
        }

        public bool ImportFullPackage(
            TiaPlcItem plc,
            TiaPackage pkg,
            string uiFolderOverride,
            string backupRootDir,
            Action<string, string, bool> stepCallback,
            out string compileSummary)
        {
            compileSummary = "";
            if (plc?.RawPlcSoftware == null)
                throw new ArgumentNullException(nameof(plc), "Hedef PLC bağlı değil.");
            if (pkg == null)
                throw new ArgumentNullException(nameof(pkg), "Aktarılacak paket boş.");

            // Ornekler klasöründeki eğitim ve derleme kurallarına göre paketi doğrula ve gerekirse otomatik düzelt
            PackageValidator.ValidateAndSanitize(pkg, stepCallback);

            string localSummary = "";
            bool ok = RunOnOpennessThreadAsync(() =>
            {
                string tiaVer = _lockedVersion?.VersionKey ?? "V20";
                var converter = new SclToLadConverter();

                // 1. Backup existing blocks BEFORE modifying any DBs (so Main [OB1] is exported while still consistent)
                string currentBackupDir = null;
                if (!string.IsNullOrWhiteSpace(backupRootDir))
                {
                    currentBackupDir = BackupExistingItemsBeforeImport(plc, pkg, backupRootDir);
                }

                // 1b. Hardware (Devices & Networks) + Technology Objects – must exist before blocks reference them
                if (pkg.HasHardwareContent())
                {
                    bool hwOk = ApplyHardwareAndTechnologyInternal(plc, pkg, stepCallback, out string hwErr);
                    if (!hwOk)
                    {
                        localSummary = hwErr;
                        stepCallback?.Invoke("donanım", $"Donanım işlemi hatayla sonuçlandı: {hwErr}", false);
                        return false;
                    }
                }

                // 2. Create PLC Tag Table & Tags
                if (pkg.Tags != null && pkg.Tags.Count > 0)
                {
                    int addedTags = EnsurePlcTagsCreatedInternal(plc.RawPlcSoftware, pkg.TagTableName, pkg.Tags, tiaVer);
                    stepCallback?.Invoke("aktarım", $"PLC Tag tablosu güncellendi ({pkg.TagTableName}: {pkg.Tags.Count} tag hazır, {addedTags} yeni eklendi)", true);
                }

                // 3. Create/Update Global Data Blocks first so symbols resolve inside FB/FC
                if (pkg.DataBlocks != null)
                {
                    foreach (var db in pkg.DataBlocks.Where(d => !string.Equals(d.DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase)))
                    {
                        var memberDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (db.Members != null)
                        {
                            foreach (var m in db.Members)
                            {
                                if (!string.IsNullOrWhiteSpace(m.Name))
                                    memberDict[m.Name] = m.DataType ?? "Bool";
                            }
                        }
                        if (memberDict.Count > 0)
                        {
                            EnsureLadderDbUpdated(plc.RawPlcSoftware, memberDict, tiaVer, db.Name);
                            stepCallback?.Invoke("aktarım", $"Global Veri Bloğu hazırlandı ve derlendi: {db.Name} ({memberDict.Count} değişken)", true);
                        }
                    }
                }

                // 4. Resolve target folder inside Program blocks
                object rootGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                object rootBlocksComp = GetPropValue(rootGroup, "Blocks");

                string effectiveFolder = !string.IsNullOrWhiteSpace(pkg.TargetFolder) ? pkg.TargetFolder.Trim() : (uiFolderOverride ?? "").Trim();
                object targetGroup = EnsureBlockSubfolder(plc.RawPlcSoftware, effectiveFolder);
                object targetBlocksComp = GetPropValue(targetGroup, "Blocks");
                if (targetBlocksComp == null)
                    throw new InvalidOperationException("Hedef klasörün Blocks koleksiyonuna ulaşılamadı.");

                Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                MethodInfo importMethod = targetBlocksComp.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
                MethodInfo rootImportMethod = rootBlocksComp?.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType }) ?? importMethod;
                object optOverride = importOptionsType != null ? Enum.Parse(importOptionsType, "Override") : null;

                // 5. Import Program Blocks (FB / FC / Main OB1)
                BlockInterfaceMember[] lastImportedInterface = null;
                if (pkg.Blocks != null)
                {
                    foreach (var blk in pkg.Blocks.Where(b => !b.IsMainObCall))
                    {
                        if (blk.InterfaceMembers != null && blk.InterfaceMembers.Count > 0)
                        {
                            lastImportedInterface = blk.InterfaceMembers.ToArray();
                        }

                        bool isMainOb = string.Equals(blk.BlockType, "OB", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(blk.Name, "Main", StringComparison.OrdinalIgnoreCase);

                        if (isMainOb && !string.IsNullOrWhiteSpace(blk.SimaticMlXml))
                        {
                            int mergedTotal = MergeOrImportMainObXml(
                                rootBlocksComp ?? targetBlocksComp,
                                rootImportMethod,
                                optOverride,
                                blk,
                                tiaVer,
                                currentBackupDir,
                                backupRootDir,
                                plc.RawPlcSoftware,
                                pkg.TagTableName,
                                pkg.Tags);
                            int incomingCuCount = (blk.Networks != null && blk.Networks.Count > 0)
                                ? blk.Networks.Count
                                : Regex.Matches(blk.SimaticMlXml, @"<SW\.Blocks\.CompileUnit\b").Count;
                            if (incomingCuCount <= 0) incomingCuCount = 1;
                            stepCallback?.Invoke("aktarım", $"Main [OB1] güncellendi ({incomingCuCount} network eklendi/değiştirildi, toplam {mergedTotal} network korundu)", true);
                        }
                        else if (string.Equals(blk.Language, "SCL", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(blk.SclCode) && string.IsNullOrWhiteSpace(blk.SimaticMlXml))
                        {
                            string tempSclFile = Path.Combine(Path.GetTempPath(), $"{blk.Name}_{Guid.NewGuid():N}.scl");
                            try
                            {
                                File.WriteAllText(tempSclFile, blk.SclCode, new UTF8Encoding(false));
                                ImportSclBlockFromFile(plc, tempSclFile);
                                stepCallback?.Invoke("aktarım", $"{blk.BlockType} bloğu (SCL) aktarıldı: {blk.Name}", true);
                            }
                            finally
                            {
                                if (File.Exists(tempSclFile)) { try { File.Delete(tempSclFile); } catch { } }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(blk.SimaticMlXml))
                        {
                            // Ensure XML <Engineering version="..." /> matches the active TIA Portal version
                            string adjustedXml = Regex.Replace(
                                blk.SimaticMlXml,
                                @"<Engineering\s+version=""[^""]+""\s*/>",
                                $"<Engineering version=\"{tiaVer}\" />");

                            string tempXml = Path.Combine(Path.GetTempPath(), $"PkgBlock_{blk.Name}_{Guid.NewGuid():N}.xml");
                            try
                            {
                                File.WriteAllText(tempXml, adjustedXml, Encoding.UTF8);
                                InvokeBlocksImport(importMethod, targetBlocksComp, tempXml, optOverride);
                                stepCallback?.Invoke("aktarım", $"{blk.BlockType} bloğu ({blk.Language}) aktarıldı: {blk.Name}", true);
                            }
                            finally
                            {
                                if (File.Exists(tempXml)) { try { File.Delete(tempXml); } catch { } }
                            }
                        }

                        // Compile imported block immediately
                        try
                        {
                            object compToSearch = isMainOb ? (rootBlocksComp ?? targetBlocksComp) : targetBlocksComp;
                            MethodInfo findBlk = compToSearch.GetType().GetMethod("Find", new[] { typeof(string) });
                            object importedBlkObj = findBlk?.Invoke(compToSearch, new object[] { blk.Name });
                            if (importedBlkObj != null)
                            {
                                CompileBlock(importedBlkObj, out _);
                            }
                        }
                        catch { }
                    }
                }

                // 6. Create/Update Instance Data Blocks (after their FBs exist and are compiled)
                if (pkg.DataBlocks != null)
                {
                    foreach (var instDb in pkg.DataBlocks.Where(d => string.Equals(d.DbType, "InstanceDB", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureInstanceDbCreatedInternal(plc.RawPlcSoftware, instDb, targetGroup, tiaVer);
                        stepCallback?.Invoke("aktarım", $"Instance DB oluşturuldu ve derlendi: {instDb.Name} ({instDb.InstanceOfFbName})", true);
                    }
                }

                // 7. Update Main [OB1] with call networks if included in package
                if (pkg.Blocks != null)
                {
                    foreach (var mainCall in pkg.Blocks.Where(b => b.IsMainObCall))
                    {
                        var targetBlk = pkg.Blocks.FirstOrDefault(b => !b.IsMainObCall && string.Equals(b.Name, mainCall.CallTargetBlockName, StringComparison.OrdinalIgnoreCase));
                        var ifaceList = targetBlk?.InterfaceMembers ?? (lastImportedInterface != null ? new List<BlockInterfaceMember>(lastImportedInterface) : new List<BlockInterfaceMember>());

                        EnsureMainObCallUpdatedInternal(plc.RawPlcSoftware, mainCall, ifaceList, tiaVer, converter);
                        stepCallback?.Invoke("aktarım", $"Main [OB1] içerisine '{mainCall.CallTargetBlockName}' çağrısı eklendi", true);
                    }
                }

                // 8. Compile entire PLC Software to synchronize all symbols and verify 0 errors
                bool compiledOk = CompileBlock(plc.RawPlcSoftware, out string compMsg);
                localSummary = compMsg ?? "Derleme tamamlandı";
                stepCallback?.Invoke("derleme", $"PLC Yazılım Derlemesi: {localSummary}", compiledOk);
                return compiledOk;
            }).GetAwaiter().GetResult();

            compileSummary = localSummary;
            return ok;
        }

        private static string GetCompileUnitTitle(XElement cu)
        {
            return cu?.Descendants()
                .FirstOrDefault(d => d.Name.LocalName == "MultilingualText" &&
                                     string.Equals(d.Attribute("CompositionName")?.Value, "Title", StringComparison.OrdinalIgnoreCase))
                ?.Descendants()
                .FirstOrDefault(d => d.Name.LocalName == "Text")?.Value?.Trim() ?? "";
        }

        private static HashSet<string> GetCompileUnitWrittenTargets(XElement cu)
        {
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cu == null) return written;

            // 1. Map Access UId -> symbol names (full "DB.Member" and leaf "Member")
            var accessMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var acc in cu.Descendants().Where(e => e.Name.LocalName == "Access"))
            {
                string uid = acc.Attribute("UId")?.Value;
                if (string.IsNullOrWhiteSpace(uid)) continue;

                var comps = acc.Descendants()
                    .Where(c => c.Name.LocalName == "Component")
                    .Select(c => c.Attribute("Name")?.Value)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                if (comps.Count > 0)
                {
                    var names = new List<string> { string.Join(".", comps), comps.Last() };
                    accessMap[uid] = names;
                }
            }

            // 2. Collect Part UIds that write to their operand/output pin, plus timer/counter instances
            var writePinByPartUid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in cu.Descendants().Where(e => e.Name.LocalName == "Part"))
            {
                string partName = part.Attribute("Name")?.Value ?? "";
                string partUid = part.Attribute("UId")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(partUid)) continue;

                if (string.Equals(partName, "Coil", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "SCoil", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "RCoil", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "Sr", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "Rs", StringComparison.OrdinalIgnoreCase))
                {
                    writePinByPartUid[partUid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operand" };
                }
                else if (string.Equals(partName, "Move", StringComparison.OrdinalIgnoreCase))
                {
                    writePinByPartUid[partUid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "out", "out1" };
                }
                else if (string.Equals(partName, "TON", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(partName, "TOF", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(partName, "TP", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(partName, "TONR", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(partName, "CTU", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(partName, "CTD", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(partName, "CTUD", StringComparison.OrdinalIgnoreCase))
                {
                    var instComps = part.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance")
                        ?.Descendants().Where(c => c.Name.LocalName == "Component")
                        .Select(c => c.Attribute("Name")?.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();

                    if (instComps != null && instComps.Count > 0)
                    {
                        written.Add(string.Join(".", instComps));
                        written.Add(instComps.Last());
                    }
                }
            }

            // 3. Inspect Wires to find which Access UId is connected to a write pin
            foreach (var wire in cu.Descendants().Where(e => e.Name.LocalName == "Wire"))
            {
                var identCon = wire.Elements().FirstOrDefault(e => e.Name.LocalName == "IdentCon");
                if (identCon == null) continue;
                string accessUid = identCon.Attribute("UId")?.Value;
                if (string.IsNullOrWhiteSpace(accessUid) || !accessMap.TryGetValue(accessUid, out var symNames))
                    continue;

                foreach (var nameCon in wire.Elements().Where(e => e.Name.LocalName == "NameCon"))
                {
                    string partUid = nameCon.Attribute("UId")?.Value ?? "";
                    string pinName = nameCon.Attribute("Name")?.Value ?? "";
                    if (writePinByPartUid.TryGetValue(partUid, out var validPins) && validPins.Contains(pinName))
                    {
                        foreach (var s in symNames)
                            written.Add(s);
                    }
                }
            }

            return written;
        }

        private static void RenumberSimaticMlDocumentIds(XDocument doc)
        {
            if (doc?.Root == null) return;
            int nextId = 0;
            foreach (var el in doc.Descendants().Where(x => x.Attribute("ID") != null))
            {
                el.SetAttributeValue("ID", nextId.ToString("X"));
                nextId++;
            }
        }

        private void EnsureReferencedPlcTagsCreated(
            object plcSoftware,
            XDocument doc,
            string preferredTableName,
            string tiaVer,
            List<PlcTagEntry> knownPackageTags)
        {
            if (plcSoftware == null || doc == null) return;
            try
            {
                var knownTagMap = new Dictionary<string, PlcTagEntry>(StringComparer.OrdinalIgnoreCase);
                if (knownPackageTags != null)
                {
                    foreach (var pt in knownPackageTags)
                    {
                        if (!string.IsNullOrWhiteSpace(pt?.Name))
                            knownTagMap[pt.Name.Trim()] = pt;
                    }
                }

                // Collect all 1-component global variable names used in the document
                var referencedGlobalSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var acc in doc.Descendants().Where(e => e.Name.LocalName == "Access" && string.Equals(e.Attribute("Scope")?.Value, "GlobalVariable", StringComparison.OrdinalIgnoreCase)))
                {
                    var comps = acc.Descendants()
                        .Where(c => c.Name.LocalName == "Component")
                        .Select(c => c.Attribute("Name")?.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();

                    // If exactly 1 component, it is a global PLC tag (not DB.Member)
                    if (comps.Count == 1)
                    {
                        referencedGlobalSymbols.Add(comps[0].Trim());
                    }
                }

                if (referencedGlobalSymbols.Count == 0) return;

                // Find existing tags across all PLC tag tables
                object tagTableGroup = GetPropValue(plcSoftware, "TagTableGroup");
                object tagTables = GetPropValue(tagTableGroup, "TagTables");
                var existingTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (tagTables is IEnumerable allTablesEnum)
                {
                    foreach (var t in allTablesEnum)
                    {
                        if (GetPropValue(t, "Tags") is IEnumerable tTags)
                        {
                            foreach (var tg in tTags)
                            {
                                string tName = GetPropString(tg, "Name");
                                if (!string.IsNullOrEmpty(tName))
                                    existingTagNames.Add(tName);
                            }
                        }
                    }
                }

                var missingTags = new List<PlcTagEntry>();
                int nextInputByte = 1;
                int nextOutputByte = 1;
                int nextMarkerByte = 20;

                string targetTableName = !string.IsNullOrWhiteSpace(preferredTableName) ? preferredTableName.Trim() : "Pres_IO_Marker_Taglari";

                foreach (var sym in referencedGlobalSymbols)
                {
                    if (existingTagNames.Contains(sym)) continue;

                    // If known in package, use exact address & datatype
                    if (knownTagMap.TryGetValue(sym, out var knownEntry))
                    {
                        missingTags.Add(new PlcTagEntry
                        {
                            Name = knownEntry.Name,
                            DataType = knownEntry.DataType ?? "Bool",
                            Direction = knownEntry.Direction ?? "Memory",
                            LogicalAddress = knownEntry.LogicalAddress ?? $"%M{nextMarkerByte++}.0",
                            TableName = targetTableName,
                            Comment = knownEntry.Comment ?? $"Otomatik tanımlanan PLC Tag: {sym}"
                        });
                    }
                    else
                    {
                        // Infer logical address & direction by Siemens standard naming convention
                        string dir = "Memory";
                        string addr;
                        if (sym.StartsWith("I_", StringComparison.OrdinalIgnoreCase) || sym.StartsWith("I", StringComparison.OrdinalIgnoreCase))
                        {
                            dir = "Input";
                            addr = $"%I{nextInputByte++}.0";
                        }
                        else if (sym.StartsWith("Q_", StringComparison.OrdinalIgnoreCase) || sym.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
                        {
                            dir = "Output";
                            addr = $"%Q{nextOutputByte++}.0";
                        }
                        else
                        {
                            dir = "Memory";
                            addr = $"%M{nextMarkerByte++}.0";
                        }

                        missingTags.Add(new PlcTagEntry
                        {
                            Name = sym,
                            DataType = "Bool",
                            Direction = dir,
                            LogicalAddress = addr,
                            TableName = targetTableName,
                            Comment = $"Otomatik tanımlanan PLC Tag: {sym}"
                        });
                    }
                }

                if (missingTags.Count > 0)
                {
                    EnsurePlcTagsCreatedInternal(plcSoftware, targetTableName, missingTags, tiaVer);
                    _log($"Eksik {missingTags.Count} adet PLC Tag otomatik tanımlandı ({targetTableName}): {string.Join(", ", missingTags.Select(t => t.Name))}");
                }
            }
            catch (Exception ex)
            {
                _log($"Referans verilen taglar taranırken uyarı: {ex.Message}");
            }
        }

        private int MergeOrImportMainObXml(
            object rootBlocksComp,
            MethodInfo importMethod,
            object optOverride,
            PlcBlockEntry blk,
            string tiaVer,
            string currentBackupDir,
            string backupRootDir,
            object plcSoftware = null,
            string preferredTableName = null,
            List<PlcTagEntry> packageTags = null)
        {
            string newMainSimaticMl = blk?.SimaticMlXml ?? "";
            string adjustedXml = Regex.Replace(
                newMainSimaticMl,
                @"<Engineering\s+version=""[^""]+""\s*/>",
                $"<Engineering version=\"{tiaVer}\" />");

            MethodInfo findMethod = rootBlocksComp?.GetType().GetMethod("Find", new[] { typeof(string) });
            object existingMainOb = findMethod?.Invoke(rootBlocksComp, new object[] { "Main" });

            if (existingMainOb != null)
            {
                string tempExistXml = Path.Combine(Path.GetTempPath(), $"Main_Exist_{Guid.NewGuid():N}.xml");
                try
                {
                    // Prefer the Main.xml backed up in Step 1 BEFORE DB updates made Main [OB1] inconsistent
                    string backedUpMainXml = !string.IsNullOrWhiteSpace(currentBackupDir)
                        ? Path.Combine(currentBackupDir, "Main.xml")
                        : null;

                    if (!string.IsNullOrWhiteSpace(backedUpMainXml) && File.Exists(backedUpMainXml))
                    {
                        File.Copy(backedUpMainXml, tempExistXml, true);
                    }
                    else
                    {
                        try
                        {
                            ExportBlockToXml(existingMainOb, tempExistXml);
                        }
                        catch
                        {
                            // If Main [OB1] is inconsistent due to a DB update, compile it first then export
                            CompileBlock(existingMainOb, out _);
                            try
                            {
                                ExportBlockToXml(existingMainOb, tempExistXml);
                            }
                            catch (Exception exExport)
                            {
                                _log($"[Uyarı] Mevcut Main [OB1] tutarsız olduğu için dışa aktarılamadı ({exExport.Message}). Yeni blok olarak içe aktarılacak.");
                            }
                        }
                    }



                    if (File.Exists(tempExistXml))
                    {
                        var existDoc = XDocument.Load(tempExistXml);
                        var newDoc = XDocument.Parse(adjustedXml);

                        var existObjList = existDoc.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "SW.Blocks.OB")
                            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");

                        var newCompileUnits = newDoc.Descendants()
                            .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit")
                            .ToList();

                        if (existObjList != null && newCompileUnits.Count > 0)
                        {
                            var incomingTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var incomingWrittenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var deleteKeywords = new List<string>();

                            if (blk?.ReplaceNetworkTargets != null)
                            {
                                foreach (var rt in blk.ReplaceNetworkTargets)
                                    if (!string.IsNullOrWhiteSpace(rt)) incomingWrittenTargets.Add(rt.Trim());
                            }
                            if (blk?.DeleteNetworkKeywords != null)
                            {
                                foreach (var kw in blk.DeleteNetworkKeywords)
                                    if (!string.IsNullOrWhiteSpace(kw)) deleteKeywords.Add(kw.Trim());
                            }

                            foreach (var ncu in newCompileUnits)
                            {
                                string tText = GetCompileUnitTitle(ncu);
                                if (!string.IsNullOrWhiteSpace(tText))
                                    incomingTitles.Add(tText);

                                foreach (var wt in GetCompileUnitWrittenTargets(ncu))
                                    incomingWrittenTargets.Add(wt);
                            }

                            XElement insertBeforeAnchor = null;
                            var existingUnits = existObjList.Elements()
                                .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit")
                                .ToList();

                            // UNIVERSAL RULE: ONLY replace existing networks that match the incoming written targets or titles.
                            // ALL unrelated networks (e.g. Pres, Motor, Conveyor, Safety) are 100% PRESERVED!
                            var oldMatches = existingUnits
                                .Where(cu =>
                                {
                                    string tText = GetCompileUnitTitle(cu);
                                    if (!string.IsNullOrWhiteSpace(tText) && incomingTitles.Contains(tText))
                                        return true;

                                    foreach (var kw in deleteKeywords)
                                    {
                                        if (!string.IsNullOrWhiteSpace(tText) &&
                                            tText.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                            return true;
                                    }

                                    var cuWritten = GetCompileUnitWrittenTargets(cu);
                                    if (cuWritten.Count > 0 && cuWritten.Any(w => incomingWrittenTargets.Contains(w)))
                                        return true;

                                    return false;
                                })
                                .ToList();

                            if (oldMatches.Count > 0)
                            {
                                insertBeforeAnchor = oldMatches.Last().ElementsAfterSelf()
                                    .FirstOrDefault(e => !oldMatches.Contains(e));

                                foreach (var oldCu in oldMatches)
                                    oldCu.Remove();
                            }

                            var trailingTitle = existObjList.Elements().LastOrDefault(e =>
                                e.Name.LocalName == "MultilingualText" &&
                                string.Equals(e.Attribute("CompositionName")?.Value, "Title", StringComparison.OrdinalIgnoreCase));

                            var targetAnchor = insertBeforeAnchor ?? trailingTitle;

                            foreach (var ncu in newCompileUnits)
                            {
                                var clonedCu = new XElement(ncu);
                                if (targetAnchor != null)
                                    targetAnchor.AddBeforeSelf(clonedCu);
                                else
                                    existObjList.Add(clonedCu);
                            }

                            // Renumber all IDs sequentially across the entire merged document so TIA Portal Openness never rejects duplicate/out-of-order IDs
                            RenumberSimaticMlDocumentIds(existDoc);

                            // Ensure all global tags referenced in the final merged Main OB exist in the PLC tag tables
                            if (plcSoftware != null)
                            {
                                EnsureReferencedPlcTagsCreated(plcSoftware, existDoc, preferredTableName, tiaVer, packageTags);
                            }

                            existDoc.Save(tempExistXml);
                            InvokeBlocksImport(importMethod, rootBlocksComp, tempExistXml, optOverride);

                            int totalCount = existObjList.Elements().Count(e => e.Name.LocalName == "SW.Blocks.CompileUnit");
                            return totalCount;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Main [OB1] network birleştirme uyarısı: {ex.GetBaseException()?.Message ?? ex.Message}. Doğrudan yeni blok olarak içe aktarılıyor...");
                }
                finally
                {
                    if (File.Exists(tempExistXml)) { try { File.Delete(tempExistXml); } catch { } }
                }
            }

            // Only executed if Main OB did not exist in the project at all
            string tempXml = Path.Combine(Path.GetTempPath(), $"PkgBlock_Main_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tempXml, adjustedXml, Encoding.UTF8);
                InvokeBlocksImport(importMethod, rootBlocksComp, tempXml, optOverride);
                return blk?.Networks?.Count ?? 1;
            }
            finally
            {
                if (File.Exists(tempXml)) { try { File.Delete(tempXml); } catch { } }
            }
        }

        public bool DeleteCircuitFromProject(
            TiaPlcItem plc,
            string targetNameOrPrompt,
            Action<string, string, bool> stepCallback,
            out string deleteSummary,
            IEnumerable<string> explicitBlockNames = null,
            IEnumerable<string> explicitTagTableNames = null,
            bool? explicitClearAllMainNetworks = null)
        {
            deleteSummary = "";
            if (plc?.RawPlcSoftware == null)
                throw new ArgumentNullException(nameof(plc), "Hedef PLC bağlı değil.");

            string localSummary = "";
            bool ok = RunOnOpennessThreadAsync(() =>
            {
                string prompt = (targetNameOrPrompt ?? "").Trim();
                string lower = prompt.ToLowerInvariant();

                var targetBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var targetTagTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var targetMainWrittenSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var targetMainTitleKeywords = new List<string>();
                var targetMainNetworkIndices = new HashSet<int>();

                if (explicitBlockNames != null)
                {
                    foreach (var b in explicitBlockNames)
                    {
                        if (!string.IsNullOrWhiteSpace(b)) targetBlockNames.Add(b.Trim());
                    }
                }

                if (explicitTagTableNames != null)
                {
                    foreach (var t in explicitTagTableNames)
                    {
                        if (!string.IsNullOrWhiteSpace(t)) targetTagTables.Add(t.Trim());
                    }
                }

                // 1. Extract explicit block / DB names from prompt (e.g. FB_MotorStartStop, DB_MotorKontrol, FC_...)
                var matches = Regex.Matches(prompt, @"\b(FB_[A-Za-z0-9_]+|FC_[A-Za-z0-9_]+|DB_[A-Za-z0-9_]+)\b", RegexOptions.IgnoreCase);
                foreach (Match m in matches)
                {
                    string token = m.Groups[1].Value;
                    targetBlockNames.Add(token);
                    if (token.StartsWith("FB_", StringComparison.OrdinalIgnoreCase))
                    {
                        string suffix = token.Substring(3);
                        targetBlockNames.Add("DB_" + suffix);
                    }
                }

                object rootBlockGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");

                // Check if user or external AI (Cursor, Copilot, etc.) wants to delete all DBs
                bool isDeleteAllDbs = (lower.Contains("db") && (lower.Contains("sil") || lower.Contains("kaldır") || lower.Contains("kaldir") || lower.Contains("temizle") || lower.Contains("delete") || lower.Contains("remove"))) ||
                                      (explicitBlockNames != null && explicitBlockNames.Any(b => b == "*" || b.Equals("DB_*", StringComparison.OrdinalIgnoreCase) || b.Equals("all_dbs", StringComparison.OrdinalIgnoreCase) || b.Equals("all", StringComparison.OrdinalIgnoreCase) || b.Equals("dbs", StringComparison.OrdinalIgnoreCase)));

                if (isDeleteAllDbs && rootBlockGroup != null)
                {
                    CollectAllDbNamesRecursive(rootBlockGroup, targetBlockNames);
                    targetBlockNames.Remove("*");
                    targetBlockNames.Remove("DB_*");
                    targetBlockNames.Remove("all_dbs");
                    targetBlockNames.Remove("all");
                    targetBlockNames.Remove("dbs");
                }

                // Check if user or external AI wants to delete all PLC tag tables
                bool isDeleteAllTags = (lower.Contains("tag") && (lower.Contains("sil") || lower.Contains("kaldır") || lower.Contains("kaldir") || lower.Contains("temizle") || lower.Contains("delete") || lower.Contains("remove"))) ||
                                       (explicitTagTableNames != null && explicitTagTableNames.Any(t => t == "*" || t.Equals("all", StringComparison.OrdinalIgnoreCase) || t.Equals("all_tags", StringComparison.OrdinalIgnoreCase) || t.Equals("tag_tables", StringComparison.OrdinalIgnoreCase)));

                if (isDeleteAllTags)
                {
                    object tagTableGroup = GetPropValue(plc.RawPlcSoftware, "TagTableGroup");
                    if (tagTableGroup != null)
                    {
                        CollectAllTagTableNamesRecursive(tagTableGroup, targetTagTables);
                    }
                    targetTagTables.Remove("*");
                    targetTagTables.Remove("all");
                    targetTagTables.Remove("all_tags");
                    targetTagTables.Remove("tag_tables");
                }

                // Extract explicit tag/marker/output names if user asks to delete a specific network by tag (e.g. M_CiftEl_Aktif, Q_Hazir_Lamba)
                var tagMatches = Regex.Matches(prompt, @"\b(M_[A-Za-z0-9_]+|Q_[A-Za-z0-9_]+|T_[A-Za-z0-9_]+)\b", RegexOptions.IgnoreCase);
                foreach (Match tm in tagMatches)
                {
                    targetMainWrittenSymbols.Add(tm.Groups[1].Value);
                }

                // Extract explicit network numbers if user asks to delete e.g. "Network 6" or "6. network"
                var netNumMatches = Regex.Matches(prompt, @"(?:network\s*(\d+)|(\d+)\s*\.\s*network)", RegexOptions.IgnoreCase);
                foreach (Match nm in netNumMatches)
                {
                    string numStr = !string.IsNullOrEmpty(nm.Groups[1].Value) ? nm.Groups[1].Value : nm.Groups[2].Value;
                    if (int.TryParse(numStr, out int netIdx) && netIdx > 0)
                        targetMainNetworkIndices.Add(netIdx);
                }

                // Check if user specifically asked to delete only the Two-Hand ("çift el") network(s)
                bool isTwoHandOnlyDelete = (lower.Contains("çift el") || lower.Contains("cift el") || lower.Contains("iki el")) &&
                                           !lower.Contains("pres program");
                if (isTwoHandOnlyDelete)
                {
                    targetMainWrittenSymbols.Add("M_CiftEl_Aktif");
                    targetMainWrittenSymbols.Add("M_CiftEl_Serbest");
                    targetMainWrittenSymbols.Add("M_CiftEl_TekBasili");
                    targetMainWrittenSymbols.Add("M_CiftEl_SureAsti");
                    targetMainWrittenSymbols.Add("T_CiftEl_500ms");
                    targetMainWrittenSymbols.Add("CiftEl_Onay_Durumu");
                    targetMainWrittenSymbols.Add("CiftEl_Zaman_Hatasi");
                    targetMainTitleKeywords.Add("Çift El");
                    targetMainTitleKeywords.Add("CiftEl");
                }

                // If explicit block names were provided (e.g. only FC_PistonKontrol), do NOT expand to delete the whole circuit's DBs, Tags, and Main networks
                bool isExplicitBlockOnly = (explicitBlockNames != null && explicitBlockNames.Any()) &&
                                           (explicitTagTableNames == null || !explicitTagTableNames.Any()) &&
                                           (explicitClearAllMainNetworks != true) &&
                                           !lower.Contains("tüm") && !lower.Contains("tum") &&
                                           !lower.Contains("bütün") && !lower.Contains("butun") &&
                                           !lower.Contains("devre") && !lower.Contains("tag") && !lower.Contains("db");

                if (!isExplicitBlockOnly)
                {
                    // 2. Match known circuit families so related Instance DBs, Global DBs, and Tag Tables are also cleaned up
                    if (targetBlockNames.Contains("FB_MotorStartStop") || targetBlockNames.Contains("FC_MotorStartStop") ||
                        (lower.Contains("motor") && (lower.Contains("start") || lower.Contains("stop"))))
                    {
                        targetBlockNames.Add("FB_MotorStartStop");
                        targetBlockNames.Add("FC_MotorStartStop");
                        targetBlockNames.Add("DB_MotorStartStop");
                        targetBlockNames.Add("DB_MotorKontrol");
                        targetTagTables.Add("Motor_IO_Taglari");
                    }

                    if (targetBlockNames.Contains("FB_AnalogGiris") || lower.Contains("analog"))
                    {
                        targetBlockNames.Add("FB_AnalogGiris");
                        targetBlockNames.Add("DB_AnalogGiris");
                        targetBlockNames.Add("DB_AnalogVeri");
                        targetTagTables.Add("Analog_IO_Taglari");
                    }

                    if (targetBlockNames.Contains("FB_YildizUcgen") || lower.Contains("yıldız") || lower.Contains("yildiz") || lower.Contains("üçgen") || lower.Contains("ucgen"))
                    {
                        targetBlockNames.Add("FB_YildizUcgen");
                        targetBlockNames.Add("DB_YildizUcgen");
                        targetBlockNames.Add("DB_YildizUcgenVeri");
                        targetTagTables.Add("YildizUcgen_IO_Taglari");
                    }

                    if (targetBlockNames.Contains("FB_IleriGeriMotor") || (lower.Contains("ileri") && lower.Contains("geri")))
                    {
                        targetBlockNames.Add("FB_IleriGeriMotor");
                        targetBlockNames.Add("DB_IleriGeriMotor");
                        targetBlockNames.Add("DB_IleriGeriVeri");
                        targetTagTables.Add("IleriGeri_IO_Taglari");
                    }

                    if (targetBlockNames.Contains("FB_TankPompaKontrol") || lower.Contains("tank") || lower.Contains("pompa"))
                    {
                        targetBlockNames.Add("FB_TankPompaKontrol");
                        targetBlockNames.Add("DB_TankPompaKontrol");
                        targetBlockNames.Add("DB_TankSistem");
                        targetTagTables.Add("TankPompa_IO_Taglari");
                    }

                    if (targetBlockNames.Contains("FB_KonveyorKontrol") || lower.Contains("konveyör") || lower.Contains("konveyor"))
                    {
                        targetBlockNames.Add("FB_KonveyorKontrol");
                        targetBlockNames.Add("DB_KonveyorKontrol");
                        targetBlockNames.Add("DB_KonveyorVeri");
                        targetTagTables.Add("Konveyor_IO_Taglari");
                    }

                    if (targetBlockNames.Contains("FB_SiraliMotorlar") || lower.Contains("sıralı") || lower.Contains("sirali"))
                    {
                        targetBlockNames.Add("FB_SiraliMotorlar");
                        targetBlockNames.Add("DB_SiraliMotorlar");
                        targetBlockNames.Add("DB_SiraliMotorVeri");
                        targetTagTables.Add("SiraliMotor_IO_Taglari");
                    }

                    if (!isTwoHandOnlyDelete &&
                        (targetBlockNames.Contains("FB_PresKontrol") || targetBlockNames.Contains("FC_PresKontrol") ||
                         targetBlockNames.Contains("DB_PresKontrol") || lower.Contains("pres")))
                    {
                        targetBlockNames.Add("FB_PresKontrol");
                        targetBlockNames.Add("FC_PresKontrol");
                        targetBlockNames.Add("DB_PresKontrol_Inst");
                        targetBlockNames.Add("DB_PresKontrol");
                        targetBlockNames.Add("M_Guvenlik_Tamam");
                        targetBlockNames.Add("Q_Pres_Inis_Valfi");
                        targetBlockNames.Add("Q_Pres_Kalkis_Valfi");
                        targetBlockNames.Add("Q_Hidrolik_Pompa");
                        targetTagTables.Add("Pres_IO_Marker_Taglari");
                    }

                    // Piston / Pnömatik Silindir Devresi
                    if (targetBlockNames.Contains("FB_PistonKontrol") || targetBlockNames.Contains("FC_PistonKontrol") ||
                        lower.Contains("piston") || lower.Contains("silindir") || lower.Contains("pnömatik") || lower.Contains("pnomatik"))
                    {
                        targetBlockNames.Add("FB_PistonKontrol");
                        targetBlockNames.Add("FC_PistonKontrol");
                        targetBlockNames.Add("DB_PistonKontrol");
                        targetBlockNames.Add("DB_PistonVeri");
                        targetBlockNames.Add("DB_PistonKontrol_Main");
                        targetTagTables.Add("Piston_IO_Taglari");
                        targetMainTitleKeywords.Add("Piston");
                        targetMainTitleKeywords.Add("PistonKontrol");
                        targetMainWrittenSymbols.Add("Q_Piston_Yukari_Valfi");
                        targetMainWrittenSymbols.Add("Q_Piston_Asagi_Valfi");
                        targetMainWrittenSymbols.Add("Q_Piston_Ustte_Lamba");
                        targetMainWrittenSymbols.Add("Q_Piston_Altta_Lamba");
                        targetMainWrittenSymbols.Add("Q_Piston_Ariza_Lamba");
                        targetMainWrittenSymbols.Add("DB_PistonVeri");
                    }
                }

                // Kullanıcı Main [OB1] içerisindeki tüm networklerin temizlenmesini istediyse
                bool clearAllMainNetworks = (explicitClearAllMainNetworks.HasValue && explicitClearAllMainNetworks.Value) ||
                                            (lower.Contains("main") && (lower.Contains("tüm") || lower.Contains("tum") || lower.Contains("hepsi") || lower.Contains("program") || lower.Contains("temizle") || lower.Contains("bütün") || lower.Contains("butun"))) ||
                                            lower.Contains("tüm programı") || lower.Contains("tum programi") ||
                                            lower.Contains("bütün programı") || lower.Contains("butun programi") ||
                                            lower.Contains("bütün network") || lower.Contains("butun network") ||
                                            lower.Contains("tüm network") || lower.Contains("tum network");

                if (clearAllMainNetworks)
                {
                    targetMainTitleKeywords.Add("Network");
                }

                if (targetBlockNames.Count == 0 && targetTagTables.Count == 0 && targetMainWrittenSymbols.Count == 0 &&
                    targetMainTitleKeywords.Count == 0 && targetMainNetworkIndices.Count == 0 && !clearAllMainNetworks)
                {
                    localSummary = "Kaldırılacak blok veya network tespit edilemedi.";
                    stepCallback?.Invoke("silme", localSummary, false);
                    return false;
                }

                var deletedItems = new List<string>();
                object rootGroup = GetPropValue(plc.RawPlcSoftware, "BlockGroup");
                object rootBlocks = GetPropValue(rootGroup, "Blocks");

                // 3. First remove any targeted call networks or targeted written-symbol networks from Main [OB1]
                if (rootBlocks != null)
                {
                    MethodInfo findMethod = rootBlocks.GetType().GetMethod("Find", new[] { typeof(string) });
                    object mainOb = findMethod?.Invoke(rootBlocks, new object[] { "Main" });
                    if (mainOb == null && rootBlocks is IEnumerable rbEnum)
                    {
                        foreach (var b in rbEnum)
                        {
                            if (string.Equals(GetPropString(b, "Name"), "Main", StringComparison.OrdinalIgnoreCase) ||
                                (GetPropString(b, "GetType").IndexOf("OB", StringComparison.OrdinalIgnoreCase) >= 0 && GetPropInt(b, "Number") == 1))
                            {
                                mainOb = b;
                                break;
                            }
                        }
                    }

                    if (mainOb != null)
                    {
                        string tempMainXml = Path.Combine(Path.GetTempPath(), $"Main_Clean_{Guid.NewGuid():N}.xml");
                        try
                        {
                            Type importOptionsType = FindEngineeringType("Siemens.Engineering.ImportOptions");
                            MethodInfo importMethod = rootBlocks.GetType().GetMethod("Import", new[] { typeof(FileInfo), importOptionsType });
                            object optOverride = importOptionsType != null ? Enum.Parse(importOptionsType, "Override") : null;

                            if (clearAllMainNetworks)
                            {
                                string tiaVer = LockedVersion?.VersionKey ?? "V20";
                                string blankMainXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""{tiaVer}"" />
  <SW.Blocks.OB ID=""0"">
    <AttributeList>
      <AutoNumber>false</AutoNumber>
      <HeaderAuthor>T-IA Agent</HeaderAuthor>
      <HeaderFamily></HeaderFamily>
      <HeaderName></HeaderName>
      <HeaderVersion>0.1</HeaderVersion>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
  <Section Name=""Input"">
    <Member Name=""Initial_Call"" Datatype=""Bool"" Accessibility=""Public"" Informative=""true"" />
    <Member Name=""Remanence"" Datatype=""Bool"" Accessibility=""Public"" Informative=""true"" />
  </Section>
  <Section Name=""Temp"">
    <Member Name=""temp_dummy"" Datatype=""Bool"" />
  </Section>
  <Section Name=""Constant"" />
</Sections></Interface>
      <IsIECCheckEnabled>false</IsIECCheckEnabled>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>Main</Name>
      <Namespace />
      <Number>1</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SecondaryType>ProgramCycle</SecondaryType>
      <SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""10"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LocalVariable"" UId=""21"">
                  <Symbol>
                    <Component Name=""Initial_Call"" />
                  </Symbol>
                </Access>
                <Part Name=""Contact"" UId=""22"" />
                <Access Scope=""LocalVariable"" UId=""23"">
                  <Symbol>
                    <Component Name=""temp_dummy"" />
                  </Symbol>
                </Access>
                <Part Name=""Coil"" UId=""24"" />
              </Parts>
              <Wires>
                <Wire UId=""25"">
                  <Powerrail />
                  <NameCon UId=""22"" Name=""in"" />
                </Wire>
                <Wire UId=""26"">
                  <IdentCon UId=""21"" />
                  <NameCon UId=""22"" Name=""operand"" />
                </Wire>
                <Wire UId=""27"">
                  <NameCon UId=""22"" Name=""out"" />
                  <NameCon UId=""24"" Name=""in"" />
                </Wire>
                <Wire UId=""28"">
                  <IdentCon UId=""23"" />
                  <NameCon UId=""24"" Name=""operand"" />
                </Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""11"" CompositionName=""Title"">
            <ObjectList>
              <MultilingualTextItem ID=""12"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text>Network 1: Boş Network</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.OB>
</Document>";
                                File.WriteAllText(tempMainXml, blankMainXml, Encoding.UTF8);
                                InvokeBlocksImport(importMethod, rootBlocks, tempMainXml, optOverride);
                                deletedItems.Add("Main [OB1] (tüm networkler temizlendi)");
                                stepCallback?.Invoke("silme", "Main [OB1] içerisindeki tüm program networkleri temizlendi", true);
                            }
                            else
                            {
                                try
                                {
                                    ExportBlockToXml(mainOb, tempMainXml);
                                }
                                catch
                                {
                                    CompileBlock(mainOb, out _);
                                    ExportBlockToXml(mainOb, tempMainXml);
                                }

                                if (File.Exists(tempMainXml))
                                {
                                    var xdoc = XDocument.Load(tempMainXml);
                                    var compileUnits = xdoc.Descendants()
                                        .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit")
                                        .ToList();

                                    int removedNetworks = 0;
                                    for (int idx = 0; idx < compileUnits.Count; idx++)
                                    {
                                        var cu = compileUnits[idx];
                                        int currentNetNum = idx + 1;

                                        bool shouldRemove = targetMainNetworkIndices.Contains(currentNetNum);

                                        if (!shouldRemove && targetMainTitleKeywords.Count > 0)
                                        {
                                            string tText = GetCompileUnitTitle(cu);
                                            if (!string.IsNullOrWhiteSpace(tText) &&
                                                targetMainTitleKeywords.Any(kw => tText.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0))
                                            {
                                                shouldRemove = true;
                                            }
                                        }

                                        if (!shouldRemove && targetMainWrittenSymbols.Count > 0)
                                        {
                                            var written = GetCompileUnitWrittenTargets(cu);
                                            if (written.Any(w => targetMainWrittenSymbols.Contains(w)))
                                            {
                                                shouldRemove = true;
                                            }
                                        }

                                        if (!shouldRemove && targetBlockNames.Count > 0)
                                        {
                                            shouldRemove = cu.Descendants().Any(d =>
                                                (d.Name.LocalName == "CallInfo" && targetBlockNames.Contains(d.Attribute("Name")?.Value ?? "")) ||
                                                (d.Name.LocalName == "Instance" && d.Descendants().Any(c => c.Name.LocalName == "Component" && targetBlockNames.Contains(c.Attribute("Name")?.Value ?? ""))));

                                            if (!shouldRemove)
                                            {
                                                string cuText = cu.ToString();
                                                foreach (var tn in targetBlockNames)
                                                {
                                                    if (cuText.IndexOf($"Name=\"{tn}\"", StringComparison.OrdinalIgnoreCase) >= 0)
                                                    {
                                                        shouldRemove = true;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        if (shouldRemove)
                                        {
                                            cu.Remove();
                                            removedNetworks++;
                                        }
                                    }

                                    if (removedNetworks > 0)
                                    {
                                        if (compileUnits.Count == removedNetworks)
                                        {
                                            var objList = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SW.Blocks.OB")
                                                              ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
                                            if (objList != null && !objList.Elements().Any(e => e.Name.LocalName == "SW.Blocks.CompileUnit"))
                                            {
                                                var tempSec = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name") == "Temp");
                                                if (tempSec != null && !tempSec.Elements().Any(e => (string)e.Attribute("Name") == "temp_dummy"))
                                                {
                                                    tempSec.Add(new XElement(XName.Get("Member", "http://www.siemens.com/automation/Openness/SW/Interface/v5"),
                                                        new XAttribute("Name", "temp_dummy"),
                                                        new XAttribute("Datatype", "Bool")));
                                                }

                                                string emptyLadCuXml = @"
      <SW.Blocks.CompileUnit ID=""10"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LocalVariable"" UId=""21"">
                  <Symbol>
                    <Component Name=""Initial_Call"" />
                  </Symbol>
                </Access>
                <Part Name=""Contact"" UId=""22"" />
                <Access Scope=""LocalVariable"" UId=""23"">
                  <Symbol>
                    <Component Name=""temp_dummy"" />
                  </Symbol>
                </Access>
                <Part Name=""Coil"" UId=""24"" />
              </Parts>
              <Wires>
                <Wire UId=""25"">
                  <Powerrail />
                  <NameCon UId=""22"" Name=""in"" />
                </Wire>
                <Wire UId=""26"">
                  <IdentCon UId=""21"" />
                  <NameCon UId=""22"" Name=""operand"" />
                </Wire>
                <Wire UId=""27"">
                  <NameCon UId=""22"" Name=""out"" />
                  <NameCon UId=""24"" Name=""in"" />
                </Wire>
                <Wire UId=""28"">
                  <IdentCon UId=""23"" />
                  <NameCon UId=""24"" Name=""operand"" />
                </Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""11"" CompositionName=""Title"">
            <ObjectList>
              <MultilingualTextItem ID=""12"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text>Network 1: Boş Network</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>";
                                                var emptyCu = XElement.Parse(emptyLadCuXml);
                                                objList.Add(emptyCu);
                                            }
                                        }

                                        RenumberSimaticMlDocumentIds(xdoc);
                                        xdoc.Save(tempMainXml);
                                        InvokeBlocksImport(importMethod, rootBlocks, tempMainXml, optOverride);
                                        deletedItems.Add($"Main [OB1] ({removedNetworks} network)");
                                        stepCallback?.Invoke("silme", $"Main [OB1] içerisinden {removedNetworks} adet network kaldırıldı (diğer networkler korundu)", true);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            string msg = $"Main [OB1] network temizleme uyarısı: {ex.GetBaseException()?.Message ?? ex.Message}";
                            _log(msg);
                            stepCallback?.Invoke("silme", msg, false);
                        }
                        finally
                        {
                            if (File.Exists(tempMainXml)) { try { File.Delete(tempMainXml); } catch { } }
                        }
                    }
                }

                // 4. Delete matching DataBlocks (InstanceDB & GlobalDB) first, then FB/FC blocks across all folders
                var foundBlocks = new List<Tuple<string, object>>();
                CollectMatchingBlocksRecursive(rootGroup, targetBlockNames, foundBlocks);

                // Sort so DBs are deleted before FBs/FCs
                var orderedBlocks = foundBlocks
                    .OrderByDescending(t => t.Item1.StartsWith("DB_", StringComparison.OrdinalIgnoreCase) ||
                                            t.Item2.GetType().Name.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                foreach (var item in orderedBlocks)
                {
                    try
                    {
                        MethodInfo delMethod = item.Item2.GetType().GetMethod("Delete", Type.EmptyTypes);
                        if (delMethod != null)
                        {
                            delMethod.Invoke(item.Item2, null);
                            deletedItems.Add(item.Item1);
                            stepCallback?.Invoke("silme", $"Projeden blok silindi: {item.Item1}", true);
                        }
                    }
                    catch (Exception ex)
                    {
                        stepCallback?.Invoke("silme", $"'{item.Item1}' silinirken hata: {ex.GetBaseException()?.Message ?? ex.Message}", false);
                    }
                }

                // 5. Delete matching PLC Tag Tables if present
                if (targetTagTables.Count > 0)
                {
                    object tagTableGroup = GetPropValue(plc.RawPlcSoftware, "TagTableGroup");
                    if (tagTableGroup != null)
                    {
                        DeleteMatchingTagTablesRecursive(tagTableGroup, targetTagTables, deletedItems, stepCallback);
                        _cachedPlcTagTypes.Clear();
                    }
                }

                // 6. Compile PLC Software to synchronize project state
                bool compiledOk = CompileBlock(plc.RawPlcSoftware, out string compMsg);
                if (deletedItems.Count > 0)
                {
                    localSummary = $"Silinen bileşenler ({deletedItems.Count}): {string.Join(", ", deletedItems)} | Derleme: {compMsg}";
                    stepCallback?.Invoke("derleme", $"Silme sonrası PLC derlemesi: {compMsg}", compiledOk);
                    return true;
                }
                else
                {
                    localSummary = $"Projede '{string.Join(", ", targetBlockNames)}' isimli aktif blok bulunamadı (zaten silinmiş olabilir).";
                    stepCallback?.Invoke("silme", localSummary, false);
                    return false;
                }
            }).GetAwaiter().GetResult();

            deleteSummary = localSummary;
            return ok;
        }

        private void CollectAllDbNamesRecursive(object currentGroup, ICollection<string> dbNames)
        {
            if (currentGroup == null) return;
            if (GetPropValue(currentGroup, "Blocks") is IEnumerable blocksEnum)
            {
                foreach (var blk in blocksEnum)
                {
                    string typeName = blk.GetType().Name;
                    string lang = GetPropValue(blk, "ProgrammingLanguage")?.ToString() ?? "";
                    string name = GetPropString(blk, "Name");

                    if (typeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(lang, "DB", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(name) && !dbNames.Contains(name))
                            dbNames.Add(name);
                    }
                }
            }

            if (GetPropValue(currentGroup, "Groups") is IEnumerable subGroups)
            {
                foreach (var sub in subGroups)
                    CollectAllDbNamesRecursive(sub, dbNames);
            }
        }

        private void CollectAllTagTableNamesRecursive(object currentGroup, ICollection<string> tableNames)
        {
            if (currentGroup == null) return;
            if (GetPropValue(currentGroup, "TagTables") is IEnumerable tablesEnum)
            {
                foreach (var tbl in tablesEnum)
                {
                    string name = GetPropString(tbl, "Name");
                    if (!string.IsNullOrWhiteSpace(name) && !tableNames.Contains(name))
                        tableNames.Add(name);
                }
            }

            if (GetPropValue(currentGroup, "Groups") is IEnumerable subGroups)
            {
                foreach (var sub in subGroups)
                    CollectAllTagTableNamesRecursive(sub, tableNames);
            }
        }

        private void CollectMatchingBlocksRecursive(object currentGroup, HashSet<string> targetNames, List<Tuple<string, object>> results)
        {
            if (currentGroup == null) return;

            object blocksComp = GetPropValue(currentGroup, "Blocks");
            if (blocksComp is IEnumerable blocksEnum)
            {
                foreach (var blk in blocksEnum)
                {
                    string name = GetPropString(blk, "Name");
                    string typeName = blk.GetType().Name;
                    bool isDb = typeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        bool matches = targetNames.Contains(name) ||
                                       targetNames.Contains("*") ||
                                       (targetNames.Contains("DB_*") && isDb);

                        if (matches && !results.Any(r => r.Item1 == name))
                        {
                            results.Add(Tuple.Create(name, blk));
                        }
                    }
                }
            }

            object subGroupsComp = GetPropValue(currentGroup, "Groups");
            if (subGroupsComp is IEnumerable subGroupsEnum)
            {
                foreach (var sub in subGroupsEnum)
                {
                    CollectMatchingBlocksRecursive(sub, targetNames, results);
                }
            }
        }

        private void ClearAllTagsInTable(
            object tagTableObj,
            string tableName,
            List<string> deletedItems,
            Action<string, string, bool> stepCallback)
        {
            if (tagTableObj == null) return;
            try
            {
                object tagsComp = GetPropValue(tagTableObj, "Tags");
                if (tagsComp is IEnumerable tagsEnum)
                {
                    var tagsList = new List<object>();
                    foreach (var t in tagsEnum)
                    {
                        tagsList.Add(t);
                    }

                    int deletedTagCount = 0;
                    foreach (var t in tagsList)
                    {
                        try
                        {
                            MethodInfo tDel = t.GetType().GetMethod("Delete", Type.EmptyTypes);
                            if (tDel != null)
                            {
                                tDel.Invoke(t, null);
                                deletedTagCount++;
                            }
                        }
                        catch { }
                    }

                    if (deletedTagCount > 0)
                    {
                        deletedItems.Add($"Taglar({tableName}):{deletedTagCount}");
                        stepCallback?.Invoke("silme", $"'{tableName}' içerisindeki {deletedTagCount} adet PLC tagı silindi", true);
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"'{tableName}' tagları temizlenirken hata: {ex.Message}");
            }
        }

        private void DeleteMatchingTagTablesRecursive(
            object currentTagGroup,
            HashSet<string> targetTableNames,
            List<string> deletedItems,
            Action<string, string, bool> stepCallback)
        {
            if (currentTagGroup == null) return;

            object tablesComp = GetPropValue(currentTagGroup, "TagTables");
            if (tablesComp is IEnumerable tablesEnum)
            {
                var toDelete = new List<Tuple<string, object>>();
                foreach (var tbl in tablesEnum)
                {
                    string tName = GetPropString(tbl, "Name");
                    if (!string.IsNullOrWhiteSpace(tName) && (targetTableNames.Contains(tName) || targetTableNames.Contains("*")))
                    {
                        toDelete.Add(Tuple.Create(tName, tbl));
                    }
                }

                foreach (var item in toDelete)
                {
                    bool isDefaultTable = string.Equals(item.Item1, "Default tag table", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(item.Item1, "Standart-Variablentabelle", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(item.Item1, "Standart tag tablosu", StringComparison.OrdinalIgnoreCase);

                    if (isDefaultTable)
                    {
                        ClearAllTagsInTable(item.Item2, item.Item1, deletedItems, stepCallback);
                        continue;
                    }

                    try
                    {
                        MethodInfo delMethod = item.Item2.GetType().GetMethod("Delete", Type.EmptyTypes);
                        if (delMethod != null)
                        {
                            delMethod.Invoke(item.Item2, null);
                            deletedItems.Add($"TagTablosu:{item.Item1}");
                            stepCallback?.Invoke("silme", $"PLC Tag tablosu silindi: {item.Item1}", true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Tag tablosu '{item.Item1}' silinirken uyarı: {ex.GetBaseException()?.Message ?? ex.Message}. İçindeki taglar temizleniyor...");
                        ClearAllTagsInTable(item.Item2, item.Item1, deletedItems, stepCallback);
                    }
                }
            }

            object subGroupsComp = GetPropValue(currentTagGroup, "Groups");
            if (subGroupsComp is IEnumerable subGroupsEnum)
            {
                foreach (var sub in subGroupsEnum)
                {
                    DeleteMatchingTagTablesRecursive(sub, targetTableNames, deletedItems, stepCallback);
                }
            }
        }

        public static bool CheckOpennessPermissions(out bool isInOpennessGroup, out bool isInRegistryWhitelist, out string details)
        {
            isInOpennessGroup = false;
            isInRegistryWhitelist = false;
            var sb = new StringBuilder();

            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                isInOpennessGroup = principal.IsInRole("Siemens TIA Openness");

                if (!isInOpennessGroup && identity?.Groups != null)
                {
                    foreach (var grp in identity.Groups)
                    {
                        try
                        {
                            string grpName = grp.Translate(typeof(System.Security.Principal.NTAccount)).Value;
                            if (grpName.IndexOf("Siemens TIA Openness", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                isInOpennessGroup = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string exeName = Path.GetFileName(exePath);

            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var opennessKey = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness"))
                {
                    if (opennessKey != null)
                    {
                        foreach (var verSub in opennessKey.GetSubKeyNames())
                        {
                            using (var wlKey = opennessKey.OpenSubKey($@"{verSub}\Whitelist\{exeName}\Entry"))
                            {
                                if (wlKey != null)
                                {
                                    string regPath = wlKey.GetValue("Path")?.ToString();
                                    if (string.Equals(regPath, exePath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isInRegistryWhitelist = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            sb.AppendLine($"Kullanıcı: {Environment.UserDomainName}\\{Environment.UserName}");
            sb.AppendLine($"Siemens TIA Openness Grubu: {(isInOpennessGroup ? "✓ Üye (Aktif)" : "✗ Üye Değil (Eklemeniz Önerilir)")}");
            sb.AppendLine($"Registry Whitelist ({exeName}): {(isInRegistryWhitelist ? "✓ Kayıtlı (Onay Penceresi Çıkmaz)" : "ℹ Kayıtlı Değil (İlk bağlantıda TIA onay sorabilir)")}");
            details = sb.ToString().TrimEnd();
            return isInOpennessGroup;
        }

        public static bool AddUserToOpennessGroupAndWhitelist(out string message)
        {
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string exeName = Path.GetFileName(exePath);
                string userName = Environment.UserName;

                string hashBase64 = "";
                string lastWriteUtc = "";
                if (File.Exists(exePath))
                {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var fs = File.OpenRead(exePath))
                    {
                        hashBase64 = Convert.ToBase64String(sha.ComputeHash(fs));
                    }
                    var fi = new FileInfo(exePath);
                    lastWriteUtc = fi.LastWriteTimeUtc.ToString("yyyy/MM/dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                }

                string tempPs1 = Path.Combine(Path.GetTempPath(), $"OpennessSetup_{Guid.NewGuid():N}.ps1");
                var ps = new StringBuilder();
                ps.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
                ps.AppendLine($"net localgroup \"Siemens TIA Openness\" \"{userName}\" /add");
                if (!string.IsNullOrEmpty(hashBase64))
                {
                    ps.AppendLine("$opennessRoot = 'HKLM:\\SOFTWARE\\Siemens\\Automation\\Openness'");
                    ps.AppendLine("if (Test-Path $opennessRoot) {");
                    ps.AppendLine("  Get-ChildItem $opennessRoot | ForEach-Object {");
                    ps.AppendLine($"    $entryPath = Join-Path $_.PSPath 'Whitelist\\{exeName}\\Entry'");
                    ps.AppendLine("    New-Item -Path $entryPath -Force | Out-Null");
                    ps.AppendLine($"    Set-ItemProperty -Path $entryPath -Name 'Path' -Value '{exePath.Replace("'", "''")}'");
                    ps.AppendLine($"    Set-ItemProperty -Path $entryPath -Name 'DateModified' -Value '{lastWriteUtc}'");
                    ps.AppendLine($"    Set-ItemProperty -Path $entryPath -Name 'FileHash' -Value '{hashBase64}'");
                    ps.AppendLine("  }");
                    ps.AppendLine("}");
                }

                File.WriteAllText(tempPs1, ps.ToString(), Encoding.UTF8);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };

                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(10000);
                try { File.Delete(tempPs1); } catch { }

                message = $"'{userName}' kullanıcısı 'Siemens TIA Openness' grubuna ve Registry Whitelist'e eklendi.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Openness izin işlemi tamamlanamadı: {ex.Message}";
                return false;
            }
        }



        private Type FindEngineeringType(string fullTypeName)
        {
            if (_engAssembly != null)
            {
                Type t = _engAssembly.GetType(fullTypeName, false);
                if (t != null) return t;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullTypeName, false);
                if (t != null) return t;
            }

            return null;
        }

        private static object GetPropValue(object obj, string propName)
        {
            if (obj == null) return null;
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(propName);
                return pi?.GetValue(obj, null);
            }
            catch { return null; }
        }

        private static string GetPropString(object obj, string propName) => GetPropValue(obj, propName)?.ToString() ?? "";

        private static int GetPropInt(object obj, string propName)
        {
            object val = GetPropValue(obj, propName);
            if (val == null) return 0;
            if (val is int i) return i;
            int.TryParse(val.ToString(), out int parsed);
            return parsed;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _workQueue.CompleteAdding();
            }
        }
    }
}


