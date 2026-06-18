using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Wpf.Ui.Common;
using Wpf.Ui.Contracts;
using Wpf.Ui.Controls;
using XAU.Services.StatEditor;

namespace XAU.ViewModels.Pages
{
    public partial class StatsViewModel : ObservableObject, INavigationAware
    {
        private readonly ISnackbarService _snackbarService;
        private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
        private bool _isInitialized = false;

        public StatsViewModel(ISnackbarService snackbarService)
        {
            _snackbarService = snackbarService;
        }

        // One editable stat row in the grid. ObservableObject so edits + read-back
        // both flow to the UI.
        public partial class StatRow : ObservableObject
        {
            public string Name { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string StatType { get; set; } = "";
            public string Scid { get; set; } = "";
            // Editable current value (two-way bound).
            [ObservableProperty] private string _value = "0";
            // Per-row save feedback ("", "Saving...", "✓", "✗").
            [ObservableProperty] private string _status = "";
        }

        [ObservableProperty] private string _titleId = "";
        [ObservableProperty] private bool _isBusy = false;
        // Inverse of IsBusy, for disabling the Load button while a load is running.
        [ObservableProperty] private bool _notBusy = true;
        [ObservableProperty] private string _statusText = "Enter a Title ID and press Load.";
        // Filter text — narrows the visible rows by stat name (live).
        [ObservableProperty] private string _filterText = "";
        [ObservableProperty] private ObservableCollection<StatRow> _stats = new ObservableCollection<StatRow>();

        // Full unfiltered set; Stats is the filtered view of this.
        private readonly List<StatRow> _allStats = new List<StatRow>();

        partial void OnIsBusyChanged(bool value) => NotBusy = !value;
        partial void OnFilterTextChanged(string value) => ApplyFilter();

        // Rebuilds Stats from _allStats applying the current filter.
        private void ApplyFilter()
        {
            var f = FilterText?.Trim() ?? "";
            Stats.Clear();
            foreach (var r in _allStats)
            {
                if (f.Length == 0
                    || (r.DisplayName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (r.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    Stats.Add(r);
                }
            }
        }

        public void OnNavigatedTo()
        {
            if (!_isInitialized)
                InitializeViewModel();
            // Convenience: prefill with the title currently open on the Achievements page.
            if (string.IsNullOrWhiteSpace(TitleId) && AchievementsViewModel.TitleID != "0")
                TitleId = AchievementsViewModel.TitleID;
        }

        public void OnNavigatedFrom() { }

        private void InitializeViewModel()
        {
            _isInitialized = true;
        }

        [RelayCommand]
        public async Task LoadStats()
        {
            if (!HomeViewModel.InitComplete)
            {
                _snackbarService.Show("Stat Editor", "Sign in before using this.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (string.IsNullOrWhiteSpace(TitleId))
            {
                _snackbarService.Show("Stat Editor", "Enter a Title ID first.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }

            IsBusy = true;
            StatusText = "Loading stats...";
            _allStats.Clear();
            Stats.Clear();
            try
            {
                var service = new StatEditorService(HomeViewModel.XAUTH, HomeViewModel.XUIDOnly);
                var stats = await service.ReadStatsAsync(TitleId.Trim(), CancellationToken.None);
                foreach (var s in stats)
                {
                    _allStats.Add(new StatRow
                    {
                        Name = s.Name,
                        DisplayName = s.DisplayName,
                        Value = s.Value,
                        StatType = s.StatType,
                        Scid = s.Scid
                    });
                }
                ApplyFilter();
                StatusText = _allStats.Count > 0
                    ? $"Loaded {_allStats.Count} stat(s). Edit a value and press Save."
                    : "No editable stats found for this title.";
            }
            catch (Exception ex)
            {
                StatusText = "Failed to load stats.";
                _snackbarService.Show("Stat Editor", ex.Message,
                    ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task SaveStat(StatRow? row)
        {
            if (row == null)
                return;
            if (!HomeViewModel.InitComplete)
            {
                _snackbarService.Show("Stat Editor", "Sign in before using this.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (string.IsNullOrWhiteSpace(row.Scid))
            {
                row.Status = "✗";
                _snackbarService.Show("Stat Editor", "This stat has no SCID and cannot be written.",
                    ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }

            row.Status = "Saving...";
            try
            {
                var service = new StatEditorService(HomeViewModel.XAUTH, HomeViewModel.XUIDOnly);
                var (ok, message) = await service.WriteStatAsync(
                    TitleId.Trim(), row.Scid, row.Name, row.Value.Trim(), CancellationToken.None);
                row.Status = ok ? "✓" : "⏳";
                _snackbarService.Show("Stat Editor", message,
                    ok ? ControlAppearance.Success : ControlAppearance.Caution,
                    new SymbolIcon(ok ? SymbolRegular.Checkmark24 : SymbolRegular.Warning24), _snackbarDuration);
            }
            catch (Exception ex)
            {
                row.Status = "✗";
                _snackbarService.Show("Stat Editor", ex.Message,
                    ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }
        }
    }
}
