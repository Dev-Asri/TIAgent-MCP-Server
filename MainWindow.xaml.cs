using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TIAgent.Models;
using TIAgent.Services;

namespace TIAgent
{
    public partial class MainWindow : Window
    {
        private TiaOpennessService _tiaService;
        private AntigravityBridgeService _bridgeService;

        private TiaVersionInfo _selectedVersion;
        private TiaProjectItem _currentProject;
        private TiaPlcItem _selectedPlc;
        private ProjectOverviewInfo _projectOverview;

        private readonly ObservableCollection<StepLogItem> _steps = new ObservableCollection<StepLogItem>();
        private readonly ObservableCollection<PlcTagEntry> _packageTags = new ObservableCollection<PlcTagEntry>();
        private TiaPackage _pendingPackage;
        private Func<Task<bool>> _pendingApprovalAction = null;
        private string _lastUserPrompt = "";

        private DispatcherTimer _approvalCountdownTimer;
        private int _approvalRemainingSeconds = 0;
        private bool _suppressVersionSelectionChange = false;
        private string _backupRootDir;
        private readonly System.Threading.SemaphoreSlim _connectLock = new System.Threading.SemaphoreSlim(1, 1);

        public MainWindow()
        {
            InitializeComponent();
            Title = "TIAgent MCP Server - asriakdeniz@gmail.com";

            _backupRootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
            Directory.CreateDirectory(_backupRootDir);

            DgSteps.ItemsSource = _steps;
            DgPackageTags.ItemsSource = _packageTags;

            _approvalCountdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _approvalCountdownTimer.Tick += ApprovalCountdownTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Ensure single active instance so port 19520 and TIA connection are not split across duplicate windows
            try
            {
                var currentProc = Process.GetCurrentProcess();
                foreach (var other in Process.GetProcessesByName(currentProc.ProcessName))
                {
                    if (other.Id != currentProc.Id)
                    {
                        try { other.Kill(); other.WaitForExit(1500); } catch { }
                    }
                }
            }
            catch { }

            _tiaService = new TiaOpennessService(
                log: msg => AppendDetailedLog(msg),
                errorLog: err => AppendDetailedLog("[HATA] " + err)
            );

            string bridgeBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string cand1 = Path.GetFullPath(Path.Combine(bridgeBaseDir, "..", "..", ".."));
                if (File.Exists(Path.Combine(cand1, "TIAgentMCPServer.csproj")))
                {
                    bridgeBaseDir = cand1;
                }
                else
                {
                    string cand2 = Path.GetFullPath(Path.Combine(bridgeBaseDir, "..", ".."));
                    if (File.Exists(Path.Combine(cand2, "TIAgentMCPServer.csproj")))
                    {
                        bridgeBaseDir = cand2;
                    }
                }
            }
            catch { }

            _bridgeService = new AntigravityBridgeService(
                baseDir: bridgeBaseDir,
                port: 19520,
                onCommandReceived: HandleBridgeCommandAsync,
                getStatusSnapshot: BuildBridgeStatusSnapshot
            );
            _bridgeService.Start();

            AddStep("AI", "TIAgent başlatıldı (AI Köprüsü: http://localhost:19520 & Packages/Inbox)", true);
            RefreshPermissionsTab();
            LoadTiaVersions();
            LoadSavedLanguage();

            // Connect to running TIA Portal in background so UI never freezes if TIA prompts for Openness approval
            _ = ConnectOrRefreshTiaAsync();
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try { _approvalCountdownTimer?.Stop(); } catch { }
            try { _bridgeService?.Dispose(); } catch { }
            try { _tiaService?.Dispose(); } catch { }
        }

        private void AppendDetailedLog(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TIAgent_detailed.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {message}\r\n",
                    Encoding.UTF8);
            }
            catch { }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {message}\r\n";
                TxtDetailedLog.AppendText(line);
                TxtDetailedLog.ScrollToEnd();
            }));
        }

        private void AddStep(string category, string description, bool isSuccess = true, bool isPending = false)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string icon = isPending ? "⏳" : (isSuccess ? "✓" : "✗");
                string color = isPending ? "#D97706" : (isSuccess ? "#16A34A" : "#DC2626");

                var item = new StepLogItem
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Icon = icon,
                    IconColor = color,
                    Category = category,
                    JobText = $"{category} – {description}"
                };

                _steps.Add(item);
                if (DgSteps.Items.Count > 0)
                {
                    DgSteps.ScrollIntoView(item);
                }

                _bridgeService?.WriteStatusFile();
            }));
        }

        private void BtnClearSteps_Click(object sender, RoutedEventArgs e)
        {
            _steps.Clear();
            _bridgeService?.WriteStatusFile();
        }

        private void BtnCopySelectedSteps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = DgSteps.SelectedItems.Cast<StepLogItem>().ToList();
                if (selected.Count == 0)
                    selected = _steps.ToList();

                if (selected.Count > 0)
                {
                    string text = string.Join("\r\n", selected.Select(s => $"{s.Time}\t{s.Icon}\t{s.JobText}"));
                    Clipboard.SetText(text);
                }
            }
            catch { }
        }

        private void BtnCopyAllSteps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_steps.Count > 0)
                {
                    string text = string.Join("\r\n", _steps.Select(s => $"{s.Time}\t{s.Icon}\t{s.JobText}"));
                    Clipboard.SetText(text);
                }
            }
            catch { }
        }

        // =========================================================================================
        // 1. TIA PORTAL SÜRÜM TESPİTİ VE BAĞLANTI YÖNETİMİ
        // =========================================================================================
        private void LoadTiaVersions()
        {
            try
            {
                var versions = _tiaService.GetInstalledAndLibVersions();
                CmbVersion.ItemsSource = versions;

                TiaVersionInfo detected = null;

                // 1. Check command-line argument (--version V19)
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--version", StringComparison.OrdinalIgnoreCase))
                    {
                        string argVer = args[i + 1].Trim('"');
                        detected = versions.FirstOrDefault(v => string.Equals(v.VersionKey, argVer, StringComparison.OrdinalIgnoreCase));
                        break;
                    }
                }

                // 2. Check saved user version selection (selected_version.txt)
                if (detected == null)
                {
                    string selFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_version.txt");
                    if (File.Exists(selFile))
                    {
                        try
                        {
                            string savedVer = File.ReadAllText(selFile, Encoding.UTF8).Trim();
                            detected = versions.FirstOrDefault(v => string.Equals(v.VersionKey, savedVer, StringComparison.OrdinalIgnoreCase));
                        }
                        catch { }
                    }
                }

                // 3. Otherwise detect running TIA Portal or fallback to V20 / first installed
                if (detected == null)
                {
                    detected = DetectRunningTiaVersion(versions)
                            ?? versions.FirstOrDefault(v => v.VersionKey == "V20")
                            ?? versions.FirstOrDefault(v => v.VersionKey == "V19")
                            ?? versions.FirstOrDefault(v => v.VersionKey == "V15.1")
                            ?? versions.FirstOrDefault();
                }

                if (detected != null)
                {
                    _suppressVersionSelectionChange = true;
                    _selectedVersion = detected;
                    CmbVersion.SelectedItem = detected;
                    Title = $"TIAgent MCP Server - asriakdeniz@gmail.com · {detected.DisplayName}";
                    _suppressVersionSelectionChange = false;
                }
            }
            catch (Exception ex)
            {
                AppendDetailedLog("Sürüm listesi yüklenemedi: " + ex.Message);
            }
        }

        private static TiaVersionInfo DetectRunningTiaVersion(List<TiaVersionInfo> versions)
        {
            try
            {
                var procs = Process.GetProcessesByName("Siemens.Automation.Portal");
                if (procs.Length > 0)
                {
                    var runningKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in procs)
                    {
                        try
                        {
                            string path = p.MainModule?.FileName ?? "";
                            if (path.IndexOf("V21", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V21");
                            else if (path.IndexOf("V20", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V20");
                            else if (path.IndexOf("V19", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V19");
                            else if (path.IndexOf("V18", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V18");
                            else if (path.IndexOf("V17", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V17");
                            else if (path.IndexOf("V16", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V16");
                            else if (path.IndexOf("V15_1", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("V15.1", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V15.1");
                            else if (path.IndexOf("V15", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V15");
                            else if (path.IndexOf("V14", StringComparison.OrdinalIgnoreCase) >= 0) runningKeys.Add("V14 SP1");
                        }
                        catch { }
                    }

                    string[] preferenceOrder = { "V21", "V20", "V19", "V18", "V17", "V16", "V15.1", "V15", "V14 SP1" };
                    foreach (var pref in preferenceOrder)
                    {
                        if (runningKeys.Contains(pref))
                        {
                            var match = versions.FirstOrDefault(v => string.Equals(v.VersionKey, pref, StringComparison.OrdinalIgnoreCase));
                            if (match != null) return match;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void CmbVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressVersionSelectionChange) return;
            if (CmbVersion.SelectedItem is TiaVersionInfo ver)
            {
                string selFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_version.txt");

                if (_tiaService.IsVersionLocked && _tiaService.LockedVersion != null &&
                    !string.Equals(_tiaService.LockedVersion.VersionKey, ver.VersionKey, StringComparison.OrdinalIgnoreCase))
                {
                    var res = MessageBox.Show(
                        $"Mevcut oturum '{_tiaService.LockedVersion.DisplayName}' için kilitlidir.\n" +
                        $"'{ver.DisplayName}' sürümüne geçmek için uygulama yeniden başlatılsın mı?",
                        "TIA Sürüm Değişikliği",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (res == MessageBoxResult.Yes)
                    {
                        try { File.WriteAllText(selFile, ver.VersionKey, Encoding.UTF8); } catch { }
                        try { _bridgeService?.Dispose(); } catch { }
                        try { _tiaService?.Dispose(); } catch { }

                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        Process.Start(new ProcessStartInfo(exePath, $"--version \"{ver.VersionKey}\"")
                        {
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                        });
                        Application.Current.Shutdown();
                        return;
                    }

                    _suppressVersionSelectionChange = true;
                    CmbVersion.SelectedItem = _tiaService.LockedVersion;
                    _suppressVersionSelectionChange = false;
                    return;
                }

                try { File.WriteAllText(selFile, ver.VersionKey, Encoding.UTF8); } catch { }
                _selectedVersion = ver;
                Title = $"TIAgent MCP Server - asriakdeniz@gmail.com · {ver.DisplayName}";
                _ = ConnectOrRefreshTiaAsync();
            }
        }

        private async void BtnConnectTia_Click(object sender, RoutedEventArgs e)
        {
            await ConnectOrRefreshTiaAsync();
        }

        public async Task<bool> ConnectOrRefreshTiaAsync()
        {
            await _connectLock.WaitAsync();
            try
            {
                if (_selectedVersion == null)
                {
                    LoadTiaVersions();
                    if (_selectedVersion == null) return false;
                }

                // If already connected and a valid PLC is selected, verify it's still reachable
                if (_currentProject != null && _selectedPlc?.RawPlcSoftware != null)
                {
                    try
                    {
                        var testPlcs = await Task.Run(() => _tiaService.GetPlcList(_currentProject));
                        if (testPlcs != null && testPlcs.Count > 0)
                        {
                            _selectedPlc = testPlcs[0];
                            CmbPlc.ItemsSource = testPlcs;
                            CmbPlc.SelectedIndex = 0;
                            return true;
                        }
                    }
                    catch { }
                }

                BtnConnectTia.IsEnabled = false;
                try
                {
                    var ver = _selectedVersion;
                    if (ver == null) return false;
                    AddStep("TIA", $"TIA durumu ({ver.DisplayName})", true);

                    var procs = await Task.Run(() => _tiaService.GetProcessList(ver));
                    if (procs == null || procs.Count == 0)
                    {
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                        TxtTiaStatus.Text = $"Açık {ver.DisplayName} bulunamadı ('Kapalı Proje Aç' ile başlatabilirsiniz)";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                        AddStep("TIA", $"Açık {ver.DisplayName} süreci bulunamadı", false);
                        return false;
                    }

                    // Prefer processes that have an open project, and try each candidate if multiple instances exist
                    var orderedProcs = procs
                        .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.ProjectName))
                        .ToList();

                    TiaProcessItem connectedProc = null;
                    Exception lastConnectEx = null;
                    _currentProject = null;

                    foreach (var candidateProc in orderedProcs)
                    {
                        try
                        {
                            AddStep("TIA", $"TIA'ya bağlan (PID: {candidateProc.ProcessId})", true);
                            var proj = await Task.Run(() => _tiaService.ConnectToProcess(candidateProc));
                            connectedProc = candidateProc;
                            if (proj != null)
                            {
                                _currentProject = proj;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastConnectEx = ex.GetBaseException() ?? ex;
                        }
                    }

                    if (connectedProc == null && lastConnectEx != null)
                    {
                        throw lastConnectEx;
                    }

                    if (_currentProject == null)
                    {
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                        TxtTiaStatus.Text = $"Bağlı (PID: {connectedProc?.ProcessId}) — TIA Portal'da açık proje yok! Lütfen TIA'da bir proje açın veya 'Kapalı Proje Aç' tıklayın.";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                        AddStep("TIA", "TIA Portal açık ancak içinde açık proje yok", false);
                        return false;
                    }

                    var plcs = await Task.Run(() => _tiaService.GetPlcList(_currentProject));
                    CmbPlc.ItemsSource = plcs;
                    if (plcs.Count > 0)
                    {
                        _selectedPlc = plcs[0];
                        CmbPlc.SelectedIndex = 0;
                        AddStep("TIA", $"PLC bilgisi ({_selectedPlc.Name})", true);

                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                        TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc.Name}";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));

                        if (_pendingPackage != null)
                        {
                            string uiFolder = TxtTargetFolder.Text?.Trim() ?? "";
                            string effectiveFolder = !string.IsNullOrWhiteSpace(_pendingPackage.TargetFolder) ? _pendingPackage.TargetFolder : uiFolder;
                            string targetDisplay = string.IsNullOrEmpty(effectiveFolder)
                                ? $"{_selectedPlc.Name} > Program blocks"
                                : $"{_selectedPlc.Name} > Program blocks > {effectiveFolder}";

                            bool isAuto = (RbImportAuto?.IsChecked == true) || (_pendingPackage.AutoApprove == true);
                            if (isAuto)
                            {
                                BtnApprovePackage.IsEnabled = false;
                                BtnRejectPackage.IsEnabled = false;
                                await ApproveAndImportPendingPackageAsync();
                            }
                            else
                            {
                                TxtApprovalHeader.Text = $"onay – Paketi aktar: {_pendingPackage.PackageName} → {targetDisplay}  ({DateTime.Now:yyyy-MM-dd HH:mm:ss} · onay bekliyor)";
                                TxtApprovalDetails.Text = _pendingPackage.GetApprovalSummaryText(_selectedPlc.Name, uiFolder);
                                BtnApprovePackage.IsEnabled = true;
                                BtnRejectPackage.IsEnabled = true;
                            }
                        }

                        await ReadCurrentProjectOverviewAsync();
                        return true;
                    }
                    else
                    {
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                        TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC bulunamadı";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                        AddStep("TIA", $"Proje '{_currentProject.Name}' açık ancak PLC istasyonu bulunamadı", false);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    var baseEx = ex.GetBaseException() ?? ex;
                    DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    TxtTiaStatus.Text = $"Bağlantı hatası: {baseEx.Message}";
                    TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    AddStep("TIA", $"TIA bağlantı hatası: {baseEx.Message}", false);
                    return false;
                }
                finally
                {
                    BtnConnectTia.IsEnabled = true;
                    _bridgeService?.WriteStatusFile();
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async void BtnOpenProjectFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "TIA Portal Proje Dosyası Seç (.ap14 .. .ap21)",
                Filter = "TIA Portal Projeleri (*.ap*)|*.ap14;*.ap15;*.ap15_1;*.ap16;*.ap17;*.ap18;*.ap19;*.ap20;*.ap21|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            BtnOpenProjectFile.IsEnabled = false;
            try
            {
                AddStep("TIA", $"Proje dosyası açılıyor: {Path.GetFileName(dlg.FileName)}", true);
                _currentProject = await Task.Run(() => _tiaService.OpenProjectFile(_selectedVersion, dlg.FileName));
                if (_currentProject != null)
                {
                    var plcs = await Task.Run(() => _tiaService.GetPlcList(_currentProject));
                    CmbPlc.ItemsSource = plcs;
                    if (plcs.Count > 0)
                    {
                        _selectedPlc = plcs[0];
                        CmbPlc.SelectedIndex = 0;
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                        TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc.Name}";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                        await ReadCurrentProjectOverviewAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                AddStep("TIA", $"Proje açma hatası: {ex.Message}", false);
                MessageBox.Show($"Proje açılamadı:\n{ex.Message}", "Proje Açma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnOpenProjectFile.IsEnabled = true;
            }
        }

        private async void CmbPlc_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPlc.SelectedItem is TiaPlcItem plc && plc != _selectedPlc)
            {
                _selectedPlc = plc;
                if (_currentProject != null)
                {
                    TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc.Name}";
                    await ReadCurrentProjectOverviewAsync();
                }
            }
        }

        private async void RbImportMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_bridgeService == null) return;
            bool isAuto = RbImportAuto?.IsChecked == true;
            AddStep("mod", isAuto ? "İşlem modu: Onay Bekleme (Sormadan doğrudan TIA Portal'a aktarır)" : "İşlem modu: Onay Bekle (Her işlem onay çubuğunda onayınızı bekler)", true);
            _bridgeService.WriteStatusFile();

            if (isAuto)
            {
                // Otomatik modda onay çubuğu butonları ve geri sayım derhal devre dışı bırakılır
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;
                _approvalCountdownTimer.Stop();
                TxtCountdown.Text = "";

                // If user switched to Otomatik while an operation or package is waiting for approval, execute it immediately
                if (_pendingPackage != null || _pendingApprovalAction != null)
                {
                    await ApproveAndImportPendingPackageAsync();
                }
            }
        }

        private async void BtnReadProject_Click(object sender, RoutedEventArgs e)
        {
            await ReadCurrentProjectOverviewAsync();
        }

        private async Task ReadCurrentProjectOverviewAsync()
        {
            if (_currentProject == null || _selectedPlc == null) return;
            try
            {
                _projectOverview = await Task.Run(() => _tiaService.ReadProjectOverview(_currentProject, _selectedPlc));
                DgExistingTags.ItemsSource = _projectOverview.ExistingTags;
                DgExistingBlocks.ItemsSource = _projectOverview.ExistingProgramBlocks;
                AddStep("proje", $"Proje okundu ({_projectOverview.ExistingProgramBlocks.Count} blok, {_projectOverview.ExistingDataBlocks.Count} DB, {_projectOverview.ExistingTags.Count} tag)", true);
            }
            catch (Exception ex)
            {
                AddStep("proje", $"Proje okuma uyarısı: {ex.Message}", false);
            }
        }

        // =========================================================================================
        // 2. PAKET VE SİLME ONAY AKIŞI
        // =========================================================================================
        private static bool IsDeleteCommand(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            string lower = prompt.Trim().ToLowerInvariant();

            // If the user is asking to replace/update ("onun yerine", "yerine", "onay versin", "500ms", "yaz", "oluştur"), it is an update/generate command, not a pure delete
            if (lower.Contains("yerine") || lower.Contains("onay versin") || lower.Contains("500ms") || lower.Contains("500 ms") ||
                lower.Contains("yaz") || lower.Contains("oluştur") || lower.Contains("olustur"))
            {
                return false;
            }

            return lower.Contains("kaldır") ||
                   lower.Contains("kaldir") ||
                   lower.Contains("sil") ||
                   lower.Contains("delete") ||
                   lower.Contains("remove");
        }

        public async Task<bool> QueueDeletionForApprovalAsync(
            string prompt, 
            string sourceChannel,
            IEnumerable<string> explicitBlockNames = null,
            IEnumerable<string> explicitTagTableNames = null,
            bool? explicitClearAllMainNetworks = null,
            bool? forceAutoApprove = null,
            IEnumerable<string> explicitDevices = null,
            IEnumerable<string> explicitTechObjects = null)
        {
            string trimmed = (prompt ?? "").Trim();
            if (trimmed.Length == 0)
            {
                var parts = new List<string>();
                if (explicitBlockNames != null && explicitBlockNames.Any()) parts.Add("Blok/DB: " + string.Join(", ", explicitBlockNames));
                if (explicitTagTableNames != null && explicitTagTableNames.Any()) parts.Add("TagTablosu: " + string.Join(", ", explicitTagTableNames));
                if (explicitDevices != null && explicitDevices.Any()) parts.Add("Cihaz: " + string.Join(", ", explicitDevices));
                if (explicitTechObjects != null && explicitTechObjects.Any()) parts.Add("TO: " + string.Join(", ", explicitTechObjects));
                if (explicitClearAllMainNetworks == true) parts.Add("Main[OB1] temizle");
                trimmed = parts.Count > 0 ? "Silme – " + string.Join(" · ", parts) : "Silme";
            }
            // Always respect the user's UI selection in TIAgent (Otomatik vs Manuel) or force flag
            bool isAutoMode = (forceAutoApprove == true) || (RbImportAuto?.IsChecked == true);

            if (isAutoMode)
            {
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;
                _approvalCountdownTimer.Stop();
                TxtCountdown.Text = "";
                TxtApprovalHeader.Text = $"otomatik – Projeden siliniyor: {trimmed}";
                await DeleteCircuitFromTiaAsync(trimmed, sourceChannel, explicitBlockNames, explicitTagTableNames, explicitClearAllMainNetworks, explicitDevices, explicitTechObjects);
                return true;
            }

            _pendingApprovalAction = async () =>
            {
                var res = await DeleteCircuitFromTiaAsync(trimmed, sourceChannel, explicitBlockNames, explicitTagTableNames, explicitClearAllMainNetworks, explicitDevices, explicitTechObjects);
                return res.Item1;
            };

            var delPkg = new TiaPackage
            {
                PackageName = "Projeden_Silme",
                Title = $"Projeden Kaldırma / Silme Onayı",
                Description = $"Silme komutu ({sourceChannel}): {trimmed}",
                SourcePrompt = trimmed,
                SourceChannel = sourceChannel,
                Language = "LAD"
            };
            _pendingPackage = delPkg;

            // Clear preview lists
            _packageTags.Clear();
            LstPackageDbs.ItemsSource = null;
            IcVisualNetworks.ItemsSource = null;
            TxtPackageSclPreview.Text = "";
            TxtPackageXmlPreview.Text = "";

            // Activate approval bar with red/amber warning header and 599s countdown
            _approvalRemainingSeconds = 599;
            _approvalCountdownTimer.Start();

            TxtApprovalHeader.Text = $"onay – Projeden Kaldır: \"{trimmed}\"  ({DateTime.Now:yyyy-MM-dd HH:mm:ss} · onay bekliyor)";
            TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));

            var detailsSb = new StringBuilder();
            detailsSb.AppendLine($"İşlem Türü: Projeden Kaldırma / Silme ({sourceChannel})");
            detailsSb.AppendLine($"Komut: \"{trimmed}\"");
            if (explicitClearAllMainNetworks == true)
                detailsSb.AppendLine("• Main [OB1] içerisindeki tüm networkler sıfırlanacak.");
            if (explicitBlockNames != null)
            {
                var blkList = explicitBlockNames.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
                if (blkList.Count > 0)
                    detailsSb.AppendLine($"• Silinecek Bloklar: {string.Join(", ", blkList)}");
            }
            if (explicitTagTableNames != null)
            {
                var tagList = explicitTagTableNames.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (tagList.Count > 0)
                    detailsSb.AppendLine($"• Silinecek Tag Tabloları: {string.Join(", ", tagList)}");
            }
            if (explicitDevices != null && explicitDevices.Any(d => !string.IsNullOrWhiteSpace(d)))
                detailsSb.AppendLine($"• Silinecek Cihaz/Modüller: {string.Join(", ", explicitDevices.Where(d => !string.IsNullOrWhiteSpace(d)))}");
            if (explicitTechObjects != null && explicitTechObjects.Any(t => !string.IsNullOrWhiteSpace(t)))
                detailsSb.AppendLine($"• Silinecek Technology Object'ler: {string.Join(", ", explicitTechObjects.Where(t => !string.IsNullOrWhiteSpace(t)))}");
            detailsSb.AppendLine("Durum: Onayınız bekleniyor. '✓ Paketi Onayla' butonuna bastığınızda silme işlemi TIA Portal'a uygulanacaktır.");

            TxtApprovalDetails.Text = detailsSb.ToString();

            BtnApprovePackage.IsEnabled = true;
            BtnRejectPackage.IsEnabled = true;
            TxtCountdown.Text = $"Cevap verilmezse {_approvalRemainingSeconds} sn sonra iptal edilir";

            AddStep("silme", $"Silme işlemi onay çubuğuna alındı: \"{trimmed}\" (Onay bekleniyor)", true, isPending: true);
            _bridgeService?.WriteStatusFile();
            return true;
        }

        public async Task<Tuple<bool, string>> DeleteCircuitFromTiaAsync(
            string prompt, 
            string sourceChannel,
            IEnumerable<string> explicitBlockNames = null,
            IEnumerable<string> explicitTagTableNames = null,
            bool? explicitClearAllMainNetworks = null,
            IEnumerable<string> explicitDevices = null,
            IEnumerable<string> explicitTechObjects = null)
        {
            _lastUserPrompt = prompt ?? "";
            AddStep("silme", $"Silme komutu alındı ({sourceChannel}): \"{prompt}\"", true);

            // If there is a pending package in the approval bar, clear it first
            if (_pendingPackage != null)
            {
                _approvalCountdownTimer.Stop();
                _pendingPackage = null;
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;
                TxtCountdown.Text = "";
            }

            if (_selectedPlc == null)
            {
                bool connected = await ConnectOrRefreshTiaAsync();
                if (!connected || _selectedPlc == null)
                {
                    string err = "TIA Portal'da açık bir PLC projesi bulunamadı!";
                    AddStep("silme", err, false);
                    TxtApprovalHeader.Text = $"✗ hata – {err}";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    return Tuple.Create(false, err);
                }
            }

            try
            {
                TxtApprovalHeader.Text = $"⏳ siliniyor – Projeden kaldırılıyor: {prompt}";
                TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));

                string delSummary = "";
                bool hwRequested = (explicitDevices != null && explicitDevices.Any(d => !string.IsNullOrWhiteSpace(d))) ||
                                   (explicitTechObjects != null && explicitTechObjects.Any(t => !string.IsNullOrWhiteSpace(t)));
                bool swRequested = (explicitBlockNames != null && explicitBlockNames.Any(b => !string.IsNullOrWhiteSpace(b))) ||
                                   (explicitTagTableNames != null && explicitTagTableNames.Any(t => !string.IsNullOrWhiteSpace(t))) ||
                                   explicitClearAllMainNetworks == true;

                bool hwOk = false;
                string hwSummary = "";
                if (hwRequested)
                {
                    hwOk = await Task.Run(() =>
                        _tiaService.DeleteHardwareAndTechnologyObjects(
                            _selectedPlc,
                            explicitDevices,
                            explicitTechObjects,
                            (cat, msg, sOk) => AddStep(cat, msg, sOk),
                            out hwSummary));
                }

                bool ok = hwOk;
                // Donanım silme talebi tek başına geldiyse yazılım tarafına (prompt anahtar kelime eşleşmesine) dokunma
                if (!hwRequested || swRequested)
                {
                    bool swOk = await Task.Run(() =>
                        _tiaService.DeleteCircuitFromProject(
                            _selectedPlc,
                            prompt,
                            (cat, msg, sOk) => AddStep(cat, msg, sOk),
                            out delSummary,
                            explicitBlockNames,
                            explicitTagTableNames,
                            explicitClearAllMainNetworks));
                    ok = ok || swOk;
                }
                if (hwRequested)
                    delSummary = string.IsNullOrWhiteSpace(delSummary) ? hwSummary : $"{delSummary} · {hwSummary}";

                if (ok)
                {
                    TxtApprovalHeader.Text = $"✓ kaldırıldı – {delSummary}";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    TxtApprovalDetails.Text = $"Son İşlem: Projeden Kaldırma ({sourceChannel})\r\nKomut: {prompt}\r\nSonuç: {delSummary}";
                    TxtCountdown.Text = "Proje güncellendi ve derlendi";
                }
                else
                {
                    TxtApprovalHeader.Text = $"ℹ bilgi – {delSummary}";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                    TxtApprovalDetails.Text = $"Komut: {prompt}\r\nDurum: {delSummary}";
                    TxtCountdown.Text = "";
                }

                await ReadCurrentProjectOverviewAsync();
                _bridgeService?.WriteStatusFile();
                return Tuple.Create(ok, delSummary);
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException() ?? ex;
                string err = baseEx.Message;
                AddStep("silme", $"Silme hatası: {err}", false);
                TxtApprovalHeader.Text = $"✗ silme hatası – {err}";
                TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                _bridgeService?.WriteStatusFile();
                return Tuple.Create(false, err);
            }
        }

        public async Task<TiaPackage> GenerateAndQueuePackageAsync(string prompt, string sourceChannel, string explicitLang = null, string explicitFolder = null, bool? forceAutoApprove = null)
        {
            try
            {
                _lastUserPrompt = prompt ?? "";
                string trimmed = (prompt ?? "").Trim();
                string lower = trimmed.ToLowerInvariant();

                // Interactive command routing directly from the prompt box or voice
                if (lower == "onayla" || lower == "paketi onayla")
                {
                    await ApproveAndImportPendingPackageAsync();
                    return _pendingPackage;
                }
                if (lower == "reddet" || lower == "paketi reddet" || lower == "iptal")
                {
                    RejectPendingPackage($"{sourceChannel} ile reddedildi");
                    return null;
                }
                if (lower == "tia ya bağlan" || lower == "bağlan" || lower == "yenile")
                {
                    await ConnectOrRefreshTiaAsync();
                    return null;
                }

                // If it is a delete command:
                if (IsDeleteCommand(trimmed))
                {
                    await QueueDeletionForApprovalAsync(trimmed, sourceChannel, forceAutoApprove: forceAutoApprove);
                    return null;
                }

                // If called from AI with raw SCL/instruction:
                string curVer = _tiaService?.LockedVersion?.VersionKey ?? _selectedVersion?.VersionKey ?? "V20";
                var pkg = await Task.Run(() => PlcPackageGenerator.GenerateFromPrompt(prompt, explicitLang, explicitFolder, curVer, sourceChannel));

                if (pkg == null)
                {
                    _approvalCountdownTimer.Stop();
                    _pendingPackage = null;
                    BtnApprovePackage.IsEnabled = false;
                    BtnRejectPackage.IsEnabled = false;
                    TxtCountdown.Text = "Paket boş";
                    TxtApprovalHeader.Text = $"⚠ Paket Çözümlenemedi – \"{trimmed}\"";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                    TxtApprovalDetails.Text = $"Komut: {trimmed}\r\nDurum: Pakette derlenebilir SCL veya hazır blok bulunamadı.";
                    AddStep("AI", $"Özel paket çözümlenemedi: \"{trimmed}\"", false);
                    _bridgeService?.WriteStatusFile();
                    return null;
                }

                await QueuePackageForApprovalOrAutoImportAsync(pkg, forceAutoApprove);
                return pkg;
            }
            catch (Exception ex)
            {
                AddStep("paket", $"Paket üretilemedi: {ex.Message}", false);
                return null;
            }
        }

        public async Task<bool> QueuePackageForApprovalOrAutoImportAsync(TiaPackage pkg, bool? forceAutoApprove = null)
        {
            if (pkg == null) return false;
            if (forceAutoApprove.HasValue) pkg.AutoApprove = forceAutoApprove.Value;

            _pendingPackage = pkg;

            // Update Preview Tab UI
            _packageTags.Clear();
            if (pkg.Tags != null)
            {
                foreach (var t in pkg.Tags) _packageTags.Add(t);
            }

            LstPackageDbs.ItemsSource = pkg.DataBlocks;

            var primaryBlock = pkg.Blocks?.FirstOrDefault(b => !b.IsMainObCall) ?? pkg.Blocks?.FirstOrDefault();
            IcVisualNetworks.ItemsSource = primaryBlock?.Networks;
            TxtPackageSclPreview.Text = primaryBlock?.SclCode ?? "";
            TxtPackageXmlPreview.Text = primaryBlock?.SimaticMlXml ?? "";

            int fileCount = (pkg.Blocks?.Count ?? 0) + (pkg.DataBlocks?.Count ?? 0) + ((pkg.Tags?.Count ?? 0) > 0 ? 1 : 0);
            string hwInfo = pkg.HasHardwareContent()
                ? $", {pkg.HardwareDevices?.Count ?? 0} cihaz, {pkg.PlcModules?.Count ?? 0} PLC modülü, {pkg.TechnologyObjects?.Count ?? 0} TO"
                : "";
            AddStep("paket", $"{pkg.PackageName} ({pkg.Language}) hazır – {pkg.Blocks?.Count ?? 0} blok, {pkg.DataBlocks?.Count ?? 0} DB, {pkg.Tags?.Count ?? 0} I/O tag{hwInfo} ({fileCount} bileşen)", true);

            string plcName = _selectedPlc?.Name ?? "PLC";
            string uiFolder = TxtTargetFolder.Text?.Trim() ?? "";
            string effectiveFolder = !string.IsNullOrWhiteSpace(pkg.TargetFolder) ? pkg.TargetFolder : uiFolder;
            string targetDisplay = string.IsNullOrEmpty(effectiveFolder)
                ? $"{plcName} > Program blocks"
                : $"{plcName} > Program blocks > {effectiveFolder}";

            // Always respect the user's UI selection in TIAgent (Otomatik vs Manuel) or package flag
            bool isAutoMode = (forceAutoApprove == true) || (RbImportAuto?.IsChecked == true) || (pkg?.AutoApprove == true);
            if (isAutoMode)
            {
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;
                _approvalCountdownTimer.Stop();
                TxtCountdown.Text = "";
                TxtApprovalHeader.Text = $"otomatik – Paket aktarılıyor: {pkg.PackageName} → {targetDisplay}";
                TxtApprovalDetails.Text = pkg.GetApprovalSummaryText(plcName, uiFolder);
                return await ApproveAndImportPendingPackageAsync();
            }
            else
            {
                // Manual approval mode (with 599s countdown timer, just like the screenshot!)
                _approvalRemainingSeconds = 599;
                _approvalCountdownTimer.Start();

                TxtApprovalHeader.Text = $"onay – Paketi aktar: {pkg.PackageName} → {targetDisplay}  ({DateTime.Now:yyyy-MM-dd HH:mm:ss} · onay bekliyor)";
                TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtApprovalDetails.Text = pkg.GetApprovalSummaryText(plcName, uiFolder);

                BtnApprovePackage.IsEnabled = true;
                BtnRejectPackage.IsEnabled = true;
                TxtCountdown.Text = $"Cevap verilmezse {_approvalRemainingSeconds} sn sonra reddedilir";

                _bridgeService?.WriteStatusFile();
                return true;
            }
        }

        private void ApprovalCountdownTimer_Tick(object sender, EventArgs e)
        {
            if (RbImportAuto?.IsChecked == true || _pendingPackage == null)
            {
                _approvalCountdownTimer.Stop();
                TxtCountdown.Text = "";
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;
                return;
            }

            _approvalRemainingSeconds--;
            if (_approvalRemainingSeconds <= 0)
            {
                RejectPendingPackage("Süre dolduğu için otomatik reddedildi (600 sn)");
            }
            else
            {
                TxtCountdown.Text = $"Cevap verilmezse {_approvalRemainingSeconds} sn sonra reddedilir";
            }
        }

        private async void BtnApprovePackage_Click(object sender, RoutedEventArgs e)
        {
            await ApproveAndImportPendingPackageAsync();
        }

        public async Task<bool> ApproveAndImportPendingPackageAsync()
        {
            if (_pendingApprovalAction != null)
            {
                _approvalCountdownTimer.Stop();
                TxtCountdown.Text = "İşlem uygulanıyor...";
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;

                var action = _pendingApprovalAction;
                _pendingApprovalAction = null;
                _pendingPackage = null;
                bool actionOk = await action();
                return actionOk;
            }

            if (_pendingPackage == null) return false;

            bool isAutoMode = (RbImportAuto?.IsChecked == true) || (_pendingPackage?.AutoApprove == true);

            _approvalCountdownTimer.Stop();
            TxtCountdown.Text = "Aktarılıyor ve derleniyor...";
            BtnApprovePackage.IsEnabled = false;
            BtnRejectPackage.IsEnabled = false;

            var pkg = _pendingPackage;

            // Ensure TIA is connected
            if (_selectedPlc == null)
            {
                bool connected = await ConnectOrRefreshTiaAsync();
                if (!connected || _selectedPlc == null)
                {
                    AddStep("aktarım", $"TIA Portal'da açık bir PLC projesi bulunamadı! Lütfen önce TIA Portal'da bir proje açın ve 'TIA'ya bağlan / yenile' tıklayın.", false);
                    TxtApprovalHeader.Text = $"hata – TIA Portal'da açık PLC projesi yok ({pkg.PackageName} bekliyor)";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));

                    if (!isAutoMode)
                    {
                        BtnApprovePackage.IsEnabled = true;
                        BtnRejectPackage.IsEnabled = true;
                        TxtCountdown.Text = "TIA bağlantısı bekleniyor";
                    }
                    else
                    {
                        BtnApprovePackage.IsEnabled = false;
                        BtnRejectPackage.IsEnabled = false;
                        TxtCountdown.Text = "TIA bağlantısı bekleniyor (Otomatik mod)";
                        _pendingPackage = null;
                    }
                    return false;
                }
            }

            // Sync any user edits from DgPackageTags into pkg.Tags
            pkg.Tags = _packageTags.ToList();

            string uiFolder = TxtTargetFolder.Text?.Trim() ?? "";
            string plcName = _selectedPlc.Name;
            AddStep("onay", $"Paket onaylandı: {pkg.PackageName} → {plcName} > Program blocks", true);

            try
            {
                string compileMsg = "";
                bool ok = await Task.Run(() =>
                    _tiaService.ImportFullPackage(
                        _selectedPlc,
                        pkg,
                        uiFolder,
                        _backupRootDir,
                        (cat, msg, sOk) => AddStep(cat, msg, sOk),
                        out compileMsg));

                _pendingPackage = null;
                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;

                if (ok)
                {
                    TxtApprovalHeader.Text = $"✓ tamamlandı – {pkg.PackageName} başarıyla TIA Portal'a aktarıldı ve derlendi ({compileMsg})";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    TxtCountdown.Text = "Aktarım ve derleme başarılı";
                    await ReadCurrentProjectOverviewAsync();
                    return true;
                }
                else
                {
                    TxtApprovalHeader.Text = $"⚠ aktarıldı – {pkg.PackageName} aktarıldı ({compileMsg})";
                    TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                    TxtCountdown.Text = compileMsg;
                    await ReadCurrentProjectOverviewAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException() ?? ex;
                AddStep("aktarım", $"{pkg.PackageName} aktarım hatası: {baseEx.Message}", false);
                TxtApprovalHeader.Text = $"✗ hata – {pkg.PackageName} aktarılamadı: {baseEx.Message}";
                TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));

                if (!isAutoMode)
                {
                    BtnApprovePackage.IsEnabled = true;
                    BtnRejectPackage.IsEnabled = true;
                    TxtCountdown.Text = "Tekrar denemek için Onayla'ya tıklayın";
                }
                else
                {
                    // Otomatik modda asla onay istenmez ve Onayla butonu asla aktif edilmez
                    BtnApprovePackage.IsEnabled = false;
                    BtnRejectPackage.IsEnabled = false;
                    TxtCountdown.Text = $"Otomatik mod – Hata: {baseEx.Message}";
                    _pendingPackage = null;
                }
                return false;
            }
            finally
            {
                _bridgeService?.WriteStatusFile();
            }
        }

        private void BtnRejectPackage_Click(object sender, RoutedEventArgs e)
        {
            RejectPendingPackage("Kullanıcı tarafından reddedildi");
        }

        public void RejectPendingPackage(string reason)
        {
            _approvalCountdownTimer.Stop();
            _pendingApprovalAction = null;
            if (_pendingPackage != null)
            {
                AddStep("onay", $"İşlem reddedildi: {_pendingPackage.PackageName} ({reason})", false);
                TxtApprovalHeader.Text = $"✗ reddedildi – {_pendingPackage.PackageName} ({reason})";
                TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                _pendingPackage = null;
            }
            BtnApprovePackage.IsEnabled = false;
            BtnRejectPackage.IsEnabled = false;
            TxtCountdown.Text = "";
            _bridgeService?.WriteStatusFile();
        }

        private void BtnShowPreviewTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabs.SelectedIndex = 1;
        }

        // =========================================================================================
        // 3. OPENNESS GRUBU, YEDEK KLASÖRÜ VE İZİNLER
        // =========================================================================================
        private void RefreshPermissionsTab()
        {
            TiaOpennessService.CheckOpennessPermissions(out _, out _, out string details);
            TxtPermissionsInfo.Text = details +
                $"\r\nAI HTTP Köprüsü: http://localhost:19520/api/status" +
                $"\r\nAI Paket Klasörü: {_bridgeService?.InboxFolder}" +
                $"\r\nOtomatik Yedek Klasörü: {_backupRootDir}";
        }

        private void BtnRefreshPermissions_Click(object sender, RoutedEventArgs e)
        {
            RefreshPermissionsTab();
        }

        private void BtnAddOpennessGroup_Click(object sender, RoutedEventArgs e)
        {
            bool ok = TiaOpennessService.AddUserToOpennessGroupAndWhitelist(out string msg);
            AddStep("izin", msg, ok);
            RefreshPermissionsTab();
            MessageBox.Show(msg, "Openness Grubu ve Whitelist", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void BtnBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_backupRootDir);
                Process.Start("explorer.exe", _backupRootDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Yedek klasörü açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =========================================================================================
        // 4. ANTIGRAVITY CANLI KÖPRÜ (HTTP REST + HOT-FOLDER) İŞLEYİCİSİ
        // =========================================================================================
        private BridgeStatusSnapshot BuildBridgeStatusSnapshot()
        {
            return Dispatcher.Invoke(() =>
            {
                var dbs = _projectOverview?.ExistingDataBlocks ?? new List<string>();
                var blocks = _projectOverview?.ExistingProgramBlocks?.Select(b => (object)new
                {
                    name = b.Name,
                    type = b.BlockType,
                    number = b.BlockNumber,
                    language = b.ProgrammingLanguage,
                    path = b.GroupPath
                }).ToList() ?? new List<object>();

                var tagTables = _projectOverview?.ExistingTags?
                    .Select(t => t.TableName)
                    .Where(tn => !string.IsNullOrWhiteSpace(tn))
                    .Distinct()
                    .ToList() ?? new List<string>();

                var tags = _projectOverview?.ExistingTags?.Select(t => (object)new
                {
                    name = t.Name,
                    address = t.LogicalAddress,
                    dataType = t.DataType,
                    table = t.TableName,
                    comment = t.Comment
                }).ToList() ?? new List<object>();

                return new BridgeStatusSnapshot
                {
                    ok = true,
                    timestamp = DateTime.Now.ToString("o"),
                    tiaVersion = _tiaService?.LockedVersion?.VersionKey ?? _selectedVersion?.VersionKey ?? "V20",
                    isTiaConnected = _currentProject != null && _selectedPlc != null,
                    projectName = _currentProject?.Name ?? "",
                    projectPath = _currentProject?.FilePath ?? "",
                    plcName = _selectedPlc?.Name ?? "",
                    importMode = RbImportAuto.IsChecked == true ? "Otomatik" : "Manuel",
                    targetFolder = TxtTargetFolder.Text?.Trim() ?? "",
                    lastUserPrompt = _lastUserPrompt ?? "",
                    pendingPackage = _pendingPackage != null ? new
                    {
                        packageName = _pendingPackage.PackageName,
                        title = _pendingPackage.Title,
                        language = _pendingPackage.Language,
                        tagCount = _pendingPackage.Tags?.Count ?? 0,
                        dbCount = _pendingPackage.DataBlocks?.Count ?? 0,
                        blockCount = _pendingPackage.Blocks?.Count ?? 0,
                        remainingSeconds = _approvalRemainingSeconds
                    } : null,
                    existingDbsCount = dbs.Count,
                    existingBlocksCount = blocks.Count,
                    existingTagsCount = tags.Count,
                    dataBlocks = dbs,
                    programBlocks = blocks,
                    tagTables = tagTables,
                    tags = tags,
                    recentSteps = _steps.Skip(Math.Max(0, _steps.Count - 12)).Select(s => $"{s.Time} {s.Icon} {s.JobText}").ToList()
                };
            });
        }

        private async Task<object> HandleBridgeCommandAsync(BridgeCommandRequest req)
        {
            var tcs = new TaskCompletionSource<object>();

            await Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    string action = (req?.Action ?? "generate").Trim().ToLowerInvariant();
                    string sourceChannel = !string.IsNullOrWhiteSpace(req?.SourceChannel) ? req.SourceChannel : "AI";

                    if (action == "connect")
                    {
                        bool connected = await ConnectOrRefreshTiaAsync();
                        tcs.TrySetResult(new { ok = connected, status = BuildBridgeStatusSnapshot() });
                        return;
                    }

                    if (action == "project" || action == "refresh" || action == "dbs" || action == "blocks" || action == "tags")
                    {
                        await ReadCurrentProjectOverviewAsync();
                        tcs.TrySetResult(BuildBridgeStatusSnapshot());
                        return;
                    }

                    if (action == "approve")
                    {
                        bool approved = await ApproveAndImportPendingPackageAsync();
                        tcs.TrySetResult(new { ok = approved, status = BuildBridgeStatusSnapshot() });
                        return;
                    }

                    if (action == "reject")
                    {
                        RejectPendingPackage($"{sourceChannel} üzerinden reddedildi");
                        tcs.TrySetResult(new { ok = true, status = BuildBridgeStatusSnapshot() });
                        return;
                    }

                    string prompt = !string.IsNullOrWhiteSpace(req?.CustomSclCode) ? req.CustomSclCode : req?.Prompt;

                    if (action == "probe_module" || action == "probemodule")
                    {
                        if (_selectedPlc == null) await ConnectOrRefreshTiaAsync();
                        object probeRes = _selectedPlc == null
                            ? (object)new { ok = false, error = "PLC yok" }
                            : await Task.Run(() => _tiaService.ProbeModulePlugging(_selectedPlc, prompt));
                        tcs.TrySetResult(probeRes);
                        return;
                    }

                    if (action == "hardware" || action == "gsd")
                    {
                        if (_selectedPlc == null) await ConnectOrRefreshTiaAsync();
                        object hwResult;
                        if (action == "hardware")
                        {
                            hwResult = _selectedPlc == null
                                ? new { ok = false, error = "TIA Portal'a bağlı PLC yok." }
                                : await Task.Run(() => _tiaService.GetHardwareOverview(_selectedPlc));
                            AddStep("donanım", $"Donanım listesi {sourceChannel}'ye gönderildi", true);
                        }
                        else
                        {
                            hwResult = await Task.Run(() => _tiaService.ListInstalledGsd(_selectedPlc, prompt));
                            AddStep("donanım", $"Yüklü GSDML listesi gönderildi{(string.IsNullOrWhiteSpace(prompt) ? "" : $" (filtre: {prompt})")}", true);
                        }
                        tcs.TrySetResult(hwResult);
                        return;
                    }

                    bool hasHwDelete = (req?.DevicesToDelete?.Count ?? 0) > 0 || (req?.TechnologyObjectsToDelete?.Count ?? 0) > 0;
                    bool hasSwDelete = (req?.BlocksToDelete?.Count ?? 0) > 0 || (req?.TagTablesToDelete?.Count ?? 0) > 0 || (req?.ClearMainNetworks == true);

                    if (action == "delete" || hasHwDelete || hasSwDelete || (action != "package" && IsDeleteCommand(prompt)))
                    {
                        bool isAutoNow = RbImportAuto.IsChecked == true;
                        bool queued = await QueueDeletionForApprovalAsync(
                            prompt, 
                            sourceChannel,
                            req?.BlocksToDelete,
                            req?.TagTablesToDelete,
                            req?.ClearMainNetworks,
                            req?.AutoApprove,
                            req?.DevicesToDelete,
                            req?.TechnologyObjectsToDelete);

                        tcs.TrySetResult(new
                        {
                            ok = queued,
                            action = "delete",
                            summary = isAutoNow
                                ? "Silme işlemi otomatik olarak TIA Portal'a uygulandı"
                                : "Silme işlemi onay çubuğuna alındı (Onay bekleniyor)",
                            status = BuildBridgeStatusSnapshot()
                        });
                        return;
                    }

                    if (action == "package" && req.CustomPackage != null)
                    {
                        AddStep("AI", $"{sourceChannel} özel paketi alındı: {req.CustomPackage.PackageName}", true);
                        bool queued = await QueuePackageForApprovalOrAutoImportAsync(req.CustomPackage, req.AutoApprove);
                        tcs.TrySetResult(new { ok = queued, packageName = req.CustomPackage.PackageName, status = BuildBridgeStatusSnapshot() });
                        return;
                    }

                    // Default: "generate" from prompt or CustomSclCode
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        tcs.TrySetResult(new { ok = false, error = "Komut veya paket içeriği boş.", status = BuildBridgeStatusSnapshot() });
                        return;
                    }

                    AddStep("AI", $"{sourceChannel} komutu alındı: \"{prompt}\"", true);
                    var pkg = await GenerateAndQueuePackageAsync(prompt, sourceChannel, req.Language, req.TargetFolder, req.AutoApprove);
                    tcs.TrySetResult(new
                    {
                        ok = pkg != null,
                        packageName = pkg?.PackageName,
                        title = pkg?.Title,
                        language = pkg?.Language,
                        tags = pkg?.Tags?.Select(t => $"{t.Name} ({t.LogicalAddress} : {t.DataType})").ToList(),
                        dbs = pkg?.DataBlocks?.Select(d => d.ToString()).ToList(),
                        blocks = pkg?.Blocks?.Select(b => b.ToString()).ToList(),
                        status = BuildBridgeStatusSnapshot()
                    });
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(new { ok = false, error = ex.Message });
                }
            }));

            return await tcs.Task;
        }

        private HelpWindow _helpWindow;

        private void BtnHeaderHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_helpWindow == null || !_helpWindow.IsLoaded)
                {
                    _helpWindow = new HelpWindow(_currentLanguage)
                    {
                        Owner = this
                    };
                    _helpWindow.Closed += (s, args) => _helpWindow = null;
                    _helpWindow.Show();
                }
                else
                {
                    if (_helpWindow.WindowState == WindowState.Minimized)
                        _helpWindow.WindowState = WindowState.Normal;
                    _helpWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                AddStep("yardım", $"Yardım penceresi açılamadı: {ex.Message}", false);
            }
        }

        private string _currentLanguage = "tr";

        private void LoadSavedLanguage()
        {
            try
            {
                string langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_language.txt");
                if (File.Exists(langFile))
                {
                    string saved = File.ReadAllText(langFile, Encoding.UTF8).Trim();
                    if (string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase))
                    {
                        if (CmbLanguage != null) CmbLanguage.SelectedIndex = 1;
                        ApplyLanguage("en");
                        return;
                    }
                }
            }
            catch { }
            ApplyLanguage("tr");
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLanguage?.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag as string;
                string lang = !string.IsNullOrEmpty(tag) ? tag : (item.Content?.ToString()?.Contains("English") == true ? "en" : "tr");
                ApplyLanguage(lang);
            }
        }

        private void ApplyLanguage(string lang)
        {
            _currentLanguage = lang;
            bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

            try
            {
                string langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_language.txt");
                File.WriteAllText(langFile, lang, Encoding.UTF8);
            }
            catch { }

            // Top Header
            if (LblTiaVersion != null) LblTiaVersion.Text = isEn ? "TIA Version:" : "TIA Sürümü:";
            if (BtnHeaderHelp != null) BtnHeaderHelp.Content = isEn ? "? Help & About" : "? Yardım & Hakkında";
            if (LblLanguage != null) LblLanguage.Text = isEn ? "Language:" : "Dil:";

            // Toolbar Labels & Buttons
            if (LblAiBridge != null) LblAiBridge.Text = isEn ? "AI Bridge:" : "AI Köprü:";
            if (LblTia != null) LblTia.Text = "TIA:";
            if (BtnConnectTia != null) BtnConnectTia.Content = isEn ? "Connect / Refresh TIA" : "TIA'ya bağlan / yenile";
            if (LblPlc != null) LblPlc.Text = "PLC:";
            if (LblFolder != null) LblFolder.Text = isEn ? "Folder:" : "Klasör:";
            if (TxtTargetFolder != null)
                TxtTargetFolder.ToolTip = isEn
                    ? "If empty, added to root of Program blocks. Nested folder example: Motors/Line1"
                    : "Boş bırakılırsa Program blocks köküne eklenir. İç içe klasör örneği: Motorlar/Hat1";

            if (BtnOpenProjectFile != null) BtnOpenProjectFile.Content = isEn ? "Open Offline Project (.ap*)" : "Kapalı Proje Aç (.ap*)";
            if (BtnAddOpennessGroup != null) BtnAddOpennessGroup.Content = isEn ? "Add to Openness group" : "Openness grubuna ekle";
            if (BtnBackupFolder != null) BtnBackupFolder.Content = isEn ? "Backup folder" : "Yedek klasörü";
            if (BtnReadProject != null) BtnReadProject.Content = isEn ? "Read Project" : "Projeyi Oku";

            // Mode
            if (RbImportManual != null) RbImportManual.Content = isEn ? "Wait for Approval" : "Onay Bekle";
            if (RbImportAuto != null) RbImportAuto.Content = isEn ? "No Approval" : "Onay Bekleme";

            // Main Tabs
            if (TabSteps != null) TabSteps.Header = isEn ? "  Steps  " : "  Adımlar  ";
            if (TabPreview != null) TabPreview.Header = isEn ? "  Package & Ladder Preview  " : "  Paket & Ladder Önizleme  ";
            if (TabExplorer != null) TabExplorer.Header = isEn ? "  Project Explorer  " : "  Proje Gezgini  ";
            if (TabLog != null) TabLog.Header = isEn ? "  Log  " : "  Kayıt  ";
            if (TabPermissions != null) TabPermissions.Header = isEn ? "  Permissions  " : "  İzinler  ";

            // Steps Toolbar
            if (BtnClearSteps != null) BtnClearSteps.Content = isEn ? "🗑 Clear Steps" : "🗑 Adımları Temizle";
            if (BtnCopySelectedSteps != null) BtnCopySelectedSteps.Content = isEn ? "📋 Copy Selected" : "📋 Seçileni Kopyala";
            if (BtnCopyAllSteps != null) BtnCopyAllSteps.Content = isEn ? "📋 Copy All" : "📋 Tümünü Kopyala";

            // Preview Sub-tabs & Labels
            if (LblPackageTags != null) LblPackageTags.Text = isEn ? "1. PLC I/O Tags to Create (Editable)" : "1. Oluşturulacak PLC I/O Tagları (Düzenlenebilir)";
            if (LblPackageDbs != null) LblPackageDbs.Text = isEn ? "2. Data Blocks to Create (DB)" : "2. Oluşturulacak Veri Blokları (DB)";
            if (TabSubVisual != null) TabSubVisual.Header = isEn ? "Graphical Ladder Networks" : "Grafiksel Ladder Networkler";
            if (TabSubScl != null) TabSubScl.Header = isEn ? "SCL Source Code" : "SCL Kaynak Kodu";
            if (TabSubXml != null) TabSubXml.Header = isEn ? "SimaticML XML (TIA Import)" : "SimaticML XML (TIA Import)";

            // Project Explorer
            if (GrpExistingTags != null) GrpExistingTags.Header = isEn ? "Existing Tags in PLC" : "PLC'deki Mevcut Taglar";
            if (GrpExistingBlocks != null) GrpExistingBlocks.Header = isEn ? "Existing Program Blocks in PLC (OB / FB / FC)" : "PLC'deki Mevcut Program Blokları (OB / FB / FC)";

            // Permissions Tab
            if (LblPermissionsTitle != null) LblPermissionsTitle.Text = isEn ? "Siemens TIA Openness & AI Bridge Permissions" : "Siemens TIA Openness & AI Köprü İzinleri";
            if (BtnAddOpennessGroupAdmin != null) BtnAddOpennessGroupAdmin.Content = isEn ? "Add User to Openness Group & Whitelist (Admin)" : "Kullanıcıyı Openness Grubuna & Whitelist'e Ekle (Yönetici)";
            if (BtnRefreshPermissions != null) BtnRefreshPermissions.Content = isEn ? "Refresh Permission Status" : "İzin Durumunu Yenile";

            // Bottom Approval Bar
            if (BtnApprovePackage != null) BtnApprovePackage.Content = isEn ? "Accept" : "Kabul Et";
            if (BtnRejectPackage != null) BtnRejectPackage.Content = isEn ? "Cancel" : "İptal Et";
            if (BtnShowPreviewTab != null) BtnShowPreviewTab.Content = isEn ? "View Schema / Tags" : "Şemayı / Tagları Gör";

            if (_pendingPackage == null && TxtApprovalHeader != null)
            {
                TxtApprovalHeader.Text = isEn
                    ? "approval – Waiting for package (operations sent by AI agents will be approved here)"
                    : "onay – Hazır paket bekleniyor (AI üzerinden gönderilen işlemler burada onaylanır)";
            }

            // Update open HelpWindow if active
            _helpWindow?.ApplyLanguage(lang);
        }
    }
}

