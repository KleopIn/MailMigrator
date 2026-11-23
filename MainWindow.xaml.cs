using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MailMigrator.Models;
using MailMigrator.Services;

namespace MailMigrator;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadProfileIntoForm();
    }

    private Profile BuildProfileFromForm()
    {
        return new Profile
        {
            SourceHost = SourceHostBox.Text.Trim(),
            SourcePort = ParseInt(SourcePortBox.Text, 993),
            SourceUser = SourceUserBox.Text.Trim(),
            SourcePassword = SourcePassBox.Password,
            SourceSsl = SourceSslCheck.IsChecked == true,

            DestHost = DestHostBox.Text.Trim(),
            DestPort = ParseInt(DestPortBox.Text, 993),
            DestUser = DestUserBox.Text.Trim(),
            DestPassword = DestPassBox.Password,
            DestSsl = DestSslCheck.IsChecked == true,

            MigrateInbox = InboxCheck.IsChecked == true,
            MigrateSent = SentCheck.IsChecked == true,
            MaxMessageBytes = (long)ParseInt(MaxBytesBox.Text, 18) * 1024 * 1024,
            RateDelayMs = ParseInt(RateDelayBox.Text, 120),
            DateBoundaryForInbox = DateBoundaryCheck.IsChecked == true,
            DateFilterEnabled = DateFromPicker.SelectedDate != null || DateToPicker.SelectedDate != null,
            DateFrom = DateFromPicker.SelectedDate,
            DateTo = DateToPicker.SelectedDate,
        };
    }

    private void FillFormFromProfile(Profile p)
    {
        SourceHostBox.Text = p.SourceHost;
        SourcePortBox.Text = p.SourcePort.ToString();
        SourceUserBox.Text = p.SourceUser;
        SourcePassBox.Password = p.SourcePassword;
        SourceSslCheck.IsChecked = p.SourceSsl;

        DestHostBox.Text = p.DestHost;
        DestPortBox.Text = p.DestPort.ToString();
        DestUserBox.Text = p.DestUser;
        DestPassBox.Password = p.DestPassword;
        DestSslCheck.IsChecked = p.DestSsl;

        InboxCheck.IsChecked = p.MigrateInbox;
        SentCheck.IsChecked = p.MigrateSent;
        MaxBytesBox.Text = (p.MaxMessageBytes / 1024 / 1024).ToString();
        RateDelayBox.Text = p.RateDelayMs.ToString();
        DateBoundaryCheck.IsChecked = p.DateBoundaryForInbox;
        DateFromPicker.SelectedDate = p.DateFrom;
        DateToPicker.SelectedDate = p.DateTo;
    }

    private void LoadProfileIntoForm()
    {
        var p = ProfileStore.Load();
        if (p is not null) FillFormFromProfile(p);
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, out var v) && v > 0 ? v : fallback;

    private void SetRunning(bool running)
    {
        StartBtn.IsEnabled = !running;
        SaveBtn.IsEnabled = !running;
        LoadBtn.IsEnabled = !running;
        CancelBtn.IsEnabled = running;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var p = BuildProfileFromForm();
        if (string.IsNullOrEmpty(p.SourceHost) || string.IsNullOrEmpty(p.SourceUser) ||
            string.IsNullOrEmpty(p.DestHost) || string.IsNullOrEmpty(p.DestUser))
        {
            MessageBox.Show("Заполните хост и логин для источника и назначения.", "Mail Migrator",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogBox.Clear();
        CountersText.Text = "Перенесено: 0   Ошибок: 0   Уже есть: 0   Крупные (>18 МБ): 0   Вне периода: 0";
        ProgressBar1.Value = 0;
        StatusText.Text = "Подключение...";
        SetRunning(true);

        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressUpdate>(OnProgress);

        try
        {
            var stats = await Task.Run(() =>
                new ImapMigrationService(p, progress, _cts.Token).RunAsync());

            StatusText.Text = "Готово.";
            MessageBox.Show(BuildSummary(stats), "Mail Migrator", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Отменено пользователем.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка.";
            MessageBox.Show(ex.Message, "Mail Migrator", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunning(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private static string BuildSummary(MigrationStats s)
    {
        string header;
        if (s.Copied == 0 && s.Errors == 0 && s.LargeSkipped == 0)
            header = "Готово — все письма уже были на Exchange, новых нет.";
        else if (s.Errors > 0)
            header = "Перенос завершён с ошибками.";
        else
            header = "Перенос завершён.";

        var lines = new List<string>
        {
            header,
            "",
            $"Перенесено сейчас: {s.Copied}",
            $"Ошибок: {s.Errors}"
        };

        int already = s.DupSkipped + s.BoundarySkipped;
        if (already > 0)
            lines.Add($"Уже было на Exchange (пропущено): {already}");
        if (s.LargeSkipped > 0)
            lines.Add($"Крупные >18 МБ (IMAP не переносит, нужен PST-импорт): {s.LargeSkipped}");
        if (s.DateSkipped > 0)
            lines.Add($"Вне выбранного периода: {s.DateSkipped}");

        return string.Join("\n", lines);
    }

    private void OnProgress(ProgressUpdate u)
    {
        if (u.LogLine is { } line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        CountersText.Text =
            $"Перенесено: {u.Copied}   Ошибок: {u.Errors}   Уже есть: {u.DupSkipped + u.BoundarySkipped}   Крупные (>18 МБ): {u.LargeSkipped}   Вне периода: {u.DateSkipped}";

        if (u.Folder is not null && u.FolderTotal > 0)
        {
            StatusText.Text = $"Перенос папки «{u.Folder}»: {u.FolderDone} / {u.FolderTotal}";
            ProgressBar1.Maximum = u.FolderTotal;
            ProgressBar1.Value = u.FolderDone;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Отмена...";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ProfileStore.Save(BuildProfileFromForm());
            MessageBox.Show("Профиль сохранён (пароли зашифрованы DPAPI).", "Mail Migrator",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось сохранить профиль: " + ex.Message, "Mail Migrator",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var p = ProfileStore.Load();
        if (p is null)
        {
            MessageBox.Show("Профиль не найден.", "Mail Migrator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        FillFormFromProfile(p);
    }
}
