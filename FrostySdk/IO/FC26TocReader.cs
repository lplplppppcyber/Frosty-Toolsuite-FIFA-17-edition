using System;
using System.IO;
using System.Text;

namespace FrostySdk.IO
{
    // Reads FC26 self-describing TocEntry binary format (layout.toc / chunk meta).
    // Header V0/V1: 556-byte prefix then entry stream. V3: no header, reads from current position.
    internal class FC26TocReader
    {
        private const int TypeInvalid   = 0;
        private const int TypeList      = 1;
        private const int TypeObject    = 2;
        private const int TypeBoolean   = 6;
        private const int TypeString    = 7;
        private const int TypeInt       = 8;
        private const int TypeLong      = 9;
        private const int TypeFloat     = 11;
        private const int TypeDouble    = 12;
        private const int TypeGuid      = 15;
        private const int TypeSha1      = 16;
        private const int TypeByteArray = 19;

        private const int HeaderV0 = 13749760;
        private const int HeaderV1 = 13749761;

        public DbObject Read(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                return Read(stream, hasHeader: true);
        }

        public DbObject Read(Stream stream, bool hasHeader = true)
        {
            // NativeReader wraps the stream; do NOT dispose it here (would close stream).
            NativeReader reader = new NativeReader(stream);
            if (hasHeader)
                SkipHeader(reader);

            string name;
            object value;
            ReadEntry(reader, out name, out value);
            return value as DbObject;
        }

        private static void SkipHeader(NativeReader reader)
        {
            int magic = reader.ReadInt(Endian.Big);
            if (magic == HeaderV0 || magic == HeaderV1)
            {
                if (magic == HeaderV1)
                {
                    // V1 has an XOR key at offset 296 (260 bytes) – not needed for reading
                    reader.Position = 296;
                    byte[] key = reader.ReadBytes(260);
                    for (int i = 0; i < key.Length; i++)
                        key[i] ^= 0x7B;
                }
                reader.Position = 556;
            }
            // V3: no skip – data starts immediately after the 4-byte magic we already read.
            // We don't seek back, so V3 is not handled (layout.toc is V0/V1).
        }

        // Reads one entry: prefix byte → type check → optional name → typed value.
        // TypeInvalid is checked BEFORE reading the name (matches fetsource TocReader exactly).
        private bool ReadEntry(NativeReader reader, out string name, out object value)
        {
            int prefix = reader.ReadByte();
            int type   = prefix & 0x1F;

            name  = string.Empty;
            value = null;

            if (type == TypeInvalid)
                return false;

            if ((prefix & 0x80) == 0)
                name = reader.ReadNullTerminatedString();

            value = ReadValue(reader, type);
            return true;
        }

        private object ReadValue(NativeReader reader, int type)
        {
            switch (type)
            {
                case TypeList:
                {
                    long length   = reader.Read7BitEncodedLong();
                    long startPos = reader.Position;
                    DbObject list = DbObject.CreateList();
                    while (reader.Position - startPos < length)
                    {
                        string n;
                        object v;
                        if (!ReadEntry(reader, out n, out v))
                            break;
                        list.Add(v);
                    }
                    return list;
                }

                case TypeObject:
                {
                    long length   = reader.Read7BitEncodedLong();
                    long startPos = reader.Position;
                    DbObject obj  = DbObject.CreateObject();
                    while (reader.Position - startPos < length)
                    {
                        string n;
                        object v;
                        if (!ReadEntry(reader, out n, out v))
                            break;
                        obj.AddValue(n, v);
                    }
                    return obj;
                }

                case TypeBoolean:
                    return reader.ReadByte() == 1;

                case TypeString:
                {
                    int len = reader.Read7BitEncodedInt();
                    return Encoding.UTF8.GetString(reader.ReadBytes(len)).TrimEnd('\0');
                }

                case TypeInt:
                    return reader.ReadInt(Endian.Little);

                case TypeLong:
                    return reader.ReadLong(Endian.Little);

                case TypeFloat:
                    return reader.ReadFloat(Endian.Little);

                case TypeDouble:
                    return reader.ReadDouble(Endian.Little);

                case TypeGuid:
                    return reader.ReadGuid(Endian.Little);

                case TypeSha1:
                    return reader.ReadSha1();

                case TypeByteArray:
                {
                    int len = reader.Read7BitEncodedInt();
                    return reader.ReadBytes(len);
                }

                default:
                    return null;
            }
        }
    }
}
