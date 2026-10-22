using System;
using System.IO;
using System.Xml.Serialization;
using ReCord.Models;

namespace ReCord.Services
{
    public sealed class ConfigurationStore
    {
        private readonly string _path;
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(ReCordConfiguration));

        /// <summary>初始化程序目录中的配置文件存储。</summary>
        public ConfigurationStore()
        {
            _path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReCord.config.xml");
        }

        public string Path { get { return _path; } }

        /// <summary>读取并规范化配置；读取失败时返回默认配置。</summary>
        public ReCordConfiguration Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return ReCordConfiguration.CreateDefault();

                using (FileStream stream = File.OpenRead(_path))
                {
                    ReCordConfiguration config = (ReCordConfiguration)_serializer.Deserialize(stream);
                    Normalize(config);
                    return config;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("读取配置失败，使用默认配置：" + ex.Message);
                return ReCordConfiguration.CreateDefault();
            }
        }

        /// <summary>以原子替换方式保存配置并记录失败信息。</summary>
        public void Save(ReCordConfiguration config)
        {
            if (config == null)
                return;

            Normalize(config);
            string temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
                using (FileStream stream = File.Create(temporary))
                {
                    _serializer.Serialize(stream, config);
                    stream.Flush();
                }
                if (File.Exists(_path))
                    File.Replace(temporary, _path, null);
                else
                    File.Move(temporary, _path);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                AppLog.Error("保存配置失败：" + ex.Message);
            }
        }

        /// <summary>补齐配置中的空集合和缺失字段。</summary>
        private static void Normalize(ReCordConfiguration config)
        {
            if (config.Settings == null)
                config.Settings = RecorderSettings.CreateDefault();
            config.Settings.Normalize();

            if (config.Cameras == null)
                config.Cameras = new System.Collections.Generic.List<CameraProfile>();

            for (int i = 0; i < config.Cameras.Count; i++)
            {
                CameraProfile camera = config.Cameras[i];
                if (camera.Name == null)
                    camera.Name = "Camera " + camera.Id;
                if (camera.Url == null)
                    camera.Url = string.Empty;
            }
        }
    }
}
