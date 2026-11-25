using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelaType.Engine;
using HelaType.Services;

namespace HelaType;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush ActiveToggleBg = new(Color.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush ActiveToggleBorder = new(Color.FromRgb(5, 150, 105));
    private static readonly SolidColorBrush InactiveToggleBg = new(Color.FromRgb(51, 65, 85));
    private static readonly SolidColorBrush InactiveToggleBorder = new(Color.FromRgb(71, 85, 105));

    private readonly App _app;
    private readonly KeyboardHookService _hookService;
    private readonly IReadOnlyList<PhoneticRuleEntry> _allCheatSheetEntries;

    private bool _suppressStartupEvents;

    public MainWindow(App app, KeyboardHookService hookService)
    {
        _app = app;
        _hookService = hookService;
        _allCheatSheetEntries = PhoneticEngine.GetCheatSheetEntries();

        InitializeComponent();

        Icon = TrayIconService.CreateAppWindowIcon(singlishActive: true);

        _suppressStartupEvents = true;
        StartupCheckBox.IsChecked = _app.IsStartWithWindowsEnabled();
        _suppressStartupEvents = false;

        ApplyModeVisuals(_hookService.IsSinglishEnabled);
        ApplyCheatSheetFilter("All");
    }

    public void RefreshStartupCheckbox(bool enabled)
    {
        _suppressStartupEvents = true;
        StartupCheckBox.IsChecked = enabled;
        _suppressStartupEvents = false;
    }

    public void UpdateLiveState(
        bool isSinglishEnabled,
        string romanBuffer,
        string sinhalaText,
        IReadOnlyList<PhoneticCandidate> candidates)
    {
        ApplyModeVisuals(isSinglishEnabled);

        if (!isSinglishEnabled || string.IsNullOrEmpty(romanBuffer))
        {
            LiveRomanText.Text = isSinglishEnabled ? "(idle)" : "(English pass-through)";
            LiveSinhalaText.Text = "—";
            LiveCandidatesText.Text = isSinglishEnabled
                ? "Type a Singlish word to preview live F1 / F2 / F3 phonetic alternatives."
                : "Press Ctrl+Space or Alt+Z to re-enable Singlish phonetic typing.";
            return;
        }

        LiveRomanText.Text = romanBuffer;
        LiveSinhalaText.Text = sinhalaText;

        if (candidates.Count > 0)
        {
            var sb = new StringBuilder("Variants: ");
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0) sb.Append("   |   ");
                sb.Append($"F{i + 1}: {candidates[i].SinhalaText}");
            }
            LiveCandidatesText.Text = sb.ToString();
        }
    }

    private void ApplyModeVisuals(bool isSinglishEnabled)
    {
        if (isSinglishEnabled)
        {
            ModeToggleButton.Content = "සිං  Singlish ON (Ctrl+Space)";
            ModeToggleButton.Background = ActiveToggleBg;
            ModeToggleButton.BorderBrush = ActiveToggleBorder;
            ModeToggleButton.Foreground = Brushes.White;
            LiveStatusDot.Background = ActiveToggleBg;
        }
        else
        {
            ModeToggleButton.Content = "EN  English Mode (Ctrl+Space)";
            ModeToggleButton.Background = InactiveToggleBg;
            ModeToggleButton.BorderBrush = InactiveToggleBorder;
            ModeToggleButton.Foreground = Brushes.White;
            LiveStatusDot.Background = InactiveToggleBg;
        }
    }

    private void ApplyCheatSheetFilter(string category)
    {
        CheatSheetItemsControl.ItemsSource = category == "All"
            ? _allCheatSheetEntries
            : _allCheatSheetEntries.Where(entry => entry.Category == category).ToList();
    }

    private void ModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _hookService.ToggleMode();
        SandboxTextBox.Focus();
    }

    private void PinHudCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _app.SetHudPinned(PinHudCheckBox.IsChecked == true);
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupEvents)
        {
            return;
        }

        bool desired = StartupCheckBox.IsChecked == true;
        if (!_app.SetStartWithWindows(desired))
        {
            _suppressStartupEvents = true;
            StartupCheckBox.IsChecked = !desired;
            _suppressStartupEvents = false;
            MessageBox.Show(
                this,
                "Could not update the Windows Registry Run key.",
                "HelaType Windows",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        _app.MinimizeMainWindowToTray();
    }

    private void CopySandboxButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(SandboxTextBox.Text))
        {
            Clipboard.SetText(SandboxTextBox.Text);
        }
    }

    private void ClearSandboxButton_Click(object sender, RoutedEventArgs e)
    {
        _hookService.CommitAndClearBuffer();
        SandboxTextBox.Clear();
        SandboxTextBox.Focus();
    }

    private void SandboxTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SandboxWatermark.Visibility = string.IsNullOrEmpty(SandboxTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string category })
        {
            ApplyCheatSheetFilter(category);
        }
    }

    private void CheatSheetCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PhoneticRuleEntry entry })
        {
            _hookService.CommitAndClearBuffer();

            int caretIndex = SandboxTextBox.CaretIndex;
            string insertion = entry.ExampleOutput.Split(' ')[0] + " ";
            SandboxTextBox.Text = SandboxTextBox.Text.Insert(caretIndex, insertion);
            SandboxTextBox.CaretIndex = caretIndex + insertion.Length;
            SandboxTextBox.Focus();
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _app.ExitApplication();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            _app.MinimizeMainWindowToTray();
            return;
        }

        base.OnClosing(e);
    }
}
