using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Controls;
using Wpf.Ui.Common;
using Wpf.Ui.Contracts;
using Wpf.Ui.Services;
using System.Threading;
using XAU.AutoUnlock;
using XAU.Services.AutoUnlock;
// The project imports System.Windows.Forms globally, so "Application" is
// ambiguous. We pin it to the WPF Application (same convention as the rest of the UI).
using Application = System.Windows.Application;

namespace XAU.ViewModels.Pages
{
    public partial class AchievementsViewModel : ObservableObject, INavigationAware
    {
        [ObservableProperty] private bool _isInitialized = false;
        [ObservableProperty] private string _titleIDOverride = "0";
        [ObservableProperty] private bool _unlockable = false;
        [ObservableProperty] private bool _titleIDEnabled = false;
        [ObservableProperty] private ObservableCollection<OneCoreAchievementResponse> _achievements = new ObservableCollection<OneCoreAchievementResponse>();
        [ObservableProperty] private ObservableCollection<DGAchievement> _dGAchievements = new ObservableCollection<DGAchievement>();
        [ObservableProperty] public string _gameInfo = "";
        [ObservableProperty] private string _gameName = "";
        [ObservableProperty] private bool _isUnlockAllEnabled = false;
        // Enables/disables the "Auto Unlocker" button depending on whether a valid game is loaded.
        [ObservableProperty] private bool _isAutoUnlockerEnabled = false;
        [ObservableProperty] private string _searchText = "";
        public static string TitleID = "0";
        private bool IsTitleIDValid = false;
        public static bool NewGame = false;
        public static bool IsSelectedGame360;
        private AchievementsResponse AchievementResponse = new AchievementsResponse();
        private Xbox360AchievementResponse Xbox360AchievementResponse = new Xbox360AchievementResponse();
        private Dictionary<int, DGAchievement> _unlockedAchievements = new Dictionary<int, DGAchievement>();

        private GameTitle GameInfoResponse = new GameTitle();
        // TODO: this needs to be updated if language changes
        private Lazy<XboxRestAPI> _xboxRestAPI = new Lazy<XboxRestAPI>(() => new XboxRestAPI(HomeViewModel.XAUTH));

        public static bool SpoofingUpdate = false;
        private bool IsFiltered = false;
        private bool IsEventBased = false;
        private dynamic EventsData = (dynamic)(new JObject());
        public static string EventsToken;

        public AchievementsViewModel(ISnackbarService snackbarService, IContentDialogService contentDialogService)
        {
            _snackbarService = snackbarService;
            _contentDialogService = contentDialogService;
        }

        private readonly IContentDialogService _contentDialogService;
        private readonly ISnackbarService _snackbarService;
        private TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
        // Games we already offered to resume the unlock for in this session.
        private readonly HashSet<string> _resumePrompted = new HashSet<string>();

        public class DGAchievement
        {
            public int Index { get; set; }
            public int ID { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public bool IsSecret { get; set; }
            public DateTime DateUnlocked { get; set; }
            public int Gamerscore { get; set; }
            public float RarityPercentage { get; set; }
            public string? RarityCategory { get; set; }
            public string? ProgressState { get; set; }
            public bool IsUnlockable { get; set; }
            // "Easy Unlock"/"Hard Unlock" label (empty for regular games).
            public string Category { get; set; } = "";
            // Auto Unlocker verification mark: "" / "⏳" (pending) / "✓" (confirmed).
            public string VerifyState { get; set; } = "";
        }
        public async void OnNavigatedTo()
        {
            if (HomeViewModel.Settings.AutoSpooferEnabled)
            {

                if (!GameInfoResponse.Titles.Any() && !String.IsNullOrWhiteSpace(GameInfoResponse.Xuid))
                {
                    _snackbarService.Show("Error: Game Info Response Contained No Titles", $"There were no titles returned from the API", ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                }
                else
                {
                    if (HomeViewModel.SpoofingStatus == 1 && !!string.IsNullOrWhiteSpace(GameInfo))
                    {
                        if (HomeViewModel.SpoofedTitleID == TitleIDOverride)
                        {
                            GameInfo = "Manually Spoofing";
                            GameName = GameInfoResponse.Titles[0].Name;
                        }
                        else
                        {
                            GameInfo = "Spoofing Another Game";
                            GameName = GameInfoResponse.Titles[0].Name;
                        }

                    }
                    else if (HomeViewModel.SpoofingStatus == 0 && !string.IsNullOrWhiteSpace(GameInfo))
                    {
                        SpoofGame();
                    }
                }
            }

            if (IsInitialized && NewGame)
                await RefreshAchievements();
            if (TitleID != "0")
            {
                TitleIDOverride = TitleID;
                TitleID = "0";
            }
            if (HomeViewModel.InitComplete && TitleIDOverride == "0")
                TitleIDEnabled = true;
            if (!IsInitialized && HomeViewModel.InitComplete && TitleIDOverride != "0")
                InitializeViewModel();
        }

        public void OnNavigatedFrom() { }

        private async void InitializeViewModel()
        {
            if (IsSelectedGame360)
                Unlockable = false;
            await LoadGameInfo();
            await LoadAchievements();
            if (HomeViewModel.Settings.AutoSpooferEnabled)
                SpoofGame();
            TitleIDEnabled = true;
            IsInitialized = true;
            NewGame = false;
            // If there is an unlock in progress for this game, offer to continue.
            await CheckResumeAsync();
        }


        private async Task LoadGameInfo()
        {
            // Check for a valid TitleID and set overrides
            if (TitleID != "0")
            {
                TitleIDOverride = TitleID;
                TitleID = "0";
            }

            GameInfo = string.Empty;

            // Fetch game information
            var gameInfoResponse = await _xboxRestAPI.Value.GetGameTitleAsync(HomeViewModel.XUIDOnly, TitleIDOverride);

            // Handle response validation and set properties accordingly
            if (gameInfoResponse?.Titles?.Any() != true)
            {
                GameName = "Error";
                IsTitleIDValid = false;
                return;
            }

            var gameTitle = gameInfoResponse.Titles.FirstOrDefault();
            if (gameTitle != null)
            {
                IsSelectedGame360 = gameTitle.Devices.Contains("Xbox360") || gameTitle.Devices.Contains("Mobile");
                GameName = gameTitle.Name;
                IsTitleIDValid = true;
            }
        }

        private async void SpoofGame()
        {
            if (HomeViewModel.SpoofingStatus == 1)
            {
                if (HomeViewModel.SpoofedTitleID == TitleIDOverride)
                {
                    GameInfo = "Manually Spoofing";
                    GameName = GameInfoResponse.Titles[0].Name;
                }
                else
                {
                    GameInfo = "Spoofing Another Game";
                    GameName = GameInfoResponse.Titles[0].Name;
                }
            }
            else
            {
                HomeViewModel.AutoSpoofedTitleID = TitleIDOverride;
                HomeViewModel.SpoofingStatus = 2;
                GameInfo = "Auto Spoofing";
                if (GameInfoResponse.Titles.Any())
                {
                    GameName = GameInfoResponse.Titles[0].Name;
                }

                await Task.Run(() => Spoofing());
                if (HomeViewModel.SpoofingStatus == 1)
                {
                    if (HomeViewModel.SpoofedTitleID == HomeViewModel.AutoSpoofedTitleID)
                    {
                        GameInfo = "Manually Spoofing";
                        GameName = GameInfoResponse.Titles[0].Name;
                    }
                    else
                    {
                        GameInfo = "Spoofing Another Game";
                        GameName = GameInfoResponse.Titles[0].Name;
                    }
                }
                HomeViewModel.AutoSpoofedTitleID = "0";
            }


        }

        public async Task Spoofing()
        {
            await _xboxRestAPI.Value.SendHeartbeatAsync(HomeViewModel.XUIDOnly, HomeViewModel.AutoSpoofedTitleID);
            var i = 0;
            Thread.Sleep(1000);
            SpoofingUpdate = false;
            while (!SpoofingUpdate)
            {
                if (i == 300)
                {
                    await _xboxRestAPI.Value.SendHeartbeatAsync(HomeViewModel.XUIDOnly, HomeViewModel.AutoSpoofedTitleID);
                    i = 0;
                }
                else
                {
                    if (SpoofingUpdate)
                    {

                        break;
                    }
                    i++;
                }
                Thread.Sleep(1000);
            }
        }

        private async Task LoadAchievements()
        {

            Achievements.Clear();
            DGAchievements.Clear();
            // clears unlocked achievements from dictionary
            _unlockedAchievements.Clear();
            // Disable the button until we confirm a valid game is loaded.
            IsAutoUnlockerEnabled = false;
            if (!IsTitleIDValid)
                return;
            if (!IsSelectedGame360)
            {
                Unlockable = true;
                AchievementResponse = await _xboxRestAPI.Value.GetAchievementsForTitleAsync(HomeViewModel.XUIDOnly, TitleIDOverride);
                try
                {
                    if (AchievementResponse.achievements[0].progression.requirements.Any())
                    {
                        if (AchievementResponse.achievements[0].progression.requirements[0].id !=
                            StringConstants.ZeroUid)
                        {
                            Unlockable = false;
                        }
                        else
                        {
                            Unlockable = true;
                        }
                    }
                }
                catch
                {
                    _snackbarService.Show("Error: No Achievements", $"There were no achievements returned from the API", ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    return;
                }
                for (int i = 0; i < AchievementResponse.achievements.Count; i++)
                {
                    //absolutely fucking dogwater event based check
                    if (AchievementResponse.achievements[i].progression.requirements.Any())
                    {
                        if (AchievementResponse.achievements[i].progression.requirements[0].id !=
                            StringConstants.ZeroUid)
                        {
                            Unlockable = false;
                            IsEventBased = true;
                        }
                        else
                        {
                            Unlockable = true;
                            IsEventBased = false;
                        }
                    }
                    var rewardnameplaceholder = "";
                    var rewarddescriptionplaceholder = "";
                    var rewardvalueplaceholder = "";
                    var rewardtypeplaceholder = "";
                    var rewardmediaAssetplaceholder = "";
                    var rewardvalueTypeplaceholder = "";
                    try
                    {
                        rewardnameplaceholder = AchievementResponse.achievements[i].rewards[0].name;
                        rewarddescriptionplaceholder = AchievementResponse.achievements[i].rewards[0].description;
                        rewardvalueplaceholder = AchievementResponse.achievements[i].rewards[0].value;
                        rewardtypeplaceholder = AchievementResponse.achievements[i].rewards[0].type;
                        //rewardmediaAssetplaceholder = AchievementResponse.achievements[i].rewards[0].mediaAsset;
                        rewardvalueTypeplaceholder = AchievementResponse.achievements[i].rewards[0].valueType;
                    }
                    catch
                    {
                        rewardnameplaceholder = "N/A";
                        rewarddescriptionplaceholder = "N/A";
                        rewardvalueplaceholder = "N/A";
                        rewardtypeplaceholder = "N/A";
                        rewardmediaAssetplaceholder = "N/A";
                        rewardvalueTypeplaceholder = "N/A";
                    }

                    var mediaAsset = new MediaAsset
                    {
                        name = AchievementResponse.achievements[i].mediaAssets[0].name,
                        type = AchievementResponse.achievements[i].mediaAssets[0].type,
                        url = AchievementResponse.achievements[i].mediaAssets[0].url
                    };
                    var titleAssociation = new TitleAssociation
                    {
                        name = AchievementResponse.achievements[i].titleAssociations[0].name,
                        id = AchievementResponse.achievements[i].titleAssociations[0].id
                    };
                    var progression = new AchievementProgression
                    {
                        timeUnlocked = AchievementResponse.achievements[i].progression.timeUnlocked
                    };
                    var rewards = new AchievementRewards
                    {
                        name = rewardnameplaceholder,
                        description = rewarddescriptionplaceholder,
                        value = rewardvalueplaceholder,
                        type = rewardtypeplaceholder,
                        mediaAsset = mediaAsset,
                        valueType = rewardvalueTypeplaceholder
                    };


                    Achievements.Add(new OneCoreAchievementResponse()
                    {
                        id = AchievementResponse.achievements[i].id,
                        serviceConfigId = AchievementResponse.achievements[i].serviceConfigId,
                        name = AchievementResponse.achievements[i].name,
                        titleAssociations = new List<TitleAssociation>() { titleAssociation },
                        progressState = AchievementResponse.achievements[i].progressState,
                        progression = progression,
                        mediaAssets = new List<MediaAsset>() { mediaAsset },
                        platforms = AchievementResponse.achievements[i].platforms,
                        isSecret = AchievementResponse.achievements[i].isSecret,
                        description = AchievementResponse.achievements[i].description,
                        lockedDescription = AchievementResponse.achievements[i].lockedDescription,
                        productId = AchievementResponse.achievements[i].productId,
                        achievementType = AchievementResponse.achievements[i].achievementType,
                        participationType = AchievementResponse.achievements[i].participationType,
                        timeWindow = AchievementResponse.achievements[i].timeWindow,
                        rewards = new List<AchievementRewards>() { rewards },
                        estimatedTime = AchievementResponse.achievements[i].estimatedTime,
                        deeplink = AchievementResponse.achievements[i].deeplink,
                        isRevoked = AchievementResponse.achievements[i].isRevoked,
                        raritycurrentCategory = AchievementResponse.achievements[i].rarity.currentCategory,
                        raritycurrentPercentage = AchievementResponse.achievements[i].rarity.currentPercentage
                    }
                    );
                }
                foreach (var achievement in Achievements)
                {
                    var gamerscore = 0;
                    if (achievement.rewards[0].type == StringConstants.Gamerscore)
                    {
                        gamerscore = int.Parse(achievement.rewards[0].value);
                    }
                    DGAchievements.Add(new DGAchievement()
                    {
                        Index = Achievements.IndexOf(achievement),
                        ID = int.Parse(achievement.id),
                        Name = achievement.name,
                        Description = achievement.description,
                        IsSecret = achievement.isSecret,
                        DateUnlocked = DateTime.Parse(achievement.progression.timeUnlocked),
                        Gamerscore = gamerscore,
                        RarityPercentage = float.Parse(achievement.raritycurrentPercentage, CultureInfo.InvariantCulture),
                        RarityCategory = achievement.raritycurrentCategory,
                        ProgressState = achievement.progressState,
                        IsUnlockable = achievement.progressState != StringConstants.Achieved && Unlockable && !IsEventBased
                    });
                }
            }
            else
            {
                Unlockable = false;
                Xbox360AchievementResponse = await _xboxRestAPI.Value.GetAchievementsFor360TitleAsync(HomeViewModel.XUIDOnly, TitleIDOverride);
                if (Xbox360AchievementResponse?.achievements.Count == 0)
                {
                    IsSelectedGame360 = false;
                    LoadAchievements();
                    return;
                }
                //cut down version of the code to display minimal information about 360 achievements
                for (int i = 0; i < Xbox360AchievementResponse?.achievements.Count; i++)
                {
                    var rewards = new AchievementRewards
                    {
                        value = Xbox360AchievementResponse.achievements[i].gamerscore.ToString(),
                        valueType = "N/a"
                    };
                    var progression = new AchievementProgression
                    {
                        timeUnlocked = Xbox360AchievementResponse.achievements[i].timeUnlocked
                    };

                    Achievements.Add(new OneCoreAchievementResponse()
                    {
                        id = Xbox360AchievementResponse.achievements[i].id.ToString(),
                        name = Xbox360AchievementResponse.achievements[i].name,
                        isSecret = Xbox360AchievementResponse.achievements[i].isSecret,
                        description = Xbox360AchievementResponse.achievements[i].description,
                        rewards = new List<AchievementRewards>() { rewards },
                        raritycurrentCategory = Xbox360AchievementResponse.achievements[i].rarity.currentCategory,
                        raritycurrentPercentage = Xbox360AchievementResponse.achievements[i].rarity.currentPercentage,
                        progression = progression
                    }
                    );
                }
                foreach (var achievement in Achievements)
                {
                    var gamerscore = 0;
                    if (achievement.rewards[0].type == "Gamerscore")
                    {
                        gamerscore = int.Parse(achievement.rewards[0].value);
                    }
                    DGAchievements.Add(new DGAchievement()
                    {
                        Index = Achievements.IndexOf(achievement),
                        ID = int.Parse(achievement.id),
                        Name = achievement.name,
                        Description = achievement.description,
                        IsSecret = achievement.isSecret,
                        DateUnlocked = DateTime.Parse(achievement.progression.timeUnlocked),
                        Gamerscore = gamerscore,
                        RarityPercentage = float.Parse(achievement.raritycurrentPercentage, CultureInfo.InvariantCulture),
                        RarityCategory = achievement.raritycurrentCategory,
                        ProgressState = achievement.progressState,
                        IsUnlockable = achievement.progressState != StringConstants.Achieved && Unlockable
                    });
                }
            }

            if (IsSelectedGame360)
            {
                _snackbarService.Show("Warning: Unsupported Game", $"This tool does not/will not support Xbox 360 titles. To unlock 360 achievements, you can try https://www.wemod.com/horizon", ControlAppearance.Caution,
                    new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                IsUnlockAllEnabled = false;

                return;
            }

            if (IsEventBased)
            {
                //Event based logic
                string DataPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\XAU\\Events\\Data.json";
                var data = JObject.Parse(File.ReadAllText(DataPath));
                JArray SupportedGamesJ = (JArray)data["SupportedTitleIDs"];
                List<int> SupportedGames = SupportedGamesJ.ToObject<List<int>>();
                if (SupportedGames.Contains(int.Parse(TitleIDOverride)))
                {
                    Unlockable = true;
                    EventsData = (dynamic)(JObject)data[TitleIDOverride];
                    var gameNode = (JObject)data[TitleIDOverride];
                    var achNode = gameNode?["Achievements"] as JObject;
                    int noDataCount = 0;
                    foreach (var achievement in DGAchievements)
                    {
                        var achId = achievement.ID.ToString();
                        bool hasData = achNode != null && achNode[achId] != null;

                        if (hasData && achievement.ProgressState != StringConstants.Achieved)
                            achievement.IsUnlockable = true;

                        // Category column: blank if already achieved, "No Data" if this
                        // event achievement has no template entry, else Easy/Hard.
                        if (achievement.ProgressState == StringConstants.Achieved)
                            achievement.Category = "";
                        else if (!hasData)
                        {
                            achievement.Category = "No Data";
                            noDataCount++;
                        }
                        else
                            achievement.Category = CategoryDetector.Label(CategoryDetector.Detect(achNode[achId]));
                    }

                    // Let the user know up front when some event achievements can't be unlocked.
                    if (noDataCount > 0)
                        _snackbarService.Show("Some achievements have no event data",
                            $"{noDataCount} achievement(s) have no event data and may not unlock. They're marked \"No Data\".",
                            ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                }
                CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
            }



            if (!Unlockable)
            {
                _snackbarService.Show("Warning: Unsupported Game", $"This tool does not support this Event Based title", ControlAppearance.Caution,
                    new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
            }
            else if (IsEventBased && EventsData.FullySupported == false)
            {
                _snackbarService.Show("Warning: Partially Unsupported Game", $"This tool does not fully support this title. Not all achievements are unlockable", ControlAppearance.Caution,
                                       new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
            }

            if (HomeViewModel.Settings.UnlockAllEnabled && Unlockable && !IsEventBased)
                IsUnlockAllEnabled = Unlockable;
            else
                IsUnlockAllEnabled = false;

            // The Auto Unlocker works for any supported game (regular or event based).
            IsAutoUnlockerEnabled = IsTitleIDValid && !IsSelectedGame360 && DGAchievements.Count > 0;
            // Reflect whether a saved order already exists for this game.
            AutoHasOrder = IsAutoUnlockerEnabled && OrderRepository.Exists(TitleIDOverride);
        }

        public async void UnlockAchievement(int AchievementIndex)
        {
            if (!IsEventBased)
            {
                try
                {
                    await _xboxRestAPI.Value.UnlockTitleBasedAchievementAsync(AchievementResponse.achievements[0].serviceConfigId, AchievementResponse.achievements[0].titleAssociations[0].id, HomeViewModel.XUIDOnly, DGAchievements[AchievementIndex].ID.ToString(), HomeViewModel.Settings.FakeSignatureEnabled);

                    _snackbarService.Show("Achievement Unlocked", $"{DGAchievements[AchievementIndex].Name} has been unlocked",
                        ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
                    DGAchievements[AchievementIndex].IsUnlockable = false;
                    DGAchievements[AchievementIndex].ProgressState = StringConstants.Achieved;
                    DGAchievements[AchievementIndex].DateUnlocked = DateTime.Now;

                    // Add achievement to the dictionary. this will fix search & filter unlockable state
                    var unlockedAchievement = DGAchievements[AchievementIndex];
                    unlockedAchievement.IsUnlockable = false;
                    unlockedAchievement.ProgressState = StringConstants.Achieved;
                    unlockedAchievement.DateUnlocked = DateTime.Now;

                    if (!_unlockedAchievements.ContainsKey(unlockedAchievement.ID))
                    {
                        _unlockedAchievements.Add(unlockedAchievement.ID, unlockedAchievement);
                    }

                    // Verify it actually landed (marks ✓ when confirmed).
                    DGAchievements[AchievementIndex].VerifyState = "⏳";
                    _ = VerifyAndMarkBatchAsync(new List<string> { unlockedAchievement.ID.ToString() }, eventBased: false, announce: false);

                    CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                }
                catch (HttpRequestException ex)
                {
                    _snackbarService.Show("Error: Achievement Not Unlocked",
                        $"{DGAchievements[AchievementIndex].Name} was not unlocked", ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                }
            }
            else
            {
                if (EventsToken == null)
                {
                    ContentDialogResult result = await _contentDialogService.ShowSimpleDialogAsync(
                        new SimpleContentDialogCreateOptions()
                        {
                            Title = "Error: You have not set an events token",
                            Content = "To unlock event based games you must supply an events token. Please refer to the guide for more information.\nPressing the \"Open Guide\" button will open the documentation and guide in your default browser.",
                            PrimaryButtonText = "Open Guide",
                            CloseButtonText = "Close",
                        });

                    switch (result)
                    {
                        case ContentDialogResult.Primary:
                            var sInfo = new System.Diagnostics.ProcessStartInfo(OpenableLinks.EventsDocumentationUrl)
                            {
                                UseShellExecute = true,
                            };
                            System.Diagnostics.Process.Start(sInfo);
                            break;
                    }
                    return;
                }

                // The event unlocker takes care of building the body (Easy or Hard)
                // and sending it. This way the manual unlock and the automatic one
                // use the same code path.
                var achId = DGAchievements[AchievementIndex].ID.ToString();
                try
                {
                    // Counter achievements (requirement target > 1): probe + measure +
                    // top-up to the target automatically. Non-counters use the normal
                    // Loop x multiplier path (driven by the "Loop ×" box).
                    if (LooksLikeCounter(achId))
                    {
                        var cu = new CounterUnlocker(_xboxRestAPI.Value, TitleIDOverride, HomeViewModel.XUIDOnly, EventsToken);
                        var cr = await cu.RunAsync(achId, (JObject)EventsData, CancellationToken.None);
                        if (cr == CounterResult.Failed)
                        {
                            _snackbarService.Show("Error: Achievement Not Unlocked",
                                $"{DGAchievements[AchievementIndex].Name} was not unlocked", ControlAppearance.Danger,
                                new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                            return;
                        }
                        if (cr != CounterResult.NotCounter)
                        {
                            DGAchievements[AchievementIndex].IsUnlockable = false;
                            if (cr == CounterResult.Achieved)
                            {
                                DGAchievements[AchievementIndex].ProgressState = StringConstants.Achieved;
                                DGAchievements[AchievementIndex].DateUnlocked = DateTime.Now;
                                _snackbarService.Show("Achievement Unlocked", $"{DGAchievements[AchievementIndex].Name} has been unlocked",
                                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
                            }
                            else
                            {
                                _snackbarService.Show("Counter sent — pending",
                                    $"{DGAchievements[AchievementIndex].Name} events sent; Xbox may still be processing.",
                                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                            }
                            CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                            return;
                        }
                        // NotCounter -> fall through to the normal event unlock below.
                    }

                    // Same Loop x multiplier (probe-then-bulk + batching) as the auto path,
                    // driven by the "Loop ×" box in the Auto Unlocker panel.
                    int multiplier = int.TryParse(AutoLoopCount?.Trim(), out var m) && m > 0 ? m : 1;
                    var eventUnlocker = new EventUnlocker(_xboxRestAPI.Value, TitleIDOverride, HomeViewModel.XUIDOnly, EventsToken);
                    await eventUnlocker.UnlockAsync(achId, (JObject)EventsData, multiplier);

                    _snackbarService.Show("Achievement Unlocked", $"{DGAchievements[AchievementIndex].Name} has been unlocked",
                        ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
                    DGAchievements[AchievementIndex].IsUnlockable = false;
                    DGAchievements[AchievementIndex].ProgressState = StringConstants.Achieved;
                    DGAchievements[AchievementIndex].DateUnlocked = DateTime.Now;
                    CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                }
                catch
                {
                    _snackbarService.Show("Error: Achievement Not Unlocked",
                        $"{DGAchievements[AchievementIndex].Name} was not unlocked", ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                }

            }

        }

        [RelayCommand]
        public async Task UnlockAll()
        {
            var lockedAchievementIds = Achievements.Where(o => o.progressState != StringConstants.Achieved).Select(o => o.id).ToList();
            try
            {
                await _xboxRestAPI.Value.UnlockTitleBasedAchievementsAsync(serviceConfigId: AchievementResponse.achievements[0].serviceConfigId,
                    titleId: AchievementResponse.achievements[0].titleAssociations[0].id, xuid: HomeViewModel.XUIDOnly, achievementIds: lockedAchievementIds, useFakeSignature: HomeViewModel.Settings.FakeSignatureEnabled);

                _snackbarService.Show("All Achievements Unlocked", $"All Achievements for this game have been unlocked",
                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
                var unlocktime = DateTime.Now;
                foreach (DGAchievement achievement in DGAchievements)
                {

                    if (achievement.ProgressState != StringConstants.Achieved)
                    {
                        achievement.IsUnlockable = false;
                        achievement.ProgressState = StringConstants.Achieved;
                        achievement.DateUnlocked = unlocktime;
                        achievement.VerifyState = "⏳";
                    }
                }
                CollectionViewSource.GetDefaultView(DGAchievements).Refresh();

                // Verify the batch actually landed and report how many confirmed.
                _ = VerifyAndMarkBatchAsync(lockedAchievementIds, eventBased: false, announce: true);
            }
            catch (HttpRequestException hre)
            {
                _snackbarService.Show("Error: Achievements Not Unlocked",
                                        $"{hre.Message}", ControlAppearance.Danger,
                                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }
        }

        [RelayCommand]
        public async Task RefreshAchievements()
        {
            // clears unlocked achievements from dictionary
            _unlockedAchievements.Clear();

            await LoadGameInfo();
            await LoadAchievements();
            NewGame = false;
            if (HomeViewModel.Settings.AutoSpooferEnabled)
                SpoofGame();
        }

        [RelayCommand]
        public async Task SearchAndFilterAchievements()
        {
            try
            {
                if (IsEventBased)
                {
                    string DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events", "Data.json");
                    var data = JObject.Parse(File.ReadAllText(DataPath));
                    JArray SupportedGamesJ = (JArray)data["SupportedTitleIDs"];
                    List<int> SupportedGames = SupportedGamesJ.ToObject<List<int>>();
                    if (SupportedGames.Contains(int.Parse(TitleIDOverride)))
                    {
                        Unlockable = true;
                        EventsData = (dynamic)data[TitleIDOverride];
                    }
                }

                CollectionViewSource.GetDefaultView(DGAchievements).Refresh();

                if (string.IsNullOrWhiteSpace(SearchText) && !IsFiltered)
                {
                    _snackbarService.Show("Error", $"Please Enter Query Text", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    return;
                }

                DGAchievements.Clear();

                if (string.IsNullOrWhiteSpace(SearchText) && IsFiltered)
                {
                    foreach (var achievement in Achievements)
                    {
                        var gamerscore = 0;
                        if (achievement.rewards[0].type == StringConstants.Gamerscore)
                        {
                            gamerscore = int.Parse(achievement.rewards[0].value);
                        }

                        var dgAchievement = new DGAchievement()
                        {
                            Index = DGAchievements.Count,
                            ID = int.Parse(achievement.id),
                            Name = achievement.name,
                            Description = achievement.description,
                            IsSecret = achievement.isSecret,
                            DateUnlocked = DateTime.Parse(achievement.progression.timeUnlocked),
                            Gamerscore = gamerscore,
                            RarityPercentage = float.Parse(achievement.raritycurrentPercentage, CultureInfo.InvariantCulture),
                            RarityCategory = achievement.raritycurrentCategory,
                            ProgressState = achievement.progressState,
                            IsUnlockable = achievement.progressState != StringConstants.Achieved && Unlockable && !IsEventBased
                        };

                        // Override with the state from _unlockedAchievements dictionary if it exists.
                        if (_unlockedAchievements.ContainsKey(dgAchievement.ID))
                        {
                            var unlocked = _unlockedAchievements[dgAchievement.ID];
                            dgAchievement.IsUnlockable = unlocked.IsUnlockable;
                            dgAchievement.ProgressState = unlocked.ProgressState;
                            dgAchievement.DateUnlocked = unlocked.DateUnlocked;
                        }

                        DGAchievements.Add(dgAchievement);
                    }

                    if (IsEventBased && Unlockable)
                    {
                        foreach (var achievement in DGAchievements)
                        {
                            if (EventsData.Achievements.ContainsKey(achievement.ID.ToString()) && achievement.ProgressState != StringConstants.Achieved)
                            {
                                achievement.IsUnlockable = true;
                            }
                        }
                        CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                    }
                    IsFiltered = false;
                    return;
                }

                bool achievementsFound = false;

                foreach (var achievement in Achievements)
                {
                    if (achievement.name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || achievement.description.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    {
                        var gamerscore = 0;
                        if (achievement.rewards[0].type == StringConstants.Gamerscore)
                        {
                            gamerscore = int.Parse(achievement.rewards[0].value);
                        }

                        var dgAchievement = new DGAchievement()
                        {
                            Index = DGAchievements.Count,
                            ID = int.Parse(achievement.id),
                            Name = achievement.name,
                            Description = achievement.description,
                            IsSecret = achievement.isSecret,
                            DateUnlocked = DateTime.Parse(achievement.progression.timeUnlocked),
                            Gamerscore = gamerscore,
                            RarityPercentage = float.Parse(achievement.raritycurrentPercentage, CultureInfo.InvariantCulture),
                            RarityCategory = achievement.raritycurrentCategory,
                            ProgressState = achievement.progressState,
                            IsUnlockable = achievement.progressState != StringConstants.Achieved && Unlockable && !IsEventBased
                        };

                        // Override with the state from _unlockedAchievements dictionary if it exists.
                        if (_unlockedAchievements.ContainsKey(dgAchievement.ID))
                        {
                            var unlockedAchievement = _unlockedAchievements[dgAchievement.ID];
                            dgAchievement.IsUnlockable = unlockedAchievement.IsUnlockable;
                            dgAchievement.ProgressState = unlockedAchievement.ProgressState;
                            dgAchievement.DateUnlocked = unlockedAchievement.DateUnlocked;
                        }

                        DGAchievements.Add(dgAchievement);
                        achievementsFound = true;
                    }
                }

                if (!achievementsFound)
                {
                    _snackbarService.Show("Error", $"No Achievements Found", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                }

                if (IsEventBased && Unlockable)
                {
                    foreach (var achievement in DGAchievements)
                    {
                        if (EventsData.Achievements.ContainsKey(achievement.ID.ToString()) && achievement.ProgressState != StringConstants.Achieved)
                        {
                            achievement.IsUnlockable = true;
                        }
                    }
                    CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                }

                IsFiltered = true;
            }
            catch (Exception ex)
            {
                // Log exception (ex) if necessary
                _snackbarService.Show("Error", "An error occurred while searching. Please try again.", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }

            await Task.CompletedTask;
        }

        #region Auto Unlocker

        // ----- Auto Unlocker panel state (bound by the bar above the grid) -----

        // True while a run is in progress (engine running or verifications pending).
        [ObservableProperty] private bool _autoRunning = false;
        // True when the run is paused (countdown frozen).
        [ObservableProperty] private bool _autoPaused = false;
        // The order-management buttons are disabled while a run is in progress.
        [ObservableProperty] private bool _autoControlsEnabled = true;
        // True when a saved order exists for the current game.
        [ObservableProperty] private bool _autoHasOrder = false;
        // Label on the single Start/Pause/Resume button.
        [ObservableProperty] private string _autoStartPauseLabel = "Start";
        // Name of the achievement currently being unlocked.
        [ObservableProperty] private string _autoCurrentAchievement = "";
        // Live countdown / status line ("Next in 00:42", "Unlocking...", "Paused").
        [ObservableProperty] private string _autoCountdownText = "";
        // Short status summary ("Idle", "3/10  •  ✓ 2  ⏳ 1").
        [ObservableProperty] private string _autoStatusText = "Idle";
        // Progress bar value, 0..1.
        [ObservableProperty] private double _autoProgressValue = 0;
        // Estimated finish time of the run ("Est. finish ~14:32").
        [ObservableProperty] private string _autoEstimateText = "";
        // Loop multiplier for counter-based event achievements (default 1).
        [ObservableProperty] private string _autoLoopCount = "1";

        // Run-scoped state.
        private CancellationTokenSource? _autoCts;
        private readonly List<Task> _verifyTasks = new List<Task>();
        private int _confirmedCount, _pendingCount, _failedCount;
        private int _autoIndex, _autoTotal;
        private string _autoFinalMessage = "";
        private HashSet<string> _autoSkip = new HashSet<string>();

        // Keep the order buttons in sync with the running state.
        partial void OnAutoRunningChanged(bool value) => AutoControlsEnabled = !value;

        // The single Start/Pause/Resume button. Start launches the run (non-blocking,
        // so the same button stays responsive for pausing); while running it toggles
        // pause/resume.
        [RelayCommand]
        public async Task ToggleAutoUnlocker()
        {
            // Running -> toggle pause/resume.
            if (AutoRunning)
            {
                AutoPaused = !AutoPaused;
                AutoStartPauseLabel = AutoPaused ? "Resume" : "Pause";
                return;
            }

            // Not running -> validate and start.
            if (!IsTitleIDValid || IsSelectedGame360)
            {
                _snackbarService.Show("Auto Unlocker", "Load a supported game first.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (!HomeViewModel.InitComplete)
            {
                _snackbarService.Show("Auto Unlocker", "Sign in before using this.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (IsEventBased && EventsToken == null)
            {
                ContentDialogResult tokenResult = await _contentDialogService.ShowSimpleDialogAsync(
                    new SimpleContentDialogCreateOptions()
                    {
                        Title = "Error: You have not set an events token",
                        Content = "To unlock event based games you must supply an events token. Please refer to the guide for more information.\nPressing the \"Open Guide\" button will open the documentation and guide in your default browser.",
                        PrimaryButtonText = "Open Guide",
                        CloseButtonText = "Close",
                    });
                if (tokenResult == ContentDialogResult.Primary)
                {
                    var sInfo = new System.Diagnostics.ProcessStartInfo(OpenableLinks.EventsDocumentationUrl)
                    {
                        UseShellExecute = true,
                    };
                    System.Diagnostics.Process.Start(sInfo);
                }
                return;
            }

            var order = OrderRepository.Load(TitleIDOverride);
            if (order == null || order.Items.Count == 0)
            {
                _snackbarService.Show("Auto Unlocker", "No unlock order yet — press \"Get Unlock Order\" first.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }

            // Fire-and-forget so this command returns immediately and the button stays
            // live for pause/resume. RunWithOrderAsync owns its own error handling.
            _ = RunWithOrderAsync(order);
        }

        // The "Get Unlock Order" button. Asks for a reference gamertag and builds (or
        // rebuilds) the order from that player's real unlock timeline.
        [RelayCommand]
        public async Task GetUnlockOrder()
        {
            if (!IsTitleIDValid || IsSelectedGame360)
            {
                _snackbarService.Show("Auto Unlocker", "Load a supported game first.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (!HomeViewModel.InitComplete)
            {
                _snackbarService.Show("Auto Unlocker", "Sign in before using this.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (AutoRunning)
            {
                _snackbarService.Show("Auto Unlocker", "Stop the unlocker before changing the order.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }

            if (OrderRepository.Exists(TitleIDOverride))
            {
                var replace = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
                {
                    Title = "Replace existing order?",
                    Content = "An unlock order already exists for this game. Generating a new one will replace it (delays you edited will be lost).",
                    PrimaryButtonText = "Replace",
                    CloseButtonText = "Keep current"
                });
                if (replace != ContentDialogResult.Primary)
                    return;
            }

            var gamertag = await AskReferenceGamertagAsync();
            if (string.IsNullOrWhiteSpace(gamertag))
                return;

            try
            {
                var alreadyEarned = new HashSet<string>(
                    DGAchievements.Where(a => a.ProgressState == StringConstants.Achieved)
                                  .Select(a => a.ID.ToString()));
                var order = await OrderRepository.GenerateFromReferenceAsync(
                    _xboxRestAPI.Value, TitleIDOverride, GameName, gamertag, alreadyEarned);
                OrderRepository.Save(order);

                // Event games: flag order items with no event data and offer to skip them.
                if (IsEventBased)
                {
                    var noData = order.Items.Where(it => !HasEventData(it.Id)).Select(it => it.Id).ToList();
                    if (noData.Count > 0)
                    {
                        var ask = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
                        {
                            Title = "Some achievements have no event data",
                            Content = $"{noData.Count} of {order.Items.Count} achievements in this order have no event data and likely won't unlock. Skip them?",
                            PrimaryButtonText = "Skip them",
                            CloseButtonText = "Keep them"
                        });
                        if (ask == ContentDialogResult.Primary)
                        {
                            var skip = OrderRepository.LoadSkip(TitleIDOverride);
                            foreach (var id in noData)
                                skip.Add(id);
                            OrderRepository.SaveSkip(TitleIDOverride, skip);
                        }
                    }
                }

                AutoHasOrder = true;
                AutoStatusText = $"Order ready — {order.Items.Count} achievements";
                _snackbarService.Show("Order created", $"{order.Items.Count} achievements queued. Press Start to begin.",
                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
            }
            catch (Exception ex)
            {
                _snackbarService.Show("Could not create the order", ex.Message,
                    ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }
        }

        // Confirms a batch of just-sent unlocks against the achievements service and
        // marks each row "✓" once it reads Achieved (rate-limited, patient for event
        // games). Used by manual unlock and Unlock All so they verify success instead
        // of only claiming it. Runs in the background; never throws.
        private async Task VerifyAndMarkBatchAsync(List<string> ids, bool eventBased, bool announce)
        {
            if (ids == null || ids.Count == 0)
                return;

            var pending = new HashSet<string>(ids);
            var deadline = DateTime.UtcNow + (eventBased ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(45));
            int[] backoff = { 3, 5, 8, 13, 21, 30 };
            int attempt = 0;

            while (pending.Count > 0 && DateTime.UtcNow < deadline)
            {
                var wait = backoff[Math.Min(attempt, backoff.Length - 1)];
                attempt++;
                try { await Task.Delay(TimeSpan.FromSeconds(wait)); } catch { break; }

                AchievementsResponse? resp = null;
                try
                {
                    await XboxRateLimiter.Achievements.WaitAsync(CancellationToken.None);
                    resp = await _xboxRestAPI.Value.GetAchievementsForTitleAsync(HomeViewModel.XUIDOnly, TitleIDOverride);
                }
                catch { continue; }

                var achieved = new HashSet<string>(
                    resp?.achievements?.Where(a => a.progressState == StringConstants.Achieved).Select(a => a.id)
                    ?? Enumerable.Empty<string>());

                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var id in pending.ToList())
                    {
                        if (achieved.Contains(id))
                        {
                            var a = DGAchievements.FirstOrDefault(x => x.ID.ToString() == id);
                            if (a != null) a.VerifyState = "✓";
                            pending.Remove(id);
                        }
                    }
                    CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                });
            }

            if (!announce)
                return;
            int confirmed = ids.Count - pending.Count;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (pending.Count == 0)
                    _snackbarService.Show("Verified", $"{confirmed} achievement(s) confirmed unlocked.",
                        ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
                else
                    _snackbarService.Show("Partly confirmed",
                        $"{confirmed} of {ids.Count} confirmed; {pending.Count} still pending (Xbox may still be processing).",
                        ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
            });
        }

        // True if this achievement has event data we can unlock with. Title-managed
        // games always "have data"; for event games it checks the Data.json template
        // entry (the same node LoadAchievements marks "No Data" when missing).
        private bool HasEventData(string id)
        {
            if (!IsEventBased)
                return true;
            var node = EventsData as JObject;
            var achs = node?["Achievements"] as JObject;
            return achs != null && achs[id] != null;
        }

        // Composes the compact status summary shown in the panel.
        private string ComposeAutoStatus()
        {
            var status = $"{_autoIndex}/{_autoTotal}  •  ✓ {_confirmedCount}";
            if (_pendingCount > 0) status += $"  ⏳ {_pendingCount}";
            if (_failedCount > 0) status += $"  ✗ {_failedCount}";
            return status;
        }

        // The "Edit Skip List" button. Lists the order's achievements with checkboxes
        // (checked = include, unchecked = skip) and saves the unchecked ones as the
        // skip list so the run passes over them.
        [RelayCommand]
        public async Task EditSkipList()
        {
            var order = OrderRepository.Load(TitleIDOverride);
            if (order == null || order.Items.Count == 0)
            {
                _snackbarService.Show("Auto Unlocker", "No unlock order yet — press \"Get Unlock Order\" first.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (AutoRunning)
            {
                _snackbarService.Show("Auto Unlocker", "Stop the unlocker before editing the skip list.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }

            var skip = OrderRepository.LoadSkip(TitleIDOverride);

            var header = new System.Windows.Controls.TextBlock
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var boxes = new List<(System.Windows.Controls.CheckBox cb, UnlockOrderItem item)>();
            var list = new System.Windows.Controls.StackPanel();
            foreach (var item in order.Items)
            {
                var cb = new System.Windows.Controls.CheckBox
                {
                    Content = $"{item.Name}  —  {FormatTime(item.DelaySeconds)}  •  {item.Gamerscore}G",
                    IsChecked = !skip.Contains(item.Id),
                    Margin = new Thickness(0, 2, 0, 2)
                };
                boxes.Add((cb, item));
                list.Children.Add(cb);
            }

            void UpdateSummary()
            {
                long seconds = 0;
                int included = 0;
                foreach (var (cb, item) in boxes)
                {
                    if (cb.IsChecked == true)
                    {
                        included++;
                        seconds += item.DelaySeconds;
                    }
                }
                header.Text = $"{included} of {order.Items.Count} included  •  estimated time {FormatTime(seconds)}";
            }
            foreach (var (cb, _) in boxes)
            {
                cb.Checked += (_, _) => UpdateSummary();
                cb.Unchecked += (_, _) => UpdateSummary();
            }
            UpdateSummary();

            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Content = list,
                MaxHeight = 320,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            var panel = new System.Windows.Controls.StackPanel { MinWidth = 400 };
            panel.Children.Add(header);
            panel.Children.Add(scroll);

            var result = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
            {
                Title = $"Skip List — {order.GameName}",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            });

            if (result == ContentDialogResult.Primary)
            {
                var newSkip = new HashSet<string>();
                foreach (var (cb, item) in boxes)
                {
                    if (cb.IsChecked != true)
                        newSkip.Add(item.Id);
                }
                OrderRepository.SaveSkip(TitleIDOverride, newSkip);
                _snackbarService.Show("Skip list saved", $"{newSkip.Count} achievement(s) will be skipped.",
                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
            }
        }

        // The "Edit Delays" button. Lets the user fine-tune the wait (in seconds)
        // before each achievement unlocks, then saves it back into the order.
        [RelayCommand]
        public async Task EditDelays()
        {
            var order = OrderRepository.Load(TitleIDOverride);
            if (order == null || order.Items.Count == 0)
            {
                _snackbarService.Show("Auto Unlocker", "No unlock order yet — press \"Get Unlock Order\" first.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }
            if (AutoRunning)
            {
                _snackbarService.Show("Auto Unlocker", "Stop the unlocker before editing delays.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }

            var hint = new System.Windows.Controls.TextBlock
            {
                Text = "Delay (in seconds) to wait before each achievement unlocks. The first is usually 0.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var rows = new List<(System.Windows.Controls.TextBox box, UnlockOrderItem item)>();
            var list = new System.Windows.Controls.StackPanel();
            foreach (var item in order.Items)
            {
                var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(90) });

                var label = new System.Windows.Controls.TextBlock
                {
                    Text = item.Name,
                    TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                System.Windows.Controls.Grid.SetColumn(label, 0);

                var box = new System.Windows.Controls.TextBox
                {
                    Text = item.DelaySeconds.ToString(),
                    TextAlignment = System.Windows.TextAlignment.Right,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                System.Windows.Controls.Grid.SetColumn(box, 1);

                row.Children.Add(label);
                row.Children.Add(box);
                list.Children.Add(row);
                rows.Add((box, item));
            }

            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Content = list,
                MaxHeight = 320,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            var panel = new System.Windows.Controls.StackPanel { MinWidth = 420 };
            panel.Children.Add(hint);
            panel.Children.Add(scroll);

            var result = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
            {
                Title = $"Edit Delays — {order.GameName}",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel"
            });

            if (result == ContentDialogResult.Primary)
            {
                foreach (var (box, item) in rows)
                {
                    if (long.TryParse(box.Text?.Trim(), out var seconds) && seconds >= 0)
                        item.DelaySeconds = seconds;
                }
                OrderRepository.Save(order);
                _snackbarService.Show("Delays saved", $"Total run time ~{FormatTime(order.TotalDurationSeconds)}.",
                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
            }
        }

        // When opening a game that has an unlock in progress, offer to continue.
        private async Task CheckResumeAsync()
        {
            if (!IsTitleIDValid || IsSelectedGame360)
                return;
            // We only ask once per game in this session.
            if (_resumePrompted.Contains(TitleIDOverride))
                return;
            // There must be a saved order AND an in-progress state.
            if (!OrderRepository.Exists(TitleIDOverride) || OrderRepository.LoadState(TitleIDOverride) == null)
                return;
            // Event game without a valid token: cannot resume right now.
            if (IsEventBased && EventsToken == null)
                return;

            _resumePrompted.Add(TitleIDOverride);

            var answer = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
            {
                Title = "Continue unlock?",
                Content = $"There is an automatic unlock in progress for \"{GameName}\". Continue from where it stopped?",
                PrimaryButtonText = "Continue",
                CloseButtonText = "Not now"
            });

            if (answer == ContentDialogResult.Primary)
            {
                var order = OrderRepository.Load(TitleIDOverride);
                if (order != null && order.Items.Count > 0)
                    _ = RunWithOrderAsync(order); // panel-driven; don't block navigation
            }
        }

        // Shows a simple dialog asking for the reference gamertag. Returns the typed
        // text or null if the user cancels.
        private async Task<string?> AskReferenceGamertagAsync()
        {
            var box = new Wpf.Ui.Controls.TextBox
            {
                PlaceholderText = "Enter the gamertag",
                MinWidth = 300
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Enter the gamertag of a player who has already completed this game. The order will copy their real unlock times so it looks natural.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(box);

            var result = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions()
            {
                Title = "Create Unlock Order",
                Content = panel,
                PrimaryButtonText = "Generate",
                CloseButtonText = "Cancel"
            });

            return result == ContentDialogResult.Primary ? box.Text?.Trim() : null;
        }

        // The compact "Stop" affordance (shown only while running). Cancels the run;
        // RunWithOrderAsync then tears down and the saved state lets it resume later.
        [RelayCommand]
        public void StopAutoUnlocker()
        {
            _autoCts?.Cancel();
            AutoPaused = false;
            AutoStartPauseLabel = "Start";
            AutoCountdownText = "Stopping...";
        }

        // Runs the order and drives the compact panel above the grid. Non-blocking:
        // the engine runs on a background task, the panel reflects progress live, and
        // the Start/Pause button toggles pause via AutoPaused. Each unlock is verified
        // in the background (RTA-accelerated, polling-backed) so the schedule keeps its
        // natural pacing while the ✓/⏳ tallies fill in.
        private RtaAchievementStream? _autoRta;
        private CancellationTokenSource? _spoofCts;
        private Task? _spoofPing;

        // Mantém a presença do jogo spoofado viva durante toda a execução reenviando o
        // heartbeat a cada 5 minutos (a mesma chamada do Auto Spoofer).
        private async Task SpoofPingLoopAsync(string titleId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(5), token); }
                catch (OperationCanceledException) { return; }
                try { await _xboxRestAPI.Value.SendHeartbeatAsync(HomeViewModel.XUIDOnly, titleId); }
                catch { /* best-effort; the next ping retries */ }
            }
        }

        private async Task RunWithOrderAsync(UnlockOrder order)
        {
            if (AutoRunning)
                return;

            _autoCts = new CancellationTokenSource();
            _verifyTasks.Clear();
            _confirmedCount = _pendingCount = _failedCount = 0;
            _autoIndex = 0;
            _autoTotal = order.Items.Count;
            _autoFinalMessage = "";
            _autoSkip = OrderRepository.LoadSkip(TitleIDOverride);

            AutoRunning = true;
            AutoPaused = false;
            AutoStartPauseLabel = "Pause";
            AutoCurrentAchievement = "";
            AutoCountdownText = "Starting...";
            AutoProgressValue = 0;
            AutoStatusText = ComposeAutoStatus();

            // Items the engine skips outright: already-achieved + skip list.
            var alreadyUnlocked = new HashSet<string>(
                DGAchievements.Where(a => a.ProgressState == StringConstants.Achieved)
                              .Select(a => a.ID.ToString()));
            alreadyUnlocked.UnionWith(_autoSkip);

            // Best-effort RTA stream to accelerate verification (fail-open: null = poll only).
            var scid = AchievementResponse?.achievements?.FirstOrDefault()?.serviceConfigId ?? "";
            try
            {
                _autoRta = await RtaAchievementStream.TryStartAsync(
                    HomeViewModel.XAUTH, HomeViewModel.XUIDOnly, scid, _autoCts.Token);
            }
            catch { _autoRta = null; }

            // Keep presence on the title alive for the whole run: spoof now, then ping
            // every 5 minutes (its own CTS so verifications aren't cut short on stop).
            _spoofCts = new CancellationTokenSource();
            try { await _xboxRestAPI.Value.SendHeartbeatAsync(HomeViewModel.XUIDOnly, TitleIDOverride); }
            catch { /* best-effort */ }
            _spoofPing = SpoofPingLoopAsync(TitleIDOverride, _spoofCts.Token);

            // Progress callback from the engine -> panel (UI thread).
            Action<AutoProgress> report = progress => Application.Current.Dispatcher.Invoke(() =>
            {
                _autoIndex = progress.Index;
                _autoTotal = progress.Total;
                AutoCurrentAchievement = progress.AchievementName ?? "";

                if (!string.IsNullOrEmpty(progress.Message) && progress.Message != "Unlocking")
                    AutoCountdownText = progress.Message;
                else if (progress.SecondsRemaining >= 0)
                    AutoCountdownText = $"Next in {FormatTime(progress.SecondsRemaining)}";
                else
                    AutoCountdownText = "Unlocking...";

                AutoProgressValue = progress.Total > 0 ? (double)progress.Index / progress.Total : 0;
                AutoStatusText = ComposeAutoStatus();

                // Estimated finish = current countdown + delays of the items still left
                // (skipping the ones on the skip list).
                long remaining = progress.SecondsRemaining > 0 ? progress.SecondsRemaining : 0;
                for (int k = progress.Index; k < order.Items.Count; k++)
                    if (!_autoSkip.Contains(order.Items[k].Id))
                        remaining += order.Items[k].DelaySeconds;
                AutoEstimateText = (!progress.Completed && remaining > 0)
                    ? $"Est. finish ~{DateTime.Now.AddSeconds(remaining):HH:mm}"
                    : "";

                if (progress.Completed)
                    _autoFinalMessage = progress.Message;
            });

            var engine = new XAU.Services.AutoUnlock.AutoUnlocker(TitleIDOverride);
            try
            {
                await Task.Run(() => engine.RunAsync(
                    order, UnlockItemAsync, alreadyUnlocked, report, () => AutoPaused, _autoCts.Token));
            }
            catch { /* engine is cancellation-safe; stay defensive */ }

            // Let outstanding background verifications finish (or cancel).
            try { await Task.WhenAll(_verifyTasks.ToArray()); } catch { }

            // Stop the spoof ping (separate from the run/verify token).
            _spoofCts?.Cancel();
            try { if (_spoofPing != null) await _spoofPing; } catch { }
            _spoofCts?.Dispose();
            _spoofCts = null;
            _spoofPing = null;

            // Tear down RTA + run state.
            _autoRta?.Dispose();
            _autoRta = null;
            _autoCts?.Dispose();
            _autoCts = null;

            AutoRunning = false;
            AutoPaused = false;
            AutoStartPauseLabel = "Start";
            AutoCurrentAchievement = "";
            AutoCountdownText = "";
            AutoEstimateText = "";
            AutoStatusText = ComposeAutoStatus();

            // Final summary.
            if (_autoFinalMessage == "Completed")
                _snackbarService.Show("Auto Unlocker",
                    $"Done — ✓ {_confirmedCount} confirmed" + (_pendingCount > 0 ? $", ⏳ {_pendingCount} still pending" : "") + ".",
                    ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
            else if (!string.IsNullOrEmpty(_autoFinalMessage) && _autoFinalMessage.StartsWith("Failed"))
                _snackbarService.Show("Auto Unlocker", _autoFinalMessage,
                    ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            else
                _snackbarService.Show("Auto Unlocker", "Stopped — it will continue from where it stopped next time.",
                    ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
        }

        // Cheap in-memory check: does this achievement have a counter requirement
        // (target > 1)? Uses the already-loaded AchievementResponse (targets don't
        // change), so non-counters never pay an extra API read.
        private bool LooksLikeCounter(string id)
        {
            var ach = AchievementResponse?.achievements?.FirstOrDefault(a => a.id == id);
            var req = ach?.progression?.requirements?
                .FirstOrDefault(r => long.TryParse(r.target, out var t) && t > 1);
            return req != null;
        }

        // Sends the unlock for one order item (regular or event game). Returns true if
        // the SEND succeeded so the engine keeps its rhythm; the actual landing is
        // confirmed in the background by VerifyAndMarkAsync (a send returning OK is not
        // proof — event-based OneCollector returns 200 even on silent failures).
        private async Task<bool> UnlockItemAsync(UnlockOrderItem item)
        {
            bool eventBased = IsEventBased;
            var token = _autoCts?.Token ?? CancellationToken.None;

            // Smart counter path: for event achievements that look like counters
            // (a requirement with target > 1), probe + measure + top-up to the target.
            // It does its own verification, so we mark the row inline and skip the
            // background verify. Non-counters fall through to the normal path.
            if (eventBased && LooksLikeCounter(item.Id))
            {
                CounterResult counter;
                try
                {
                    var cu = new CounterUnlocker(_xboxRestAPI.Value, TitleIDOverride, HomeViewModel.XUIDOnly, EventsToken);
                    counter = await cu.RunAsync(item.Id, (JObject)EventsData, token);
                }
                catch { counter = CounterResult.Failed; }

                if (counter == CounterResult.Failed)
                    return false;

                if (counter != CounterResult.NotCounter)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var a = DGAchievements.FirstOrDefault(x => x.ID.ToString() == item.Id);
                        if (a != null) a.IsUnlockable = false;
                        if (counter == CounterResult.Achieved)
                        {
                            _confirmedCount++;
                            if (a != null) { a.ProgressState = StringConstants.Achieved; a.DateUnlocked = DateTime.Now; a.VerifyState = "✓"; }
                        }
                        else
                        {
                            _pendingCount++;
                            if (a != null) a.VerifyState = "⏳";
                        }
                        AutoStatusText = ComposeAutoStatus();
                        CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
                    });
                    return true;
                }
                // counter == NotCounter -> fall through to the normal event unlock.
            }

            try
            {
                if (eventBased)
                {
                    int multiplier = int.TryParse(AutoLoopCount?.Trim(), out var m) && m > 0 ? m : 1;
                    var eventUnlocker = new EventUnlocker(
                        _xboxRestAPI.Value, TitleIDOverride, HomeViewModel.XUIDOnly, EventsToken);
                    await eventUnlocker.UnlockAsync(item.Id, (JObject)EventsData, multiplier);
                }
                else
                {
                    // (UnlockTitleBasedAchievementsAsync rate-limits internally.)
                    await _xboxRestAPI.Value.UnlockTitleBasedAchievementAsync(
                        AchievementResponse.achievements[0].serviceConfigId,
                        AchievementResponse.achievements[0].titleAssociations[0].id,
                        HomeViewModel.XUIDOnly, item.Id, HomeViewModel.Settings.FakeSignatureEnabled);
                }
            }
            catch
            {
                return false; // send failed -> engine stops
            }

            // Optimistic UI: mark as Achieved right away (same as the manual unlock),
            // with a "⏳" confirming marker. Background verification upgrades it to "✓"
            // once confirmed; it never reverts the Achieved state.
            Application.Current.Dispatcher.Invoke(() =>
            {
                var targetAch = DGAchievements.FirstOrDefault(a => a.ID.ToString() == item.Id);
                if (targetAch != null)
                {
                    targetAch.IsUnlockable = false;
                    targetAch.ProgressState = StringConstants.Achieved;
                    targetAch.DateUnlocked = DateTime.Now;
                    targetAch.VerifyState = "⏳";
                }
                CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
            });

            // Verify in the background so the schedule keeps its natural pacing.
            _verifyTasks.Add(VerifyAndMarkAsync(item, eventBased, token));
            return true;
        }

        // Background confirmation of a single unlock via the achievements service
        // (RTA-accelerated when available, polling-backed otherwise). Updates the grid
        // row and the panel tallies.
        private async Task VerifyAndMarkAsync(UnlockOrderItem item, bool eventBased, CancellationToken token)
        {
            var verifier = new UnlockVerifier(_xboxRestAPI.Value, HomeViewModel.XUIDOnly, TitleIDOverride);
            var outcome = await verifier.WaitForAchievedAsync(item.Id, eventBased, token, _autoRta);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var targetAch = DGAchievements.FirstOrDefault(a => a.ID.ToString() == item.Id);
                switch (outcome)
                {
                    case VerifyOutcome.Confirmed:
                        _confirmedCount++;
                        if (targetAch != null)
                        {
                            targetAch.IsUnlockable = false;
                            targetAch.ProgressState = StringConstants.Achieved;
                            targetAch.DateUnlocked = DateTime.Now;
                            targetAch.VerifyState = "✓";
                        }
                        break;
                    case VerifyOutcome.Pending:
                        _pendingCount++;
                        if (targetAch != null)
                            targetAch.VerifyState = "⏳";
                        break;
                    case VerifyOutcome.Cancelled:
                        if (targetAch != null)
                            targetAch.VerifyState = "⏳";
                        break;
                }
                AutoStatusText = ComposeAutoStatus();
                CollectionViewSource.GetDefaultView(DGAchievements).Refresh();
            });
        }

        // Formats seconds into HH:MM:SS (or MM:SS when it does not reach an hour).
        private static string FormatTime(long seconds)
        {
            var h = seconds / 3600;
            var m = (seconds % 3600) / 60;
            var s = seconds % 60;
            return h > 0 ? $"{h:00}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        }

        #endregion
    }
}
