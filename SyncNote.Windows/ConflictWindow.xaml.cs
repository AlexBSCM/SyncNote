using System.Windows;
using System.Windows.Controls;
using SyncNote.Core;

namespace SyncNote.Windows;

// Разрешение конфликтов: пары находятся по суффиксу копии
// " (копия конфликта)" (см. SyncEngine). Все три действия сохраняют данные:
// удаление копии после явного выбора, переименование при «оставить обе».
public partial class ConflictWindow : Window
{
    private readonly INoteStore _store;
    private List<(Note Original, Note Copy)> _pairs = new();

    public ConflictWindow(INoteStore store)
    {
        InitializeComponent();
        _store = store;
        RefreshPairs();
    }

    private void RefreshPairs()
    {
        _pairs = Conflicts.FindPairs(_store).ToList();
        PairsList.ItemsSource = _pairs.Select(p => p.Original.Title).ToList();
        if (_pairs.Count > 0)
            PairsList.SelectedIndex = 0;
        else
            Close();
    }

    private (Note Original, Note Copy)? Current()
    {
        int i = PairsList.SelectedIndex;
        return i >= 0 && i < _pairs.Count ? _pairs[i] : null;
    }

    private void PairsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cur = Current();
        OriginalBox.Text = cur is null ? "" :
            $"{cur.Value.Original.Title}\n---\n{cur.Value.Original.Body}";
        CopyBox.Text = cur is null ? "" :
            $"{cur.Value.Copy.Title}\n---\n{cur.Value.Copy.Body}";
    }

    private void KeepOriginal_Click(object sender, RoutedEventArgs e)
    {
        var cur = Current();
        if (cur is null) return;
        _store.Delete(cur.Value.Copy.Id); // явной выбор: копия больше не нужна
        RefreshPairs();
    }

    private void TakeCopy_Click(object sender, RoutedEventArgs e)
    {
        var cur = Current();
        if (cur is null) return;
        _store.Delete(cur.Value.Original.Id);
        cur.Value.Copy.Title = cur.Value.Original.Title;
        _store.Update(cur.Value.Copy);
        RefreshPairs();
    }

    private void KeepBoth_Click(object sender, RoutedEventArgs e)
    {
        var cur = Current();
        if (cur is null) return;
        cur.Value.Copy.Title = cur.Value.Copy.Title[..^Conflicts.CopySuffix.Length] + " (вторая версия)";
        _store.Update(cur.Value.Copy);
        RefreshPairs();
    }
}
