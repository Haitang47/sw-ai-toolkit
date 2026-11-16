using System;
using System.IO;

namespace SwKit.Core.Parts
{
    /// <summary>标准件库的保存位置：默认在"文档\SW工具箱\标准件"，按类别分子文件夹。</summary>
    public static class PartsLibrary
    {
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SW工具箱", "标准件");
    }
}
