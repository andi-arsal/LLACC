// Model gambar LLACC: menyalin pola secara dua dimensi + range coder adaptif yang paham gambar.
// Copyright (c) 2026 Andi Arsal Nur. MIT License.
// Range coder mengikuti cara kerja range coder LZMA (LZMA SDK karya Igor Pavlov, domain publik); ditulis ulang di sini.
// Tebakan piksel memakai "median edge detector" dari LOCO-I / JPEG-LS.
//
// PENTING: pembongkar harus menjalankan model yang sama persis dengan pemampat. Mengubah konteks, laju belajar,
// atau urutan bit berarti format baru dan nomor versi harus dinaikkan.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Llacc
{
	static class ImageCoder
	{
		// ---------- model peluang adaptif: cepat belajar di awal, lalu stabil ----------
		sealed class Model
		{
			public readonly ushort[] P;      // peluang bit 0 (skala 65536)
			public readonly byte[] Seen;
			public Model(int size)
			{
				P = new ushort[size];
				Seen = new byte[size];
				for (int i = 0; i < size; i++) P[i] = 32768;
			}
			public void Update(int i, int bit)
			{
				int shift = Seen[i] < 2 ? 2 : (Seen[i] < 6 ? 3 : (Seen[i] < 16 ? 4 : (Seen[i] < 48 ? 5 : 6)));
				if (Seen[i] < 255) Seen[i]++;
				int p = P[i];
				if (bit == 0) p += (65536 - p) >> shift; else p -= p >> shift;
				if (p < 24) p = 24; else if (p > 65512) p = 65512;
				P[i] = (ushort)p;
			}
		}

		/// <summary>Satu antarmuka untuk menulis, membaca, dan menaksir biaya, sehingga ketiganya memakai kode yang sama.</summary>
		abstract class Coder
		{
			public abstract int Bit(Model m, int index, int bit);
		}

		sealed class RangeEncoder : Coder
		{
			readonly Stream o;
			ulong low;
			uint range = 0xFFFFFFFF;
			byte cache;
			long pending = 1;
			public RangeEncoder(Stream output) { o = output; }

			public override int Bit(Model m, int index, int bit)
			{
				uint bound = (range >> 16) * m.P[index];
				if (bit == 0) range = bound;
				else { low += bound; range -= bound; }
				m.Update(index, bit);
				while (range < (1u << 24)) { range <<= 8; ShiftLow(); }
				return bit;
			}

			void ShiftLow()
			{
				if ((uint)low < 0xFF000000 || (low >> 32) != 0)
				{
					byte carry = (byte)(low >> 32), temp = cache;
					do { o.WriteByte((byte)(temp + carry)); temp = 0xFF; } while (--pending != 0);
					cache = (byte)(low >> 24);
				}
				pending++;
				low = (low << 8) & 0xFFFFFFFFUL;
			}

			public void Finish() { for (int i = 0; i < 5; i++) ShiftLow(); }
		}

		sealed class RangeDecoder : Coder
		{
			readonly byte[] data;
			int at;
			uint range = 0xFFFFFFFF, code;
			public RangeDecoder(byte[] input, int start)
			{
				data = input; at = start;
				for (int i = 0; i < 5; i++) code = (code << 8) | Next();
			}
			byte Next()
			{
				if (at >= data.Length + 8) throw LlaccCodec.Damaged();
				byte b = at < data.Length ? data[at] : (byte)0;
				at++;
				return b;
			}
			public override int Bit(Model m, int index, int ignored)
			{
				uint bound = (range >> 16) * m.P[index];
				int bit;
				if (code < bound) { range = bound; bit = 0; }
				else { code -= bound; range -= bound; bit = 1; }
				m.Update(index, bit);
				while (range < (1u << 24)) { range <<= 8; code = (code << 8) | Next(); }
				return bit;
			}
		}

		sealed class PriceCoder : Coder
		{
			static readonly float[] table = BuildTable();
			static float[] BuildTable()
			{
				float[] t = new float[1024];
				for (int i = 0; i < 1024; i++) t[i] = (float)(-Math.Log((i + 0.5) / 1024.0, 2));
				return t;
			}
			public float Bits;
			public override int Bit(Model m, int index, int bit)
			{
				int p = bit == 0 ? m.P[index] : 65536 - m.P[index];
				Bits += table[Math.Min(1023, p >> 6)];
				return bit;
			}
		}

		// ---------- kode bilangan ----------
		static int Tree(Coder c, Model m, int origin, int symbol, int bits)
		{
			int node = 1;
			for (int i = bits - 1; i >= 0; i--) node = node * 2 + c.Bit(m, origin + node, (symbol >> i) & 1);
			return node - (1 << bits);
		}

		const int NumberSlots = 32 + 32 * 32;

		/// <summary>Bilangan >= 0: panjang bit dikode berderet, lalu isi bitnya.</summary>
		static int Number(Coder c, Model m, int origin, int value)
		{
			uint v = (uint)value + 1;
			int length = 0;
			while ((v >> length) > 1) length++;
			int got = 0;
			while (got < 30 && c.Bit(m, origin + got, got < length ? 1 : 0) == 1) got++;
			uint result = 1;
			for (int i = got - 1; i >= 0; i--) result = result * 2 + (uint)c.Bit(m, origin + 32 + got * 32 + i, (int)(v >> i) & 1);
			return (int)result - 1;
		}

		// ---------- model gambar ----------
		sealed class ImageModel
		{
			public const int Activity = 10, Contexts = Activity * Activity;
			public readonly int Width;
			public int[] Pixels;
			public int[] Palette;                 // null = warna penuh
			public Dictionary<int, int> PaletteIndex;

			readonly Model isW = new Model(16), isN = new Model(16);
			readonly Model paletteTree = new Model(257 * 512);
			readonly Model zero = new Model(3 * Contexts), sign = new Model(3 * Contexts), magnitude = new Model(3 * Contexts * NumberSlots);
			public readonly Model IsMatch = new Model(32), IsRep = new Model(2), RepIndex = new Model(8), Length = new Model(2 * NumberSlots);
			public readonly Model Rows = new Model(NumberSlots), ColZero = new Model(1), ColSign = new Model(1), Cols = new Model(NumberSlots);

			public ImageModel(int width) { Width = width; }

			void Neighbours(int i, out int w, out int n, out int nw, out int ne)
			{
				int x = i % Width;
				bool up = i >= Width;
				w = x > 0 ? Pixels[i - 1] : (up ? Pixels[i - Width] : 0);
				n = up ? Pixels[i - Width] : w;
				nw = x > 0 && up ? Pixels[i - Width - 1] : n;
				ne = up && x < Width - 1 ? Pixels[i - Width + 1] : n;
			}

			public int Pattern(int i)
			{
				int w, n, nw, ne;
				Neighbours(i, out w, out n, out nw, out ne);
				return (w == nw ? 1 : 0) | (n == nw ? 2 : 0) | (n == ne ? 4 : 0) | (w == n ? 8 : 0);
			}

			static int Bucket(int v)
			{
				if (v == 0) return 0;
				if (v <= 1) return 1;
				if (v <= 3) return 2;
				if (v <= 6) return 3;
				if (v <= 10) return 4;
				if (v <= 17) return 5;
				if (v <= 28) return 6;
				if (v <= 46) return 7;
				return v <= 90 ? 8 : 9;
			}

			// tebakan "median edge detector" (dipakai juga oleh JPEG-LS)
			static int Guess(int a, int b, int c)
			{
				int low = Math.Min(a, b), high = Math.Max(a, b);
				return c >= high ? low : (c <= low ? high : a + b - c);
			}

			int Residual(Coder c, int plane, int context, int error)
			{
				int slot = plane * Contexts + context;
				if (c.Bit(zero, slot, error == 0 ? 1 : 0) == 1) return 0;
				int negative = c.Bit(sign, slot, error < 0 ? 1 : 0);
				int size = Number(c, magnitude, slot * NumberSlots, Math.Abs(error) - 1) + 1;
				return negative == 1 ? -size : size;
			}

			/// <summary>Kode satu piksel baru. Saat membaca, nilai "value" diabaikan.</summary>
			public int Literal(Coder c, int i, int value)
			{
				int w, n, nw, ne;
				Neighbours(i, out w, out n, out nw, out ne);
				int pattern = (w == nw ? 1 : 0) | (n == nw ? 2 : 0) | (n == ne ? 4 : 0) | (w == n ? 8 : 0);
				if (c.Bit(isW, pattern, value == w ? 1 : 0) == 1) return w;
				if (n != w && c.Bit(isN, pattern, value == n ? 1 : 0) == 1) return n;

				if (Palette != null)
				{
					int left;
					if (!PaletteIndex.TryGetValue(w, out left)) left = 256;
					int index = 0;
					if (c is RangeDecoder) index = Tree(c, paletteTree, left * 512, 0, 8);
					else index = Tree(c, paletteTree, left * 512, PaletteIndex[value], 8);
					if (index >= Palette.Length) throw LlaccCodec.Damaged();
					return Palette[index];
				}

				// warna penuh: hijau dulu, lalu biru dan merah memakai bantuan hijau
				int wg = (w >> 8) & 255, ng = (n >> 8) & 255, cg = (nw >> 8) & 255, eg = (ne >> 8) & 255;
				int activity = Bucket(Math.Abs(wg - cg) + Math.Abs(ng - cg) + Math.Abs(ng - eg));
				int guessG = Guess(wg, ng, cg);
				int errorG = Residual(c, 0, activity, (sbyte)(((value >> 8) & 255) - guessG));
				int g = (byte)(guessG + errorG);
				int previous = Bucket(Math.Abs(errorG));
				int result = g << 8;
				for (int plane = 1; plane <= 2; plane++)
				{
					int shift = plane == 1 ? 0 : 16;
					int guess = g + Guess(((w >> shift) & 255) - wg, ((n >> shift) & 255) - ng, ((nw >> shift) & 255) - cg);
					guess = guess < 0 ? 0 : (guess > 255 ? 255 : guess);
					int error = Residual(c, plane, previous * Activity + activity, (sbyte)(((value >> shift) & 255) - guess));
					result |= (byte)(guess + error) << shift;
					previous = Bucket(Math.Abs(error));
				}
				return result;
			}

			/// <summary>Kode satu salinan (panjang dan jarak). reps = 4 jarak terakhir.</summary>
			public void Match(Coder c, int[] reps, ref int distance, ref int length)
			{
				int rep = Array.IndexOf(reps, distance);
				int isRep = c.Bit(IsRep, 0, rep >= 0 ? 1 : 0);
				if (isRep == 1)
				{
					rep = Tree(c, RepIndex, 0, Math.Max(0, rep), 2);
					distance = reps[rep];
				}
				else
				{
					int rows = (distance + Width / 2) / Width;
					int cols = distance - rows * Width;
					rows = Number(c, Rows, 0, rows);
					if (c.Bit(ColZero, 0, cols == 0 ? 1 : 0) == 1) cols = 0;
					else
					{
						int negative = c.Bit(ColSign, 0, cols < 0 ? 1 : 0);
						cols = Number(c, Cols, 0, Math.Abs(cols) - 1) + 1;
						if (negative == 1) cols = -cols;
					}
					distance = rows * Width + cols;
				}
				length = Number(c, Length, isRep * NumberSlots, length - 2) + 2;
			}
		}

		static void PushDistance(int[] reps, int distance)
		{
			int at = Array.IndexOf(reps, distance);
			if (at < 0) at = reps.Length - 1;
			for (; at > 0; at--) reps[at] = reps[at - 1];
			reps[0] = distance;
		}

		static int[] FirstDistances(int width)
		{
			// kiri, atas, atas-kanan, atas-kiri (dibuat unik untuk gambar sangat sempit)
			return new int[] { 1, Math.Max(2, width), Math.Max(3, width - 1), Math.Max(4, width + 1) };
		}

		static int MatchLength(int[] p, int at, int distance)
		{
			int n = 0, max = p.Length - at;
			while (n < max && p[at + n] == p[at + n - distance]) n++;
			return n;
		}

		// ---------- pemampatan ----------
		public static byte[] Encode(BmpImage bmp, Action<double> progress)
		{
			int[] px = bmp.Pixels;
			int n = px.Length;
			ImageModel model = new ImageModel(bmp.Width);
			model.Pixels = px;

			int[] found = bmp.FindPalette();
			if (found != null) SetPalette(model, found);

			MemoryStream shell = new MemoryStream();
			using (DeflateStream ds = new DeflateStream(shell, CompressionLevel.Optimal, true))
			using (BinaryWriter w = new BinaryWriter(ds))
			{
				bmp.WriteShell(w);
				w.Write(model.Palette == null ? 0 : model.Palette.Length);
				if (model.Palette != null) foreach (int color in model.Palette) BmpImage.WriteColor(w, color);
			}
			MemoryStream o = new MemoryStream();
			BinaryWriter ow = new BinaryWriter(o);
			ow.Write((int)shell.Length);
			ow.Write(shell.ToArray());
			ow.Flush();

			RangeEncoder encoder = new RangeEncoder(o);
			PriceCoder price = new PriceCoder();
			float[] literalPrice = new float[n];      // taksiran biaya per piksel (dihitung sekali saat pertama diperlukan)
			bool[] priced = new bool[n];
			int[] reps = FirstDistances(bmp.Width);
			int[] head = new int[1 << 20], chain = new int[n];
			for (int i = 0; i < head.Length; i++) head[i] = -1;
			int previousWasMatch = 0, inserted = 0, at = 0, nextReport = 0;

			while (at < n)
			{
				if (progress != null && at >= nextReport) { progress((double)at / n); nextReport = at + 16384; }
				for (; inserted < at; inserted++)
					if (inserted + 2 < n)
					{
						int h = Hash(px, inserted);
						chain[inserted] = head[h];
						head[h] = inserted;
					}

				int bestLength = 0, bestDistance = 0;
				float bestSaving = 0;
				int longestRep = 0;
				foreach (int distance in reps)
				{
					if (distance > at) continue;
					int length = MatchLength(px, at, distance);
					if (length > longestRep) longestRep = length;
					if (length >= 2) Consider(model, price, px, literalPrice, priced, reps, at, distance, length, ref bestLength, ref bestDistance, ref bestSaving);
				}
				if (longestRep < 128 && at + 2 < n)
				{
					int candidate = head[Hash(px, at)], longest = 2;
					for (int depth = 0; candidate >= 0 && depth < 64; depth++, candidate = chain[candidate])
					{
						int length = MatchLength(px, at, at - candidate);
						if (length <= longest) continue;
						longest = length;
						Consider(model, price, px, literalPrice, priced, reps, at, at - candidate, length, ref bestLength, ref bestDistance, ref bestSaving);
						if (length >= 273) break;
					}
				}

				int context = previousWasMatch * 16 + model.Pattern(at);
				if (bestLength > 0)
				{
					encoder.Bit(model.IsMatch, context, 1);
					int distance = bestDistance, length = bestLength;
					model.Match(encoder, reps, ref distance, ref length);
					PushDistance(reps, bestDistance);
					at += bestLength;
					previousWasMatch = 1;
				}
				else
				{
					encoder.Bit(model.IsMatch, context, 0);
					model.Literal(encoder, at, px[at]);
					at++;
					previousWasMatch = 0;
				}
			}
			encoder.Finish();
			return o.ToArray();
		}

		static int Hash(int[] p, int i)
		{
			uint h = (uint)p[i] * 2654435761u;
			h = (h ^ (uint)p[i + 1]) * 2246822519u;
			h = (h ^ (uint)p[i + 2]) * 3266489917u;
			return (int)(h >> 12);
		}

		/// <summary>Bandingkan biaya menyalin dengan biaya menulis piksel satu per satu; simpan kandidat paling hemat.</summary>
		static void Consider(ImageModel model, PriceCoder price, int[] px, float[] literalPrice, bool[] priced, int[] reps,
			int at, int distance, int length, ref int bestLength, ref int bestDistance, ref float bestSaving)
		{
			int sample = Math.Min(length, 24);
			float sum = 0;
			for (int j = at; j < at + sample; j++)
			{
				if (!priced[j])
				{
					price.Bits = 0;
					price.Bit(model.IsMatch, model.Pattern(j), 0);
					model.Literal(price, j, px[j]);
					literalPrice[j] = price.Bits;
					priced[j] = true;
				}
				sum += literalPrice[j];
			}
			price.Bits = 0;
			price.Bit(model.IsMatch, model.Pattern(at), 1);
			int d = distance, len = length;
			model.Match(price, reps, ref d, ref len);
			float saving = sum / sample * length - price.Bits;
			if (saving > bestSaving) { bestSaving = saving; bestLength = length; bestDistance = distance; }
		}

		static void SetPalette(ImageModel model, int[] palette)
		{
			model.Palette = palette;
			model.PaletteIndex = new Dictionary<int, int>();
			for (int i = 0; i < palette.Length; i++) model.PaletteIndex[palette[i]] = i;
		}

		// ---------- pembongkaran ----------
		public static byte[] Decode(byte[] pack, int start, int originalSize)
		{
			if (pack.Length - start < 4) throw LlaccCodec.Damaged();
			int shellLength = BitConverter.ToInt32(pack, start);
			if (shellLength < 0 || shellLength > pack.Length - start - 4) throw LlaccCodec.Damaged();
			BmpImage bmp;
			int[] palette = null;
			using (DeflateStream ds = new DeflateStream(new MemoryStream(pack, start + 4, shellLength), CompressionMode.Decompress))
			using (BinaryReader r = new BinaryReader(new BufferedStream(ds)))
			{
				bmp = BmpImage.ReadShell(r, originalSize);
				int colors = r.ReadInt32();
				if (colors < 0 || colors > 256) throw LlaccCodec.Damaged();
				if (colors > 0)
				{
					palette = new int[colors];
					for (int i = 0; i < colors; i++) palette[i] = BmpImage.ReadColor(r);
				}
			}
			int[] px = bmp.Pixels;
			ImageModel model = new ImageModel(bmp.Width);
			model.Pixels = px;
			if (palette != null) SetPalette(model, palette);

			RangeDecoder decoder = new RangeDecoder(pack, start + 4 + shellLength);
			int[] reps = FirstDistances(bmp.Width);
			int previousWasMatch = 0, at = 0;
			while (at < px.Length)
			{
				if (decoder.Bit(model.IsMatch, previousWasMatch * 16 + model.Pattern(at), 0) == 1)
				{
					int distance = 1, length = 2;
					model.Match(decoder, reps, ref distance, ref length);
					if (distance <= 0 || distance > at || length < 2 || length > px.Length - at) throw LlaccCodec.Damaged();
					for (int j = 0; j < length; j++, at++) px[at] = px[at - distance];
					PushDistance(reps, distance);
					previousWasMatch = 1;
				}
				else
				{
					px[at] = model.Literal(decoder, at, 0);
					at++;
					previousWasMatch = 0;
				}
			}
			return bmp.Build();
		}
	}
}
