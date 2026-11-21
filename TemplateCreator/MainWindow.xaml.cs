using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TIAgent.Models;
using TIAgent.Services;
using TIAgentTemplateCreator.Models;

namespace TIAgentTemplateCreator
{
    public partial class MainWindow : Window
    {
        private TiaOpennessService _opennessService;
        private List<TiaVersionInfo> _availableVersions = new List<TiaVersionInfo>();
        private TiaVersionInfo _selectedVersion;
        private TiaProcessItem _selectedProcess;
        private TiaProjectItem _currentProject;
        private List<TiaPlcItem> _plcs = new List<TiaPlcItem>();
        private TiaPlcItem _selectedPlc;

        private ObservableCollection<BatchProjectItem> _batchProjects = new ObservableCollection<BatchProjectItem>();
        private TemplateCreatorSettings _settings = new TemplateCreatorSettings();

        private CancellationTokenSource _cts;
        private string _lastExportedDir;
        private bool _suppressVersionSelection = false;
        private bool _isSettingsLoaded = false;
        private bool _isExportRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            GridBatchProjects.ItemsSource = _batchProjects;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _opennessService = new TiaOpennessService(
                    log => Dispatcher.Invoke(() => AppendLog(log)),
                    err => Dispatcher.Invoke(() => AppendLog($"[HATA] {err}"))
                );

                LoadSettings();
                LoadTiaVersions();
            }
            catch (Exception ex)
            {
                AppendLog($"[HATA] Başlatma hatası: {ex.Message}");
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            try
            {
                SaveSettings();
                _cts?.Cancel();
                _opennessService?.Dispose();
            }
            catch { }
        }

        #region Settings Management (Hatırlama)

        private void LoadSettings()
        {
            try
            {
                _settings = TemplateCreatorSettingsService.Load();

                // Restore Target Directory
                if (!string.IsNullOrWhiteSpace(_settings.TargetDirectory) && Directory.Exists(_settings.TargetDirectory))
                {
                    TxtTargetDir.Text = _settings.TargetDirectory;
                }
                else
                {
                    SetDefaultTargetDirectory();
                }

                // Restore Batch Scanned Folder
                if (!string.IsNullOrWhiteSpace(_settings.LastScannedBatchFolder))
                {
                    TxtBatchFolder.Text = _settings.LastScannedBatchFolder;
                }

                // Restore Checkbox States
                ChkExportBlocks.IsChecked = _settings.ExportBlocks;
                ChkExtractScl.IsChecked = _settings.ExtractScl;
                ChkExportUdts.IsChecked = _settings.ExportUdts;
                ChkExportTagTables.IsChecked = _settings.ExportTagTables;
                ChkExportHardware.IsChecked = _settings.ExportHardware;
                ChkGenerateAiSummary.IsChecked = _settings.GenerateAiSummary;

                // Restore Active Tab
                if (_settings.SelectedTab >= 0 && _settings.SelectedTab < MainTabControl.Items.Count)
                {
                    MainTabControl.SelectedIndex = _settings.SelectedTab;
                }

                _isSettingsLoaded = true;
            }
            catch
            {
                SetDefaultTargetDirectory();
                _isSettingsLoaded = true;
            }
        }

        private void SaveSettings()
        {
            if (!_isSettingsLoaded) return;

            try
            {
                _settings.TargetDirectory = TxtTargetDir.Text?.Trim();
                _settings.LastScannedBatchFolder = TxtBatchFolder.Text?.Trim();
                _settings.ExportBlocks = ChkExportBlocks.IsChecked == true;
                _settings.ExtractScl = ChkExtractScl.IsChecked == true;
                _settings.ExportUdts = ChkExportUdts.IsChecked == true;
                _settings.ExportTagTables = ChkExportTagTables.IsChecked == true;
                _settings.ExportHardware = ChkExportHardware.IsChecked == true;
                _settings.GenerateAiSummary = ChkGenerateAiSummary.IsChecked == true;
                _settings.SelectedTab = MainTabControl.SelectedIndex;
                if (_selectedVersion != null)
                {
                    _settings.SelectedTiaVersion = _selectedVersion.VersionKey;
                }

                TemplateCreatorSettingsService.Save(_settings);
            }
            catch { }
        }

        private void SaveSettingsTrigger(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        private void TxtTargetDir_TextChanged(object sender, TextChangedEventArgs e)
        {
            SaveSettings();
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl)
            {
                UpdateButtonLabel();
                SaveSettings();
            }
        }

        private void UpdateButtonLabel()
        {
            if (MainTabControl.SelectedIndex == 0)
            {
                BtnStartExport.Content = "🚀 ŞABLONLARI ÇIKAR";
                BtnStartExport.IsEnabled = _selectedPlc != null && _opennessService != null && _opennessService.IsConnected && !_isExportRunning;
            }
            else
            {
                int count = _batchProjects.Count(p => p.IsSelected);
                BtnStartExport.Content = count > 0 ? $"🚀 SEÇİLİLERİ DIŞA AKTAR ({count} Proje)" : "🚀 SEÇİLİLERİ DIŞA AKTAR";
                BtnStartExport.IsEnabled = count > 0 && !_isExportRunning;
            }
        }

        private void SetDefaultTargetDirectory()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string searchDir = baseDir;
                string orneklerDir = null;

                for (int i = 0; i < 4; i++)
                {
                    string candidate = Path.Combine(searchDir, "Ornekler");
                    if (Directory.Exists(candidate))
                    {
                        orneklerDir = candidate;
                        break;
                    }
                    DirectoryInfo parent = Directory.GetParent(searchDir);
                    if (parent == null) break;
                    searchDir = parent.FullName;
                }

                if (string.IsNullOrEmpty(orneklerDir))
                {
                    orneklerDir = Path.Combine(baseDir, "Ornekler");
                }

                TxtTargetDir.Text = orneklerDir;
            }
            catch
            {
                TxtTargetDir.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ornekler");
            }
        }

        #endregion

        #region TIA Portal Version & Process Handling (Live Mode)

        private void LoadTiaVersions()
        {
            try
            {
                AppendLog("[Bilgi] Sistemdeki TIA Portal sürümleri taranıyor...");
                _availableVersions = _opennessService.GetInstalledAndLibVersions();

                _suppressVersionSelection = true;
                CmbTiaVersion.Items.Clear();
                foreach (var v in _availableVersions)
                {
                    CmbTiaVersion.Items.Add(v);
                }

                // 1. Önce aktif çalışan TIA Portal oturumunun sürümünü tespit et
                TiaVersionInfo detected = DetectRunningTiaVersion(_availableVersions);

                // 2. Kayıtlı tercih var mı?
                if (detected == null && !string.IsNullOrWhiteSpace(_settings.SelectedTiaVersion))
                {
                    detected = _availableVersions.FirstOrDefault(v => string.Equals(v.VersionKey, _settings.SelectedTiaVersion, StringComparison.OrdinalIgnoreCase));
                }

                // 3. Çalışan veya kayıtlı bulunamadıysa tercih sırasına göre varsayılan seç
                if (detected == null)
                {
                    string[] preferred = { "V20", "V21", "V19", "V18", "V17", "V16", "V15.1", "V15", "V14 SP1" };
                    foreach (var pref in preferred)
                    {
                        detected = _availableVersions.FirstOrDefault(v => string.Equals(v.VersionKey, pref, StringComparison.OrdinalIgnoreCase));
                        if (detected != null) break;
                    }
                }

                if (detected == null && _availableVersions.Count > 0)
                {
                    detected = _availableVersions[0];
                }

                if (detected != null)
                {
                    _selectedVersion = detected;
                    CmbTiaVersion.SelectedItem = detected;
                    AppendLog($"[✓ Sürüm Seçimi] {detected.DisplayName} seçildi.");
                }
                else
                {
                    AppendLog("[Uyarı] Sistemde kayıtlı TIA Portal Openness sürümü bulunamadı.");
                }

                _suppressVersionSelection = false;

                RefreshProcesses();
            }
            catch (Exception ex)
            {
                AppendLog($"[HATA] Sürüm listesi alınamadı: {ex.Message}");
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

                    string[] preferenceOrder = { "V20", "V21", "V19", "V18", "V17", "V16", "V15.1", "V15", "V14 SP1" };
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

        private void BtnAutoDetectVersion_Click(object sender, RoutedEventArgs e)
        {
            var detected = DetectRunningTiaVersion(_availableVersions);
            if (detected != null)
            {
                AppendLog($"[🔍 Otomatik Tespit] Açık çalışan {detected.DisplayName} bulundu!");
                _selectedVersion = detected;
                CmbTiaVersion.SelectedItem = detected;
            }
            else
            {
                AppendLog("[Bilgi] Açık bir TIA Portal süreci tespit edilemedi. Lütfen TIA Portal'da projenizi açın veya listeden sürüm seçin.");
                RefreshProcesses();
            }
        }

        private void CmbTiaVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressVersionSelection) return;

            _selectedVersion = CmbTiaVersion.SelectedItem as TiaVersionInfo;
            if (_selectedVersion != null)
            {
                AppendLog($"[Sürüm Seçildi] {_selectedVersion.DisplayName}");
                SaveSettings();
                RefreshProcesses();
            }
        }

        private void BtnRefreshProcesses_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcesses();
        }

        private void RefreshProcesses()
        {
            if (_selectedVersion == null) return;

            try
            {
                CmbProcesses.Items.Clear();
                var procs = _opennessService.GetProcessList(_selectedVersion);

                if (procs == null || procs.Count == 0)
                {
                    var runningVer = DetectRunningTiaVersion(_availableVersions);
                    if (runningVer != null && !string.Equals(runningVer.VersionKey, _selectedVersion.VersionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"[İpucu] {_selectedVersion.DisplayName} için açık oturum yok, ancak sistemde {runningVer.DisplayName} çalışıyor!");
                        AppendLog($"[Otomatik Düzeltme] Sürüm {runningVer.DisplayName} olarak güncelleniyor...");
                        _suppressVersionSelection = true;
                        _selectedVersion = runningVer;
                        CmbTiaVersion.SelectedItem = runningVer;
                        _suppressVersionSelection = false;

                        procs = _opennessService.GetProcessList(runningVer);
                    }
                }

                if (procs == null || procs.Count == 0)
                {
                    AppendLog($"[Süreç] {_selectedVersion.DisplayName} için açık çalışan TIA Portal oturumu bulunamadı. Lütfen TIA Portal'da projenizi açın veya 'Toplu Proje Klasörü' sekmesini kullanın.");
                    return;
                }

                foreach (var p in procs)
                {
                    CmbProcesses.Items.Add(p);
                }

                CmbProcesses.SelectedIndex = 0;
                AppendLog($"[✓ Süreç] {_selectedVersion.DisplayName} için {procs.Count} adet çalışan oturum tespit edildi.");
            }
            catch (Exception ex)
            {
                AppendLog($"[HATA] Süreç listesi alınırken hata: {ex.Message}");
            }
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var proc = CmbProcesses.SelectedItem as TiaProcessItem;
            if (proc == null)
            {
                MessageBox.Show("Lütfen bağlanmak için açık bir TIA Portal oturumu seçin.", "Oturum Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnConnect.IsEnabled = false;
            TxtStatusBadge.Text = "● Bağlanıyor...";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber

            try
            {
                AppendLog($"[Bağlantı] TIA Portal oturumuna bağlanılıyor (PID: {proc.ProcessId})...");

                _currentProject = await Task.Run(() => _opennessService.ConnectToProcess(proc));

                if (_currentProject != null && _opennessService.IsConnected)
                {
                    _selectedProcess = proc;

                    TxtStatusBadge.Text = $"● Bağlandı ({_currentProject?.Name ?? "Açık"})";
                    StatusBadge.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    AppendLog($"[✓] TIA Portal projesine başarıyla bağlanıldı: {_currentProject?.Name} ({_currentProject?.FilePath})");

                    await ScanPlcsAsync();
                }
                else
                {
                    TxtStatusBadge.Text = "● Bağlantı Başarısız";
                    StatusBadge.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    AppendLog("[HATA] TIA Portal oturumuna bağlanılamadı. Openness güvenlik izinlerini kontrol edin.");
                }
            }
            catch (Exception ex)
            {
                TxtStatusBadge.Text = "● Hata";
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                AppendLog($"[HATA] Bağlantı sırasında istisna: {ex.Message}");
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        private async Task ScanPlcsAsync()
        {
            try
            {
                TxtStatusBadge.Text = "● PLC'ler Taranıyor...";
                TxtProgressStatus.Text = "Projedeki cihaz ve PLC istasyonları taranıyor...";
                AppendLog("[Tarama] Açık projedeki donanım cihazları ve PLC istasyonları taranıyor, lütfen bekleyin...");
                CmbPlcs.Items.Clear();
                BtnStartExport.IsEnabled = false;

                _plcs = await Task.Run(() => _opennessService.GetPlcList(_currentProject));

                if (_plcs == null || _plcs.Count == 0)
                {
                    TxtPlcInfo.Text = "Projede PLC istasyonu bulunamadı.";
                    TxtProgressStatus.Text = "Projede PLC istasyonu bulunamadı.";
                    BtnStartExport.IsEnabled = false;
                    AppendLog("[Uyarı] Projede programlanabilir bir PLC istasyonu bulunamadı.");
                    return;
                }

                foreach (var plc in _plcs)
                {
                    CmbPlcs.Items.Add(plc);
                }

                CmbPlcs.SelectedIndex = 0;
                TxtStatusBadge.Text = $"● Bağlandı ({_currentProject?.Name ?? "Açık"})";
                TxtProgressStatus.Text = $"Hazır! {_plcs.Count} adet PLC istasyonu yüklendi. Artık şablon çıkarımı başlatabilirsiniz.";
                AppendLog($"[✓ Hazır] {_plcs.Count} adet PLC istasyonu yüklendi. Varsayılan PLC: {_plcs[0].Name}");
                UpdateButtonLabel();
            }
            catch (Exception ex)
            {
                TxtStatusBadge.Text = "● Hata";
                TxtProgressStatus.Text = "PLC taraması sırasında hata oluştu.";
                AppendLog($"[HATA] PLC istasyonları taranırken hata: {ex.Message}");
            }
        }

        private void CmbPlcs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedPlc = CmbPlcs.SelectedItem as TiaPlcItem;
            if (_selectedPlc != null)
            {
                TxtPlcInfo.Text = $"Seçili PLC: {_selectedPlc.Name} | Donanım: {_selectedPlc.TypeName}";
            }
            else
            {
                TxtPlcInfo.Text = "PLC seçilmedi.";
            }
            UpdateButtonLabel();
        }

        #endregion

        #region Batch Project Folder Scanning (Toplu Proje Modu)

        private void BtnBrowseBatchFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "İçinde TIA Portal Projeleri Bulunan Ana Klasörü Seçin";
                if (!string.IsNullOrWhiteSpace(TxtBatchFolder.Text) && Directory.Exists(TxtBatchFolder.Text))
                {
                    dialog.SelectedPath = TxtBatchFolder.Text;
                }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TxtBatchFolder.Text = dialog.SelectedPath;
                    SaveSettings();
                    ScanBatchProjects(dialog.SelectedPath);
                }
            }
        }

        private void BtnScanBatchProjects_Click(object sender, RoutedEventArgs e)
        {
            string folder = TxtBatchFolder.Text?.Trim();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show("Lütfen taranacak geçerli bir klasör seçin.", "Klasör Bulunamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ScanBatchProjects(folder);
        }

        private async void ScanBatchProjects(string rootFolder)
        {
            AppendLog($"[Toplu Tarama] '{rootFolder}' klasörü ve alt dizinleri taranıyor...");
            TxtProgressStatus.Text = "Klasördeki TIA projeleri aranıyor...";

            _batchProjects.Clear();
            var list = await Task.Run(() => FindTiaProjectsInFolder(rootFolder, _availableVersions));

            foreach (var item in list)
            {
                item.PropertyChanged += (s, e) => UpdateButtonLabel();
                _batchProjects.Add(item);
            }

            TxtBatchCountInfo.Text = $"{_batchProjects.Count} proje bulundu.";
            AppendLog($"[✓ Toplu Tarama] Toplam {_batchProjects.Count} adet TIA Portal projesi tespit edildi.");
            TxtProgressStatus.Text = $"{_batchProjects.Count} proje listelendi. İstediğiniz projeleri işaretleyip dışa aktarımı başlatabilirsiniz.";
            UpdateButtonLabel();
        }

        private static List<BatchProjectItem> FindTiaProjectsInFolder(string rootFolder, List<TiaVersionInfo> availableVersions)
        {
            var list = new List<BatchProjectItem>();
            if (!Directory.Exists(rootFolder)) return list;

            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".ap14", ".ap15", ".ap15_1", ".ap16", ".ap17", ".ap18", ".ap19", ".ap20", ".ap21"
            };

            try
            {
                var files = Directory.GetFiles(rootFolder, "*.ap*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file);
                    if (extensions.Contains(ext))
                    {
                        var fi = new FileInfo(file);
                        // Skip temporary, backup or lock files
                        if (fi.Name.StartsWith("~") || fi.Name.StartsWith(".") || file.IndexOf(@"\System\", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        string verKey = MapExtensionToVersionKey(ext);
                        var matchedVer = availableVersions.FirstOrDefault(v => string.Equals(v.VersionKey, verKey, StringComparison.OrdinalIgnoreCase));

                        list.Add(new BatchProjectItem
                        {
                            IsSelected = true,
                            ProjectName = Path.GetFileNameWithoutExtension(file),
                            FilePath = file,
                            VersionKey = verKey,
                            DisplayVersion = matchedVer?.DisplayName ?? $"TIA Portal {verKey}",
                            FileSize = $"{fi.Length / (1024.0 * 1024.0):F1} MB",
                            LastModified = fi.LastWriteTime,
                            Status = "Bekliyor"
                        });
                    }
                }
            }
            catch { }

            return list;
        }

        private static string MapExtensionToVersionKey(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".ap14" => "V14 SP1",
                ".ap15" => "V15",
                ".ap15_1" => "V15.1",
                ".ap16" => "V16",
                ".ap17" => "V17",
                ".ap18" => "V18",
                ".ap19" => "V19",
                ".ap20" => "V20",
                ".ap21" => "V21",
                _ => "V20"
            };
        }

        private void BtnSelectAllBatch_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in _batchProjects) p.IsSelected = true;
            UpdateButtonLabel();
        }

        private void BtnDeselectAllBatch_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in _batchProjects) p.IsSelected = false;
            UpdateButtonLabel();
        }

        #endregion

        #region Execution & Export (Single & Batch)

        private void BtnBrowseTarget_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Şablonların ve Yapay Zekâ Eğitim Verisinin Kaydedileceği Klasörü Seçin";
                dialog.SelectedPath = TxtTargetDir.Text;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TxtTargetDir.Text = dialog.SelectedPath;
                    SaveSettings();
                }
            }
        }

        private async void BtnStartExport_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl.SelectedIndex == 0)
            {
                await StartSingleExportAsync();
            }
            else
            {
                await StartBatchExportAsync();
            }
        }

        private async Task StartSingleExportAsync()
        {
            if (_selectedPlc == null || !_opennessService.IsConnected)
            {
                MessageBox.Show("Lütfen önce açık bir TIA Portal oturumuna bağlanın ve bir PLC seçin.", "Bağlantı Yok", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = GetExportConfig();

            _isExportRunning = true;
            BtnStartExport.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            BtnConnect.IsEnabled = false;
            BtnOpenFolder.IsEnabled = false;
            ProgressBarExport.Value = 0;
            TxtProgressPercent.Text = "%0";

            _cts = new CancellationTokenSource();

            try
            {
                AppendLog("===============================================================================");
                AppendLog($"🚀 [Şablon Çıkarımı Başlatıldı] Proje: {_currentProject?.Name} | PLC: {_selectedPlc.Name}");
                AppendLog("🔒 [GÜVENLİK] Salt okunur mod devrede. Orijinal projeye ASLA dokunulmaz.");
                AppendLog("===============================================================================");

                var result = await Task.Run(() => _opennessService.ExportProjectTemplatesAsync(
                    _currentProject,
                    _selectedPlc,
                    config,
                    msg => Dispatcher.Invoke(() => AppendLog(msg)),
                    (cur, max, status) => Dispatcher.Invoke(() =>
                    {
                        ProgressBarExport.Maximum = max;
                        ProgressBarExport.Value = cur;
                        int pct = max > 0 ? (int)((cur / (double)max) * 100) : 0;
                        TxtProgressPercent.Text = $"%{pct}";
                        TxtProgressStatus.Text = status;
                    }),
                    _cts.Token
                ));

                if (result.Success)
                {
                    _lastExportedDir = result.OutputDirectory;
                    BtnOpenFolder.IsEnabled = true;
                    TxtProgressStatus.Text = $"Tamamlandı! ({result.BlocksExported} blok, {result.UdtsExported} UDT, {result.TotalTags} tag)";

                    MessageBox.Show(
                        $"Şablon paketi başarıyla oluşturuldu!\n\n" +
                        $"• Program Blokları: {result.BlocksExported} adet (SCL: {result.SclFilesExtracted})\n" +
                        $"• Kullanıcı Veri Tipleri (UDT): {result.UdtsExported} adet\n" +
                        $"• PLC Tagleri: {result.TotalTags} adet ({result.TagTablesExported} tablo)\n" +
                        $"• AI Rehber: AI_Project_Summary.md ve AI_Training_Manifest.json\n\n" +
                        $"Konum: {result.OutputDirectory}",
                        "Şablon Çıkarımı Başarılı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                else
                {
                    TxtProgressStatus.Text = "Şablon çıkarma sırasında hata oluştu.";
                    MessageBox.Show($"Şablon çıkarma başarısız oldu:\n{result.ErrorMessage}", "İşlem Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[KRİTİK HATA] {ex.Message}");
                TxtProgressStatus.Text = "Hata oluştu.";
            }
            finally
            {
                _isExportRunning = false;
                BtnCancel.IsEnabled = false;
                BtnConnect.IsEnabled = true;
                UpdateButtonLabel();
            }
        }

        private async Task StartBatchExportAsync()
        {
            var selectedProjects = _batchProjects.Where(p => p.IsSelected).ToList();
            if (selectedProjects.Count == 0)
            {
                MessageBox.Show("Lütfen dışa aktarılacak en az bir proje seçin.", "Proje Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = GetExportConfig();

            _isExportRunning = true;
            BtnStartExport.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            BtnOpenFolder.IsEnabled = false;
            ProgressBarExport.Value = 0;
            TxtProgressPercent.Text = "%0";

            _cts = new CancellationTokenSource();

            AppendLog("===============================================================================");
            AppendLog($"🚀 [TOPLU DIŞA AKTARIM BAŞLATILDI] Toplam {selectedProjects.Count} adet proje işlenecektir.");
            AppendLog("🔒 [GÜVENLİK KURALI] Projeler salt okunur taranır. Asla kaydetme/değişiklik yapılmaz.");
            AppendLog("===============================================================================");

            int total = selectedProjects.Count;
            int current = 0;
            int successCount = 0;

            try
            {
                foreach (var projItem in selectedProjects)
                {
                    if (_cts.IsCancellationRequested) break;
                    current++;
                    projItem.Status = "Açılıyor...";

                    AppendLog($"\n>>> [{current}/{total}] Proje Açılıyor: {projItem.ProjectName} ({projItem.DisplayVersion})");
                    TxtProgressStatus.Text = $"[{current}/{total}] Proje açılıyor: {projItem.ProjectName}...";

                    var ver = _availableVersions.FirstOrDefault(v => string.Equals(v.VersionKey, projItem.VersionKey, StringComparison.OrdinalIgnoreCase))
                           ?? _selectedVersion;

                    TiaProjectItem proj = null;
                    try
                    {
                        proj = await Task.Run(() => _opennessService.OpenProjectFile(ver, projItem.FilePath));
                        if (proj == null)
                        {
                            projItem.Status = "Açılamadı ✗";
                            AppendLog($"[HATA] Proje açılamadı: {projItem.FilePath}");
                            continue;
                        }

                        projItem.Status = "PLC Taranıyor...";
                        TxtProgressStatus.Text = $"[{current}/{total}] PLC taranıyor: {projItem.ProjectName}...";

                        var plcs = await Task.Run(() => _opennessService.GetPlcList(proj));
                        if (plcs == null || plcs.Count == 0)
                        {
                            projItem.Status = "PLC Yok ✗";
                            AppendLog($"[Uyarı] '{projItem.ProjectName}' içinde programlanabilir PLC bulunamadı.");
                        }
                        else
                        {
                            projItem.Status = "Dışa Aktarılıyor...";
                            foreach (var plc in plcs)
                            {
                                if (_cts.IsCancellationRequested) break;
                                TxtProgressStatus.Text = $"[{current}/{total}] Aktarılıyor: {projItem.ProjectName} ({plc.Name})...";

                                var res = await Task.Run(() => _opennessService.ExportProjectTemplatesAsync(
                                    proj,
                                    plc,
                                    config,
                                    msg => Dispatcher.Invoke(() => AppendLog(msg)),
                                    (curP, maxP, s) => Dispatcher.Invoke(() =>
                                    {
                                        int overallPct = (int)(((current - 1 + (curP / (double)Math.Max(1, maxP))) / total) * 100);
                                        ProgressBarExport.Value = overallPct;
                                        TxtProgressPercent.Text = $"%{overallPct}";
                                    }),
                                    _cts.Token
                                ));

                                if (res.Success)
                                {
                                    _lastExportedDir = res.OutputDirectory;
                                }
                            }
                            projItem.Status = "Tamamlandı ✓";
                            successCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        projItem.Status = "Hata ✗";
                        AppendLog($"[HATA] '{projItem.ProjectName}' işlenirken hata: {ex.Message}");
                    }
                    finally
                    {
                        if (proj != null)
                        {
                            AppendLog($"[Güvenli Kapatma] '{projItem.ProjectName}' projesi değişiklik yapılmadan güvenle kapatılıyor...");
                            await Task.Run(() => _opennessService.CloseProject(proj));
                        }
                    }
                }

                ProgressBarExport.Value = 100;
                TxtProgressPercent.Text = "%100";
                TxtProgressStatus.Text = $"Toplu işlem bitti! ({successCount}/{total} proje başarıyla dışa aktarıldı)";
                BtnOpenFolder.IsEnabled = true;

                MessageBox.Show(
                    $"Toplu dışa aktarım işlemi tamamlandı!\n\n" +
                    $"• Toplam Proje: {total}\n" +
                    $"• Başarıyla Aktarılan: {successCount}\n" +
                    $"• Hedef Klasör: {config.TargetDirectory}\n\n" +
                    $"Tüm projeler orijinal halleri korunarak, tek bir byte dahi değiştirilmeden kapatılmıştır.",
                    "Toplu İşlem Tamamlandı",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                AppendLog($"[KRİTİK HATA] {ex.Message}");
            }
            finally
            {
                _isExportRunning = false;
                BtnCancel.IsEnabled = false;
                UpdateButtonLabel();
            }
        }

        private TemplateExportConfig GetExportConfig()
        {
            return new TemplateExportConfig
            {
                TargetDirectory = TxtTargetDir.Text?.Trim(),
                ExportBlocks = ChkExportBlocks.IsChecked == true,
                ExtractScl = ChkExtractScl.IsChecked == true,
                ExportUdts = ChkExportUdts.IsChecked == true,
                ExportTagTables = ChkExportTagTables.IsChecked == true,
                ExportHardware = ChkExportHardware.IsChecked == true,
                GenerateAiSummary = ChkGenerateAiSummary.IsChecked == true
            };
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                AppendLog("[İptal] Kullanıcı işlemi iptal etti. Mevcut işlem bitince durdurulacak...");
                TxtProgressStatus.Text = "İşlem iptal ediliyor...";
                BtnCancel.IsEnabled = false;
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dirToOpen = !string.IsNullOrEmpty(_lastExportedDir) && Directory.Exists(_lastExportedDir)
                    ? _lastExportedDir
                    : TxtTargetDir.Text;

                if (Directory.Exists(dirToOpen))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dirToOpen}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show("Açılacak klasör bulunamadı.", "Klasör Yok", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        private void AppendLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}\r\n";
            TxtLog.AppendText(line);
            TxtLog.ScrollToEnd();
        }

        #endregion
    }
}
