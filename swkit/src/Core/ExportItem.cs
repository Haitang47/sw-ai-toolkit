using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace SwKit.Core
{
    public enum ItemStatus { Waiting, Running, Done, Warning, Failed, Skipped }

    /// <summary>导出队列里的一个文件。属性变化会通知界面刷新。</summary>
    public sealed class ExportItem : INotifyPropertyChanged
    {
        public ExportItem(string path)
        {
            Path = path;
            Kind = ExportFormats.KindFromPath(path);
        }

        public string Path { get; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path);
        public DocKind Kind { get; }
        public string KindText => ExportFormats.KindText(Kind);
        public List<string> Outputs { get; } = new List<string>();

        ItemStatus _status = ItemStatus.Waiting;
        public ItemStatus Status
        {
            get => _status;
            set { _status = value; Raise(nameof(Status)); Raise(nameof(StatusText)); }
        }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case ItemStatus.Running: return "导出中…";
                    case ItemStatus.Done: return "完成";
                    case ItemStatus.Warning: return "部分完成";
                    case ItemStatus.Failed: return "失败";
                    case ItemStatus.Skipped: return "已跳过";
                    default: return "等待";
                }
            }
        }

        string _message = "";
        public string Message
        {
            get => _message;
            set { _message = value ?? ""; Raise(nameof(Message)); }
        }

        public void Reset()
        {
            Outputs.Clear();
            Status = ItemStatus.Waiting;
            Message = "";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
