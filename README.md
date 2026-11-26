# LLACC — Low Level Archive Compression Cluster

**Bahasa Indonesia** · [English](README.en.md)

`llacc.dll` adalah pustaka .NET untuk **kompresi tanpa kehilangan data**. Keunggulannya ada pada **gambar BMP 24-bit tanpa kompresi**. Data lain tetap bisa diproses, tetapi memakai LZMA biasa.

Gagasan awal LLACC adalah mengelompokkan piksel yang warnanya sama atau hampir sama. Dibuat oleh **Andi Arsal Nur**, lisensi MIT.

## Cara kerja

LLACC mengambil dua kekuatan LZMA, yaitu menyalin pola yang pernah muncul dan *range coder* yang terus belajar, lalu membuatnya memahami bentuk gambar:

| Bagian | LZMA | Model gambar LLACC |
|---|---|---|
| Satuan data | byte | piksel |
| Menyalin pola | jarak mundur | jarak dua dimensi (baris ke atas dan kolom ke samping), 4 jarak terakhir dikodekan lebih murah |
| Piksel baru | ditebak dari byte sebelumnya | ditebak dari piksel kiri, atas, dan sudut |
| Gambar sampai 256 warna | tidak dibedakan | tabel kelompok warna |
| Foto | tidak dibedakan | menyimpan selisih terhadap tebakan; hijau lebih dulu, lalu membantu biru dan merah |
| Salin atau tulis baru | perkiraan biaya | perkiraan biaya dengan model gambar |

LLACC **bukan** versi resmi atau penerus LZMA/LZMA2.

### Metode di dalam format

| Nomor | Nama | Kapan dipakai |
|---|---|---|
| 0 | `Stored` | Data tidak bisa diperkecil, disimpan apa adanya |
| 1 | `Lzma` | LZMA biasa, untuk data selain gambar yang didukung (atau bila LZMA paling kecil) |
| 2 | `PaletteLzma` | Gambar sampai 256 warna: tabel warna + peta, lalu LZMA |
| 3 | `Image` | Model gambar LLACC |

Gagasan "catat posisi setiap warna" juga sudah diuji. Hasilnya hanya lebih kecil dari LZMA pada 1 dari 19 gambar, jadi tidak dimasukkan ke pustaka.

## Mode

| Mode | Yang dilakukan | Pilih bila |
|---|---|---|
| `LlaccMode.Best` (bawaan) | Untuk BMP yang didukung: mencoba model gambar, LZMA, dan tabel warna (bila ≤ 256 warna), lalu mengambil yang terkecil | Ukuran paling penting |
| `LlaccMode.Fast` | Untuk BMP yang didukung: hanya model gambar. Selain itu: LZMA | Waktu dan memori penting |

## Hasil uji

19 gambar BMP (foto, gambar render, kartun, diagram, grafik, ikon, *pixel art*, halaman teks, tangkapan layar). Pembanding: LZMA dari SharpCompress 0.38.0 dengan kamus sampai 64 MB dan 128 *fast bytes*.

| Terhadap LZMA | Fast | Best |
|---|---|---|
| Gambar yang hasilnya lebih kecil | 16 dari 19 | 19 dari 19 |
| Selisih ukuran, rata-rata geometris | 13,6% lebih kecil | 23,4% lebih kecil |
| Selisih total byte | 26,6% lebih kecil | 26,9% lebih kecil |
| Total waktu kompres (LZMA: 73,0 detik) | 31,3 detik | 106,6 detik |

Beberapa contoh (byte):

| Gambar | LZMA | Fast | Best |
|---|---|---|---|
| Foto 1 | 949.513 | 648.538 | 648.538 |
| Foto 2 | 2.677.418 | 1.855.857 | 1.855.857 |
| Gambar render bergradasi | 1.297.986 | 925.160 | 925.160 |
| Tangkapan layar terang | 37.490 | 30.573 | 30.573 |
| Diagram tanpa tepi halus | 7.277 | 5.740 | 5.119 |
| Grafik, 6 warna | 5.630 | 7.399 | 3.288 |
| *Pixel art*, 10 warna | 10.334 | 16.181 | 6.848 |

**Cara membaca angka ini:**

- Delapan gambar pertama sudah dipakai selama pengembangan. Sebelas gambar sisanya dibuat setelah model gambar selesai ditulis. Pada sebelas gambar itu, keunggulan mode Fast jauh lebih kecil: **rata-rata geometris 6,0% lebih kecil, menang pada 8 dari 11**. Pada delapan gambar awal angkanya 23,0%, menang pada semuanya.
- Foto dan gambar render berasal dari file JPEG bawaan Windows (sebagian diperkecil 50%). Foto langsung dari kamera belum diuji. Sebagian gambar lain dibuat oleh program uji.
- Waktu diukur satu kali pada satu komputer, jadi anggap sebagai perkiraan. Waktu LZMA diukur dengan prototipe 64-bit; waktu LLACC dengan pustaka pada proses 64-bit.
- Membongkar data LLACC bisa **lebih lambat** daripada LZMA, terutama pada foto.
- LLACC **belum dibandingkan** dengan format gambar khusus seperti WebP *lossless* atau JPEG XL, maupun dengan 7-Zip. Jangan menganggap LLACC mengunggulinya.

### Kapan LLACC tidak berguna

JPG, PNG, GIF, MP3, MP4, dan arsip sudah terkompresi, sehingga hampir tidak bisa diperkecil lagi. File suara (WAV) belum punya model khusus dan diproses dengan LZMA. BMP 8-bit, 32-bit, atau terkompresi juga diproses dengan LZMA.

## Persyaratan

- .NET Framework 4.7.2 atau lebih baru.
- Kode memakai C# 5, sehingga bisa dibangun dengan MSBuild 4.0 maupun Visual Studio 2022. Pembukaan proyek di SharpDevelop belum diuji.
- File yang disertakan bersama aplikasi Anda:

| File | Keterangan |
|---|---|
| `llacc.dll` | Pustaka ini |
| `SharpCompress.dll` | Mesin LZMA |
| `System.Memory.dll`, `System.Runtime.CompilerServices.Unsafe.dll` | Diperlukan SharpCompress |
| `System.Buffers.dll`, `System.Numerics.Vectors.dll` | Dependensi yang dinyatakan `System.Memory`; sebaiknya disertakan |

- **Wajib:** salin bagian `<runtime>` dari [app.config.example](app.config.example) ke `NamaAplikasi.exe.config`. Tanpa *binding redirect* ini, uji gagal.

## Membangun dan menguji

```
msbuild llacc.csproj /p:Configuration=Release
powershell -ExecutionPolicy Bypass -File test.ps1
powershell -ExecutionPolicy Bypass -File test.ps1 -Images C:\folder\berisi\bmp
```

Hasil build ada di `bin\Release\llacc.dll`.

## Cara pakai

Tambahkan referensi ke `llacc.dll`, lalu:

```csharp
using System;
using System.IO;
using Llacc;

class Contoh
{
	static void Main()
	{
		byte[] asli = File.ReadAllBytes("gambar.bmp");

		byte[] kecil = LlaccCodec.Compress(asli, LlaccMode.Best);
		LlaccInfo info = LlaccCodec.Inspect(kecil);
		Console.WriteLine("{0}: {1} -> {2} byte", info.Method, asli.Length, kecil.Length);

		try
		{
			byte[] kembali = LlaccCodec.Decompress(kecil);
			File.WriteAllBytes("kembali.bmp", kembali);
		}
		catch (InvalidDataException ex)
		{
			Console.WriteLine("Data rusak: " + ex.Message);
		}
	}
}
```

## API

Semua ada di kelas statis `Llacc.LlaccCodec`. Semua anggota aman dipakai dari banyak *thread*.

| Anggota | Fungsi |
|---|---|
| `Compress(byte[] data)` | Memampatkan dengan mode `Best` |
| `Compress(byte[] data, LlaccMode mode)` | Memampatkan dengan mode pilihan |
| `Compress(byte[] data, LlaccMode mode, Action<double> progress)` | Sama, dengan laporan kemajuan 0–1. Untuk membatalkan, lempar exception (mis. `OperationCanceledException`) dari callback; exception itu diteruskan ke pemanggil |
| `Decompress(byte[] packed)` | Membongkar, dengan batas hasil 512 MB |
| `Decompress(byte[] packed, int maxOutputSize)` | Membongkar dengan batas hasil sendiri |
| `CompressFile(string input, string output, LlaccMode mode)` | Memampatkan file |
| `DecompressFile(string input, string output)` | Membongkar file; keluaran ditulis hanya setelah data terbukti utuh |
| `Inspect(byte[] packed)` | Membaca metode, ukuran asli, dan CRC32 tanpa membongkar |
| `IsLlacc(byte[] data)` | Memeriksa tanda LLACC |
| `IsSupportedImage(byte[] data)` | Memeriksa apakah data diproses oleh model gambar |
| `DefaultMaxOutputSize` | Batas bawaan hasil bongkar (512 MB) |

**Kesalahan:** data yang rusak atau melebihi batas melempar `InvalidDataException`. Argumen `null` pada `Compress`, `Decompress`, dan `Inspect` melempar `ArgumentNullException`. Memori yang tidak cukup melempar `OutOfMemoryException`.

Bila membongkar data dari sumber yang tidak dipercaya, berikan `maxOutputSize` yang kecil sesuai kebutuhan.

## Batasan

- Seluruh data diproses **di memori**. Belum ada API *stream*. Laporan kemajuan dan pembatalan baru tersedia untuk pemampatan, belum untuk pembongkaran.
- Kemajuan adalah perkiraan: porsi tiap tahap pada mode `Best` ditetapkan tetap, sehingga kecepatannya bisa terasa tidak rata.
- Model gambar hanya untuk **BMP 24-bit tanpa kompresi**, maksimum 100 juta piksel. BMP lain diproses seperti data biasa.
- Pada gambar uji 8,3 megapiksel (24,9 MB), proses prototipe model gambar memakai puncak sekitar **154 MB** RAM, sedangkan prosesnya dengan LZMA sekitar 333 MB. Mode `Best` juga menjalankan LZMA, sehingga kebutuhannya minimal sebesar LZMA.
- Pada proses 32-bit, kamus LZMA dibatasi 32 MB. Karena itu, hasil metode LZMA untuk data lebih besar dari 32 MB bisa berbeda dengan proses 64-bit. Gambar yang sangat besar bisa kehabisan memori pada proses 32-bit.
- Tidak ada enkripsi.
- CRC32 mendeteksi kerusakan, tetapi **bukan** pengaman terhadap perubahan yang disengaja.
- Pustaka ini baru dan **belum diaudit** pihak lain. Simpan salinan data penting.

## Format data (versi 1)

Semua bilangan memakai urutan *little-endian*.

| Offset | Panjang | Isi |
|---|---|---|
| 0 | 4 | Tanda `LLAC` |
| 4 | 1 | Versi format: 1 |
| 5 | 1 | Metode: 0–3 |
| 6 | 4 | Ukuran asli (int32) |
| 10 | 4 | CRC32 data asli |
| 14 | sisa | Isi, sesuai metode |

- **0 `Stored`:** data asli.
- **1 `Lzma`:** 5 byte properti LZMA, lalu aliran LZMA.
- **2 `PaletteLzma`:** int32 panjang isi dalam, 5 byte properti, lalu aliran LZMA. Isi dalamnya: *bagian pelengkap BMP*, int16 jumlah warna, warna (3 byte B, G, R per warna), lalu 1 byte nomor warna per piksel.
- **3 `Image`:** int32 panjang bagian terkompresi, blok Deflate berisi *bagian pelengkap BMP*, int32 jumlah warna (0 = warna penuh), dan tabel warna; setelah itu aliran *range coder*.

**Bagian pelengkap BMP:** int32 lebar, int32 jumlah baris, int32 panjang header, header asli, byte pengisi semua baris, int32 panjang ekor, dan ekor file. Dengan bagian ini file disusun kembali sama persis.

Aliran *range coder* metode 3 bergantung pada rincian model: konteks, laju belajar, dan urutan bit. Acuan lengkapnya adalah [src/ImageCoder.cs](src/ImageCoder.cs). **Mengubah rincian model berarti membuat format baru**, sehingga nomor versi harus dinaikkan.

File `.llacc` buatan prototipe awal (tanda `LLC1`) tidak bisa dibuka oleh pustaka ini.

## Lisensi dan penghargaan

Lisensi MIT. Anda boleh memakai ulang LLACC, termasuk untuk keperluan komersial, selama **nama pembuat asli (Andi Arsal Nur)** dan pemberitahuan lisensi tetap disertakan. Lihat [LICENSE.txt](LICENSE.txt) dan [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

LLACC berdiri di atas karya yang sudah ada:

- **LZ77** dan **LZMA** (Igor Pavlov, LZMA SDK, domain publik): cara menyalin pola dan *range coder*. *Range coder* di LLACC ditulis ulang mengikuti cara kerja milik LZMA.
- **LOCO-I / JPEG-LS:** tebakan piksel *median edge detector*.
- **SharpCompress** (Adam Hathcock, MIT): mesin LZMA.

Konteks piksel, tabel warna, dan salinan dua dimensi juga dikenal dalam kompresi gambar. LLACC tidak mengklaim teknik-teknik tersebut sebagai temuan baru.
