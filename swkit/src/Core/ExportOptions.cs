using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SwKit.Core
{
    /// <summary>导出设置。会保存到 %APPDATA%\SwKit\export-options.json，下次打开时恢复。</summary>
    [DataContract]
    public sealed class ExportOptions
    {
        [DataMember] public List<string> Formats;
        /// <summary>STEP 协议：203 或 214（实测 242 不生效，SolidWorks 会输出 AP203）。</summary>
        [DataMember] public int StepAP;
        /// <summary>STL 精度："coarse" 粗糙 或 "fine" 精细。</summary>
        [DataMember] public string StlQuality;
        [DataMember] public bool StlBinary;
        /// <summary>保持模型原坐标（不平移到正空间）。导出 URDF 网格、多零件对位打印时必须保持。</summary>
        [DataMember] public bool StlKeepCoordinates;
        /// <summary>装配体导出 STL 时合并为一个文件；否则每个零部件一个文件。</summary>
        [DataMember] public bool StlAssemblyOneFile;
        /// <summary>STL 单位："mm" 或 "m"。</summary>
        [DataMember] public string MeshUnits;
        [DataMember] public bool FlatIncludeBendLines;
        [DataMember] public bool OutputToSourceFolder;
        [DataMember] public string OutputFolder;
        [DataMember] public bool SubfolderPerFormat;
        [DataMember] public bool AllConfigurations;
        [DataMember] public bool Overwrite;
        /// <summary>
        /// 0 = 独立后台 SolidWorks（界面和命令行都只用这个）。
        /// 1 = 使用用户已打开的 SolidWorks：只保留在代码里，不对外提供——实测在用户正在工作的 SolidWorks 里批量导出曾导致它崩溃。
        /// </summary>
        [DataMember] public int Mode;
        [DataMember] public bool ShowLaunchedSw;
        [DataMember] public bool CloseLaunchedSw;
        [DataMember] public bool IncludeSubfolders;

        public ExportOptions() => SetDefaults();

        [OnDeserializing]
        void OnDeserializing(StreamingContext _) => SetDefaults();

        void SetDefaults()
        {
            Formats = new List<string> { "step", "stl" };
            StepAP = 214;
            StlQuality = "fine";
            StlBinary = true;
            StlKeepCoordinates = true;
            StlAssemblyOneFile = true;
            MeshUnits = "mm";
            FlatIncludeBendLines = false;
            OutputToSourceFolder = true;
            OutputFolder = "";
            SubfolderPerFormat = false;
            AllConfigurations = false;
            Overwrite = true;
            Mode = (int)SwMode.Isolated;
            ShowLaunchedSw = false;
            CloseLaunchedSw = true;
            IncludeSubfolders = true;
        }

        static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwKit", "export-options.json");

        public static ExportOptions Load()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    using (var fs = File.OpenRead(StorePath))
                    {
                        var o = (ExportOptions)new DataContractJsonSerializer(typeof(ExportOptions)).ReadObject(fs);
                        if (o.Formats == null) o.Formats = new List<string>();
                        return o;
                    }
                }
            }
            catch { /* 设置文件损坏时用默认值 */ }
            return new ExportOptions();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                using (var ms = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(ExportOptions)).WriteObject(ms, this);
                    File.WriteAllText(StorePath, Encoding.UTF8.GetString(ms.ToArray()), new UTF8Encoding(false));
                }
            }
            catch { /* 保存失败不影响导出 */ }
        }

        public ExportOptions Clone()
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(ExportOptions));
                ser.WriteObject(ms, this);
                ms.Position = 0;
                return (ExportOptions)ser.ReadObject(ms);
            }
        }
    }
}
