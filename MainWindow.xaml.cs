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
        private bool _suppressProjectSelectionChange = false;
        private bool _suppressLanguageSelectionChange = true;
        private bool _suppressImportModeChanged = false;
        private bool _isBridgeActive = true;
        private string _backupRootDir;
        private readonly System.Threading.SemaphoreSlim _connectLock = new System.Threading.SemaphoreSlim(1, 1);
        private readonly List<(DateTime time, string textTr, string textEn)> _detailedLogEntries = new List<(DateTime, string, string)>();

        public MainWindow()
        {
            InitializeComponent();
            Title = "TIAgent MCP Server - asriakdeniz@gmail.com";

            try
            {
                _backupRootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
                Directory.CreateDirectory(_backupRootDir);
            }
            catch { }

            DgSteps.ItemsSource = _steps;

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

            LoadSavedWindowBounds();
            LoadSavedTheme();
            LoadSavedTopmost();
            LoadSavedLanguage();
            LoadSavedImportMode();
            LoadSavedBridgeActive();
            AddStep("AI", "TIAgent MCP Server başlatıldı (AI Köprüsü: http://localhost:19520 & Packages/Inbox)", true);
            RefreshPermissionsTab();
            LoadTiaVersions();

            // Connect to running TIA Portal in background so UI never freezes if TIA prompts for Openness approval
            _ = ConnectOrRefreshTiaAsync();
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowBounds();
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window_closed.txt"), "MainWindow_Closing called at " + DateTime.Now + "\r\nStack: " + Environment.StackTrace); } catch { }
            try { _approvalCountdownTimer?.Stop(); } catch { }
            try { _bridgeService?.Dispose(); } catch { }
            try { _tiaService?.Dispose(); } catch { }
        }

        private void AppendDetailedLog(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string msgTr = LanguageTranslator.TranslateLog(message, "tr");
            string msgEn = LanguageTranslator.TranslateLog(message, "en");
            var now = DateTime.Now;

            lock (_detailedLogEntries)
            {
                _detailedLogEntries.Add((now, msgTr, msgEn));
                if (_detailedLogEntries.Count > 2000)
                    _detailedLogEntries.RemoveAt(0);
            }

            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TIAgent_detailed.log"),
                    $"[{now:HH:mm:ss}] {msgTr}\r\n",
                    Encoding.UTF8);
            }
            catch { }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
                string line = $"[{now:HH:mm:ss}] {(isEn ? msgEn : msgTr)}\r\n";
                TxtDetailedLog.AppendText(line);
                TxtDetailedLog.ScrollToEnd();
            }));
        }

        private void AddStep(string category, string description, bool isSuccess = true, bool isPending = false)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string statusTr = isPending ? "BEKLİYOR" : (isSuccess ? "BAŞARILI" : "HATA");
                string statusEn = isPending ? "PENDING" : (isSuccess ? "SUCCESS" : "ERROR");
                string color = isPending ? "#D97706" : (isSuccess ? "#16A34A" : "#DC2626");

                string catTr = LanguageTranslator.TranslateCategory(category, "tr");
                string catEn = LanguageTranslator.TranslateCategory(category, "en");
                string descTr = LanguageTranslator.TranslateLog(description, "tr");
                string descEn = LanguageTranslator.TranslateLog(description, "en");

                string jobTr = $"{catTr} – {descTr}";
                string jobEn = $"{catEn} – {descEn}";
                bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);

                var item = new StepLogItem
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Icon = isEn ? statusEn : statusTr,
                    IconColor = color,
                    Category = category,
                    JobTextTr = jobTr,
                    JobTextEn = jobEn,
                    JobText = isEn ? jobEn : jobTr,
                    StatusTr = statusTr,
                    StatusEn = statusEn
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
                    Title = "TIAgent MCP Server - asriakdeniz@gmail.com";
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
                Title = "TIAgent MCP Server - asriakdeniz@gmail.com";
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
                        _suppressProjectSelectionChange = true;
                        CmbProjects.ItemsSource = null;
                        _suppressProjectSelectionChange = false;

                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                        TxtTiaStatus.Text = $"Açık {ver.DisplayName} bulunamadı ('Kapalı Proje Aç' ile başlatabilirsiniz)";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                        AddStep("TIA", $"Açık {ver.DisplayName} süreci bulunamadı", false);
                        return false;
                    }

                    // Prefer processes that have an open project, and try each candidate if multiple instances exist
                    var orderedProcs = procs
                        .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.ProjectName))
                        .ToList();

                    _suppressProjectSelectionChange = true;
                    CmbProjects.ItemsSource = orderedProcs;
                    _suppressProjectSelectionChange = false;

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

                    if (connectedProc != null)
                    {
                        _suppressProjectSelectionChange = true;
                        CmbProjects.SelectedItem = connectedProc;
                        _suppressProjectSelectionChange = false;
                    }

                    if (connectedProc == null && lastConnectEx != null)
                    {
                        throw lastConnectEx;
                    }

                    if (_currentProject == null)
                    {
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
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

                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                        TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc.Name}";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));

                        if (_pendingPackage != null)
                        {
                            string effectiveFolder = _pendingPackage.TargetFolder ?? "";
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
                                SetApprovalStatusProcessing();
                                TxtApprovalDetails.Text = _pendingPackage.GetApprovalSummaryText(_selectedPlc.Name, effectiveFolder);
                                BtnApprovePackage.IsEnabled = true;
                                BtnRejectPackage.IsEnabled = true;
                            }
                        }

                        await ReadCurrentProjectOverviewAsync();
                        return true;
                    }
                    else
                    {
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                        TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC bulunamadı";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                        AddStep("TIA", $"Proje '{_currentProject.Name}' açık ancak PLC istasyonu bulunamadı", false);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    var baseEx = ex.GetBaseException() ?? ex;
                    DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
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
                        DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                        TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc.Name}";
                        TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
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

        private void LoadSavedImportMode()
        {
            _suppressImportModeChanged = true;
            try
            {
                string modeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_import_mode.txt");
                if (File.Exists(modeFile))
                {
                    string saved = File.ReadAllText(modeFile, Encoding.UTF8).Trim();
                    if (string.Equals(saved, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        if (RbImportAuto != null) RbImportAuto.IsChecked = true;
                        if (RbImportManual != null) RbImportManual.IsChecked = false;
                        _suppressImportModeChanged = false;
                        return;
                    }
                }
            }
            catch { }
            if (RbImportManual != null) RbImportManual.IsChecked = true;
            if (RbImportAuto != null) RbImportAuto.IsChecked = false;
            _suppressImportModeChanged = false;
        }

        private async void RbImportMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressImportModeChanged) return;
            if (_bridgeService == null) return;
            bool isAuto = RbImportAuto?.IsChecked == true;

            try
            {
                string modeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_import_mode.txt");
                File.WriteAllText(modeFile, isAuto ? "auto" : "manual", Encoding.UTF8);
            }
            catch { }

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
                SetApprovalStatusProcessing();
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

            // Activate approval bar with red/amber warning header and 599s countdown
            _approvalRemainingSeconds = 599;
            _approvalCountdownTimer.Start();

            SetApprovalStatusProcessing();

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
            detailsSb.AppendLine("Durum: Onayınız bekleniyor. '✓ PLC'ye Aktar' butonuna bastığınızda silme işlemi TIA Portal'a uygulanacaktır.");

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
                    AddStep("silme", $"Hata: {err}", false);
                    AppendDetailedLog($"[HATA] {err}");
                    SetApprovalStatusWaiting();
                    return Tuple.Create(false, err);
                }
            }

            try
            {
                SetApprovalStatusProcessing();

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
                    SetApprovalStatusCompleted();
                    TxtApprovalDetails.Text = $"Son İşlem: Projeden Kaldırma ({sourceChannel})\r\nKomut: {prompt}\r\nSonuç: {delSummary}";
                    TxtCountdown.Text = "";
                }
                else
                {
                    SetApprovalStatusCompleted();
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
                AppendDetailedLog($"[HATA] Silme hatası: {err}");
                SetApprovalStatusWaiting();
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
                    TxtCountdown.Text = "";
                    SetApprovalStatusWaiting();
                    TxtApprovalDetails.Text = $"Komut: {trimmed}\r\nDurum: Pakette derlenebilir blok bulunamadı.";
                    AddStep("AI", $"Özel paket çözümlenemedi: \"{trimmed}\"", false);
                    AppendDetailedLog($"[UYARI] Özel paket çözümlenemedi: \"{trimmed}\"");
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

            // Ornekler eğitim ve derleme kurallarına göre paketi doğrula ve gerekirse düzelt
            PackageValidator.ValidateAndSanitize(pkg, (cat, msg, sOk) => AddStep(cat, msg, sOk));

            if (forceAutoApprove.HasValue) pkg.AutoApprove = forceAutoApprove.Value;

            _pendingPackage = pkg;

            // Update Package Tags in memory
            _packageTags.Clear();
            if (pkg.Tags != null)
            {
                foreach (var t in pkg.Tags) _packageTags.Add(t);
            }

            int fileCount = (pkg.Blocks?.Count ?? 0) + (pkg.DataBlocks?.Count ?? 0) + ((pkg.Tags?.Count ?? 0) > 0 ? 1 : 0);
            string hwInfo = pkg.HasHardwareContent()
                ? $", {pkg.HardwareDevices?.Count ?? 0} cihaz, {pkg.PlcModules?.Count ?? 0} PLC modülü, {pkg.TechnologyObjects?.Count ?? 0} TO"
                : "";
            AddStep("paket", $"{pkg.PackageName} ({pkg.Language}) hazır – {pkg.Blocks?.Count ?? 0} blok, {pkg.DataBlocks?.Count ?? 0} DB, {pkg.Tags?.Count ?? 0} I/O tag{hwInfo} ({fileCount} bileşen)", true);

            string plcName = _selectedPlc?.Name ?? "PLC";
            string effectiveFolder = pkg.TargetFolder ?? "";
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
                SetApprovalStatusProcessing();
                TxtApprovalDetails.Text = pkg.GetApprovalSummaryText(plcName, effectiveFolder);
                return await ApproveAndImportPendingPackageAsync();
            }
            else
            {
                // Manual approval mode (with 599s countdown timer, just like the screenshot!)
                _approvalRemainingSeconds = 599;
                _approvalCountdownTimer.Start();

                SetApprovalStatusProcessing();
                TxtApprovalDetails.Text = pkg.GetApprovalSummaryText(plcName, effectiveFolder);

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

        private void SetApprovalStatusWaiting()
        {
            if (TxtApprovalHeader == null) return;
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
            TxtApprovalHeader.Text = isEn ? "Waiting for package" : "Paket bekleniyor";
            TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        }

        private void SetApprovalStatusProcessing()
        {
            if (TxtApprovalHeader == null) return;
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
            TxtApprovalHeader.Text = isEn ? "Processing package" : "Paket işleniyor";
            TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        }

        private void SetApprovalStatusCompleted()
        {
            if (TxtApprovalHeader == null) return;
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
            TxtApprovalHeader.Text = isEn ? "Operation completed" : "İşlem tamamlandı";
            TxtApprovalHeader.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
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
                TxtCountdown.Text = "";
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
            TxtCountdown.Text = "";
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
                    AppendDetailedLog($"[HATA] TIA Portal'da açık PLC projesi yok ({pkg.PackageName} bekliyor)");
                    SetApprovalStatusWaiting();

                    BtnApprovePackage.IsEnabled = false;
                    BtnRejectPackage.IsEnabled = false;
                    TxtCountdown.Text = "";
                    if (isAutoMode) _pendingPackage = null;
                    return false;
                }
            }

            // Sync any user edits from DgPackageTags into pkg.Tags
            pkg.Tags = _packageTags.ToList();

            string uiFolder = pkg.TargetFolder ?? "";
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
                    SetApprovalStatusCompleted();
                    TxtCountdown.Text = "";
                    await ReadCurrentProjectOverviewAsync();
                    return true;
                }
                else
                {
                    SetApprovalStatusCompleted();
                    TxtCountdown.Text = "";
                    AppendDetailedLog($"[BİLGİ] {compileMsg}");
                    await ReadCurrentProjectOverviewAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                var baseEx = ex.GetBaseException() ?? ex;
                AddStep("aktarım", $"{pkg.PackageName} aktarım hatası: {baseEx.Message}", false);
                AppendDetailedLog($"[HATA] {pkg.PackageName} aktarım hatası:\r\n{baseEx.Message}");
                if (ex.InnerException != null)
                    AppendDetailedLog($"[HATA DETAYI] {ex.InnerException.Message}");
                SetApprovalStatusWaiting();

                BtnApprovePackage.IsEnabled = false;
                BtnRejectPackage.IsEnabled = false;
                TxtCountdown.Text = "";
                _pendingPackage = null;
                if (TxtApprovalDetails != null)
                {
                    bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
                    TxtApprovalDetails.Text = isEn
                        ? $"AI Request: {pkg.PackageName}\nProcess failed. Check Openness Process Log for details."
                        : $"AI Talebi: {pkg.PackageName}\nİşlem başarısız oldu. Hata detayları için Openness İşlem Günlüğü sekmesine bakın.";
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

        private void BtnClearPackage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _approvalCountdownTimer.Stop();
                _pendingApprovalAction = null;
                _pendingPackage = null;
                SetApprovalStatusWaiting();
                bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
                if (TxtApprovalDetails != null)
                {
                    TxtApprovalDetails.Text = isEn
                        ? "AI Request: -\nPLC Location: -\nProcess Details: No pending operations waiting for approval."
                        : "AI Talebi: -\nPLC Konumu: -\nİşlem Detayı: Henüz onay bekleyen bir işlem yok.";
                }
                if (BtnApprovePackage != null) BtnApprovePackage.IsEnabled = false;
                if (BtnRejectPackage != null) BtnRejectPackage.IsEnabled = false;
                if (TxtCountdown != null) TxtCountdown.Text = "";
                _bridgeService?.WriteStatusFile();
                AppendDetailedLog(isEn ? "[INFO] Incoming AI package details cleared." : "[BİLGİ] AI'den gelen paket detayları temizlendi.");
            }
            catch (Exception ex)
            {
                AppendDetailedLog($"[HATA] Paket temizleme hatası: {ex.Message}");
            }
        }

        private void BtnCopyPackage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string header = TxtApprovalHeader?.Text ?? "";
                string details = TxtApprovalDetails?.Text ?? "";
                string textToCopy = string.IsNullOrWhiteSpace(header) ? details : $"{header}\n\n{details}";
                if (!string.IsNullOrWhiteSpace(textToCopy))
                {
                    Clipboard.SetText(textToCopy);
                    bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
                    AppendDetailedLog(isEn ? "[INFO] Package details copied to clipboard." : "[BİLGİ] Paket detayları panoya kopyalandı.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kopyalama hatası: " + ex.Message);
            }
        }

        public void RejectPendingPackage(string reason)
        {
            _approvalCountdownTimer.Stop();
            _pendingApprovalAction = null;
            if (_pendingPackage != null)
            {
                AddStep("onay", $"İşlem reddedildi: {_pendingPackage.PackageName} ({reason})", false);
                AppendDetailedLog($"[BİLGİ] İşlem reddedildi: {_pendingPackage.PackageName} ({reason})");
                SetApprovalStatusWaiting();
                _pendingPackage = null;
            }
            BtnApprovePackage.IsEnabled = false;
            BtnRejectPackage.IsEnabled = false;
            TxtCountdown.Text = "";
            _bridgeService?.WriteStatusFile();
        }

        // =========================================================================================
        // 3. OPENNESS GRUBU, YEDEK KLASÖRÜ VE İZİNLER
        // =========================================================================================
        private void RefreshPermissionsTab()
        {
            TiaOpennessService.CheckOpennessPermissions(out _, out _, out string details);
            AppendDetailedLog(details);
        }

        private void BtnAddOpennessGroup_Click(object sender, RoutedEventArgs e)
        {
            bool ok = TiaOpennessService.AddUserToOpennessGroupAndWhitelist(out string msg);
            AddStep("izin", msg, ok);
            RefreshPermissionsTab();
            MessageBox.Show(msg, "Openness Grubu ve Whitelist", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private RollbackWindow _rollbackWindow;

        private void BtnOpenRollbackWindow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rollbackWindow == null || !_rollbackWindow.IsLoaded)
                {
                    _rollbackWindow = new RollbackWindow(
                        _backupRootDir,
                        _tiaService,
                        _selectedPlc,
                        (cat, msg, ok) => AddStep(cat, msg, ok),
                        _currentLanguage);
                    _rollbackWindow.Owner = this;
                    _rollbackWindow.Closed += (s, args) => _rollbackWindow = null;
                    _rollbackWindow.Show();
                }
                else
                {
                    if (_rollbackWindow.WindowState == WindowState.Minimized)
                        _rollbackWindow.WindowState = WindowState.Normal;
                    _rollbackWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Geri alma penceresi açılamadı: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    targetFolder = "",
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

                    if (!_isBridgeActive && action != "status")
                    {
                        tcs.TrySetResult(new { ok = false, error = "AI Köprüsü kullanıcı tarafından geçici olarak devre dışı bırakıldı (Pasif modda). TIAgent MCP Server ekranından 'Köprü Açık' butonuna basarak aktifleştirebilirsiniz." });
                        return;
                    }

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
            _suppressLanguageSelectionChange = true;
            string lang = "tr";
            try
            {
                string langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_language.txt");
                if (File.Exists(langFile))
                {
                    string saved = File.ReadAllText(langFile, Encoding.UTF8).Trim();
                    if (string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase))
                    {
                        lang = "en";
                    }
                }
            }
            catch { }

            if (CmbLanguage != null)
            {
                CmbLanguage.SelectedIndex = (lang == "en") ? 1 : 0;
            }
            _suppressLanguageSelectionChange = false;
            ApplyLanguage(lang);
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLanguageSelectionChange) return;
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

            // Top Header & Themes
            if (BtnThemeLight != null) BtnThemeLight.Content = isEn ? "☀️ Light" : "☀️ Açık";
            if (BtnThemeDark != null) BtnThemeDark.Content = isEn ? "🌙 Dark" : "🌙 Koyu";
            if (BtnHeaderHelp != null) BtnHeaderHelp.Content = isEn ? "? Help" : "? Yardım";
            if (LblLanguage != null) LblLanguage.Text = isEn ? "Language / Dil:" : "Arayüz Dili / Language:";

            // Tab Headers
            if (TabItemConnection != null) TabItemConnection.Header = isEn ? "🔗 Connection" : "🔗 Bağlantı";
            if (TabItemApproval != null) TabItemApproval.Header = isEn ? "🛡️ Approval" : "🛡️ Onay";
            if (TabItemQuickActions != null) TabItemQuickActions.Header = isEn ? "⚡ Quick Actions" : "⚡ Hızlı İşlemler";
            if (TabItemThemeLang != null) TabItemThemeLang.Header = isEn ? "🎨 Theme-Lang-Help" : "🎨 Tema-Dil-Yardım";
            if (TabItemAiLog != null) TabItemAiLog.Header = isEn ? "🌐 AI Bridge Logs" : "🌐 AI Köprüsü Logu";
            if (TabItemTiaLog != null) TabItemTiaLog.Header = isEn ? "⚙️ Openness & Process Log" : "⚙️ Openness İşlem Günlüğü";

            // Toolbar Row 1: TIA Version, Open Projects, AI Bridge
            if (LblTiaVersion != null) LblTiaVersion.Text = isEn ? "TIA Version:" : "TIA Sürümü:";
            if (LblOpenProjects != null) LblOpenProjects.Text = isEn ? "Open Projects:" : "Açık Projeler:";
            if (LblAiBridge != null) LblAiBridge.Text = isEn ? "AI Bridge:" : "AI Köprüsü:";
            if (BtnToggleBridge != null)
            {
                BtnToggleBridge.Content = _isBridgeActive
                    ? (isEn ? "🟢 Bridge Active" : "🟢 Köprü Açık")
                    : (isEn ? "🔴 Bridge Inactive" : "🔴 Köprü Kapalı");
            }
            if (TxtAiStatus != null)
            {
                TxtAiStatus.Text = _isBridgeActive
                    ? (isEn ? "localhost:19520 (Active)" : "localhost:19520 (Aktif)")
                    : (isEn ? "localhost:19520 (Inactive)" : "localhost:19520 (Pasif)");
            }

            // Toolbar Row 2: Action Buttons
            if (BtnRefreshTia != null) BtnRefreshTia.Content = isEn ? "🔄 Refresh" : "🔄 Yenile";
            if (BtnConnectTia != null) BtnConnectTia.Content = isEn ? "🔌 Connect" : "🔌 Bağlan";
            if (BtnOpenProjectFile != null) BtnOpenProjectFile.Content = isEn ? "📂 Open Offline (.apXX)" : "📂 Kapalı Proje Aç (.apXX)";
            if (BtnDisconnectTia != null) BtnDisconnectTia.Content = isEn ? "✂️ Disconnect" : "✂️ Bağlantıyı Kes";
            if (BtnRestartApp != null) BtnRestartApp.Content = isEn ? "🔁 Restart" : "🔁 Yeniden Başlat";

            // Toolbar Row 3: Status & PLC
            if (LblPlc != null) LblPlc.Text = isEn ? "PLC Station:" : "PLC İstasyonu:";
            if (TxtTiaStatus != null)
            {
                if (_currentProject == null)
                {
                    TxtTiaStatus.Text = isEn ? "Waiting for connection... | Active Project: - |" : "Bağlantı bekleniyor... | Aktif Proje: - |";
                }
                else
                {
                    TxtTiaStatus.Text = isEn
                        ? $"Connected: {_currentProject.Name}  |  PLC: {_selectedPlc?.Name ?? "-"}"
                        : $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc?.Name ?? "-"}";
                }
            }

            // Approval Card
            if (LblApprovalBadge != null) LblApprovalBadge.Text = isEn ? "Incoming AI Packages:" : "AI'den Gelen Paketler:";
            if (BtnApprovePackage != null) BtnApprovePackage.Content = isEn ? "✓ Write to PLC" : "✓ PLC'ye Aktar";
            if (BtnRejectPackage != null) BtnRejectPackage.Content = isEn ? "✕ Cancel" : "✕ İptal Et";
            if (BtnClearPackage != null) BtnClearPackage.Content = isEn ? "🗑 Clear" : "🗑 Temizle";
            if (BtnCopyPackage != null) BtnCopyPackage.Content = isEn ? "📋 Copy" : "📋 Kopyala";
            if (MenuCopyPackage != null) MenuCopyPackage.Header = isEn ? "📋 Copy" : "📋 Kopyala";
            if (MenuClearPackage != null) MenuClearPackage.Header = isEn ? "🗑 Clear" : "🗑 Temizle";

            if (_pendingPackage == null)
            {
                if (TxtApprovalHeader != null)
                {
                    if (TxtApprovalHeader.Text.Contains("tamamlandı") || TxtApprovalHeader.Text.Contains("completed"))
                        SetApprovalStatusCompleted();
                    else
                        SetApprovalStatusWaiting();
                }
                if (TxtApprovalDetails != null)
                {
                    TxtApprovalDetails.Text = isEn
                        ? "AI Request: -\nPLC Location: -\nProcess Details: No pending operations waiting for approval."
                        : "AI Talebi: -\nPLC Konumu: -\nİşlem Detayı: Henüz onay bekleyen bir işlem yok.";
                }
            }
            else
            {
                if (TxtApprovalDetails != null)
                {
                    TxtApprovalDetails.Text = _pendingPackage.GetApprovalSummaryText(_selectedPlc?.Name, "", lang);
                }
            }

            // Logs Section: Headers, Column Titles & Buttons
            if (LblAiLogsTitle != null)
                LblAiLogsTitle.Text = isEn ? "🌐 AI Bridge Communication & Request Log" : "🌐 AI Köprüsü İletişim & Talep Logu";
            if (LblTiaLogsTitle != null)
                LblTiaLogsTitle.Text = isEn ? "⚙️ TIAgent & Openness Process Log" : "⚙️ TIAgent & Openness İşlem Günlüğü";

            if (ColTime != null) ColTime.Header = isEn ? "TIME" : "ZAMAN";
            if (ColStatus != null) ColStatus.Header = isEn ? "STATUS" : "DURUM";
            if (ColDetails != null) ColDetails.Header = isEn ? "AI REQUEST / PROCESS DETAILS" : "AI TALEP / İŞLEM DETAYI";

            if (BtnClearAiLogs != null) BtnClearAiLogs.Content = isEn ? "🗑 Clear" : "🗑 Temizle";
            if (BtnCopyAiLogs != null) BtnCopyAiLogs.Content = isEn ? "📋 Copy" : "📋 Kopyala";
            if (BtnClearTiaLogs != null) BtnClearTiaLogs.Content = isEn ? "🗑 Clear" : "🗑 Temizle";
            if (BtnCopyTiaLogs != null) BtnCopyTiaLogs.Content = isEn ? "📋 Copy" : "📋 Kopyala";

            // Operations & Settings Horizontal Bar
            if (LblApprovalModeTitle != null)
                LblApprovalModeTitle.Text = "";
            if (RbImportManual != null)
                RbImportManual.Content = isEn ? "Wait for Approval" : "Onay Bekle";
            if (RbImportAuto != null)
                RbImportAuto.Content = isEn ? "No Approval" : "Onay Bekleme";
            if (TxtApprovalExplanation != null)
            {
                TxtApprovalExplanation.Text = isEn
                    ? "Wait for Approval: Packages are written using 'Write to PLC' button.\nNo Approval: Incoming packages are processed automatically without confirmation."
                    : "Onay Bekle: Paketler PLC'ye Aktar butonu ile işlenir.\nOnay Bekleme: Onay olmadan otomatik olarak gelen paketler işlenir.";
            }
            if (LblQuickActionsTitle != null)
                LblQuickActionsTitle.Text = "";
            if (BtnReadProject != null)
                BtnReadProject.Content = isEn ? "🔍 Read Project" : "🔍 Projeyi Oku";
            if (BtnAddOpennessGroup != null)
                BtnAddOpennessGroup.Content = isEn ? "🛡️ Openness Group" : "🛡️ Openness Grubu";
            if (BtnOpenRollbackWindow != null)
                BtnOpenRollbackWindow.Content = isEn ? "↺ History & Undo" : "↺ İşlem Geçmişi";

            // Update all existing step log rows into the chosen language!
            foreach (var step in _steps)
            {
                step.UpdateLanguage(lang);
            }

            // Update detailed log textbox with the chosen language!
            var sb = new StringBuilder();
            lock (_detailedLogEntries)
            {
                foreach (var entry in _detailedLogEntries)
                {
                    string text = isEn ? entry.textEn : entry.textTr;
                    sb.AppendLine($"[{entry.time:HH:mm:ss}] {text}");
                }
            }
            if (TxtDetailedLog != null)
            {
                TxtDetailedLog.Text = sb.ToString();
                TxtDetailedLog.ScrollToEnd();
            }

            // Update open HelpWindow & RollbackWindow if active
            _helpWindow?.ApplyLanguage(lang);
            _rollbackWindow?.ApplyLanguage(lang);
            UpdateTopmostButton();
        }

        private void LoadSavedTopmost()
        {
            try
            {
                string topFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_topmost.txt");
                if (File.Exists(topFile))
                {
                    string saved = File.ReadAllText(topFile, Encoding.UTF8).Trim();
                    if (string.Equals(saved, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        Topmost = false;
                        UpdateTopmostButton();
                        return;
                    }
                }
            }
            catch { }
            Topmost = true; // Varsayılan: Her zaman üstte
            UpdateTopmostButton();
        }

        private void BtnToggleTopmost_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            UpdateTopmostButton();
            try
            {
                string topFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_topmost.txt");
                File.WriteAllText(topFile, Topmost ? "true" : "false", Encoding.UTF8);
            }
            catch { }
        }

        private void UpdateTopmostButton()
        {
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);
            if (BtnToggleTopmost != null)
            {
                BtnToggleTopmost.Content = Topmost
                    ? (isEn ? "📌 On Top" : "📌 Üstte")
                    : (isEn ? "📍 Normal" : "📍 Normal");
            }
        }

        private void LoadSavedWindowBounds()
        {
            try
            {
                string boundsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_window_bounds.txt");
                if (File.Exists(boundsFile))
                {
                    var lines = File.ReadAllLines(boundsFile, Encoding.UTF8);
                    if (lines.Length >= 2 && double.TryParse(lines[0], out double w) && double.TryParse(lines[1], out double h))
                    {
                        if (w >= 300 && h >= 250)
                        {
                            Width = w;
                            Height = h;
                        }
                    }
                    if (lines.Length >= 3 && Enum.TryParse(lines[2], out WindowState state) && state != WindowState.Minimized)
                    {
                        WindowState = state;
                    }
                }
            }
            catch { }
        }

        private void SaveWindowBounds()
        {
            try
            {
                string boundsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_window_bounds.txt");
                double w = (WindowState == WindowState.Normal) ? ActualWidth : RestoreBounds.Width;
                double h = (WindowState == WindowState.Normal) ? ActualHeight : RestoreBounds.Height;
                if (w < 320) w = 320;
                if (h < 250) h = 250;
                File.WriteAllLines(boundsFile, new[] { w.ToString(), h.ToString(), WindowState.ToString() }, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadSavedTheme()
        {
            try
            {
                string themeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_theme.txt");
                if (File.Exists(themeFile))
                {
                    string saved = File.ReadAllText(themeFile, Encoding.UTF8).Trim();
                    if (string.Equals(saved, "dark", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyTheme(isDark: true);
                        return;
                    }
                    if (string.Equals(saved, "light", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyTheme(isDark: false);
                        return;
                    }
                }
            }
            catch { }
            ApplyTheme(isDark: false); // Varsayılan tema: Açık Tema
        }

        private void BtnThemeLight_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(isDark: false);
        }

        private void BtnThemeDark_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(isDark: true);
        }

        private void ApplyTheme(bool isDark)
        {
            try
            {
                string themeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_theme.txt");
                File.WriteAllText(themeFile, isDark ? "dark" : "light", Encoding.UTF8);
            }
            catch { }

            if (isDark)
            {
                Resources["BgWindow"] = new SolidColorBrush(Color.FromRgb(0x11, 0x1A, 0x24));
                Resources["BgHeader"] = new SolidColorBrush(Color.FromRgb(0x16, 0x22, 0x2F));
                Resources["BgSubHeader"] = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x26));
                Resources["BgCard"] = new SolidColorBrush(Color.FromRgb(0x18, 0x25, 0x35));
                Resources["BgSubCard"] = new SolidColorBrush(Color.FromRgb(0x12, 0x1B, 0x26));
                Resources["BgBox"] = new SolidColorBrush(Color.FromRgb(0x0C, 0x13, 0x1B));
                Resources["BorderCard"] = new SolidColorBrush(Color.FromRgb(0x26, 0x37, 0x4A));
                Resources["BorderHeader"] = new SolidColorBrush(Color.FromRgb(0x23, 0x32, 0x45));
                Resources["TextMain"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                Resources["TextMuted"] = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                Resources["TextConsole"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                Resources["DgBg"] = new SolidColorBrush(Color.FromRgb(0x0C, 0x13, 0x1B));
                Resources["DgRowBg"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x22));
                Resources["DgAltRowBg"] = new SolidColorBrush(Color.FromRgb(0x12, 0x1D, 0x2B));
                Resources["DgHeaderBg"] = new SolidColorBrush(Color.FromRgb(0x18, 0x25, 0x35));
                Resources["DgGridLines"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x3A));

                // Dark Button Colors
                Resources["BtnToolBg"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x3A));
                Resources["BtnToolFg"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                Resources["BtnToolBorder"] = new SolidColorBrush(Color.FromRgb(0x33, 0x46, 0x5C));
                Resources["BtnToolHoverBg"] = new SolidColorBrush(Color.FromRgb(0x29, 0x3A, 0x4E));
                Resources["BtnToolHoverFg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["BtnToolHoverBorder"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));

                Resources["BtnDangerBg"] = new SolidColorBrush(Color.FromRgb(0x5B, 0x19, 0x1F));
                Resources["BtnDangerFg"] = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
                Resources["BtnDangerBorder"] = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                Resources["BtnDangerHoverBg"] = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));

                Resources["BtnMiniBg"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x26, 0x34));
                Resources["BtnMiniFg"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                Resources["BtnMiniBorder"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x3F, 0x54));
                Resources["BtnMiniHoverBg"] = new SolidColorBrush(Color.FromRgb(0x24, 0x34, 0x47));
            }
            else
            {
                // Soft light theme: Clean, modern, soft white/slate with crisp text and subtle border
                Resources["BgWindow"] = new SolidColorBrush(Color.FromRgb(0xEA, 0xEE, 0xF4));
                Resources["BgHeader"] = new SolidColorBrush(Color.FromRgb(0xDE, 0xE4, 0xEC));
                Resources["BgSubHeader"] = new SolidColorBrush(Color.FromRgb(0xD5, 0xDC, 0xE6));
                Resources["BgCard"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["BgSubCard"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                Resources["BgBox"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["BorderCard"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                Resources["BorderHeader"] = new SolidColorBrush(Color.FromRgb(0xB0, 0xBF, 0xD0));
                Resources["TextMain"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
                Resources["TextMuted"] = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                Resources["TextConsole"] = new SolidColorBrush(Color.FromRgb(0x03, 0x69, 0xA1));
                Resources["DgBg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["DgRowBg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["DgAltRowBg"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                Resources["DgHeaderBg"] = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF6));
                Resources["DgGridLines"] = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));

                // Button colors (Açık temada şık, aydınlık, yumuşak gri/beyaz butonlar + koyu net yazılar)
                Resources["BtnToolBg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["BtnToolFg"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
                Resources["BtnToolBorder"] = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                Resources["BtnToolHoverBg"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xFE));
                Resources["BtnToolHoverFg"] = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7));
                Resources["BtnToolHoverBorder"] = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7));

                Resources["BtnDangerBg"] = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
                Resources["BtnDangerFg"] = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B));
                Resources["BtnDangerBorder"] = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                Resources["BtnDangerHoverBg"] = new SolidColorBrush(Color.FromRgb(0xFE, 0xCD, 0xCD));

                Resources["BtnMiniBg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["BtnMiniFg"] = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
                Resources["BtnMiniBorder"] = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                Resources["BtnMiniHoverBg"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
            }
        }

        private async void CmbProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProjectSelectionChange) return;
            if (CmbProjects.SelectedItem is TiaProcessItem proc && proc != null)
            {
                if (_currentProject == null || _currentProject.FilePath != proc.ProjectPath)
                {
                    try
                    {
                        AddStep("TIA", $"Seçilen sürece bağlanılıyor (PID: {proc.ProcessId})...", true);
                        var proj = await Task.Run(() => _tiaService.ConnectToProcess(proc));
                        if (proj != null)
                        {
                            _currentProject = proj;
                            var plcs = await Task.Run(() => _tiaService.GetPlcList(_currentProject));
                            CmbPlc.ItemsSource = plcs;
                            if (plcs.Count > 0)
                            {
                                _selectedPlc = plcs[0];
                                CmbPlc.SelectedIndex = 0;
                            }
                            DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                            TxtTiaStatus.Text = $"Bağlı: {_currentProject.Name}  |  PLC: {_selectedPlc?.Name ?? "-"}";
                            TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                            AddStep("TIA", $"TIA projesine bağlanıldı: {_currentProject.Name}", true);
                            await ReadCurrentProjectOverviewAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        var baseEx = ex.GetBaseException() ?? ex;
                        AppendDetailedLog("Projeye bağlanırken hata: " + baseEx.Message);
                        AddStep("TIA", $"Bağlantı hatası: {baseEx.Message}", false);
                    }
                }
            }
        }

        private void BtnDisconnectTia_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _currentProject = null;
                _selectedPlc = null;
                CmbPlc.ItemsSource = null;
                DotTiaStatus.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                TxtTiaStatus.Text = "Bağlantı kesildi | Aktif Proje: -";
                TxtTiaStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                AddStep("TIA", "TIA Portal bağlantısı kesildi", true);
                _bridgeService?.WriteStatusFile();
            }
            catch (Exception ex)
            {
                AppendDetailedLog("Bağlantı kesme hatası: " + ex.Message);
            }
        }

        private void BtnRestartApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                try { _bridgeService?.Dispose(); } catch { }
                try { _tiaService?.Dispose(); } catch { }

                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo(exePath)
                {
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Yeniden başlatılamadı: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSavedBridgeActive()
        {
            try
            {
                string bridgeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_bridge_active.txt");
                if (File.Exists(bridgeFile))
                {
                    string saved = File.ReadAllText(bridgeFile, Encoding.UTF8).Trim();
                    if (string.Equals(saved, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(saved, "0", StringComparison.OrdinalIgnoreCase))
                    {
                        SetBridgeActive(false, logAction: false);
                        return;
                    }
                }
            }
            catch { }
            SetBridgeActive(true, logAction: false);
        }

        private void SetBridgeActive(bool active, bool logAction = true)
        {
            _isBridgeActive = active;
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);

            if (_isBridgeActive)
            {
                if (DotAiStatus != null) DotAiStatus.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                if (TxtAiStatus != null)
                {
                    TxtAiStatus.Text = isEn ? "localhost:19520 (Active)" : "localhost:19520 (Aktif)";
                    TxtAiStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                }
                if (BtnToggleBridge != null) BtnToggleBridge.Content = isEn ? "🟢 Bridge Active" : "🟢 Köprü Açık";
                if (logAction)
                    AddStep("AI", isEn ? "AI Bridge ENABLED by user" : "AI Köprüsü kullanıcı tarafından AKTİF yapıldı", true);
            }
            else
            {
                if (DotAiStatus != null) DotAiStatus.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                if (TxtAiStatus != null)
                {
                    TxtAiStatus.Text = isEn ? "Bridge Inactive" : "Köprü Pasif";
                    TxtAiStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                }
                if (BtnToggleBridge != null) BtnToggleBridge.Content = isEn ? "🔴 Bridge Inactive" : "🔴 Köprü Kapalı";
                if (logAction)
                    AddStep("AI", isEn ? "AI Bridge DISABLED by user" : "AI Köprüsü kullanıcı tarafından PASİF yapıldı", false);
            }

            try
            {
                string bridgeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_bridge_active.txt");
                File.WriteAllText(bridgeFile, active ? "true" : "false", Encoding.UTF8);
            }
            catch { }

            _bridgeService?.WriteStatusFile();
        }

        private void BtnToggleBridge_Click(object sender, RoutedEventArgs e)
        {
            SetBridgeActive(!_isBridgeActive, logAction: true);
        }

        private void BtnClearAiLogs_Click(object sender, RoutedEventArgs e)
        {
            _steps.Clear();
            _bridgeService?.WriteStatusFile();
        }

        private void BtnCopyAiLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var step in _steps)
                {
                    sb.AppendLine($"[{step.Time}] {step.Icon} {step.JobText}");
                }
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kopyalama hatası: " + ex.Message);
            }
        }

        private void BtnClearTiaLogs_Click(object sender, RoutedEventArgs e)
        {
            lock (_detailedLogEntries) { _detailedLogEntries.Clear(); }
            TxtDetailedLog.Text = "";
        }

        private void BtnCopyTiaLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(TxtDetailedLog.Text))
                {
                    Clipboard.SetText(TxtDetailedLog.Text);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kopyalama hatası: " + ex.Message);
            }
        }
    }
}

