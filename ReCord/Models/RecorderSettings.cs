using System;
using System.IO;
using System.Xml.Serialization;

namespace ReCord.Models
{
    [Serializable]
    public sealed class RecorderSettings
    {
        public string OutputDirectory { get; set; }

        [XmlAttribute]
        public string FileNamePrefix { get; set; }

        [XmlAttribute]
        public int PreferredFps { get; set; }

        [XmlAttribute]
        public int VideoBitrateKbps { get; set; }

        [XmlAttribute]
        public bool AutoStartRecording { get; set; }

        /// <summary>创建使用默认输出目录和编码参数的录制设置。</summary>
        public static RecorderSettings CreateDefault()
        {
            return new RecorderSettings
            {
                OutputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings"),
                FileNamePrefix = "ReCord",
                PreferredFps = 25,
                VideoBitrateKbps = 4000,
                AutoStartRecording = false
            };
        }

        /// <summary>将无效或缺失的录制设置恢复为有效默认值。</summary>
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(OutputDirectory))
                OutputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings");
            if (string.IsNullOrWhiteSpace(FileNamePrefix))
                FileNamePrefix = "ReCord";
            if (PreferredFps < 1 || PreferredFps > 60)
                PreferredFps = 25;
            if (VideoBitrateKbps < 500 || VideoBitrateKbps > 30000)
                VideoBitrateKbps = 4000;
        }
    }
}
