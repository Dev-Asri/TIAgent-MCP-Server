using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TIAgent.Services;

namespace TIAgent.Models
{
    public class SclBlockItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        private string _status = "Hazır";
        private string _originalSclSourceCode;
        private string _sclSourceCode;
        private int _lineCount;
        private string _summary;

        public string Name { get; set; }
        public string BlockType { get; set; } // "FC", "FB", "OB", "DB"
        public int? BlockNumber { get; set; }
        public string ProgrammingLanguage { get; set; } = "SCL";
        public string GroupPath { get; set; }
        public object RawBlockObject { get; set; }
        public object RawPlcSoftware { get; set; }
        public object RawParentGroup { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string OriginalSclSourceCode
        {
            get => _originalSclSourceCode;
            set
            {
                _originalSclSourceCode = value;
                OnPropertyChanged();
            }
        }

        public string SclSourceCode
        {
            get => _sclSourceCode;
            set
            {
                _sclSourceCode = value;
                OnPropertyChanged();
                LineCount = string.IsNullOrEmpty(value) ? 0 : value.Split('\n').Length;
            }
        }

        public int LineCount
        {
            get => _lineCount;
            set { _lineCount = value; OnPropertyChanged(); }
        }

        public string Summary
        {
            get => _summary;
            set { _summary = value; OnPropertyChanged(); }
        }

        public List<SclStatementInfo> Statements { get; set; } = new List<SclStatementInfo>();
        public ParsedSclBlock ParsedBlock { get; set; }
        public string ConvertedLadXml { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

        public override string ToString()
        {
            string numStr = BlockNumber.HasValue ? $" [{BlockType}{BlockNumber.Value}]" : "";
            return $"{BlockType} \"{Name}\"{numStr} ({ProgrammingLanguage})";
        }
    }
}

