using System;
using System.Xml.Serialization;

namespace ReCord.Models
{
    [Serializable]
    public sealed class CameraProfile
    {
        [XmlAttribute]
        public int Id { get; set; }

        [XmlAttribute]
        public string Name { get; set; }

        public string Url { get; set; }

        /// <summary>创建当前相机配置的独立副本。</summary>
        public CameraProfile Clone()
        {
            return new CameraProfile
            {
                Id = Id,
                Name = Name ?? string.Empty,
                Url = Url ?? string.Empty
            };
        }
    }
}
