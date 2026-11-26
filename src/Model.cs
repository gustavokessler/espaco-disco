using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EspacoDisco
{
    struct FileItem
    {
        public string Name;
        public long Size;
        public long Mtime;
    }

    class DirItem
    {
        public string Name;
        public string RootPath;      // só na raiz da análise
        public DirItem Parent;
        public List<DirItem> Dirs = new List<DirItem>();
        public List<FileItem> Files = new List<FileItem>();
        public long Size, FileCount, DirCount, Mtime;
        public bool Denied;

        public string FullPath
        {
            get
            {
                if (Parent == null) return RootPath;
                string p = Parent.FullPath;
                return p.EndsWith("\\") ? p + Name : p + "\\" + Name;
            }
        }

        public string Label { get { return Parent == null ? RootPath : Name; } }
    }

    class Scanner
    {
        public long Files, Bytes, Denied;
        public volatile string Current = "";
        public volatile bool Cancel;

        public DirItem Scan(string path)
        {
            var root = new DirItem { RootPath = path, Name = path };
            try { root.Mtime = System.IO.Directory.GetLastWriteTimeUtc(path).ToFileTimeUtc(); } catch { }
            ScanDir(root, path, 0);
            return root;
        }

        void ScanDir(DirItem d, string path, int depth)
        {
            if (Cancel) return;
            Current = path;
            Native.WIN32_FIND_DATA fd;
            IntPtr h = Native.FindFirstFileExW(Native.Long(path.TrimEnd('\\')) + "\\*", 1, out fd, 0, IntPtr.Zero, 2);
            if (h == Native.INVALID)
            {
                d.Denied = true;
                Interlocked.Increment(ref Denied);
                return;
            }
            long localBytes = 0;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    long mt = ((long)fd.ftWriteHigh << 32) | fd.ftWriteLow;
                    if ((fd.dwFileAttributes & Native.ATTR_DIR) != 0)
                    {
                        // junctions e links simbólicos: não segue (evita contar duas vezes e laços)
                        if ((fd.dwFileAttributes & Native.ATTR_REPARSE) != 0) continue;
                        d.Dirs.Add(new DirItem { Name = n, Parent = d, Mtime = mt });
                    }
                    else
                    {
                        long sz = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        d.Files.Add(new FileItem { Name = n, Size = sz, Mtime = mt });
                        localBytes += sz;
                    }
                } while (!Cancel && Native.FindNextFileW(h, out fd));
            }
            finally { Native.FindClose(h); }

            Interlocked.Add(ref Files, d.Files.Count);
            Interlocked.Add(ref Bytes, localBytes);

            string basePath = path.EndsWith("\\") ? path : path + "\\";
            if (depth < 2 && d.Dirs.Count > 1)
                Parallel.ForEach(d.Dirs, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                    c => ScanDir(c, basePath + c.Name, depth + 1));
            else
                foreach (var c in d.Dirs) ScanDir(c, basePath + c.Name, depth + 1);

            Sum(d);
        }

        public static void Sum(DirItem d)
        {
            long s = 0, fc = d.Files.Count, dc = d.Dirs.Count;
            foreach (var f in d.Files) s += f.Size;
            foreach (var c in d.Dirs) { s += c.Size; fc += c.FileCount; dc += c.DirCount; }
            d.Size = s; d.FileCount = fc; d.DirCount = dc;
            d.Dirs.Sort(BySizeDesc);
            d.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
        }

        public static int BySizeDesc(DirItem a, DirItem b) { return b.Size.CompareTo(a.Size); }
    }

    /// <summary>Exclusão permanente: apaga tudo por dentro, tira "somente leitura",
    /// e nunca entra em junction/link (só remove o link).</summary>
    static class Deleter
    {
        public static volatile string Current = "";

        public static void Permanent(string path, bool isDir, List<string> errors)
        {
            string lp = Native.Long(path.TrimEnd('\\'));
            if (isDir) DelDir(lp, path, errors);
            else DelFile(lp, path, errors);
        }

        static void DelFile(string lp, string shown, List<string> errors)
        {
            Native.SetFileAttributesW(lp, Native.ATTR_NORMAL);
            if (!Native.DeleteFileW(lp))
                errors.Add(shown + "  —  " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        static void DelDir(string lp, string shown, List<string> errors)
        {
            Current = shown;
            uint a = Native.GetFileAttributesW(lp);
            if (a == Native.ATTR_INVALID) return;
            if ((a & Native.ATTR_REPARSE) == 0)
            {
                Native.WIN32_FIND_DATA fd;
                IntPtr h = Native.FindFirstFileExW(lp + "\\*", 1, out fd, 0, IntPtr.Zero, 2);
                if (h != Native.INVALID)
                {
                    var subs = new List<KeyValuePair<string, bool>>();
                    try
                    {
                        do
                        {
                            string n = fd.cFileName;
                            if (n == "." || n == "..") continue;
                            subs.Add(new KeyValuePair<string, bool>(n, (fd.dwFileAttributes & Native.ATTR_DIR) != 0));
                        } while (Native.FindNextFileW(h, out fd));
                    }
                    finally { Native.FindClose(h); }

                    foreach (var s in subs)
                    {
                        if (s.Value) DelDir(lp + "\\" + s.Key, shown + "\\" + s.Key, errors);
                        else DelFile(lp + "\\" + s.Key, shown + "\\" + s.Key, errors);
                    }
                }
            }
            if ((a & 0x1u) != 0) // tira só o "somente leitura"
            {
                uint na = a & ~0x11u;
                Native.SetFileAttributesW(lp, na == 0 ? Native.ATTR_NORMAL : na);
            }
            if (!Native.RemoveDirectoryW(lp))
                errors.Add(shown + "  —  " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }
}
