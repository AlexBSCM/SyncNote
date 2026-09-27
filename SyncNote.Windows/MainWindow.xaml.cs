using System.Windows;
using System.Windows.Controls;
using SyncNote.Core;

namespace SyncNote.Windows;

public partial class MainWindow : Window
{
    private readonly SqliteNoteStore _store = new(SqliteNoteStore.DefaultPath);

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _store.Dispose();
        RefreshList();
    }

    private void RefreshList()
    {
        var selectedId = (NotesList.SelectedItem as Note)?.Id;
        var notes = _store.Search(SearchBox.Text);
        NotesList.ItemsSource = notes;
        var reselected = notes.FirstOrDefault(n => n.Id == selectedId);
        if (reselected is not null)
            NotesList.SelectedItem = reselected;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshList();

    private void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;
            TitleBox.Text = note.Title;
            BodyBox.Text = note.Body;
            RefreshChecklist();
        }
        else
        {
            EmptyHint.Visibility = Visibility.Visible;
            EditorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var note = _store.Add("Новая заметка", string.Empty);
        RefreshList();
        NotesList.SelectedItem = _store.Search(SearchBox.Text).FirstOrDefault(n => n.Id == note.Id);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note)
        {
            _store.Delete(note.Id);
            RefreshList();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note)
        {
            note.Title = TitleBox.Text;
            note.Body = BodyBox.Text;
            _store.Update(note);
            RefreshList();
        }
    }

    private void RefreshChecklist()
    {
        if (NotesList.SelectedItem is Note note)
            ChecklistBox.ItemsSource = _store.GetChecklist(note.Id);
        else
            ChecklistBox.ItemsSource = null;
    }

    private void AddChecklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is Note note && !string.IsNullOrWhiteSpace(NewItemBox.Text))
        {
            _store.AddChecklistItem(note.Id, NewItemBox.Text.Trim());
            NewItemBox.Clear();
            RefreshChecklist();
            RefreshList();
        }
    }

    private void ChecklistItem_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is Guid itemId
            && NotesList.SelectedItem is Note note)
        {
            var item = _store.GetChecklist(note.Id).FirstOrDefault(i => i.Id == itemId);
            if (item is not null)
            {
                item.IsChecked = box.IsChecked == true;
                _store.UpdateChecklistItem(item);
                RefreshList();
            }
        }
    }

    private void DeleteChecklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Guid itemId)
        {
            _store.DeleteChecklistItem(itemId);
            RefreshChecklist();
            RefreshList();
        }
    }
}
