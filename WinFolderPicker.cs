// Сучасний нативний діалог вибору теки (Vista+ IFileOpenDialog).
//
// ВАЖЛИВО: COM-interop виконується в ОКРЕМОМУ процесі powershell.exe (STA,
// повний .NET Framework), а НЕ в Unity. Пряме звернення до IFileOpenDialog
// з Mono у standalone-білді спричиняло Access Violation (крах vtable),
// тому весь COM винесено у зовнішній процес. Unity лише запускає скрипт
// і читає результат — без жодного ризику для процесу застосунку.
//
// Якщо сучасний діалог недоступний (стара ОС / помилка компіляції interop),
// скрипт сам відкочується на класичний FolderBrowserDialog.
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Text;

public static class WinFolderPicker
{
    public static string Pick(string title, string initialDir)
    {
        string outFile = Path.GetTempFileName();
        string csFile  = outFile + ".cs";
        string psFile  = outFile + ".ps1";

        string init = Directory.Exists(initialDir) ? initialDir : "";

        File.WriteAllText(csFile, InteropSource, new UTF8Encoding(false));

        string st = (title ?? "").Replace("'", "''");
        string si = init.Replace("'", "''");
        string sc = csFile.Replace("'", "''");
        string so = outFile.Replace("'", "''");

        var ps = new StringBuilder();
        ps.Append("$ErrorActionPreference='Stop';");
        ps.Append("$title='").Append(st).Append("';");
        ps.Append("$init='").Append(si).Append("';");
        ps.Append("$out='").Append(so).Append("';");
        ps.Append("$res='';");
        ps.Append("try{");
        ps.Append("Add-Type -Path '").Append(sc).Append("';");
        ps.Append("$res=[TeslFolderPick]::Run($title,$init);");
        ps.Append("}catch{");                       // фолбек на класичний діалог
        ps.Append("try{Add-Type -AssemblyName System.Windows.Forms;");
        ps.Append("$d=New-Object System.Windows.Forms.FolderBrowserDialog;");
        ps.Append("$d.Description=$title;");
        ps.Append("if($init -and (Test-Path $init)){$d.SelectedPath=$init};");
        ps.Append("if($d.ShowDialog() -eq 'OK'){$res=$d.SelectedPath}}catch{$res=''}");
        ps.Append("};");
        ps.Append("if($res){[System.IO.File]::WriteAllText($out,$res)}");
        File.WriteAllText(psFile, ps.ToString(), new UTF8Encoding(false));

        string picked = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName        = "powershell.exe";
            // -STA обовʼязковий для IFileOpenDialog
            psi.Arguments       = "-STA -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                                  + psFile + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow  = true;
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null) proc.WaitForExit();
            if (File.Exists(outFile))
                picked = File.ReadAllText(outFile).Trim();
        }
        finally
        {
            try { File.Delete(csFile); } catch {}
            try { File.Delete(psFile); } catch {}
            try { File.Delete(outFile); } catch {}
        }
        return picked;
    }

    // C# джерело interop, що компілюється й виконується всередині powershell.exe.
    const string InteropSource = @"
using System;
using System.Runtime.InteropServices;
public static class TeslFolderPick {
  public static string Run(string title, string init) {
    IFileDialog d = (IFileDialog)(new FOD());
    uint o; d.GetOptions(out o); d.SetOptions(o | 0x20 | 0x40 | 0x8);
    if (!string.IsNullOrEmpty(title)) d.SetTitle(title);
    if (!string.IsNullOrEmpty(init) && System.IO.Directory.Exists(init)) {
      IShellItem si;
      if (SHCreateItemFromParsingName(init, IntPtr.Zero, typeof(IShellItem).GUID, out si) == 0 && si != null)
        d.SetFolder(si);
    }
    int hr = d.Show(IntPtr.Zero);
    if (hr != 0) return "";
    IShellItem r; d.GetResult(out r);
    string p; r.GetDisplayName(0x80058000, out p); return p;
  }
  [DllImport(""shell32.dll"", CharSet=CharSet.Unicode)]
  static extern int SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);
  [ComImport, Guid(""DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"")] class FOD {}
  [ComImport, Guid(""42f85136-db7e-439c-85f1-e4075d135fc8""), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IFileDialog {
    [PreserveSig] int Show(IntPtr p);
    void SetFileTypes(uint c, IntPtr r); void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
    void Advise(IntPtr e, out uint c); void Unadvise(uint c); void SetOptions(uint o); void GetOptions(out uint o);
    void SetDefaultFolder(IShellItem i); void SetFolder(IShellItem i); void GetFolder(out IShellItem i);
    void GetCurrentSelection(out IShellItem i); void SetFileName(string n); void GetFileName(out string n);
    void SetTitle(string t); void SetOkButtonLabel(string t); void SetFileNameLabel(string t);
    void GetResult(out IShellItem i); void AddPlace(IShellItem i, int o); void SetDefaultExtension(string e);
    void Close(int hr); void SetClientGuid(ref Guid g); void ClearClientData(); void SetFilter(IntPtr f);
  }
  [ComImport, Guid(""43826d1e-e718-42ee-bc55-a1e261c37bfe""), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItem {
    void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi); void GetDisplayName(uint sigdn, out string name);
    void GetAttributes(uint mask, out uint attr); void Compare(IShellItem psi, uint hint, out int order);
  }
}
";
}
#endif
