using System.Collections.Generic;
using System.IO;

/// <summary>
/// Записує MJPEG AVI файл (Motion JPEG у AVI контейнері).
/// Чистий C# — жодних зовнішніх залежностей.
/// Підтримується: VLC, Windows Media Player, ffmpeg, DaVinci, всі браузери.
/// </summary>
public class AviWriter
{
    private BinaryWriter  bw;
    private FileStream    fs;
    private int           width, height, fps;

    // Для індексу та патчингу заголовків
    private long          posTotalFrames;  // в avih
    private long          posMaxBytes;
    private long          posRiffSize;
    private long          posMoviSize;
    private long          posStrhLength;   // totalFrames у strh
    private long          moviDataStart;   // після "movi" тегу

    private List<int>     frameOffsets = new List<int>();
    private List<int>     frameSizes   = new List<int>();

    // MJPEG quality (0-100). 90 = quasi-lossless
    public int JpegQuality = 90;

    // ── Open ─────────────────────────────────────────────────
    public void Open(string path, int w, int h, int frameRate)
    {
        width = w; height = h; fps = frameRate;
        fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        bw = new BinaryWriter(fs);
        WriteHeaders();
    }

    // ── AddFrame ─────────────────────────────────────────────
    /// <param name="jpegBytes">JPEG байти кадру (з Texture2D.EncodeToJPG)</param>
    public void AddFrame(byte[] jpegBytes)
    {
        // Offset відносно початку movi DATA (після тегу "movi")
        int offsetFromMoviData = (int)(fs.Position - moviDataStart);
        frameOffsets.Add(offsetFromMoviData);
        frameSizes.Add(jpegBytes.Length);

        // Chunk: "00dc" + size + data (padded to even)
        bw.Write(FCC("00dc"));
        bw.Write(jpegBytes.Length);
        bw.Write(jpegBytes);
        if (jpegBytes.Length % 2 != 0) bw.Write((byte)0); // padding
    }

    // ── Close ────────────────────────────────────────────────
    public void Close()
    {
        if (bw == null) return;
        int totalFrames = frameOffsets.Count;

        // Завершення LIST movi
        long moviEndPos = fs.Position;
        long moviSize   = moviEndPos - (moviDataStart - 4); // includes "movi" tag

        // ── idx1 (AVI index) ──────────────────────────────
        bw.Write(FCC("idx1"));
        bw.Write(totalFrames * 16); // 16 bytes per entry
        for (int i = 0; i < totalFrames; i++)
        {
            bw.Write(FCC("00dc"));
            bw.Write(0x00000010); // AVIIF_KEYFRAME
            bw.Write(frameOffsets[i] + 4); // +4 past "movi" tag
            bw.Write(frameSizes[i]);
        }

        long fileEnd = fs.Position;

        // ── Patch headers ─────────────────────────────────
        // RIFF size
        fs.Position = posRiffSize;
        bw.Write((int)(fileEnd - 8));

        // avih: totalFrames
        fs.Position = posTotalFrames;
        bw.Write(totalFrames);

        // avih: maxBytesPerSec (estimate)
        int maxJpeg = 0;
        foreach (int s in frameSizes) if (s > maxJpeg) maxJpeg = s;
        fs.Position = posMaxBytes;
        bw.Write(maxJpeg * fps);

        // LIST movi size
        fs.Position = posMoviSize;
        bw.Write((int)(moviSize));

        // strh: length (totalFrames)
        fs.Position = posStrhLength;
        bw.Write(totalFrames);

        bw.Close();
        fs.Close();
        bw = null; fs = null;
    }

    // ══════════════════════════════════════════════════════════
    // PRIVATE: Write RIFF/AVI headers
    // ══════════════════════════════════════════════════════════
    void WriteHeaders()
    {
        int microPerFrame = 1000000 / fps;
        int sugBufSize    = width * height * 3;

        // ── RIFF AVI ──────────────────────────────────────
        bw.Write(FCC("RIFF"));
        posRiffSize = fs.Position;
        bw.Write(0);             // placeholder: file size - 8
        bw.Write(FCC("AVI "));

        // ── LIST hdrl ─────────────────────────────────────
        bw.Write(FCC("LIST"));
        long hdrlSizePos = fs.Position;
        bw.Write(0);
        bw.Write(FCC("hdrl"));

        // avih (Main AVI Header) — 56 bytes payload
        bw.Write(FCC("avih"));
        bw.Write(56);
        bw.Write(microPerFrame);      // microSecPerFrame
        posMaxBytes = fs.Position;
        bw.Write(0);                  // maxBytesPerSec (patched)
        bw.Write(0);                  // paddingGranularity
        bw.Write(0x00000910);         // flags: AVIF_HASINDEX | AVIF_ISINTERLEAVED
        posTotalFrames = fs.Position;
        bw.Write(0);                  // totalFrames (patched)
        bw.Write(0);                  // initialFrames
        bw.Write(1);                  // streams
        bw.Write(sugBufSize);         // suggestedBufferSize
        bw.Write(width);
        bw.Write(height);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); // reserved

        // ── LIST strl ─────────────────────────────────────
        bw.Write(FCC("LIST"));
        long strlSizePos = fs.Position;
        bw.Write(0);
        bw.Write(FCC("strl"));

        // strh (Stream Header) — 56 bytes
        bw.Write(FCC("strh"));
        bw.Write(56);
        bw.Write(FCC("vids"));        // fccType: video
        bw.Write(FCC("MJPG"));        // fccHandler: MJPEG
        bw.Write(0);                  // flags
        bw.Write((short)0);           // priority
        bw.Write((short)0);           // language
        bw.Write(0);                  // initialFrames
        bw.Write(1);                  // scale
        bw.Write(fps);                // rate (fps)
        bw.Write(0);                  // start
        posStrhLength = fs.Position;
        bw.Write(0);                  // length = totalFrames (patched)
        bw.Write(sugBufSize);         // suggestedBufferSize
        bw.Write(-1);                 // quality (-1 = default)
        bw.Write(0);                  // sampleSize
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)width); bw.Write((short)height);

        // strf (BITMAPINFOHEADER for MJPEG) — 40 bytes
        bw.Write(FCC("strf"));
        bw.Write(40);
        bw.Write(40);                 // biSize
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);           // biPlanes
        bw.Write((short)24);          // biBitCount
        bw.Write(FCC("MJPG"));        // biCompression
        bw.Write(0);                  // biSizeImage
        bw.Write(0);                  // biXPelsPerMeter
        bw.Write(0);                  // biYPelsPerMeter
        bw.Write(0);                  // biClrUsed
        bw.Write(0);                  // biClrImportant

        // Patch hdrl and strl sizes
        long hdrlEnd = fs.Position;
        fs.Position = hdrlSizePos; bw.Write((int)(hdrlEnd - hdrlSizePos - 4));
        long strlEnd = fs.Position = hdrlEnd;
        fs.Position = strlSizePos; bw.Write((int)(hdrlEnd - strlSizePos - 4));
        fs.Position = hdrlEnd;

        // ── LIST movi ─────────────────────────────────────
        bw.Write(FCC("LIST"));
        posMoviSize = fs.Position;
        bw.Write(0);                  // patched on Close()
        bw.Write(FCC("movi"));
        moviDataStart = fs.Position;  // frame offsets counted from here
    }

    // ── Helpers ──────────────────────────────────────────────
    static byte[] FCC(string fourCC)
    {
        return new byte[] {
            (byte)fourCC[0], (byte)fourCC[1],
            (byte)fourCC[2], (byte)fourCC[3]
        };
    }
}
