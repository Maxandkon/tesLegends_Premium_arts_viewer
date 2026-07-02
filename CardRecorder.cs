using UnityEngine;
using System.Collections;
using System.IO;

/// <summary>
/// Записує анімацію.
/// Якщо ffmpeg.exe знайдено в StreamingAssets або PATH → пряме .mp4 (H.264, ~20x менший файл)
/// Інакше → MJPEG .avi через AviWriter (без залежностей)
/// </summary>
public class CardRecorder : MonoBehaviour
{
    public static CardRecorder Instance { get; private set; }

    public System.Action<float, string> OnRecordProgress;
    public System.Action<string>        OnRecordDone;
    public System.Action<string>        OnRecordError;

    public bool IsRecording { get; private set; }
    public UnityEngine.UI.RawImage MaskImageToHide;

    public enum RecordFormat { MP4, AVI, MOV, WebM, GIF }
    public RecordFormat Format = RecordFormat.MP4;

    private const int   FPS          = 30;
    private const int   JPEG_QUALITY = 90;
    private const float ART_ASPECT   = 0.5748f;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ── FFmpeg detection ─────────────────────────────────────
    static string FindFFmpeg()
    {
        string appDir = Path.GetDirectoryName(Application.dataPath);
        string sa     = Application.streamingAssetsPath;
        // Повертаємо РЕАЛЬНИЙ шлях до знайденого exe (а не літерал "ffmpeg").
        foreach (string p in new[]{ Path.Combine(sa, "Tools", "ffmpeg.exe"), // основне місце в проекті
                                    Path.Combine(sa, "ffmpeg.exe"),
                                    Path.Combine(appDir, "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        // Остання спроба — ffmpeg у системному PATH
        try {
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = "ffmpeg";
            proc.StartInfo.Arguments = "-version";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.Start(); proc.WaitForExit(2000);
            if (proc.ExitCode == 0) return "ffmpeg";
        } catch {}
        return null;
    }

    public static void BrowseOutputFolder(UnityEngine.UI.InputField target)
    {
        string title = "Select output folder for recorded videos";
        bool modernOk = false;
        string picked = "";
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try { picked = WinFolderPicker.Pick(title, target.text); modernOk = true; }
        catch (System.Exception e)
        { UnityEngine.Debug.LogWarning("[Browse] фолбек на PowerShell: " + e.Message); }
#endif
        if (!modernOk) picked = RunFolderDialog(target.text, title);
        if (!string.IsNullOrEmpty(picked)) target.text = picked;
    }

    static string RunFolderDialog(string initPath, string title)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string outFile    = System.IO.Path.GetTempFileName();
        string scriptFile = outFile + ".ps1";
        string safeTitle  = title.Replace("'", "''");
        string safePath   = System.IO.Directory.Exists(initPath)
                            ? initPath.Replace("'", "''") : "";
        string safeOut    = outFile.Replace("\\", "\\\\");
        var sb = new System.Text.StringBuilder();
        sb.Append("Add-Type -AssemblyName System.Windows.Forms; ");
        sb.Append("$d=New-Object System.Windows.Forms.FolderBrowserDialog; ");
        sb.Append("$d.Description='" + safeTitle + "'; ");
        if (safePath.Length > 0)
            sb.Append("$d.SelectedPath='" + safePath + "'; ");
        sb.Append("if($d.ShowDialog()-eq'OK'){$d.SelectedPath|");
        sb.Append("Out-File -FilePath '" + safeOut + "' -NoNewline -Encoding utf8}");
        System.IO.File.WriteAllText(scriptFile, sb.ToString(),
            System.Text.Encoding.UTF8);
        var psi = new System.Diagnostics.ProcessStartInfo();
        psi.FileName        = "powershell.exe";
        psi.Arguments       = "-ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                              + scriptFile + "\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow  = true;
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null) proc.WaitForExit();
        string picked = "";
        if (System.IO.File.Exists(outFile))
            picked = System.IO.File.ReadAllText(outFile).Trim();
        try { System.IO.File.Delete(scriptFile); System.IO.File.Delete(outFile); }
        catch (System.Exception) {}
        return picked;
#else
        return "";
#endif
    }
    public void StartRecording(Camera cam, string cardName, string outputRoot,
                               int heightPx, float seconds, bool isCropped, bool isBack = false)
    {
        if (IsRecording) return;
        StartCoroutine(DoRecord(cam, cardName, outputRoot, heightPx, seconds, isCropped, Format, isBack));
    }

    public void StopRecording() { IsRecording = false; }

    IEnumerator DoRecord(Camera cam, string cardName, string outputRoot,
                         int heightPx, float seconds, bool isCropped, RecordFormat fmt, bool isBack = false)
    {
        IsRecording = true;
        // Default output path
        if (string.IsNullOrEmpty(outputRoot))
            outputRoot = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                "TESLRecordings");
        // Width: arts=square(1:1), backs=viewport aspect, isCropped=art UV crop
        // Always 1:1 square — arts fill previewRT exactly; backs show centered with natural framing
        float _useAsp = 1.0f;
        int widthPx = Mathf.Max(2, Mathf.RoundToInt(heightPx * _useAsp));
        if (widthPx  % 2 != 0) widthPx++;
        if (heightPx % 2 != 0) heightPx++;

        string ffmpeg = FindFFmpeg();
        string safe   = Sanitize(cardName);
        Directory.CreateDirectory(outputRoot);
        try
        {

        if (ffmpeg != null)
        {
            string ext = fmt == RecordFormat.WebM ? ".webm"
                       : fmt == RecordFormat.MOV  ? ".mov"
                       : fmt == RecordFormat.GIF  ? ".gif"
                       : fmt == RecordFormat.AVI  ? ".avi"
                       : ".mp4";
            string outFile2 = System.IO.Path.Combine(outputRoot,
                string.Format("{0}_{1}p_{2}s{3}", safe, heightPx, Mathf.RoundToInt(seconds), ext));

            if (fmt == RecordFormat.GIF)
            {
                // GIF needs two-pass palette generation for correct colors
                yield return StartCoroutine(RecordGIF(cam, ffmpeg, outFile2, widthPx, heightPx, seconds));
            }
            else if (fmt == RecordFormat.WebM)
            {
                // VP8 (libvpx) — better compatibility than VP9
                yield return StartCoroutine(RecordMP4(cam, ffmpeg, outFile2, widthPx, heightPx, seconds,
                    "-c:v libvpx -b:v 2M -auto-alt-ref 0"));
            }
            else if (fmt == RecordFormat.AVI)
            {
                // MJPEG AVI via ffmpeg — universal compatibility
                yield return StartCoroutine(RecordMP4(cam, ffmpeg, outFile2, widthPx, heightPx, seconds,
                    "-c:v mjpeg -q:v 3"));
            }
            else
            {
                // MP4 / MOV: H264
                yield return StartCoroutine(RecordMP4(cam, ffmpeg, outFile2, widthPx, heightPx, seconds,
                    "-c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p"));
            }
        }
        else
        {
            // ── Fallback: MJPEG AVI ───────────────────────────
            string outAvi = Path.Combine(outputRoot,
                string.Format("{0}_{1}p_{2}s.avi", safe, heightPx, Mathf.RoundToInt(seconds)));
            yield return StartCoroutine(RecordAVI(cam, outAvi, widthPx, heightPx, seconds));
        }
        } // end try
        finally { Time.captureFramerate = 0; IsRecording = false; cam.targetTexture = null; }
    }

    // ── MP4 via ffmpeg ────────────────────────────────────────
    IEnumerator RecordMP4(Camera cam, string ffmpegPath, string outFile,
                          int w, int h, float seconds,
                          string codec = "-c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p")
    {
        // Назва формату для повідомлень береться з розширення файлу
        // (раніше скрізь було хардкодовано "mp4", через що WebM/MOV/AVI
        // виглядали так, ніби спершу пишеться mp4).
        string fmtLabel = Path.GetExtension(outFile).TrimStart('.').ToLower();
        if (string.IsNullOrEmpty(fmtLabel)) fmtLabel = "mp4";

        // Write PNG frames to temp dir then encode
        string tempDir = Path.Combine(Path.GetTempPath(),
            "tesl_" + System.Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(tempDir);

        // Adjust width to match the art's aspect ratio (center viewport)
        var rt    = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var oldRT    = cam.targetTexture;
        var oldRect  = cam.rect;
        float _oldAsp = cam.aspect;
        cam.rect   = new Rect(0, 0, 1, 1);
        cam.aspect = (float)w / h;  // match RT — no black bars
        cam.targetTexture = rt;
        var tex   = new Texture2D(w, h, TextureFormat.RGB24, false);
        int total = Mathf.RoundToInt(seconds * FPS);
        Time.captureFramerate = (int)FPS;

        for (int f = 0; f < total && IsRecording; f++)
        {
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0,0,w,h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(tempDir, string.Format("f{0:D5}.png",f)),
                               tex.EncodeToPNG());
            OnRecordProgress?.Invoke((float)(f+1)/total,
                string.Format("{0}/{1} кадрів ({2})", f+1, total, fmtLabel));
            yield return new WaitForSeconds(1f/FPS);
        }

        cam.rect   = oldRect;
        cam.aspect = _oldAsp;
        cam.targetTexture = null;
        if (oldRT != null) cam.targetTexture = oldRT;
        Time.captureFramerate = 0;
        Destroy(rt); Destroy(tex);

        OnRecordProgress?.Invoke(1f, "Кодування " + fmtLabel + "... (зачекайте)");
        bool done = false; string err = null;
        System.Threading.ThreadPool.QueueUserWorkItem(_ => {
            try {
                string args = string.Format(
                    "-y -framerate {0} -i \"{1}/f%05d.png\" {2} \"{3}\"",
                    FPS, tempDir, codec, outFile);
                var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = ffmpegPath;
                proc.StartInfo.Arguments = args;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.Start();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0) err = stderr;
            } catch (System.Exception ex) { err = ex.Message; }
            done = true;
        });
        // Живий індикатор під час кодування ffmpeg (інакше прогрес завмирав
        // на 100% і було незрозуміло, чи процес ще триває).
        float _encT = 0f;
        while (!done)
        {
            _encT += UnityEngine.Time.unscaledDeltaTime;
            int dots = ((int)(_encT * 2f)) % 4;
            OnRecordProgress?.Invoke(1f, "Кодування " + fmtLabel
                + new string('.', dots) + " (зачекайте)");
            yield return null;
        }
        try { Directory.Delete(tempDir, true); } catch {}
        if (err != null) OnRecordError?.Invoke("FFmpeg: " + err);
        else             OnRecordDone?.Invoke(outFile);
    }

    // ── GIF: two-pass palette generation ──────────────────────────
    IEnumerator RecordGIF(Camera cam, string ffmpegPath, string outFile,
                          int w, int h, float seconds)
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "tesl_gif_" + System.Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(tempDir);

        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var oldRT   = cam.targetTexture;
        var oldRect = cam.rect;
        float oldAsp = cam.aspect;
        cam.rect = new Rect(0,0,1,1);
        cam.aspect = (float)w / h;
        cam.targetTexture = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        int total = Mathf.RoundToInt(seconds * FPS);
        Time.captureFramerate = (int)FPS;

        for (int f = 0; f < total && IsRecording; f++)
        {
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0,0,w,h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(tempDir, string.Format("f{0:D5}.png", f)), tex.EncodeToPNG());
            OnRecordProgress?.Invoke((float)(f+1)/total, string.Format("{0}/{1} кадрів (gif)", f+1, total));
            yield return new WaitForSeconds(1f/FPS);
        }

        cam.rect = oldRect; cam.aspect = oldAsp;
        cam.targetTexture = null; if (oldRT != null) cam.targetTexture = oldRT;
        Time.captureFramerate = 0;
        Destroy(rt); Destroy(tex);

        OnRecordProgress?.Invoke(1f, "Генерація палітри GIF...");
        bool done = false; string err = null;
        System.Threading.ThreadPool.QueueUserWorkItem(_ => {
            try {
                string paletteFile = Path.Combine(tempDir, "palette.png");
                // Pass 1: generate palette
                RunFFmpegSync(ffmpegPath,
                    string.Format("-y -i \"{0}/f%05d.png\" -vf palettegen \"{1}\"", tempDir, paletteFile));
                // Pass 2: generate GIF using palette
                string gifArgs = string.Format(
                    "-y -i \"{0}/f%05d.png\" -i \"{1}\" "
                    + "-lavfi \"fps={2}[x];[x][1:v]paletteuse\" -loop 0 \"{3}\"",
                    tempDir, paletteFile, Mathf.RoundToInt(FPS), outFile);
                string gifErr = RunFFmpegSync(ffmpegPath, gifArgs);
                if (!string.IsNullOrEmpty(gifErr) && !System.IO.File.Exists(outFile))
                    err = gifErr;
            } catch (System.Exception ex) { err = ex.Message; }
            done = true;
        });
        while (!done) yield return null;
        try { Directory.Delete(tempDir, true); } catch {}
        if (err != null) OnRecordError?.Invoke("FFmpeg GIF: " + err);
        else             OnRecordDone?.Invoke(outFile);
    }

    // Run ffmpeg synchronously, return stderr
    static string RunFFmpegSync(string ffmpegPath, string args)
    {
        var proc = new System.Diagnostics.Process();
        proc.StartInfo.FileName = ffmpegPath;
        proc.StartInfo.Arguments = args;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardError = true;
        proc.Start();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode != 0 ? stderr : null;
    }

    // ── MJPEG AVI fallback ────────────────────────────────────
    IEnumerator RecordAVI(Camera cam, string outFile, int w, int h, float seconds)
    {
        var avi   = new AviWriter(); avi.JpegQuality = JPEG_QUALITY;
        try { avi.Open(outFile, w, h, FPS); }
        catch (System.Exception ex) { OnRecordError?.Invoke(ex.Message); yield break; }

        // Adjust width to match the art's aspect ratio (center viewport)
        var rt    = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var oldRT    = cam.targetTexture;
        var oldRect  = cam.rect;
        float _oldAsp = cam.aspect;
        cam.rect   = new Rect(0, 0, 1, 1);
        cam.aspect = (float)w / h;  // match RT — no black bars
        cam.targetTexture = rt;
        var tex   = new Texture2D(w, h, TextureFormat.RGB24, false);
        int total = Mathf.RoundToInt(seconds * FPS);
        Time.captureFramerate = (int)FPS;

        for (int f = 0; f < total && IsRecording; f++)
        {
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0,0,w,h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            avi.AddFrame(tex.EncodeToJPG(JPEG_QUALITY));
            OnRecordProgress?.Invoke((float)(f+1)/total,
                string.Format("{0}/{1} кадрів (avi)", f+1, total));
            yield return new WaitForSeconds(1f/FPS);
        }

        Time.captureFramerate = 0;
        cam.rect   = oldRect;
        cam.aspect = _oldAsp;
        cam.targetTexture = null;
        if (oldRT != null) cam.targetTexture = oldRT;
        Destroy(rt); Destroy(tex);
        try { avi.Close(); } catch (System.Exception ex) { Time.captureFramerate = 0; OnRecordError?.Invoke(ex.Message); yield break; }
        Time.captureFramerate = 0;
        OnRecordDone?.Invoke(outFile);
    }

    static string Sanitize(string name)
    {
        char[] inv = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (char c in name) sb.Append(System.Array.IndexOf(inv,c)<0 ? c : '_');
        return sb.ToString().TrimEnd('_',' ');
    }
}
