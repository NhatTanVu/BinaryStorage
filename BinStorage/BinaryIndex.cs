using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BinStorage
{
    [Serializable]
    public class BinaryIndex
    {
        /// <summary>
        /// Reference of the stream within the Storage file.
        /// </summary>
        public BinaryReference Reference { get; set; }

        /// <summary>
        /// All information of the stream required for a normal operation of the Binary Storage.
        /// </summary>
        public StreamInfo Information { get; set; }
    }

    [Serializable]
    public class BinaryReference
    {
        /// <summary>
        /// Byte offset within the Storage file.
        /// </summary>
        public long Offset { get; set; }

        /// <summary>
        /// Length of the stream.
        /// </summary>
        public long Length { get; set; }
    }
}
