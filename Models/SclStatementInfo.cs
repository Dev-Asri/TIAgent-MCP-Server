using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TIAgent.Models
{
    public class SclStatementInfo : INotifyPropertyChanged
    {
        private int _index;
        private string _title;
        private string _rawSclCode;
        private bool _canConvertToLad;
        private bool _keepAsScl;
        private string _conversionSummary;
        private string _generatedSimaticMlXml;
        private string _originalStructuredTextXml;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int Index
        {
            get => _index;
            set { if (_index != value) { _index = value; OnPropertyChanged(); } }
        }

        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }

        public string RawSclCode
        {
            get => _rawSclCode;
            set { if (_rawSclCode != value) { _rawSclCode = value; OnPropertyChanged(); } }
        }

        public bool CanConvertToLad
        {
            get => _canConvertToLad;
            set
            {
                if (_canConvertToLad != value)
                {
                    _canConvertToLad = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsLadActive));
                    OnPropertyChanged(nameof(CanToggleLanguage));
                    OnPropertyChanged(nameof(TargetLanguage));
                    OnPropertyChanged(nameof(LanguageStatusText));
                    OnPropertyChanged(nameof(BadgeBackground));
                    OnPropertyChanged(nameof(BadgeBorderBrush));
                    OnPropertyChanged(nameof(BadgeForeground));
                }
            }
        }

        public bool KeepAsScl
        {
            get => _keepAsScl;
            set
            {
                if (_keepAsScl != value)
                {
                    _keepAsScl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsLadActive));
                    OnPropertyChanged(nameof(TargetLanguage));
                    OnPropertyChanged(nameof(LanguageStatusText));
                    OnPropertyChanged(nameof(BadgeBackground));
                    OnPropertyChanged(nameof(BadgeBorderBrush));
                    OnPropertyChanged(nameof(BadgeForeground));
                }
            }
        }

        public bool IsLadActive
        {
            get => CanConvertToLad && !KeepAsScl;
            set
            {
                if (CanConvertToLad)
                {
                    KeepAsScl = !value;
                }
            }
        }

        public bool CanToggleLanguage => CanConvertToLad;

        public string TargetLanguage => IsLadActive ? "LAD" : "SCL";

        public string LanguageStatusText
        {
            get
            {
                if (IsLadActive) return "Ladder Network (FlgNet)";
                if (CanConvertToLad && KeepAsScl) return "SCL Network (Kullanıcı Seçimi)";
                return "SCL Network";
            }
        }

        public string BadgeBackground => IsLadActive ? "#F6FFED" : (CanConvertToLad && KeepAsScl ? "#FFF7E6" : "#FFFBE6");
        public string BadgeBorderBrush => IsLadActive ? "#B7EB8F" : (CanConvertToLad && KeepAsScl ? "#FFD591" : "#FFE58F");
        public string BadgeForeground => IsLadActive ? "#389E0D" : (CanConvertToLad && KeepAsScl ? "#D4380D" : "#D46B08");

        public string ConversionSummary
        {
            get => _conversionSummary;
            set { if (_conversionSummary != value) { _conversionSummary = value; OnPropertyChanged(); } }
        }

        public string GeneratedSimaticMlXml
        {
            get => _generatedSimaticMlXml;
            set { if (_generatedSimaticMlXml != value) { _generatedSimaticMlXml = value; OnPropertyChanged(); } }
        }

        public string OriginalStructuredTextXml
        {
            get => _originalStructuredTextXml;
            set { if (_originalStructuredTextXml != value) { _originalStructuredTextXml = value; OnPropertyChanged(); } }
        }
    }
}

