using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    public class Note
    {
        public string File;
        public string Text = "";
        public DateTime Modified;
        public string Title => FirstLine(Text) is { Length: > 0 } t ? t : "New note";
        public string Body
        {
            get
            {
                var lines = Text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1);
                return string.Join(" ", lines);
            }
        }
        static string FirstLine(string s) => s.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
    }

    /// Quick notes. Each note is a .txt file in the Notes folder (Documents\GlassBar\Notes by default).
    public partial class BarWindow
    {
        readonly List<Note> notes = new();
        NotesFlyout notesFly;

        void InitNotes()
        {
            LoadNotes();
            // notes edited in another program show up too
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            t.Tick += (s, e) => { if (notesFly?.IsVisible != true) LoadNotes(); };
            t.Start();
        }

        public void LoadNotes()
        {
            notes.Clear();
            try
            {
                foreach (var f in new DirectoryInfo(NotesDir).GetFiles("*.txt"))
                {
                    try { notes.Add(new Note { File = f.FullName, Text = File.ReadAllText(f.FullName), Modified = f.LastWriteTime }); } catch { }
                }
            }
            catch { }
            notes.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            UpdateNotesCard();
            if (notesFly?.IsVisible == true) notesFly.Refresh();
        }

        public IReadOnlyList<Note> Notes => notes;

        void UpdateNotesCard()
        {
            var n = notes.FirstOrDefault();
            NoteTitle.Text = n?.Title ?? "Notes";
            NotePreview.Text = n == null ? "Write something down" : n.Body.Length > 0 ? n.Body : AgoLong(n.Modified);
            NotesCard.ToolTip = notes.Count == 0 ? "Notes" : notes.Count == 1 ? "1 note" : $"{notes.Count} notes";
        }

        public Note NewNote(string text = "")
        {
            var n = new Note { File = Path.Combine(NotesDir, $"Note {DateTime.Now:yyyy-MM-dd HH.mm.ss}.txt"), Text = text, Modified = DateTime.Now };
            notes.Insert(0, n);
            if (text.Length > 0) SaveNote(n);
            UpdateNotesCard();
            return n;
        }

        public void SaveNote(Note n)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(n.Text))
                {
                    // an empty note isn't kept
                    if (File.Exists(n.File)) File.Delete(n.File);
                    notes.Remove(n);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(n.File));
                    File.WriteAllText(n.File, n.Text, new UTF8Encoding(false));
                    n.Modified = DateTime.Now;
                    notes.Remove(n);
                    notes.Insert(0, n);
                }
            }
            catch (Exception ex) { ShowToast("Couldn't save the note: " + ex.Message); }
            UpdateNotesCard();
        }

        public void DeleteNote(Note n)
        {
            try
            {
                if (File.Exists(n.File))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(n.File, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch { }
            notes.Remove(n);
            UpdateNotesCard();
            ShowToast("Note moved to the Recycle Bin");
        }

        void Notes_Click(object s, RoutedEventArgs e)
        {
            notesFly ??= Register(new NotesFlyout(this));
            notesFly.Refresh();
            notesFly.Toggle(NotesCard);
        }

        void NewNote_Click(object s, RoutedEventArgs e)
        {
            notesFly ??= Register(new NotesFlyout(this));
            if (!notesFly.IsVisible) notesFly.ShowAbove(NotesCard);
            notesFly.Edit(NewNote());
        }
    }

    public class NotesFlyout : Flyout
    {
        readonly BarWindow bar;
        readonly StackPanel list = new();
        readonly Border editor;
        readonly TextBox editBox;
        readonly TextBox newBox;
        readonly TextBlock count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock empty = new() { Text = "No notes yet. Type one below.", Margin = new Thickness(16, 14, 16, 14), FontSize = 13 };
        Note editing;
        readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromSeconds(1.2) };

        public NotesFlyout(BarWindow bar) : base("Notes", 400, 580)
        {
            this.bar = bar;
            count.Foreground = Res("TextSecondary");
            empty.Foreground = Res("TextSecondary");
            HeaderExtra.Children.Add(count);
            AddHeaderButton("", "Open the notes folder", () => bar.Launch(BarWindow.NotesDir));

            // new note row at the bottom
            newBox = new TextBox { Style = (Style)Application.Current.Resources["PlainBox"], FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
            var hint = new TextBlock { Text = "New note", FontSize = 13, Foreground = Res("TextTertiary"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            newBox.TextChanged += (s, e) => hint.Visibility = newBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            newBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddFromBox(); e.Handled = true; } };
            var add = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = "", Width = 32, Height = 32, FontSize = 12, ToolTip = "Add note" };
            add.Click += (s, e) => AddFromBox();
            var newGrid = new Grid();
            newGrid.ColumnDefinitions.Add(new ColumnDefinition());
            newGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            newGrid.Children.Add(newBox);
            newGrid.Children.Add(hint);
            Grid.SetColumn(add, 1);
            newGrid.Children.Add(add);
            var newRow = new Border { CornerRadius = new CornerRadius(14), Background = Res("ControlBg"), BorderBrush = Res("ControlBorder"), BorderThickness = new Thickness(1), Height = 44, Margin = new Thickness(14, 6, 14, 14), Padding = new Thickness(0, 0, 6, 0), Child = newGrid };
            DockPanel.SetDock(newRow, Dock.Bottom);
            Body.Children.Add(newRow);

            // editor (shown while a note is open)
            editBox = new TextBox
            {
                AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = Res("TextPrimary"), CaretBrush = Brushes.White, MinHeight = 110, MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, SpellCheck = { IsEnabled = true }
            };
            editBox.TextChanged += (s, e) => { if (editing != null) { editing.Text = editBox.Text; autosave.Stop(); autosave.Start(); } };
            autosave.Tick += (s, e) => { autosave.Stop(); if (editing != null && !string.IsNullOrWhiteSpace(editing.Text)) bar.SaveNote(editing); };
            var done = Pill("Done", Done);
            done.HorizontalAlignment = HorizontalAlignment.Right;
            done.Margin = new Thickness(0);
            var del = SmallIcon("", "Delete note", () => { var n = editing; CloseEditor(save: false); if (n != null) bar.DeleteNote(n); Refresh(); });
            var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(done, Dock.Right);
            bottom.Children.Add(done);
            bottom.Children.Add(new Border { Child = del, HorizontalAlignment = HorizontalAlignment.Left });
            var edStack = new StackPanel();
            edStack.Children.Add(editBox);
            edStack.Children.Add(bottom);
            editor = new Border
            {
                CornerRadius = new CornerRadius(14), Margin = new Thickness(14, 0, 14, 8), Padding = new Thickness(14, 12, 12, 12),
                Background = Flyout.Res("AccentSoft"), BorderBrush = Flyout.Res("AccentLine"),
                BorderThickness = new Thickness(1), Child = edStack, Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(editor, Dock.Top);
            Body.Children.Add(editor);

            var stack = new StackPanel();
            stack.Children.Add(empty);
            stack.Children.Add(list);
            var listBox = new Border
            {
                CornerRadius = new CornerRadius(14), Background = Res("CardBg"), BorderBrush = Res("CardBorder"), BorderThickness = new Thickness(1),
                Margin = new Thickness(14, 0, 14, 0),
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack, Padding = new Thickness(0, 4, 0, 4) }
            };
            Body.Children.Add(listBox);
            IsVisibleChanged += (s, e) => { if (!IsVisible) CloseEditor(save: true); };
        }

        void AddFromBox()
        {
            var text = newBox.Text.Trim();
            if (text.Length == 0) { Edit(bar.NewNote()); return; }
            newBox.Text = "";
            bar.NewNote(text);
            Refresh();
        }

        public void Edit(Note n)
        {
            if (editing != null && editing != n) CloseEditor(save: true);
            editing = n;
            editBox.Text = n.Text;
            editor.Visibility = Visibility.Visible;
            Refresh();
            Dispatcher.BeginInvoke(() => { Activate(); editBox.Focus(); editBox.CaretIndex = editBox.Text.Length; }, DispatcherPriority.Input);
        }

        void Done() { CloseEditor(save: true); Refresh(); }

        void CloseEditor(bool save)
        {
            autosave.Stop();
            var n = editing;
            editing = null;
            editor.Visibility = Visibility.Collapsed;
            if (n != null && save) { n.Text = editBox.Text; bar.SaveNote(n); }
        }

        public void Refresh()
        {
            list.Children.Clear();
            var all = bar.Notes.Where(n => n != editing).ToList();
            int total = bar.Notes.Count(n => !string.IsNullOrWhiteSpace(n.Text));
            count.Text = total == 1 ? "1 note" : $"{total} notes";
            empty.Visibility = all.Count == 0 && editing == null ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < all.Count; i++)
            {
                var n = all[i];
                if (string.IsNullOrWhiteSpace(n.Text)) continue;
                var title = new TextBlock { Text = n.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var sub = new TextBlock { FontSize = 11.5, Foreground = Res("TextTertiary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
                                          Text = BarWindow.AgoLong(n.Modified) + (n.Body.Length > 0 ? "  ·  " + n.Body : "") };
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(title);
                txt.Children.Add(sub);
                var del = SmallIcon("", "Delete note", () => { bar.DeleteNote(n); Refresh(); });
                del.Opacity = 0;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(txt);
                Grid.SetColumn(del, 1);
                g.Children.Add(del);
                var row = new Border { Padding = new Thickness(14, 10, 8, 10), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g, CornerRadius = new CornerRadius(10), Margin = new Thickness(4, 0, 4, 0) };
                row.MouseEnter += (s, e) => { row.Background = Res("CardHover"); del.Opacity = 0.8; };
                row.MouseLeave += (s, e) => { row.Background = Brushes.Transparent; del.Opacity = 0; };
                row.MouseLeftButtonUp += (s, e) => { if (BarWindow.FindAncestor<Button>(e.OriginalSource) == null) Edit(n); };
                if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = Res("CardBorder"), Margin = new Thickness(16, 0, 16, 0) });
                list.Children.Add(row);
            }
        }
    }
}
