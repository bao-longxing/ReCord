using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace ReCord.Models
{
    [Serializable]
    [XmlRoot("ReCordConfiguration")]
    public sealed class ReCordConfiguration
    {
        [XmlElement("Settings")]
        public RecorderSettings Settings { get; set; }

        [XmlArray("Cameras")]
        [XmlArrayItem("Camera")]
        public List<CameraProfile> Cameras { get; set; }

        /// <summary>创建包含默认录制设置和空相机列表的配置。</summary>
        public static ReCordConfiguration CreateDefault()
        {
            return new ReCordConfiguration
            {
                Settings = RecorderSettings.CreateDefault(),
                Cameras = new List<CameraProfile>()
            };
        }
    }
}
