using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace EspacoDisco
{
    class Row
    {
        public bool IsDir;
        public DirItem Dir;
        public string Name;
        public long Size, Files, Dirs, Mtime;
        public int Icon;
    }

    class Target
    {
        public DirItem Parent;
        public DirItem Dir;        // null quando é arquivo
        public string FileName;
        public string Path;
        public long Size;
        public bool Gone;          // sumiu do disco depois de apagar
        public DirItem Rescan;     // apagou só parte: nova leitura da pasta
    }

    class DriveEntry
    {
        public string Path, Text;
        public override string ToString() { return Text; }
    }

    class Icons
    {
        public ImageList List;
        Dictionary<string, int> map = new Dictionary<string, int>();

        public Icons(int size)
        {
            List = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
            Load("<dir>", "pasta", Native.ATTR_DIR);
        }

        public int For(string name, bool dir)
        {
            string key;
            if (dir) key = "<dir>";
            else
            {
                int i = name.LastIndexOf('.');
                key = i >= 0 ? name.Substring(i).ToLowerInvariant() : "";
            }
            int idx;
            if (map.TryGetValue(key, out idx)) return idx;
            return Load(key, "arquivo" + key, Native.ATTR_NORMAL);
        }

        int Load(string key, string fake, uint attr)
        {
            var info = new Native.SHFILEINFO();
            // SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES: não toca no disco
            Native.SHGetFileInfoW(fake, attr, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), 0x100 | 0x1 | 0x10);
            int idx = -1;
            if (info.hIcon != IntPtr.Zero)
            {
                using (var ic = Icon.FromHandle(info.hIcon)) List.Images.Add((Icon)ic.Clone());
                Native.DestroyIcon(info.hIcon);
                idx = List.Images.Count - 1;
            }
            map[key] = idx;
            return idx;
        }
    }

    class MainForm : Form
    {
        static readonly CultureInfo pt = new CultureInfo("pt-BR");

        ToolStripComboBox cbDrive;
        ToolStripButton btnScan, btnStop, btnFolder, btnRecycle, btnNuke, btnRefresh, btnExplorer, btnAdmin;
        Label lblInfo;
        TreeView tree;
        ListView list;
        SplitContainer split;
        ToolStripStatusLabel stText, stFree;
        System.Windows.Forms.Timer timer;
        ContextMenuStrip menu;
        Font boldFont;
        Icons icons;
        float dpi = 1f;

        Scanner scanner;
        DirItem root, cur;
        List<Row> rows = new List<Row>();
        int sortCol = 1;
        bool sortDesc = true;
        string mode;          // null, "scan", "del"
        Stopwatch sw;
        Control activeSrc;
        string startPath;

        int S(int v) { return (int)Math.Round(v * dpi); }

        public MainForm(string startPath)
        {
            this.startPath = startPath;
            Text = "Espaço em Disco" + (IsAdmin() ? "  (administrador)" : "");
            Font = SystemFonts.MessageBoxFont;
            using (var g = CreateGraphics()) dpi = g.DpiX / 96f;
            Size = new Size(S(1250), S(780));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            icons = new Icons(SystemInformation.SmallIconSize.Width);

            // ---- barra de cima
            var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(S(6), S(4), S(6), S(4)) };
            cbDrive = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = S(330) };
            btnScan = Btn("Analisar", "Ler o disco ou a pasta escolhida", (s, e) => ScanSelected());
            btnStop = Btn("Parar", "Interromper a análise", (s, e) => { if (scanner != null) scanner.Cancel = true; });
            btnFolder = Btn("Escolher pasta…", "Analisar só uma pasta", (s, e) => PickFolder());
            btnRefresh = Btn("Atualizar (F5)", "Ler de novo a pasta selecionada", (s, e) => RefreshSelected());
            btnExplorer = Btn("Abrir no Explorer", "Mostrar o item no Explorer", (s, e) => ShowInExplorer(Targets(activeSrc)));
            btnRecycle = Btn("Lixeira (Del)", "Mandar os itens selecionados para a Lixeira", (s, e) => DoDelete(Targets(activeSrc), false));
            btnNuke = Btn("Excluir permanente (Shift+Del)", "Apagar de vez, sem passar pela Lixeira", (s, e) => DoDelete(Targets(activeSrc), true));
            btnNuke.ForeColor = Color.FromArgb(180, 30, 30);
            btnAdmin = Btn("Rodar como administrador", "Abrir de novo com permissão de administrador, para ver pastas do sistema", (s, e) => RelaunchAdmin());
            btnAdmin.Visible = !IsAdmin();
            btnStop.Enabled = false;
            ts.Items.Add(new ToolStripLabel("Disco:"));
            ts.Items.Add(cbDrive);
            ts.Items.Add(btnScan);
            ts.Items.Add(btnStop);
            ts.Items.Add(btnFolder);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnRefresh);
            ts.Items.Add(btnExplorer);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnRecycle);
            ts.Items.Add(btnNuke);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnAdmin);

            lblInfo = new Label
            {
                Dock = DockStyle.Top, Height = S(28), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(8), 0, 0, 0), AutoEllipsis = true,
                Text = "Escolha um disco e clique em Analisar."
            };

            // ---- árvore
            tree = new TreeView
            {
                Dock = DockStyle.Fill, DrawMode = TreeViewDrawMode.OwnerDrawText, FullRowSelect = true,
                ShowLines = false, HideSelection = false, ImageList = icons.List, ImageIndex = 0,
                SelectedImageIndex = 0, BorderStyle = BorderStyle.None, ItemHeight = S(22)
            };
            boldFont = new Font(tree.Font, FontStyle.Bold);
            SetDoubleBuffered(tree);
            tree.BeforeExpand += (s, e) => Populate(e.Node);
            tree.AfterSelect += (s, e) => { var d = e.Node.Tag as DirItem; if (d != null) ShowFolder(d); };
            tree.DrawNode += DrawNode;
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            tree.Enter += (s, e) => activeSrc = tree;
            tree.GotFocus += (s, e) => tree.Invalidate();
            tree.LostFocus += (s, e) => tree.Invalidate();

            // ---- lista
            list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true,
                HideSelection = false, SmallImageList = icons.List, BorderStyle = BorderStyle.None
            };
            SetDoubleBuffered(list);
            list.Columns.Add("Nome", S(330));
            list.Columns.Add("Tamanho", S(95), HorizontalAlignment.Right);
            list.Columns.Add("% da pasta", S(85), HorizontalAlignment.Right);
            list.Columns.Add("Arquivos", S(80), HorizontalAlignment.Right);
            list.Columns.Add("Pastas", S(70), HorizontalAlignment.Right);
            list.Columns.Add("Modificado", S(130));
            list.RetrieveVirtualItem += RetrieveItem;
            list.ColumnClick += (s, e) =>
            {
                int c = e.Column == 2 ? 1 : e.Column;
                if (c == sortCol) sortDesc = !sortDesc; else { sortCol = c; sortDesc = c != 0; }
                SortRows();
                list.Invalidate();
            };
            list.DoubleClick += (s, e) => OpenSelectedRow();
            list.Enter += (s, e) => activeSrc = list;

            split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(list);

            // ---- menu do botão direito
            menu = new ContextMenuStrip();
            menu.Items.Add("Abrir", null, (s, e) => OpenTargets(Targets(menu.SourceControl)));
            menu.Items.Add("Mostrar no Explorer", null, (s, e) => ShowInExplorer(Targets(menu.SourceControl)));
            menu.Items.Add("Copiar caminho", null, (s, e) =>
            {
                var t = Targets(menu.SourceControl);
                if (t.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, t.Select(x => x.Path)));
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Atualizar esta pasta", null, (s, e) => { activeSrc = menu.SourceControl; RefreshSelected(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Mandar para a Lixeira        Del", null, (s, e) => DoDelete(Targets(menu.SourceControl), false));
            var nuke = menu.Items.Add("Excluir permanente      Shift+Del", null, (s, e) => DoDelete(Targets(menu.SourceControl), true));
            nuke.ForeColor = Color.FromArgb(180, 30, 30);
            tree.ContextMenuStrip = menu;
            list.ContextMenuStrip = menu;

            // ---- barra de baixo
            var status = new StatusStrip();
            stText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            stFree = new ToolStripStatusLabel();
            status.Items.Add(stText);
            status.Items.Add(stFree);

            Controls.Add(split);
            Controls.Add(lblInfo);
            Controls.Add(ts);
            Controls.Add(status);

            timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (s, e) => Tick();

            Load += (s, e) => OnLoaded();
            KeyDown += OnKey;
        }

        ToolStripButton Btn(string text, string tip, EventHandler click)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
            b.Click += click;
            return b;
        }

        static void SetDoubleBuffered(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(c, true, null);
        }

        static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        void OnLoaded()
        {
            split.SplitterDistance = S(460);
            FillDrives();
            if (startPath != null)
            {
                SelectDriveFor(startPath);
                StartScan(startPath);
            }
        }

        // ================= discos =================

        void FillDrives()
        {
            cbDrive.Items.Clear();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel + "  ";
                    cbDrive.Items.Add(new DriveEntry
                    {
                        Path = d.RootDirectory.FullName,
                        Text = d.Name + "   " + label + "—  " + Fmt(d.AvailableFreeSpace) + " livres de " + Fmt(d.TotalSize)
                    });
                }
                catch { }
            }
            if (cbDrive.Items.Count > 0) cbDrive.SelectedIndex = 0;
        }

        void SelectDriveFor(string path)
        {
            foreach (DriveEntry d in cbDrive.Items)
                if (string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)) { cbDrive.SelectedItem = d; return; }
            var e = new DriveEntry { Path = path, Text = "Pasta:  " + path };
            cbDrive.Items.Add(e);
            cbDrive.SelectedItem = e;
        }

        void UpdateFree(string path)
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(path));
                double pct = d.TotalSize > 0 ? 100.0 * d.AvailableFreeSpace / d.TotalSize : 0;
                stFree.Text = "Livre em " + d.Name + ":  " + Fmt(d.AvailableFreeSpace) + " de " + Fmt(d.TotalSize) + "  (" + pct.ToString("0.0", pt) + " %)";
            }
            catch { stFree.Text = ""; }
        }

        void PickFolder()
        {
            if (mode != null) return;
            using (var dlg = new FolderBrowserDialog { Description = "Escolha a pasta para analisar", ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SelectDriveFor(dlg.SelectedPath);
                StartScan(dlg.SelectedPath);
            }
        }

        void ScanSelected()
        {
            var d = cbDrive.SelectedItem as DriveEntry;
            if (d != null) StartScan(d.Path);
        }

        // ================= análise =================

        void StartScan(string path)
        {
            if (mode != null) return;
            scanner = new Scanner();
            SetBusy("scan");
            tree.Nodes.Clear();
            rows.Clear();
            list.VirtualListSize = 0;
            root = null; cur = null;
            lblInfo.Text = "Analisando " + path + " …";
            var sc = scanner;
            var th = new Thread(() =>
            {
                DirItem r = null; Exception err = null;
                try { r = sc.Scan(path); } catch (Exception ex) { err = ex; }
                BeginInvoke(new Action(() => ScanDone(sc, r, err, path)));
            }, 64 * 1024 * 1024) { IsBackground = true };
            th.Start();
        }

        void ScanDone(Scanner sc, DirItem r, Exception err, string path)
        {
            SetBusy(null);
            if (err != null)
            {
                MessageBox.Show(this, "Não deu para analisar " + path + ":\n\n" + err.Message, "Espaço em Disco", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            root = r;
            var rn = MakeNode(root);
            tree.Nodes.Add(rn);
            Populate(rn);
            rn.Expand();
            tree.SelectedNode = rn;
            ShowFolder(root);
            tree.Focus();
            string txt = (sc.Cancel ? "Análise interrompida — " : "Pronto — ")
                + N(sc.Files) + " arquivos, " + Fmt(root.Size) + " em " + sw.Elapsed.TotalSeconds.ToString("0.0", pt) + " s";
            if (sc.Denied > 0)
                txt += "   ·   " + N(sc.Denied) + " pastas sem acesso" + (IsAdmin() ? "" : " (rode como administrador para ver tudo)");
            stText.Text = txt;
            UpdateFree(path);
        }

        void SetBusy(string m)
        {
            mode = m;
            bool idle = m == null;
            btnScan.Enabled = idle; btnFolder.Enabled = idle; cbDrive.Enabled = idle;
            btnRecycle.Enabled = idle; btnNuke.Enabled = idle; btnRefresh.Enabled = idle;
            btnStop.Enabled = m == "scan";
            menu.Enabled = idle;
            UseWaitCursor = !idle;
            if (idle) timer.Stop(); else { sw = Stopwatch.StartNew(); timer.Start(); Tick(); }
        }

        void Tick()
        {
            if (mode == "scan" && scanner != null)
                stText.Text = "Analisando…  " + N(scanner.Files) + " arquivos  ·  " + Fmt(scanner.Bytes) + "  ·  " + scanner.Current;
            else if (mode == "del")
                stText.Text = "Apagando…  " + Deleter.Current;
        }

        // ================= árvore =================

        TreeNode MakeNode(DirItem d)
        {
            var n = new TreeNode(NodeText(d)) { Tag = d, ImageIndex = 0, SelectedImageIndex = 0 };
            if (d.Dirs.Count > 0) n.Nodes.Add(new TreeNode("…"));
            return n;
        }

        static string NodeText(DirItem d) { return Fmt(d.Size) + "    " + d.Label; }

        static bool IsPopulated(TreeNode n)
        {
            return !(n.Nodes.Count == 1 && n.Nodes[0].Tag == null);
        }

        void Populate(TreeNode n)
        {
            var d = n.Tag as DirItem;
            if (d == null || IsPopulated(n)) return;
            tree.BeginUpdate();
            n.Nodes.Clear();
            n.Nodes.AddRange(d.Dirs.Select(MakeNode).ToArray());
            tree.EndUpdate();
        }

        void DrawNode(object s, DrawTreeNodeEventArgs e)
        {
            var d = e.Node.Tag as DirItem;
            if (d == null || e.Bounds.Width == 0 || e.Bounds.Height == 0) { e.DrawDefault = d == null; return; }
            var g = e.Graphics;
            Rectangle b = e.Bounds;
            var rowRect = new Rectangle(b.X, b.Y, Math.Max(0, tree.ClientSize.Width - b.X), b.Height);
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            bool foc = tree.Focused;
            Color back = sel ? (foc ? SystemColors.Highlight : Color.FromArgb(220, 220, 220)) : tree.BackColor;
            Color fore = sel && foc ? SystemColors.HighlightText : tree.ForeColor;
            using (var br = new SolidBrush(back)) g.FillRectangle(br, rowRect);

            double pct = d.Parent == null ? 1.0 : (d.Parent.Size > 0 ? (double)d.Size / d.Parent.Size : 0);
            int sizeW = S(82);
            if (!(sel && foc))
            {
                using (var br = new SolidBrush(Color.FromArgb(255, 246, 214))) g.FillRectangle(br, b.X, b.Y + 1, sizeW, b.Height - 2);
                int w = (int)Math.Round(sizeW * pct);
                if (w > 0) using (var br = new SolidBrush(Color.FromArgb(250, 204, 92))) g.FillRectangle(br, b.X, b.Y + 1, w, b.Height - 2);
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, Fmt(d.Size), tree.Font, new Rectangle(b.X, b.Y, sizeW - S(4), b.Height), fore, flags | TextFormatFlags.Right);
            string name = d.Label + (d.Denied ? "   (sem acesso)" : "");
            Font f = pct >= 0.10 ? boldFont : tree.Font;
            Color nameColor = d.Denied && !(sel && foc) ? Color.Gray : fore;
            TextRenderer.DrawText(g, name, f, new Rectangle(b.X + sizeW + S(6), b.Y, Math.Max(0, rowRect.Width - sizeW - S(6)), b.Height),
                nameColor, flags | TextFormatFlags.EndEllipsis);
        }

        TreeNode FindNode(DirItem d)
        {
            if (tree.Nodes.Count == 0) return null;
            var chain = new List<DirItem>();
            for (var a = d; a != null; a = a.Parent) chain.Insert(0, a);
            TreeNode n = tree.Nodes[0];
            if (n.Tag != chain[0]) return null;
            for (int i = 1; i < chain.Count; i++)
            {
                if (!IsPopulated(n)) return null;
                TreeNode next = null;
                foreach (TreeNode c in n.Nodes) if (c.Tag == chain[i]) { next = c; break; }
                if (next == null) return null;
                n = next;
            }
            return n;
        }

        void SelectInTree(DirItem d)
        {
            if (tree.Nodes.Count == 0) return;
            var chain = new List<DirItem>();
            for (var a = d; a != null; a = a.Parent) chain.Insert(0, a);
            TreeNode n = tree.Nodes[0];
            if (n.Tag != chain[0]) return;
            for (int i = 1; i < chain.Count; i++)
            {
                Populate(n);
                TreeNode next = null;
                foreach (TreeNode c in n.Nodes) if (c.Tag == chain[i]) { next = c; break; }
                if (next == null) break;
                n = next;
            }
            tree.SelectedNode = n;
            n.EnsureVisible();
        }

        /// <summary>Acerta texto, ordem e filhos de um nó depois de mudar o modelo.</summary>
        void SyncNode(TreeNode n)
        {
            var d = n.Tag as DirItem;
            if (d == null) return;
            n.Text = NodeText(d);
            if (!IsPopulated(n))
            {
                n.Nodes.Clear();
                if (d.Dirs.Count > 0) n.Nodes.Add(new TreeNode("…"));
                return;
            }
            var existing = new Dictionary<DirItem, TreeNode>();
            foreach (TreeNode c in n.Nodes) { var cd = c.Tag as DirItem; if (cd != null) existing[cd] = c; }
            var wanted = new List<TreeNode>();
            foreach (var c in d.Dirs)
            {
                TreeNode cn;
                if (existing.TryGetValue(c, out cn)) cn.Text = NodeText(c);
                else cn = MakeNode(c);
                wanted.Add(cn);
            }
            bool same = wanted.Count == n.Nodes.Count;
            for (int i = 0; same && i < wanted.Count; i++) same = n.Nodes[i] == wanted[i];
            if (!same)
            {
                bool exp = n.IsExpanded;
                n.Nodes.Clear();
                n.Nodes.AddRange(wanted.ToArray());
                if (exp) n.Expand();
            }
        }

        void SyncChain(DirItem p)
        {
            tree.BeginUpdate();
            for (var a = p; a != null; a = a.Parent)
            {
                var n = FindNode(a);
                if (n != null) SyncNode(n);
            }
            tree.EndUpdate();
        }

        // ================= lista =================

        void ShowFolder(DirItem d)
        {
            cur = d;
            rows.Clear();
            foreach (var c in d.Dirs)
                rows.Add(new Row { IsDir = true, Dir = c, Name = c.Name, Size = c.Size, Files = c.FileCount, Dirs = c.DirCount, Mtime = c.Mtime, Icon = 0 });
            foreach (var f in d.Files)
                rows.Add(new Row { Name = f.Name, Size = f.Size, Mtime = f.Mtime, Icon = icons.For(f.Name, false) });
            SortRows();
            list.SelectedIndices.Clear();
            list.VirtualListSize = rows.Count;
            list.Invalidate();
            lblInfo.Text = d.FullPath + "        Tamanho: " + Fmt(d.Size) + "      Arquivos: " + N(d.FileCount) + "      Pastas: " + N(d.DirCount)
                + (d.Denied ? "      (sem acesso a esta pasta)" : "");
        }

        void SortRows()
        {
            Comparison<Row> c;
            switch (sortCol)
            {
                case 0: c = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case 3: c = (a, b) => a.Files.CompareTo(b.Files); break;
                case 4: c = (a, b) => a.Dirs.CompareTo(b.Dirs); break;
                case 5: c = (a, b) => a.Mtime.CompareTo(b.Mtime); break;
                default: c = (a, b) => a.Size.CompareTo(b.Size); break;
            }
            if (sortDesc) rows.Sort((a, b) => c(b, a)); else rows.Sort(c);
        }

        void RetrieveItem(object s, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= rows.Count) { e.Item = new ListViewItem(new[] { "", "", "", "", "", "" }); return; }
            var r = rows[e.ItemIndex];
            double pct = cur != null && cur.Size > 0 ? 100.0 * r.Size / cur.Size : 0;
            var it = new ListViewItem(r.Name, r.Icon);
            it.SubItems.Add(Fmt(r.Size));
            it.SubItems.Add(pct.ToString("0.0", pt) + " %");
            it.SubItems.Add(r.IsDir ? N(r.Files) : "");
            it.SubItems.Add(r.IsDir ? N(r.Dirs) : "");
            string dt = "";
            if (r.Mtime > 0) { try { dt = DateTime.FromFileTimeUtc(r.Mtime).ToLocalTime().ToString("dd/MM/yyyy HH:mm", pt); } catch { } }
            it.SubItems.Add(dt);
            if (r.IsDir && r.Dir.Denied) it.ForeColor = Color.Gray;
            e.Item = it;
        }

        void OpenSelectedRow()
        {
            if (list.SelectedIndices.Count == 0) return;
            var r = rows[list.SelectedIndices[0]];
            if (r.IsDir) { SelectInTree(r.Dir); list.Focus(); }
            else OpenTargets(Targets(list));
        }

        // ================= ações =================

        List<Target> Targets(Control src)
        {
            var res = new List<Target>();
            if (src == list && cur != null)
            {
                foreach (int i in list.SelectedIndices)
                {
                    var r = rows[i];
                    if (r.IsDir) res.Add(new Target { Parent = cur, Dir = r.Dir, Path = r.Dir.FullPath, Size = r.Size });
                    else
                    {
                        string p = cur.FullPath;
                        res.Add(new Target { Parent = cur, FileName = r.Name, Path = (p.EndsWith("\\") ? p : p + "\\") + r.Name, Size = r.Size });
                    }
                }
            }
            else if (tree.SelectedNode != null)
            {
                var d = tree.SelectedNode.Tag as DirItem;
                if (d != null) res.Add(new Target { Parent = d.Parent, Dir = d, Path = d.FullPath, Size = d.Size });
            }
            return res;
        }

        void OpenTargets(List<Target> t)
        {
            foreach (var x in t.Take(5))
            {
                try
                {
                    if (x.Dir != null) Process.Start("explorer.exe", "\"" + x.Path + "\"");
                    else Process.Start(new ProcessStartInfo(x.Path) { UseShellExecute = true });
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Abrir", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        void ShowInExplorer(List<Target> t)
        {
            if (t.Count == 0) return;
            try { Process.Start("explorer.exe", "/select,\"" + t[0].Path + "\""); } catch { }
        }

        void OnKey(object s, KeyEventArgs e)
        {
            bool inView = tree.Focused || list.Focused;
            if (e.KeyCode == Keys.F5) { RefreshSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete && inView)
            {
                DoDelete(Targets(tree.Focused ? (Control)tree : list), e.Shift);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Back && list.Focused && cur != null && cur.Parent != null)
            {
                SelectInTree(cur.Parent); e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && list.Focused) { OpenSelectedRow(); e.Handled = true; }
        }

        static bool IsSystemPath(string p)
        {
            var sys = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };
            foreach (var x in sys)
            {
                if (string.IsNullOrEmpty(x)) continue;
                if (p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!string.IsNullOrEmpty(users))
            {
                // C:\Users e a pasta de cada usuário em si
                if (p.Equals(users, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(Path.GetDirectoryName(p), users, StringComparison.OrdinalIgnoreCase)) return true;
            }
            string name = Path.GetFileName(p);
            return name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }

        void DoDelete(List<Target> t, bool permanent)
        {
            if (mode != null || t.Count == 0) return;
            if (t.Any(x => x.Parent == null))
            {
                MessageBox.Show(this, "A pasta raiz da análise não pode ser apagada por aqui.", "Espaço em Disco", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            long total = t.Sum(x => x.Size);
            var sb = new StringBuilder();
            string what = t.Count == 1 ? "1 item" : t.Count + " itens";
            if (permanent) sb.AppendLine("EXCLUIR PERMANENTE " + what + " (" + Fmt(total) + ")?");
            else sb.AppendLine("Mandar para a Lixeira " + what + " (" + Fmt(total) + ")?");
            sb.AppendLine();
            foreach (var x in t.Take(12)) sb.AppendLine("•  " + x.Path + "   (" + Fmt(x.Size) + ")");
            if (t.Count > 12) sb.AppendLine("…  e mais " + (t.Count - 12));
            if (t.Any(x => IsSystemPath(x.Path)))
            {
                sb.AppendLine();
                sb.AppendLine("ATENÇÃO: tem pasta ou arquivo do sistema na seleção. Apagar pode quebrar o Windows ou programas instalados.");
            }
            sb.AppendLine();
            sb.AppendLine(permanent ? "Não passa pela Lixeira e NÃO dá para desfazer."
                                    : "Dá para recuperar pela Lixeira depois. Se algo for grande demais para a Lixeira, o Windows avisa antes de apagar de vez.");

            var ok = MessageBox.Show(this, sb.ToString(), permanent ? "Excluir permanente" : "Mandar para a Lixeira",
                MessageBoxButtons.YesNo, permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (ok != DialogResult.Yes) return;

            long before = root.Size;
            var errors = new List<string>();

            if (!permanent)
            {
                // Lixeira: o próprio Windows mostra o progresso e os erros
                SetBusy("del");
                Deleter.Current = "mandando para a Lixeira…";
                var op = new Native.SHFILEOPSTRUCT
                {
                    hwnd = Handle,
                    wFunc = 3, // FO_DELETE
                    pFrom = string.Join("\0", t.Select(x => x.Path)) + "\0\0",
                    // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING
                    fFlags = 0x40 | 0x10 | 0x4000
                };
                int rc = Native.SHFileOperationW(ref op);
                if (rc != 0 && !op.fAnyOperationsAborted) errors.Add("O Windows devolveu o código " + rc + " ao mandar para a Lixeira.");
                Resolve(t);
                SetBusy(null);
                ApplyDelete(t, errors, before);
                return;
            }

            SetBusy("del");
            var th = new Thread(() =>
            {
                foreach (var x in t) Deleter.Permanent(x.Path, x.Dir != null, errors);
                Deleter.Current = "conferindo o que sobrou…";
                Resolve(t);
                BeginInvoke(new Action(() => { SetBusy(null); ApplyDelete(t, errors, before); }));
            }, 16 * 1024 * 1024) { IsBackground = true };
            th.Start();
        }

        /// <summary>Vê o que sumiu de fato; pasta que sobrou em parte é lida de novo.</summary>
        static void Resolve(List<Target> t)
        {
            foreach (var x in t)
            {
                x.Gone = !Native.Exists(x.Path);
                if (!x.Gone && x.Dir != null) x.Rescan = new Scanner().Scan(x.Path);
            }
        }

        void ApplyDelete(List<Target> t, List<string> errors, long before)
        {
            DirItem focus = cur;
            foreach (var x in t)
            {
                if (x.Gone)
                {
                    if (x.Dir != null) RemoveDir(x.Parent, x.Dir);
                    else RemoveFile(x.Parent, x.FileName);
                }
                else if (x.Rescan != null)
                {
                    ReplaceDir(x.Parent, x.Dir, x.Rescan);
                    if (focus == x.Dir) focus = x.Rescan;
                }
            }
            foreach (var p in t.Select(x => x.Parent).Distinct()) SyncChain(p);
            while (focus != null && !Attached(focus)) focus = focus.Parent;
            if (focus == null) focus = root;
            SelectInTree(focus);
            ShowFolder(focus);
            tree.Invalidate();

            long freed = before - root.Size;
            stText.Text = "Liberado: " + Fmt(Math.Max(0, freed)) + (errors.Count > 0 ? "   ·   " + errors.Count + " itens não puderam ser apagados" : "");
            UpdateFree(root.FullPath);

            if (errors.Count > 0)
            {
                var sb = new StringBuilder("Alguns itens não foram apagados (em uso, sem permissão ou cancelados):\n\n");
                foreach (var e in errors.Take(15)) sb.AppendLine(e);
                if (errors.Count > 15) sb.AppendLine("…  e mais " + (errors.Count - 15));
                MessageBox.Show(this, sb.ToString(), "Espaço em Disco", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        bool Attached(DirItem d)
        {
            for (var a = d; a != null; a = a.Parent)
            {
                if (a.Parent == null) return a == root;
                if (!a.Parent.Dirs.Contains(a)) return false;
            }
            return false;
        }

        void Propagate(DirItem p, long dSize, long dFiles, long dDirs)
        {
            for (var a = p; a != null; a = a.Parent)
            {
                a.Size += dSize; a.FileCount += dFiles; a.DirCount += dDirs;
                a.Dirs.Sort(Scanner.BySizeDesc);
            }
        }

        void RemoveDir(DirItem p, DirItem d)
        {
            if (!p.Dirs.Remove(d)) return;
            Propagate(p, -d.Size, -d.FileCount, -(d.DirCount + 1));
        }

        void RemoveFile(DirItem p, string name)
        {
            int i = p.Files.FindIndex(f => f.Name == name);
            if (i < 0) return;
            long s = p.Files[i].Size;
            p.Files.RemoveAt(i);
            Propagate(p, -s, -1, 0);
        }

        void ReplaceDir(DirItem p, DirItem old, DirItem nw)
        {
            int i = p.Dirs.IndexOf(old);
            if (i < 0) return;
            nw.Name = old.Name; nw.RootPath = null; nw.Parent = p; nw.Mtime = old.Mtime;
            p.Dirs[i] = nw;
            Propagate(p, nw.Size - old.Size, nw.FileCount - old.FileCount, nw.DirCount - old.DirCount);
        }

        void RefreshSelected()
        {
            if (mode != null || root == null) return;
            DirItem d = null;
            if (activeSrc == list && list.SelectedIndices.Count == 1 && rows[list.SelectedIndices[0]].IsDir)
                d = rows[list.SelectedIndices[0]].Dir;
            else if (tree.SelectedNode != null) d = tree.SelectedNode.Tag as DirItem;
            if (d == null) d = cur;
            if (d == null) return;
            if (d.Parent == null) { StartScan(d.RootPath); return; }

            scanner = new Scanner();
            var sc = scanner;
            string path = d.FullPath;
            SetBusy("scan");
            var th = new Thread(() =>
            {
                DirItem nw = null;
                bool exists = Native.Exists(path);
                if (exists) nw = sc.Scan(path);
                BeginInvoke(new Action(() =>
                {
                    SetBusy(null);
                    var p = d.Parent;
                    if (nw == null) RemoveDir(p, d); else ReplaceDir(p, d, nw);
                    SyncChain(p);
                    var target = nw ?? p;
                    SelectInTree(target);
                    ShowFolder(target);
                    tree.Invalidate();
                    stText.Text = "Atualizado: " + path;
                    UpdateFree(path);
                }));
            }, 64 * 1024 * 1024) { IsBackground = true };
            th.Start();
        }

        void RelaunchAdmin()
        {
            try
            {
                string arg = "";
                if (root != null) arg = "\"" + root.RootPath.TrimEnd('\\') + "\"";
                else
                {
                    var d = cbDrive.SelectedItem as DriveEntry;
                    if (d != null) arg = "\"" + d.Path.TrimEnd('\\') + "\"";
                }
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, arg) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Win32Exception) { /* cancelou o UAC */ }
        }

        // ================= formatação =================

        public static string Fmt(long b)
        {
            if (b < 1024) return b.ToString(pt) + " B";
            double v = b;
            string[] u = { "KB", "MB", "GB", "TB", "PB" };
            int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return v.ToString(v < 100 ? "0.0" : "0", pt) + " " + u[i];
        }

        static string N(long n) { return n.ToString("N0", pt); }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string p = args.Length > 0 ? args[0].Trim('"') : null;
            if (p != null && p.Length == 2 && p[1] == ':') p += "\\";
            if (p != null && !Directory.Exists(p)) p = null;
            Application.Run(new MainForm(p));
        }
    }
}
