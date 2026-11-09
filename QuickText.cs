using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

namespace KeyParcel
{
    [DataContract]
    internal sealed class QuickTextEntry
    {
        [DataMember] internal string Id;
        [DataMember] internal string Title;
        [DataMember] internal string Text;
        [DataMember] internal int Hotkey;

        internal string ShortcutId { get { return "quick:" + Id; } }
        internal Keys Chord { get { return (Keys)Hotkey; } }

        internal string Preview
        {
            get
            {
                if (String.IsNullOrWhiteSpace(Text)) return "还没有内容";
                return Text.TrimStart().Split(new[] { '\r', '\n' }, 2)[0];
            }
        }

        public override string ToString() { return Title + " · " + Hotkeys.Display(Chord); }
    }

    internal sealed class QuickTextStore
    {
        [DataContract]
        private sealed class Settings
        {
            [DataMember] internal bool Enabled;
            [DataMember] internal List<QuickTextEntry> Entries;
        }

        private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(Settings));
        private readonly string path;
        private Settings settings;
        internal bool Enabled { get { return settings.Enabled; } }
        internal IReadOnlyList<QuickTextEntry> Entries { get { return settings.Entries; } }

        internal QuickTextStore(string folder)
        {
            path = Path.Combine(folder, "quick-text.json");
            settings = new Settings { Entries = new List<QuickTextEntry>() };
            try
            {
                using (var file = File.OpenRead(path))
                {
                    var saved = Serializer.ReadObject(file) as Settings;
                    if (saved == null || saved.Entries == null) return;
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    saved.Entries = saved.Entries.Where(entry => entry != null && GuidIsUnique(entry.Id, ids)).ToList();
                    foreach (var entry in saved.Entries)
                    {
                        entry.Title = String.IsNullOrWhiteSpace(entry.Title) ? "快捷文本" : entry.Title;
                        entry.Text = entry.Text ?? "";
                        if (!Hotkeys.Valid(entry.Chord)) entry.Hotkey = 0;
                    }
                    settings = saved;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SerializationException) { }
        }

        private static bool GuidIsUnique(string id, HashSet<string> ids)
        {
            Guid parsed;
            return Guid.TryParse(id, out parsed) && ids.Add(parsed.ToString("N"));
        }

        internal void Save(bool enabled, IEnumerable<QuickTextEntry> entries)
        {
            var next = new Settings { Enabled = enabled, Entries = entries.ToList() };
            using (var buffer = new MemoryStream())
            {
                Serializer.WriteObject(buffer, next);
                MobileInbox.SaveSetting(path, Encoding.UTF8.GetString(buffer.ToArray()));
            }
            settings = next;
        }

        internal Keys NextChord(Func<Keys, bool> available)
        {
            var taken = new HashSet<Keys>(Entries.Select(entry => entry.Chord));
            foreach (Keys modifiers in new[] { Keys.Control, Keys.Control | Keys.Shift,
                Keys.Control | Keys.Alt, Keys.Control | Keys.Alt | Keys.Shift })
                for (int index = 1; index <= 10; index++)
                {
                    var chord = modifiers | (Keys)((int)Keys.D0 + index % 10);
                    if (!taken.Contains(chord) && available(chord)) return chord;
                }
            return Keys.None;
        }
    }

    internal sealed class QuickTextWindow : Form
    {
        private readonly QuickTextStore store;
        private readonly Func<Keys, bool> available;
        private readonly CheckBox enable = new CheckBox { AutoSize = true, Text = "启用快捷文本" };
        private readonly EntryList list = new EntryList();
        private readonly TextBox title = new TextBox { BorderStyle = BorderStyle.None, MaxLength = 80, AccessibleName = "条目标题" };
        private readonly TextBox content = new TextBox { BorderStyle = BorderStyle.None, Multiline = true,
            AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "条目内容" };
        private readonly ShortcutField recorder = new ShortcutField(Keys.None, "条目快捷键");
        private readonly Label status;
        private readonly Button remove, save, clear;
        private readonly Control editor;
        private QuickTextEntry current;
        private Keys candidate;
        private bool updating;

        internal QuickTextWindow(QuickTextStore store, Func<Keys, bool> available)
        {
            this.store = store;
            this.available = available;
            Theme.Window(this, "KeyParcel · 快捷文本", 714, 458);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            AutoScroll = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var root = Theme.Table(0);
            root.Padding = new Padding(22, 17, 22, 12);
            Controls.Add(root);
            var header = Theme.Table(0, -1);
            Theme.Put(header, Theme.Label("快捷文本", Theme.Section, Theme.Ink), 0, 0, Padding.Empty);
            Theme.Put(header, enable, 1, 0, Padding.Empty);
            Theme.Put(root, header, 0, 0, Padding.Empty);
            Theme.Put(root, Theme.Label("编辑完成后关闭此窗口，已启用的快捷键即可在其他应用中使用。", Theme.Small, Theme.Muted),
                0, 1, new Padding(0, 5, 0, 14));

            var body = Theme.Table(0, 0);
            body.ColumnStyles[0].Width = 33;
            body.ColumnStyles[1].Width = 67;
            var sidebar = Theme.Table(0);
            Theme.Put(sidebar, Theme.Field(list, 5, stretch: true), 0, 0, new Padding(0, 0, 0, 8));
            var actions = Theme.Table(0, -1);
            var add = Theme.Button("新增条目", true);
            remove = Theme.Button("删除");
            Theme.Put(actions, add, 0, 0, new Padding(0, 0, 8, 0));
            Theme.Put(actions, remove, 1, 0, Padding.Empty);
            Theme.Put(sidebar, actions, 0, 1, Padding.Empty);
            Theme.Stretch(sidebar, 0);
            Theme.Put(body, sidebar, 0, 0, new Padding(0, 0, 15, 0));

            var fields = Theme.Table(0);
            editor = fields;
            Theme.Put(fields, Theme.Label("标题", Theme.Small, Theme.Muted), 0, 0, new Padding(0, 0, 0, 4));
            Theme.Put(fields, Theme.Field(title), 0, 1, new Padding(0, 0, 0, 9));
            Theme.Put(fields, Theme.Label("粘贴内容", Theme.Small, Theme.Muted), 0, 2, new Padding(0, 0, 0, 4));
            Theme.Put(fields, Theme.Field(content, stretch: true), 0, 3, new Padding(0, 0, 0, 9));
            var bindings = Theme.Table(0, -1, -1);
            clear = Theme.Button("清除按键");
            save = Theme.Button("保存条目", true);
            Theme.Put(bindings, Theme.Field(recorder), 0, 0, new Padding(0, 0, 7, 0));
            Theme.Put(bindings, clear, 1, 0, new Padding(0, 0, 7, 0));
            Theme.Put(bindings, save, 2, 0, Padding.Empty);
            Theme.Put(fields, bindings, 0, 4, Padding.Empty);
            Theme.Stretch(fields, 3);
            Theme.Put(body, fields, 1, 0, Padding.Empty);
            Theme.Stretch(body, 0);
            Theme.Put(root, body, 0, 2, Padding.Empty);
            status = Theme.Label("", Theme.Small, Theme.Muted);
            Theme.Put(root, status, 0, 3, new Padding(0, 12, 0, 0));
            Theme.Put(root, Theme.AuthorLink(value => status.Text = value), 0, 4, new Padding(0, 9, 0, 0));
            Theme.Stretch(root, 2);

            enable.Checked = store.Enabled;
            enable.CheckedChanged += Toggle;
            add.Click += AddEntry;
            remove.Click += DeleteEntry;
            save.Click += delegate { if (SaveCurrent()) Reload(current.Id); };
            clear.Click += delegate { candidate = Keys.None; recorder.Text = Hotkeys.Display(candidate); };
            recorder.Recorded += chord => { candidate = chord; status.Text = "点击「保存条目」应用新的快捷键。"; };
            list.SelectedIndexChanged += SelectEntry;
            Reload(store.Entries.Select(entry => entry.Id).FirstOrDefault());
            ResumeLayout(true);
        }

        protected override void OnLoad(EventArgs args)
        {
            base.OnLoad(args);
            var workArea = Screen.FromControl(Owner ?? this).WorkingArea;
            var availableSize = new Size(workArea.Width - 16, workArea.Height - 16);
            float scale = CurrentAutoScaleDimensions.Width / 96f;
            MinimumSize = new Size(Math.Min(availableSize.Width, (int)(565 * scale)),
                Math.Min(availableSize.Height, (int)(340 * scale)));
            Size = new Size(Math.Min(availableSize.Width, Width), Math.Min(availableSize.Height, Height));
        }

        private void Reload(string id)
        {
            updating = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var entry in store.Entries) list.Items.Add(entry);
            var selected = store.Entries.FirstOrDefault(entry => entry.Id == id);
            list.SelectedItem = selected;
            list.EndUpdate();
            current = selected;
            title.Text = current == null ? "" : current.Title;
            content.Text = current == null ? "" : current.Text;
            candidate = current == null ? Keys.None : current.Chord;
            recorder.Text = Hotkeys.Display(candidate);
            editor.Enabled = remove.Enabled = current != null;
            updating = false;
            if (current == null) status.Text = "点击「新增条目」，添加第一段快捷文本。";
        }

        private bool HasDraft()
        {
            return current != null && (title.Text.Trim() != current.Title || content.Text != current.Text ||
                candidate != current.Chord);
        }

        private bool ResolveDraft()
        {
            if (!HasDraft()) return true;
            var choice = MessageBox.Show(this, "保存当前条目的修改吗？", "快捷文本",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            return choice == DialogResult.No || (choice == DialogResult.Yes && SaveCurrent());
        }

        private bool SaveCurrent()
        {
            if (current == null) return false;
            if (candidate != Keys.None && candidate != current.Chord &&
                (store.Entries.Any(entry => entry.Id != current.Id && entry.Chord == candidate) || !available(candidate)))
            {
                status.Text = "这组快捷键已被占用。请换一组再保存。";
                return false;
            }
            var replacement = new QuickTextEntry
            {
                Id = current.Id, Title = String.IsNullOrWhiteSpace(title.Text) ? "快捷文本" : title.Text.Trim(),
                Text = content.Text, Hotkey = (int)candidate
            };
            if (!TrySave(store.Enabled, store.Entries.Select(entry => entry.Id == replacement.Id ? replacement : entry)))
                return false;
            current = replacement;
            status.Text = "条目已保存。";
            return true;
        }

        private void SelectEntry(object sender, EventArgs args)
        {
            if (updating) return;
            var next = list.SelectedItem as QuickTextEntry;
            if (next == null || next == current) return;
            if (ResolveDraft()) Reload(next.Id);
            else
            {
                updating = true;
                list.SelectedItem = current;
                updating = false;
            }
        }

        private void AddEntry(object sender, EventArgs args)
        {
            if (!ResolveDraft()) return;
            var entry = new QuickTextEntry
            {
                Id = Guid.NewGuid().ToString("N"), Title = "新快捷文本",
                Text = "", Hotkey = (int)store.NextChord(available)
            };
            if (!TrySave(store.Enabled, store.Entries.Concat(new[] { entry }))) return;
            Reload(entry.Id);
            status.Text = entry.Chord == Keys.None
                ? "自动编号已用完，可以手动录制一组快捷键。"
                : "已分配 " + Hotkeys.Display(entry.Chord) + "。填写内容后保存。";
            content.Focus();
        }

        private void DeleteEntry(object sender, EventArgs args)
        {
            if (current == null) return;
            if ((!String.IsNullOrEmpty(current.Text) || !String.IsNullOrEmpty(content.Text)) &&
                MessageBox.Show(this, "删除选中的快捷文本？", "快捷文本", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            int index = list.SelectedIndex;
            if (!TrySave(store.Enabled, store.Entries.Where(entry => entry.Id != current.Id))) return;
            string next = store.Entries.Count == 0 ? null : store.Entries[Math.Min(index, store.Entries.Count - 1)].Id;
            Reload(next);
            if (next != null) status.Text = "已删除选中的条目。";
        }

        private void Toggle(object sender, EventArgs args)
        {
            if (updating) return;
            if (TrySave(enable.Checked, store.Entries))
                status.Text = enable.Checked ? "关闭此窗口后启用快捷文本。" : "快捷文本已关闭。";
            else
            {
                updating = true;
                enable.Checked = store.Enabled;
                updating = false;
            }
        }

        private bool TrySave(bool enabled, IEnumerable<QuickTextEntry> entries)
        {
            try { store.Save(enabled, entries); return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SerializationException) { }
            status.Text = "无法保存快捷文本，请检查用户设置目录。";
            return false;
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (args.CloseReason == CloseReason.UserClosing && !ResolveDraft()) args.Cancel = true;
            base.OnFormClosing(args);
        }

        private sealed class EntryList : ListBox
        {
            internal EntryList()
            {
                BorderStyle = BorderStyle.None;
                IntegralHeight = false;
                DrawMode = DrawMode.OwnerDrawFixed;
                AccessibleName = "快捷文本列表";
                ItemHeight = Font.Height * 2 + 12;
            }

            protected override void OnFontChanged(EventArgs args)
            {
                base.OnFontChanged(args);
                ItemHeight = Font.Height * 2 + 12;
            }

            protected override void OnDrawItem(DrawItemEventArgs args)
            {
                if (args.Index < 0 || args.Index >= Items.Count) return;
                var entry = (QuickTextEntry)Items[args.Index];
                bool selected = (args.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Theme.Tint : BackColor))
                    args.Graphics.FillRectangle(brush, args.Bounds);
                var line = Rectangle.Inflate(args.Bounds, -7, -4);
                line.Height = Font.Height;
                const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(args.Graphics, entry.ToString(), Font, line, Theme.Ink, flags);
                line.Y += Font.Height + 3;
                TextRenderer.DrawText(args.Graphics, entry.Preview, Font, line, Theme.Muted, flags);
                args.DrawFocusRectangle();
            }
        }
    }
}
