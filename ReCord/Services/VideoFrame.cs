using System;

namespace ReCord.Services
{
    public sealed class VideoFrame
    {
        public byte[] Data { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public long Sequence { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
