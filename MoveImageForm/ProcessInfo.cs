using System.Xml.Serialization;

namespace MoveImageForm
{
    public class ProcessInfo
    {
        [XmlElement]
        public string Name { get; set; } = "";

        [XmlElement]
        public string Path { get; set; } = "";

        [XmlElement]
        public bool Enabled { get; set; } = true;
    }
}
