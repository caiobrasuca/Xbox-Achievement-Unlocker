using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Windows.Security.Credentials;
using Wpf.Ui.Common;
using Wpf.Ui.Contracts;
using Wpf.Ui.Controls;
using XAU.Services;
using XAU.ViewModels.Pages;

namespace XAU.Views.Pages
{
    public partial class SettingsPage : INavigableView<SettingsViewModel>
    {
        public SettingsViewModel ViewModel { get; }
        private readonly ISnackbarService _snackbarService;
        private readonly IContentDialogService _contentDialogService;

        public SettingsPage(SettingsViewModel viewModel, ISnackbarService snackbarService, IContentDialogService contentDialogService)
        {
            ViewModel = viewModel;
            _snackbarService = snackbarService;
            _contentDialogService = contentDialogService;
            DataContext = this;

            ViewModel.OnNavigatedToEvent += (_, _) =>
            {
                XauthTextBox.Text = HomeViewModel.XAUTH;
                EventsTokenBox.Text = AchievementsViewModel.EventsToken;
            };

            InitializeComponent();
        }

        private void XauthTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(XauthTextBox.Text) || string.IsNullOrEmpty(XauthTextBox.Text))
            {
                _snackbarService.Show(
                    "Error",
                    "XAuth Token cannot be empty/whitespace",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24)
                );
                return;
            }

            HomeViewModel.XAUTH = XauthTextBox.Text;
            SettingsViewModel.ManualXauth = true;
            HomeViewModel.XAUTHTested = false;
        }

        private void EventsToken_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(EventsTokenBox.Text) || string.IsNullOrEmpty(EventsTokenBox.Text))
            {
                _snackbarService.Show(
                    "Error",
                    "Events Token cannot be empty/whitespace",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24)
                );
                return;
            }

            AchievementsViewModel.EventsToken = EventsTokenBox.Text;
        }

        private void XAuthBox_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            XauthTextBox.MaxWidth = e.NewSize.Width / 3;
        }

        private void EventsBoxGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            EventsTokenBox.MaxWidth = e.NewSize.Width / 3;
        }

        // Mints the events token via WAM (Windows Web Account Manager) from a Microsoft
        // account already signed into Windows — without touching the XAUTH the user gets
        // from the Xbox app memory scan.
        private async void GetEventTokenWam_OnClick(object sender, System.Windows.RoutedEventArgs e)
        {
            GetEventTokenWamButton.IsEnabled = false;
            try
            {
                var accounts = await WamEventTokenService.GetAccountsAsync();
                if (accounts.Count == 0)
                {
                    _snackbarService.Show("Event Token (WAM)",
                        "No Microsoft account found in Windows. Sign in to a Microsoft account (Windows Settings > Accounts, or the Xbox/Store app) and try again.",
                        ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24));
                    return;
                }

                WebAccount? account = null;
                // Prefer the last-used account if it is still available.
                var lastId = ReadLastWamAccountId();
                if (!string.IsNullOrEmpty(lastId))
                    account = accounts.FirstOrDefault(a => a.Id == lastId);
                if (account == null)
                {
                    if (accounts.Count == 1)
                    {
                        account = accounts[0];
                    }
                    else
                    {
                        account = await PickAccountAsync(accounts);
                        if (account == null)
                            return;
                    }
                }

                _snackbarService.Show("Event Token (WAM)", "Requesting token...",
                    ControlAppearance.Info, new SymbolIcon(SymbolRegular.Info24));

                var token = await WamEventTokenService.GetEventsTokenAsync(account);
                if (string.IsNullOrEmpty(token))
                {
                    _snackbarService.Show("Event Token (WAM)",
                        "Could not get an events token for that account. Make sure it is signed into Windows and has Xbox access.",
                        ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24));
                    return;
                }

                EventsTokenBox.Text = token;               // fires EventsToken_OnTextChanged
                AchievementsViewModel.EventsToken = token;  // belt-and-suspenders
                WriteLastWamAccountId(account.Id);          // remember for next time
                _snackbarService.Show("Event Token (WAM)", "Events token set successfully.",
                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24));
            }
            catch (System.Exception ex)
            {
                _snackbarService.Show("Event Token (WAM)", ex.Message,
                    ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24));
            }
            finally
            {
                GetEventTokenWamButton.IsEnabled = true;
            }
        }

        private async Task<WebAccount?> PickAccountAsync(List<WebAccount> accounts)
        {
            var listbox = new ListBox { SelectedIndex = 0, MinWidth = 280 };
            foreach (var a in accounts)
                listbox.Items.Add(string.IsNullOrWhiteSpace(a.UserName) ? a.Id : a.UserName);

            var result = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
            {
                Title = "Choose a Microsoft account",
                Content = listbox,
                PrimaryButtonText = "Use account",
                CloseButtonText = "Cancel"
            });
            if (result != ContentDialogResult.Primary)
                return null;
            var idx = listbox.SelectedIndex;
            return idx >= 0 && idx < accounts.Count ? accounts[idx] : null;
        }

        // Last-used WAM account id is remembered in a small file so the picker can be
        // skipped next time. Kept separate from settings.json (which SaveSettings
        // rewrites from the VM and would otherwise drop this).
        private static string WamAccountFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "wam_account.txt");

        private static string? ReadLastWamAccountId()
        {
            try
            {
                return File.Exists(WamAccountFilePath) ? File.ReadAllText(WamAccountFilePath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteLastWamAccountId(string id)
        {
            try
            {
                var dir = Path.GetDirectoryName(WamAccountFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(WamAccountFilePath, id);
            }
            catch
            {
                // Best-effort; not remembering the account is harmless.
            }
        }
    }
}
