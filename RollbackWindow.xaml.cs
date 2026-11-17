using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using TIAgent.Models;
using TIAgent.Services;

namespace TIAgent
{
    public partial class RollbackWindow : Window
    {
        private readonly string _backupRootDir;
        private readonly TiaOpennessService _tiaService;
        private readonly TiaPlcItem _plc;
        private readonly Action<string, string, bool> _stepCallback;
        private readonly ObservableCollection<BackupOperationItem> _operations = new ObservableCollection<BackupOperationItem>();
        private string _currentLanguage = "tr";

        public RollbackWindow(
            string backupRootDir,
            TiaOpennessService tiaService,
            TiaPlcItem plc,
            Action<string, string, bool> stepCallback,
            string initialLanguage = "tr")
        {
            InitializeComponent();
            _backupRootDir = backupRootDir;
            _tiaService = tiaService;
            _plc = plc;
            _stepCallback = stepCallback;
            _currentLanguage = initialLanguage;

            DgOperations.ItemsSource = _operations;
            Loaded += (s, e) =>
            {
                ApplyLanguage(_currentLanguage);
                LoadOperations();
            };
        }

        public void ApplyLanguage(string lang)
        {
            _currentLanguage = lang;
            bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

            Title = isEn
                ? "TIAgent — Operation History & Rollback Center"
                : "TIAgent — İşlem Geçmişi & Geri Alma (Rollback Center)";

            if (LblWindowTitle != null)
                LblWindowTitle.Text = isEn
                    ? "Operation History & Rollback Center"
                    : "İşlem Geçmişi & Geri Alma Merkezi";

            if (LblWindowSubtitle != null)
                LblWindowSubtitle.Text = isEn
                    ? "Time and operation-based backups are listed. Use 'Rollback' to restore TIA Portal blocks to their pre-operation state."
                    : "Zaman ve işlem bazlı yedekler listelenir. 'Geri Al' ile TIA Portal'daki bloklar işlem öncesine döndürülür.";

            if (BtnRefresh != null)
                BtnRefresh.Content = isEn ? "🔄 Refresh" : "🔄 Yenile";

            if (BtnClearAllBackups != null)
                BtnClearAllBackups.Content = isEn ? "🗑️ Clear All Backups" : "🗑️ Tüm Yedekleri Temizle";

            if (BtnClose != null)
                BtnClose.Content = isEn ? "Close" : "Kapat";

            if (ColTimestamp != null) ColTimestamp.Header = isEn ? "TIMESTAMP" : "ZAMAN";
            if (ColOpName != null) ColOpName.Header = isEn ? "OPERATION NAME" : "İŞLEM ADI";
            if (ColPrompt != null) ColPrompt.Header = isEn ? "COMMAND / PROMPT" : "KOMUT / TALEP";
            if (ColSummary != null) ColSummary.Header = isEn ? "BLOCK DETAILS" : "BLOK BİLGİSİ";
            if (ColStatus != null) ColStatus.Header = isEn ? "STATUS" : "DURUM";
            if (ColAction != null) ColAction.Header = isEn ? "ACTION" : "EYLEM";

            foreach (var op in _operations)
            {
                op.Language = lang;
            }

            if (_operations.Count > 0)
            {
                TxtStatus.Text = isEn
                    ? $"Found {_operations.Count} operation record(s). You can roll back any operation with a single click."
                    : $"Toplam {_operations.Count} işlem kaydı bulundu. İstenen işlemi tek tıkla geri alabilirsiniz.";
            }
            else
            {
                TxtStatus.Text = isEn
                    ? "No operation or backup records found."
                    : "Kayıtlı işlem veya yedek bulunamadı.";
            }
        }

        private void LoadOperations()
        {
            _operations.Clear();
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (!Directory.Exists(_backupRootDir))
                {
                    Directory.CreateDirectory(_backupRootDir);
                }

                var dirs = Directory.GetDirectories(_backupRootDir);
                var items = new List<BackupOperationItem>();
                var serializer = new JavaScriptSerializer();

                foreach (var dir in dirs)
                {
                    string jsonFile = Path.Combine(dir, "operation.json");
                    if (File.Exists(jsonFile))
                    {
                        try
                        {
                            string json = File.ReadAllText(jsonFile, Encoding.UTF8);
                            var item = serializer.Deserialize<BackupOperationItem>(json);
                            if (item != null)
                            {
                                item.BackupFolderPath = dir;
                                item.Language = _currentLanguage;
                                items.Add(item);
                                continue;
                            }
                        }
                        catch { }
                    }

                    // Fallback for folders created before manifest was implemented
                    string dirName = Path.GetFileName(dir);
                    var xmlFiles = Directory.GetFiles(dir, "*.xml")
                        .Select(f => Path.GetFileNameWithoutExtension(f))
                        .ToList();

                    var fallbackItem = new BackupOperationItem
                    {
                        Id = dirName,
                        Timestamp = Directory.GetCreationTime(dir).ToString("yyyy-MM-dd HH:mm:ss"),
                        OperationName = dirName,
                        PackageName = dirName,
                        Prompt = isEn ? "(Legacy backup record)" : "(Eski yedek kaydı)",
                        SourceChannel = isEn ? "System" : "Sistem",
                        PlcName = _plc?.Name ?? "-",
                        BackupFolderPath = dir,
                        BackedUpBlocks = xmlFiles,
                        Language = _currentLanguage
                    };
                    items.Add(fallbackItem);
                }

                // Newest first
                foreach (var it in items.OrderByDescending(x => x.Timestamp))
                {
                    _operations.Add(it);
                }

                TxtStatus.Text = _operations.Count > 0
                    ? (isEn
                        ? $"Found {_operations.Count} operation record(s). You can roll back any operation with a single click."
                        : $"Toplam {_operations.Count} işlem kaydı bulundu. İstenen işlemi tek tıkla geri alabilirsiniz.")
                    : (isEn ? "No operation or backup records found." : "Kayıtlı işlem veya yedek bulunamadı.");
            }
            catch (Exception ex)
            {
                TxtStatus.Text = (isEn ? "Error scanning backups: " : "Yedekler taranırken hata: ") + ex.Message;
            }
        }

        private async void BtnRollbackItem_Click(object sender, RoutedEventArgs e)
        {
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);

            if (_plc == null || _plc.RawPlcSoftware == null)
            {
                MessageBox.Show(
                    isEn ? "TIA Portal and PLC connection must be active to roll back an operation." : "İşlemi geri alabilmek için TIA Portal ve PLC bağlantısının aktif olması gerekir.",
                    isEn ? "Connection Required" : "Bağlantı Gerekli",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is BackupOperationItem item)
            {
                string msg = isEn
                    ? $"Are you sure you want to roll back this operation?\n\n" +
                      $"Operation: {item.OperationName}\n" +
                      $"Time: {item.Timestamp}\n" +
                      $"Command: {item.Prompt}\n\n" +
                      $"• New blocks created in this operation will be removed.\n" +
                      $"• Backed up original blocks ({string.Join(", ", item.BackedUpBlocks)}) will be restored to TIA Portal.\n" +
                      $"• The project will be automatically compiled."
                    : $"Bu işlemi geri almak istediğinizden emin misiniz?\n\n" +
                      $"İşlem: {item.OperationName}\n" +
                      $"Zaman: {item.Timestamp}\n" +
                      $"Komut: {item.Prompt}\n\n" +
                      $"• Bu işlemde oluşturulmuş yeni bloklar kaldırılacaktır.\n" +
                      $"• Yedeklenen orijinal bloklar ({string.Join(", ", item.BackedUpBlocks)}) TIA Portal'a geri yüklenecektir.\n" +
                      $"• Proje otomatik olarak derlenecektir.";

                var res = MessageBox.Show(msg, isEn ? "Rollback Operation (Confirmation)" : "İşlemi Geri Al (Rollback Onayı)", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;

                IsEnabled = false;
                Cursor = System.Windows.Input.Cursors.Wait;
                TxtStatus.Text = isEn ? $"Rolling back '{item.OperationName}'..." : $"'{item.OperationName}' işlemi geri alınıyor...";

                try
                {
                    string err = "";
                    bool ok = await Task.Run(() => _tiaService.RollbackBackupOperation(_plc, item, _stepCallback, out err));

                    if (ok)
                    {
                        MessageBox.Show(
                            isEn ? $"Operation '{item.OperationName}' successfully rolled back and TIA Portal project compiled." : $"'{item.OperationName}' işlemi başarıyla geri alındı ve TIA Portal projesi derlendi.",
                            isEn ? "Rollback Successful" : "Geri Alma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                        LoadOperations();
                    }
                    else
                    {
                        MessageBox.Show(
                            isEn ? $"Error during rollback:\n{err}" : $"Geri alma işlemi sırasında hata oluştu:\n{err}",
                            isEn ? "Rollback Error" : "Geri Alma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        (isEn ? "Rollback error: " : "Geri alma hatası: ") + ex.Message,
                        isEn ? "Critical Error" : "Kritik Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    IsEnabled = true;
                    Cursor = System.Windows.Input.Cursors.Arrow;
                }
            }
        }

        private void BtnClearAllBackups_Click(object sender, RoutedEventArgs e)
        {
            bool isEn = string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase);

            if (_operations.Count == 0)
            {
                MessageBox.Show(
                    isEn ? "No backup records to clear." : "Temizlenecek herhangi bir yedek kaydı bulunmuyor.",
                    isEn ? "Info" : "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var res = MessageBox.Show(
                isEn ? "Permanently delete all operation history and backup records?\n\n(Note: This does not affect your project in TIA Portal, it only clears local rollback files)" : "Tüm işlem geçmişi ve yedek kayıtları kalıcı olarak silinsin mi?\n\n(Not: Bu işlem TIA Portal'daki projenizi etkilemez, yalnızca yerel geri alma dosyalarını temizler)",
                isEn ? "Clear All Backups" : "Tüm Yedekleri Temizle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            try
            {
                if (Directory.Exists(_backupRootDir))
                {
                    foreach (var dir in Directory.GetDirectories(_backupRootDir))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
                LoadOperations();
                MessageBox.Show(
                    isEn ? "All operation records and backups successfully cleared." : "Tüm işlem kayıtları ve yedekler başarıyla temizlendi.",
                    isEn ? "Cleared" : "Temizlendi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    (isEn ? "Error during cleanup: " : "Temizleme sırasında hata: ") + ex.Message,
                    isEn ? "Error" : "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadOperations();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
