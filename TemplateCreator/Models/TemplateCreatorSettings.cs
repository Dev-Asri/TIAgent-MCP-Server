using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace TIAgentTemplateCreator.Models
{
    public class TemplateCreatorSettings
    {
        public string TargetDirectory { get; set; }
        public string LastScannedBatchFolder { get; set; }
        public bool ExportBlocks { get; set; } = true;
        public bool ExtractScl { get; set; } = true;
        public bool ExportUdts { get; set; } = true;
        public bool ExportTagTables { get; set; } = true;
        public bool ExportHardware { get; set; } = true;
        public bool GenerateAiSummary { get; set; } = true;
        public string SelectedTiaVersion { get; set; }
        public int SelectedTab { get; set; } = 0;
    }

    public class BatchProjectItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _status = "Bekliyor";

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string ProjectName { get; set; }
        public string FilePath { get; set; }
        public string VersionKey { get; set; }
        public string DisplayVersion { get; set; }
        public string FileSize { get; set; }
        public DateTime LastModified { get; set; }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public static class TemplateCreatorSettingsService
    {
        private static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template_creator_settings.json");

        public static TemplateCreatorSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                    var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                    return ser.Deserialize<TemplateCreatorSettings>(json) ?? new TemplateCreatorSettings();
                }
            }
            catch { }
            return new TemplateCreatorSettings();
        }

        public static void Save(TemplateCreatorSettings settings)
        {
            try
            {
                if (settings == null) return;
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                string json = ser.Serialize(settings);
                File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
            }
            catch { }
        }
    }
}
