// Copyright (c) 2026 Andi Arsal Nur. MIT License.
using System;
using System.Collections.Generic;
using System.IO;

namespace Llacc
{
	/// <summary>BMP 24-bit tanpa kompresi. Header, byte pengisi baris, dan ekstensi file disimpan apa adanya.</summary>
	sealed class BmpImage
	{
		/// <summary>Batas piksel (sekitar 100 megapiksel) agar kebutuhan memori tetap wajar.</summary>
		const int MaxPixels = 100000000;

		public int Width, Rows;
		public int[] Pixels;          // urutan sesuai isi file; nilai = B | G<<8 | R<<16
		byte[] header, padding, tail;

		static int PadFor(int width) { return (4 - (int)(width * 3L % 4)) % 4; }

		/// <summary>null bila data bukan BMP yang didukung.</summary>
		public static BmpImage TryParse(byte[] f)
		{
			if (f.Length < 54 || f[0] != 'B' || f[1] != 'M') return null;
			int offset = BitConverter.ToInt32(f, 10), headerSize = BitConverter.ToInt32(f, 14);
			int width = BitConverter.ToInt32(f, 18), height = BitConverter.ToInt32(f, 22);
			int bits = BitConverter.ToInt16(f, 28), compression = BitConverter.ToInt32(f, 30);
			if (headerSize < 40 || bits != 24 || compression != 0 || width <= 0 || height == 0 || height == int.MinValue) return null;
			int rows = Math.Abs(height);
			if ((long)width * rows > MaxPixels) return null;
			long stride = width * 3L + PadFor(width);
			if (offset < 54 || offset > f.Length || stride * rows > f.Length - offset) return null;

			BmpImage b = new BmpImage();
			b.Width = width;
			b.Rows = rows;
			b.header = new byte[offset];
			Array.Copy(f, b.header, offset);
			b.Pixels = new int[width * rows];
			int pad = PadFor(width);
			b.padding = new byte[pad * rows];
			for (int y = 0; y < rows; y++)
			{
				int at = offset + (int)(y * stride);
				for (int x = 0; x < width; x++, at += 3) b.Pixels[y * width + x] = f[at] | (f[at + 1] << 8) | (f[at + 2] << 16);
				Array.Copy(f, at, b.padding, y * pad, pad);
			}
			int end = offset + (int)(stride * rows);
			b.tail = new byte[f.Length - end];
			Array.Copy(f, end, b.tail, 0, b.tail.Length);
			return b;
		}

		/// <summary>Tabel warna terurut menurut kecerahan, atau null bila warnanya lebih dari 256.</summary>
		public int[] FindPalette()
		{
			HashSet<int> unique = new HashSet<int>();
			foreach (int color in Pixels)
				if (unique.Add(color) && unique.Count > 256) return null;
			int[] palette = new int[unique.Count];
			unique.CopyTo(palette);
			Array.Sort(palette, CompareBrightness);
			return palette;
		}

		static int Brightness(int c) { return (c & 0xFF) * 11 + ((c >> 8) & 0xFF) * 59 + ((c >> 16) & 0xFF) * 30; }
		static int CompareBrightness(int a, int b) { int d = Brightness(a) - Brightness(b); return d != 0 ? d : a - b; }

		public static void WriteColor(BinaryWriter w, int c) { w.Write((byte)c); w.Write((byte)(c >> 8)); w.Write((byte)(c >> 16)); }
		public static int ReadColor(BinaryReader r) { int a = r.ReadByte(), b = r.ReadByte(), c = r.ReadByte(); return a | (b << 8) | (c << 16); }

		public void WriteShell(BinaryWriter w)
		{
			w.Write(Width); w.Write(Rows);
			w.Write(header.Length); w.Write(header);
			w.Write(padding);
			w.Write(tail.Length); w.Write(tail);
		}

		/// <summary>Semua ukuran diperiksa terhadap ukuran file asli sebelum memori dialokasikan.</summary>
		public static BmpImage ReadShell(BinaryReader r, int originalSize)
		{
			BmpImage b = new BmpImage();
			b.Width = r.ReadInt32();
			b.Rows = r.ReadInt32();
			if (b.Width <= 0 || b.Rows <= 0 || (long)b.Width * b.Rows > MaxPixels) throw LlaccCodec.Damaged();
			int pad = PadFor(b.Width);
			long body = (b.Width * 3L + pad) * b.Rows;
			if (body > originalSize) throw LlaccCodec.Damaged();
			int headerLength = r.ReadInt32();
			if (headerLength < 0 || headerLength > originalSize - body) throw LlaccCodec.Damaged();
			b.header = Exactly(r, headerLength);
			b.padding = Exactly(r, pad * b.Rows);
			int tailLength = r.ReadInt32();
			if (tailLength < 0 || headerLength + body + tailLength != originalSize) throw LlaccCodec.Damaged();
			b.tail = Exactly(r, tailLength);
			b.Pixels = new int[b.Width * b.Rows];
			return b;
		}

		static byte[] Exactly(BinaryReader r, int count)
		{
			byte[] data = r.ReadBytes(count);
			if (data.Length != count) throw LlaccCodec.Damaged();
			return data;
		}

		public byte[] Build()
		{
			int pad = PadFor(Width), stride = Width * 3 + pad;
			byte[] f = new byte[header.Length + stride * Rows + tail.Length];
			Array.Copy(header, f, header.Length);
			for (int y = 0; y < Rows; y++)
			{
				int at = header.Length + y * stride;
				for (int x = 0; x < Width; x++, at += 3)
				{
					int c = Pixels[y * Width + x];
					f[at] = (byte)c; f[at + 1] = (byte)(c >> 8); f[at + 2] = (byte)(c >> 16);
				}
				Array.Copy(padding, y * pad, f, at, pad);
			}
			Array.Copy(tail, 0, f, header.Length + stride * Rows, tail.Length);
			return f;
		}
	}
}
