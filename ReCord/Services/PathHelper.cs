using System;
using System.IO;
using System.Text;

namespace ReCord.Services
{
    public static class PathHelper
    {
        /// <summary>将文本转换为可安全用于文件名的片段。</summary>
        public static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "camera";

            StringBuilder builder = new StringBuilder(value.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in value.Trim())
            {
                if (Array.IndexOf(invalid, c) < 0 && c != ' ')
                    builder.Append(c);
                else if (c == ' ' && builder.Length > 0 && builder[builder.Length - 1] != '_')
                    builder.Append('_');
            }
            return builder.Length == 0 ? "camera" : builder.ToString();
        }
    }
}
