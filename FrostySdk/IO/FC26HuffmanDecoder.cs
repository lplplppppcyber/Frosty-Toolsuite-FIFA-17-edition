using System.Text;

namespace FrostySdk.IO
{
    internal class FC26HuffmanDecoder
    {
        private readonly int[] table;
        private readonly uint[] data;

        public FC26HuffmanDecoder(int[] table, uint[] data)
        {
            this.table = table;
            this.data = data;
        }

        public static uint[] ReadData(NativeReader reader, int count)
        {
            uint[] result = new uint[count];
            for (int i = 0; i < count; i++)
                result[i] = reader.ReadUInt(Endian.Big);
            return result;
        }

        public static int[] ReadTable(NativeReader reader, int count)
        {
            int[] result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = reader.ReadInt(Endian.Big);
            return result;
        }

        public string ReadString(int bitIndex)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int val = table.Length / 2 - 1;
                do
                {
                    uint bit = (data[bitIndex / 32] >> (bitIndex % 32)) & 1u;
                    val = table[val * 2 + (int)bit];
                    bitIndex++;
                }
                while (val >= 0);

                char c = (char)(-1 - val);
                if (c == '\0')
                    break;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
