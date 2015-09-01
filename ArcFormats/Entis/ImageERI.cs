//! \file       ImageERI.cs
//! \date       Tue May 26 12:04:30 2015
//! \brief      Entis rasterized image format.
//
// Copyright (C) 2015 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Media;
using GameRes.Utility;

namespace GameRes.Formats.Entis
{
    internal class EriMetaData : ImageMetaData
    {
        public int      StreamPos;
        public int      Version;
        public CvType   Transformation;
        public EriCode  Architecture;
        public int      FormatType;
        public bool     VerticalFlip;
        public int      ClippedPixel;
        public int      SamplingFlags;
        public ulong    QuantumizedBits;
        public ulong    AllottedBits;
        public int      BlockingDegree;
        public int      LappedBlock;
        public int      FrameTransform;
        public int      FrameDegree;
    }

    public enum CvType
    {
        Lossless_ERI =  0x03020000,
        DCT_ERI      =  0x00000001,
        LOT_ERI      =  0x00000005,
        LOT_ERI_MSS  =  0x00000105,
    }

    public enum EriCode
    {
        RunlengthGamma      = -1,
        RunlengthHuffman    = -4,
        Nemesis             = -16,
    }

    public enum EriImage
    {
        RGB         = 0x00000001,
        RGBA        = 0x04000001,
        Gray        = 0x00000002,
        TypeMask    = 0x00FFFFFF,
        WithPalette = 0x01000000,
        UseClipping = 0x02000000,
        WithAlpha   = 0x04000000,
        SideBySide  = 0x10000000,
    }

    internal class EriFile : BinaryReader
    {
        internal struct Section
        {
            public AsciiString  Id;
            public long         Length;
        }

        public EriFile (Stream stream) : base (stream, System.Text.Encoding.ASCII, true)
        {
        }

        public Section ReadSection ()
        {
            var section = new Section();
            section.Id = new AsciiString (8);
            if (8 != this.Read (section.Id.Value, 0, 8))
                throw new EndOfStreamException();
            section.Length = this.ReadInt64();
            return section;
        }

        public long FindSection (string name)
        {
            var id = new AsciiString (8);
            for (;;)
            {
                if (8 != this.Read (id.Value, 0, 8))
                    throw new EndOfStreamException();
                var length = this.ReadInt64();
                if (length < 0)
                    throw new EndOfStreamException();
                if (id == name)
                    return length;
                this.BaseStream.Seek (length, SeekOrigin.Current);
            }
        }
    }

    [Export(typeof(ImageFormat))]
    public class EriFormat : ImageFormat
    {
        public override string         Tag { get { return "ERI"; } }
        public override string Description { get { return "Entis rasterized image format"; } }
        public override uint     Signature { get { return 0x69746e45u; } } // 'Enti'

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            byte[] header = new byte[0x40];
            if (header.Length != stream.Read (header, 0, header.Length))
                return null;
            if (0x03000100 != LittleEndian.ToUInt32 (header, 8))
                return null;
            if (!Binary.AsciiEqual (header, 0x10, "Entis Rasterized Image"))
                return null;
            using (var reader = new EriFile (stream))
            {
                var section = reader.ReadSection();
                if (section.Id != "Header  " || section.Length <= 0)
                    return null;
                int header_size = (int)section.Length;
                int stream_pos = 0x50 + header_size;
                EriMetaData info = null;
                while (header_size > 8)
                {
                    section = reader.ReadSection();
                    header_size -= 8;
                    if (section.Length <= 0 || section.Length > header_size)
                        break;
                    if ("ImageInf" == section.Id)
                    {
                        int version = reader.ReadInt32();
                        if (version != 0x00020100 && version != 0x00020200)
                            return null;
                        info = new EriMetaData { StreamPos = stream_pos, Version = version };
                        info.Transformation = (CvType)reader.ReadInt32();
                        info.Architecture = (EriCode)reader.ReadInt32();
                        info.FormatType = reader.ReadInt32();
                        int w = reader.ReadInt32();
                        int h = reader.ReadInt32();
                        info.Width  = (uint)Math.Abs (w);
                        info.Height = (uint)Math.Abs (h);
                        info.VerticalFlip = h < 0;
                        info.BPP = reader.ReadInt32();
                        info.ClippedPixel = reader.ReadInt32();
                        info.SamplingFlags = reader.ReadInt32();
                        info.QuantumizedBits = reader.ReadUInt64();
                        info.AllottedBits = reader.ReadUInt64();
                        info.BlockingDegree = reader.ReadInt32();
                        info.LappedBlock = reader.ReadInt32();
                        info.FrameTransform = reader.ReadInt32();
                        info.FrameDegree = reader.ReadInt32();
                        break;
                    }
                    header_size -= (int)section.Length;
                    reader.BaseStream.Seek (section.Length, SeekOrigin.Current);
                }
                return info;
            }
        }

        public override ImageData Read (Stream stream, ImageMetaData info)
        {
            var meta = info as EriMetaData;
            if (null == meta)
                throw new ArgumentException ("EriFormat.Read should be supplied with EriMetaData", "info");
            stream.Position = meta.StreamPos;
            using (var input = new EriFile (stream))
            {
                Color[] palette = null;
                for (;;) // ReadSection throws an exception in case of EOF
                {
                    var section = input.ReadSection();
                    if ("Stream  " == section.Id)
                        continue;
                    if ("ImageFrm" == section.Id)
                        break;
                    if ("Palette " == section.Id && info.BPP <= 8 && section.Length <= 0x400)
                    {
                        palette = ReadPalette (stream, (int)section.Length);
                        continue;
                    }
                    input.BaseStream.Seek (section.Length, SeekOrigin.Current);
                }
                var reader = new EriReader (stream, meta, palette);
                reader.DecodeImage();
                return ImageData.Create (info, reader.Format, reader.Palette, reader.Data, reader.Stride);
            }
        }

        private Color[] ReadPalette (Stream input, int palette_length)
        {
            var palette_data = new byte[0x400];
            if (palette_length > palette_data.Length)
                throw new InvalidFormatException();
            if (palette_length != input.Read (palette_data, 0, palette_length))
                throw new InvalidFormatException();
            var colors = new Color[256];
            for (int i = 0; i < 256; ++i)
            {
                colors[i] = Color.FromRgb (palette_data[i*4+2], palette_data[i*4+1], palette_data[i*4]);
            }
            return colors;
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("EriFormat.Write not implemented");
        }
    }
}