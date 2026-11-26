// LLACC (Low Level Archive Compression Cluster)
// Copyright (c) 2026 Andi Arsal Nur. MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SharpCompress.Compressors.LZMA;

namespace Llacc
{
	/// <summary>Cara memilih metode saat memampatkan. | How the method is chosen when compressing.</summary>
	public enum LlaccMode
	{
		/// <summary>Coba semua metode yang cocok, pakai hasil terkecil. | Try every suitable method, keep the smallest.</summary>
		Best = 0,
		/// <summary>Satu metode saja: model gambar untuk BMP yang didukung, selain itu LZMA. | One method only: image model for supported BMP, otherwise LZMA.</summary>
		Fast = 1
	}

	/// <summary>Metode yang tersimpan di dalam data LLACC. | Method stored inside LLACC data.</summary>
	public enum LlaccMethod
	{
		/// <summary>Disimpan apa adanya. | Stored as is.</summary>
		Stored = 0,
		/// <summary>LZMA biasa. | Plain LZMA.</summary>
		Lzma = 1,
		/// <summary>Tabel warna + peta, lalu LZMA (gambar sampai 256 warna). | Colour table + map, then LZMA (images with up to 256 colours).</summary>
		PaletteLzma = 2,
		/// <summary>Model gambar LLACC. | LLACC image model.</summary>
		Image = 3
	}

	/// <summary>Keterangan singkat tentang data LLACC. | Short description of LLACC data.</summary>
	public sealed class LlaccInfo
	{
		public LlaccMethod Method { get; internal set; }
		public int OriginalSize { get; internal set; }
		public uint Crc32 { get; internal set; }
	}

	/// <summary>
	/// Pemampatan tanpa kehilangan data. Menerima byte apa saja; keunggulan LLACC ada pada gambar BMP 24-bit tanpa kompresi.
	/// Lossless compression. Accepts any bytes; LLACC's advantage is on uncompressed 24-bit BMP images.
	/// Semua anggota aman dipanggil dari banyak thread. | All members are thread safe.
	/// </summary>
	public static class LlaccCodec
	{
		/// <summary>Batas bawaan ukuran hasil bongkar. | Default limit for decompressed size.</summary>
		public const int DefaultMaxOutputSize = 512 * 1024 * 1024;

		const int HeaderSize = 14;
		const byte FormatVersion = 1;

		/// <summary>Apakah data berawalan tanda LLACC. | Whether the data starts with the LLACC signature.</summary>
		public static bool IsLlacc(byte[] data)
		{
			return data != null && data.Length >= HeaderSize && data[0] == 'L' && data[1] == 'L' && data[2] == 'A' && data[3] == 'C';
		}

		/// <summary>Apakah data ini BMP 24-bit tanpa kompresi yang diolah model gambar. | Whether the image model supports this data.</summary>
		public static bool IsSupportedImage(byte[] data)
		{
			return data != null && BmpImage.TryParse(data) != null;
		}

		public static byte[] Compress(byte[] data)
		{
			return Compress(data, LlaccMode.Best);
		}

		public static byte[] Compress(byte[] data, LlaccMode mode)
		{
			return Compress(data, mode, null);
		}

		/// <summary>
		/// Seperti Compress(data, mode), dengan laporan kemajuan 0..1 (tidak pernah mundur). Untuk membatalkan,
		/// lempar exception dari callback (mis. OperationCanceledException); exception itu diteruskan ke pemanggil.
		/// Progress from 0 to 1 (never goes backwards). To cancel, throw from the callback; the exception reaches the caller.
		/// </summary>
		public static byte[] Compress(byte[] data, LlaccMode mode, Action<double> progress)
		{
			if (data == null) throw new ArgumentNullException("data");
			uint crc = Checksum.Compute(data);
			BmpImage image = BmpImage.TryParse(data);

			byte[] best = null;
			LlaccMethod method = LlaccMethod.Stored;
			if (data.Length > 0)
			{
				bool tryLzma = image == null || mode == LlaccMode.Best;
				bool tryPalette = image != null && mode == LlaccMode.Best && image.FindPalette() != null;
				// porsi kemajuan tiap tahap (perkiraan lama relatifnya)
				double imageShare = image == null ? 0 : (tryLzma ? (tryPalette ? 0.2 : 0.25) : 1);
				double paletteShare = tryPalette ? 0.2 : 0;
				if (image != null)
				{
					best = ImageCoder.Encode(image, Stage(progress, 0, imageShare));
					method = LlaccMethod.Image;
				}
				if (tryLzma)
				{
					Keep(EncodeLzma(data, Stage(progress, imageShare, 1 - imageShare - paletteShare)), LlaccMethod.Lzma, ref best, ref method);
					if (tryPalette)
					{
						byte[] palette = EncodePalette(image, Stage(progress, 1 - paletteShare, paletteShare));
						if (palette != null) Keep(palette, LlaccMethod.PaletteLzma, ref best, ref method);
					}
				}
			}
			if (best == null || best.Length >= data.Length) { best = data; method = LlaccMethod.Stored; }

			MemoryStream o = new MemoryStream(HeaderSize + best.Length);
			BinaryWriter w = new BinaryWriter(o);
			w.Write(new byte[] { (byte)'L', (byte)'L', (byte)'A', (byte)'C', FormatVersion, (byte)method });
			w.Write(data.Length);
			w.Write(crc);
			w.Write(best);
			w.Flush();
			if (progress != null) progress(1);
			return o.ToArray();
		}

		static Action<double> Stage(Action<double> progress, double start, double share)
		{
			if (progress == null) return null;
			return delegate(double part) { progress(start + share * part); };
		}

		static void Keep(byte[] candidate, LlaccMethod candidateMethod, ref byte[] best, ref LlaccMethod method)
		{
			if (best == null || candidate.Length < best.Length) { best = candidate; method = candidateMethod; }
		}

		/// <summary>Baca keterangan tanpa membongkar. | Read the header without decompressing.</summary>
		public static LlaccInfo Inspect(byte[] packed)
		{
			if (packed == null) throw new ArgumentNullException("packed");
			if (!IsLlacc(packed)) throw new InvalidDataException("Bukan data LLACC. | Not LLACC data.");
			if (packed[4] != FormatVersion) throw new InvalidDataException("Versi LLACC tidak didukung. | Unsupported LLACC version.");
			if (packed[5] > (byte)LlaccMethod.Image) throw new InvalidDataException("Metode LLACC tidak dikenal. | Unknown LLACC method.");
			int size = BitConverter.ToInt32(packed, 6);
			if (size < 0) throw new InvalidDataException("Ukuran tidak valid. | Invalid size.");
			LlaccInfo info = new LlaccInfo();
			info.Method = (LlaccMethod)packed[5];
			info.OriginalSize = size;
			info.Crc32 = BitConverter.ToUInt32(packed, 10);
			return info;
		}

		public static byte[] Decompress(byte[] packed)
		{
			return Decompress(packed, DefaultMaxOutputSize);
		}

		/// <summary>
		/// Bongkar data. Melempar InvalidDataException bila data rusak atau hasilnya melebihi maxOutputSize.
		/// Decompress. Throws InvalidDataException when the data is damaged or the result exceeds maxOutputSize.
		/// </summary>
		public static byte[] Decompress(byte[] packed, int maxOutputSize)
		{
			LlaccInfo info = Inspect(packed);
			if (info.OriginalSize > maxOutputSize)
				throw new InvalidDataException("Hasil bongkar melebihi batas. | Decompressed size exceeds the limit.");
			byte[] result;
			try
			{
				switch (info.Method)
				{
					case LlaccMethod.Stored:
						if (packed.Length - HeaderSize != info.OriginalSize) throw Damaged();
						result = new byte[info.OriginalSize];
						Array.Copy(packed, HeaderSize, result, 0, result.Length);
						break;
					case LlaccMethod.Lzma:
						result = DecodeLzma(packed, HeaderSize, info.OriginalSize);
						break;
					case LlaccMethod.PaletteLzma:
						result = DecodePalette(packed, HeaderSize, info.OriginalSize);
						break;
					default:
						result = ImageCoder.Decode(packed, HeaderSize, info.OriginalSize);
						break;
				}
			}
			catch (InvalidDataException) { throw; }
			catch (OutOfMemoryException) { throw; }
			catch (Exception ex) { throw new InvalidDataException("Data LLACC rusak. | Damaged LLACC data.", ex); }
			if (result.Length != info.OriginalSize || Checksum.Compute(result) != info.Crc32)
				throw new InvalidDataException("CRC tidak cocok, data rusak. | CRC mismatch, data is damaged.");
			return result;
		}

		public static void CompressFile(string inputPath, string outputPath, LlaccMode mode)
		{
			File.WriteAllBytes(outputPath, Compress(File.ReadAllBytes(inputPath), mode));
		}

		/// <summary>File keluaran baru ditulis setelah data terbukti utuh. | The output file is written only after the data is verified.</summary>
		public static void DecompressFile(string inputPath, string outputPath)
		{
			byte[] result = Decompress(File.ReadAllBytes(inputPath));
			File.WriteAllBytes(outputPath, result);
		}

		internal static InvalidDataException Damaged()
		{
			return new InvalidDataException("Data LLACC rusak. | Damaged LLACC data.");
		}

		// ---------- LZMA ----------
		static byte[] EncodeLzma(byte[] data, Action<double> progress)
		{
			// proses 32-bit dibatasi 32 MB agar encoder tidak kehabisan memori
			int limit = IntPtr.Size == 8 ? 64 << 20 : 32 << 20;
			int dictionary = 1 << 16;
			while (dictionary < data.Length && dictionary < limit) dictionary <<= 1;
			MemoryStream packed = new MemoryStream();
			byte[] properties;
			using (LzmaStream lz = new LzmaStream(new LzmaEncoderProperties(false, dictionary, 128), false, packed))
			{
				properties = (byte[])lz.Properties.Clone();
				if (progress == null) lz.Write(data, 0, data.Length);
				else
					for (int at = 0; at < data.Length; )
					{
						progress((double)at / data.Length);
						int part = Math.Min(65536, data.Length - at);
						lz.Write(data, at, part);
						at += part;
					}
			}
			byte[] body = packed.ToArray();
			byte[] result = new byte[5 + body.Length];
			Array.Copy(properties, result, 5);
			Array.Copy(body, 0, result, 5, body.Length);
			return result;
		}

		static byte[] DecodeLzma(byte[] packed, int start, int length)
		{
			if (packed.Length - start < 5) throw Damaged();
			byte[] properties = new byte[5];
			Array.Copy(packed, start, properties, 0, 5);
			if (BitConverter.ToUInt32(properties, 1) > (64u << 20)) throw Damaged();   // cegah alokasi kamus raksasa
			byte[] data = new byte[length];
			int body = packed.Length - start - 5;
			using (LzmaStream lz = new LzmaStream(properties, new MemoryStream(packed, start + 5, body), body, length))
			{
				int done = 0, n;
				while (done < length && (n = lz.Read(data, done, length - done)) > 0) done += n;
				if (done != length) throw Damaged();
			}
			return data;
		}

		// ---------- tabel warna + peta + LZMA ----------
		static byte[] EncodePalette(BmpImage image, Action<double> progress)
		{
			int[] palette = image.FindPalette();
			if (palette == null) return null;
			Dictionary<int, int> index = new Dictionary<int, int>();
			for (int i = 0; i < palette.Length; i++) index[palette[i]] = i;

			MemoryStream plain = new MemoryStream();
			BinaryWriter w = new BinaryWriter(plain);
			image.WriteShell(w);
			w.Write((short)palette.Length);
			foreach (int color in palette) BmpImage.WriteColor(w, color);
			byte[] map = new byte[image.Pixels.Length];
			for (int i = 0; i < map.Length; i++) map[i] = (byte)index[image.Pixels[i]];
			w.Write(map);
			w.Flush();

			byte[] inner = plain.ToArray();
			byte[] lzma = EncodeLzma(inner, progress);
			byte[] result = new byte[4 + lzma.Length];
			Array.Copy(BitConverter.GetBytes(inner.Length), result, 4);
			Array.Copy(lzma, 0, result, 4, lzma.Length);
			return result;
		}

		static byte[] DecodePalette(byte[] packed, int start, int originalSize)
		{
			if (packed.Length - start < 4) throw Damaged();
			int innerLength = BitConverter.ToInt32(packed, start);
			// isi dalam: bagian pelengkap BMP (<= ukuran asli) + 1 byte per piksel + tabel warna
			if (innerLength < 0 || innerLength > originalSize * 2L + 1024) throw Damaged();
			BinaryReader r = new BinaryReader(new MemoryStream(DecodeLzma(packed, start + 4, innerLength)));
			BmpImage image = BmpImage.ReadShell(r, originalSize);
			int count = r.ReadInt16();
			if (count < 1 || count > 256) throw Damaged();
			int[] palette = new int[count];
			for (int i = 0; i < count; i++) palette[i] = BmpImage.ReadColor(r);
			for (int i = 0; i < image.Pixels.Length; i++)
			{
				int c = r.ReadByte();
				if (c >= count) throw Damaged();
				image.Pixels[i] = palette[c];
			}
			return image.Build();
		}
	}

	static class Checksum
	{
		static readonly uint[] table = Build();

		static uint[] Build()
		{
			uint[] t = new uint[256];
			for (uint i = 0; i < 256; i++)
			{
				uint c = i;
				for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				t[i] = c;
			}
			return t;
		}

		public static uint Compute(byte[] data)
		{
			uint c = 0xFFFFFFFFu;
			foreach (byte b in data) c = table[(c ^ b) & 255] ^ (c >> 8);
			return c ^ 0xFFFFFFFFu;
		}
	}
}
