// Uji llacc.dll: keutuhan, data bukan gambar, dan ketahanan terhadap data rusak.
//   LlaccTests.exe [folder_berisi_bmp]
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Llacc;

static class LlaccTests
{
	static int passed, failed;

	static void Check(bool ok, string what)
	{
		if (ok) passed++;
		else { failed++; Console.WriteLine("GAGAL: " + what); }
	}

	static bool Same(byte[] a, byte[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
		return true;
	}

	static byte[] RoundTrip(byte[] data, LlaccMode mode, string what)
	{
		byte[] packed = LlaccCodec.Compress(data, mode);
		Check(Same(LlaccCodec.Decompress(packed), data), what + " " + mode);
		Check(packed.Length <= data.Length + 14, what + " " + mode + ": hasil melebihi asli + header");
		return packed;
	}

	/// <summary>BMP 24-bit buatan dengan jumlah warna, pengulangan, ekor, dan byte pengisi acak.</summary>
	static byte[] MakeBmp(int w, int h, int colors, int repeat, int tail, int seed)
	{
		Random r = new Random(seed);
		int rows = Math.Abs(h), stride = (w * 3 + 3) / 4 * 4;
		byte[] f = new byte[54 + stride * rows + tail];
		r.NextBytes(f);
		MemoryStream m = new MemoryStream();
		BinaryWriter b = new BinaryWriter(m);
		b.Write((byte)'B'); b.Write((byte)'M'); b.Write(f.Length); b.Write(0); b.Write(54); b.Write(40); b.Write(w); b.Write(h);
		b.Write((short)1); b.Write((short)24);
		for (int i = 0; i < 6; i++) b.Write(0);
		Array.Copy(m.ToArray(), f, 54);
		int n = w * rows;
		int[] px = new int[n], palette = new int[Math.Max(1, colors)];
		for (int i = 0; i < palette.Length; i++) palette[i] = r.Next(1 << 24);
		for (int at = 0; at < n; )
		{
			if (repeat > 0 && at > 3 && r.Next(100) < repeat)
			{
				int d = r.Next(3) == 0 ? Math.Min(at, w) : 1 + r.Next(at);
				int len = 1 + r.Next(r.Next(5) == 0 ? 2000 : 30);
				for (int j = 0; j < len && at < n; j++, at++) px[at] = px[at - d];
			}
			else { px[at] = colors > 0 ? palette[r.Next(palette.Length)] : r.Next(1 << 24); at++; }
		}
		for (int y = 0; y < rows; y++)
			for (int x = 0; x < w; x++)
			{
				int o = 54 + y * stride + x * 3, c = px[y * w + x];
				f[o] = (byte)c; f[o + 1] = (byte)(c >> 8); f[o + 2] = (byte)(c >> 16);
			}
		return f;
	}

	/// <summary>Data rusak harus ditolak dengan InvalidDataException, atau (bila perubahannya tak berpengaruh) tetap menghasilkan data asli.</summary>
	static void Damage(byte[] original, byte[] packed, int seed, string what)
	{
		Random r = new Random(seed);
		for (int trial = 0; trial < 250; trial++)
		{
			byte[] bad = (byte[])packed.Clone();
			if (trial % 5 == 0) Array.Resize(ref bad, r.Next(bad.Length));
			else if (trial % 5 == 1) { for (int i = 0; i < 8; i++) bad[r.Next(bad.Length)] = (byte)r.Next(256); }
			else if (trial % 5 == 2) { int at = 6 + r.Next(Math.Max(1, Math.Min(bad.Length - 10, 40))); Array.Copy(BitConverter.GetBytes(r.Next(int.MinValue, int.MaxValue)), 0, bad, at, 4); }
			else bad[r.Next(bad.Length)] ^= (byte)(1 << r.Next(8));
			try
			{
				byte[] result = LlaccCodec.Decompress(bad, 8 << 20);
				Check(Same(result, original), what + ": data rusak menghasilkan keluaran SALAH (percobaan " + trial + ")");
			}
			catch (InvalidDataException) { passed++; }
			catch (Exception ex) { Check(false, what + ": " + ex.GetType().Name + " (percobaan " + trial + ") " + ex.Message); }
		}
	}

	static int Main(string[] args)
	{
		Console.WriteLine("proses " + (IntPtr.Size * 8) + "-bit");

		// 1) data bukan gambar
		Random random = new Random(1);
		byte[] noise = new byte[200000];
		random.NextBytes(noise);
		StringBuilder text = new StringBuilder();
		for (int i = 0; i < 4000; i++) text.Append("arsip data ").Append(i % 37).Append('\n');
		byte[] textBytes = Encoding.UTF8.GetBytes(text.ToString());
		byte[] fakeBmp = Encoding.ASCII.GetBytes("BMini bukan gambar sungguhan, hanya diawali huruf BM .................................");
		byte[][] others = { new byte[0], new byte[] { 7 }, noise, textBytes, new byte[100000], fakeBmp };
		for (int i = 0; i < others.Length; i++)
		{
			RoundTrip(others[i], LlaccMode.Best, "bukan gambar #" + i);
			RoundTrip(others[i], LlaccMode.Fast, "bukan gambar #" + i);
			Check(!LlaccCodec.IsSupportedImage(others[i]), "bukan gambar #" + i + " dikira gambar");
		}
		Check(LlaccCodec.Inspect(LlaccCodec.Compress(noise)).Method == LlaccMethod.Stored, "data acak harus disimpan apa adanya");
		Check(LlaccCodec.Inspect(LlaccCodec.Compress(textBytes)).Method == LlaccMethod.Lzma, "teks harus memakai LZMA");

		// 2) gambar acak berbagai bentuk
		int[] widths = { 1, 2, 3, 4, 5, 7, 16, 31, 64, 200 };
		int[] colorCounts = { 0, 1, 2, 5, 255, 256, 257, 1000 };
		int[] repeats = { 0, 20, 60, 95 };
		for (int i = 0; i < 300; i++)
		{
			int h = 1 + random.Next(150);
			if (i % 7 == 0) h = -h;
			byte[] bmp = MakeBmp(widths[random.Next(widths.Length)], h, colorCounts[random.Next(colorCounts.Length)], repeats[random.Next(repeats.Length)], i % 3 == 0 ? 9 : 0, i);
			Check(LlaccCodec.IsSupportedImage(bmp), "BMP buatan #" + i + " tidak dikenali");
			RoundTrip(bmp, LlaccMode.Best, "BMP buatan #" + i);
			RoundTrip(bmp, LlaccMode.Fast, "BMP buatan #" + i);
		}

		// 3) setiap metode terpakai, lalu dirusak
		byte[][] samples = { MakeBmp(120, 90, 0, 60, 5, 3), MakeBmp(120, 90, 6, 20, 0, 4), textBytes, noise };
		for (int i = 0; i < samples.Length; i++)
		{
			byte[] packed = LlaccCodec.Compress(samples[i], LlaccMode.Best);
			Console.WriteLine("contoh rusak #" + i + ": metode " + LlaccCodec.Inspect(packed).Method);
			Damage(samples[i], packed, 100 + i, "metode " + LlaccCodec.Inspect(packed).Method);
			byte[] fast = LlaccCodec.Compress(samples[i], LlaccMode.Fast);
			Damage(samples[i], fast, 200 + i, "mode cepat #" + i);
		}
		byte[] tabled = LlaccCodec.Compress(MakeBmp(200, 150, 4, 0, 0, 9), LlaccMode.Best);
		Console.WriteLine("contoh warna sedikit: metode " + LlaccCodec.Inspect(tabled).Method);
		try { LlaccCodec.Decompress(LlaccCodec.Compress(textBytes), 100); Check(false, "batas ukuran diabaikan"); }
		catch (InvalidDataException) { passed++; }

		// 4) dipakai bersamaan dari beberapa thread
		byte[] shared = MakeBmp(300, 200, 0, 60, 0, 12);
		byte[] expected = LlaccCodec.Compress(shared);
		int wrong = 0;
		Thread[] threads = new Thread[8];
		for (int t = 0; t < threads.Length; t++)
		{
			threads[t] = new Thread(delegate()
			{
				for (int k = 0; k < 5; k++)
					if (!Same(LlaccCodec.Compress(shared), expected) || !Same(LlaccCodec.Decompress(expected), shared)) Interlocked.Increment(ref wrong);
			});
			threads[t].Start();
		}
		foreach (Thread t in threads) t.Join();
		Check(wrong == 0, "hasil berbeda saat dipakai banyak thread");

		// 4b) laporan kemajuan dan pembatalan
		foreach (byte[] input in new byte[][] { MakeBmp(500, 300, 0, 60, 0, 31), MakeBmp(500, 300, 6, 40, 0, 32), textBytes })
			foreach (LlaccMode mode in new LlaccMode[] { LlaccMode.Fast, LlaccMode.Best })
			{
				double last = 0;
				int calls = 0;
				bool ordered = true;
				byte[] packed = LlaccCodec.Compress(input, mode, delegate(double p)
				{
					if (p < last - 1e-9 || p < 0 || p > 1.000001) ordered = false;
					last = p;
					calls++;
				});
				Check(ordered && calls >= 2 && last == 1, "kemajuan " + mode + " (panggilan " + calls + ", akhir " + last + ")");
				Check(Same(LlaccCodec.Decompress(packed), input), "keutuhan dengan kemajuan " + mode);
				foreach (double limit in new double[] { 0, 0.1, 0.5, 0.9 })
				{
					try
					{
						LlaccCodec.Compress(input, mode, delegate(double p) { if (p >= limit) throw new OperationCanceledException(); });
						Check(false, "pembatalan diabaikan " + mode + " " + limit);
					}
					catch (OperationCanceledException) { passed++; }
					catch (Exception ex) { Check(false, "pembatalan: " + ex.GetType().Name); }
				}
			}

		// 5) gambar nyata
		if (args.Length > 0)
		{
			Console.WriteLine("GAMBAR;ASLI;FAST;BEST;METODE_BEST;FAST_MS;BEST_MS;BONGKAR_MS");
			string[] files = Directory.GetFiles(args[0], "*.bmp");
			Array.Sort(files);
			foreach (string file in files)
			{
				byte[] data = File.ReadAllBytes(file);
				Stopwatch w1 = Stopwatch.StartNew();
				byte[] fast = LlaccCodec.Compress(data, LlaccMode.Fast);
				w1.Stop();
				Stopwatch w2 = Stopwatch.StartNew();
				byte[] best = LlaccCodec.Compress(data, LlaccMode.Best);
				w2.Stop();
				Stopwatch w3 = Stopwatch.StartNew();
				byte[] back = LlaccCodec.Decompress(best);
				w3.Stop();
				Check(Same(back, data), Path.GetFileName(file) + " Best");
				Check(Same(LlaccCodec.Decompress(fast), data), Path.GetFileName(file) + " Fast");
				Console.WriteLine("{0};{1};{2};{3};{4};{5};{6};{7}", Path.GetFileNameWithoutExtension(file), data.Length, fast.Length, best.Length,
					LlaccCodec.Inspect(best).Method, w1.ElapsedMilliseconds, w2.ElapsedMilliseconds, w3.ElapsedMilliseconds);
			}
		}

		Console.WriteLine("LULUS " + passed + ", GAGAL " + failed);
		return failed == 0 ? 0 : 1;
	}
}
