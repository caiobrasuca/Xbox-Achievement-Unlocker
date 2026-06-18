using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace XAU.Services.AutoUnlock
{
    public enum CounterResult
    {
        NotCounter, // sem requisito numérico alvo > 1 — o chamador desbloqueia normal
        Achieved,
        Pending,    // enviado, mas não confirmado no tempo (pode cair depois)
        Failed
    }

    public sealed class CounterUnlocker
    {
        private const int MaxTotalEvents = 10000;
        private static readonly int[] VerifyBackoff = { 3, 5, 8, 13, 21, 30 };
        private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(3);

        private readonly XboxRestAPI _api;
        private readonly string _titleId;
        private readonly string _xuid;
        private readonly string _eventsToken;

        public CounterUnlocker(XboxRestAPI api, string titleId, string xuid, string eventsToken)
        {
            _api = api;
            _titleId = titleId;
            _xuid = xuid;
            _eventsToken = eventsToken;
        }

        public async Task<CounterResult> RunAsync(string achievementId, JObject gameData, CancellationToken token)
        {
            var progress = await ReadProgressAsync(achievementId, token);
            if (progress == null)
                return CounterResult.NotCounter;
            var (current, target, achieved) = progress.Value;
            if (achieved)
                return CounterResult.Achieved;
            if (target <= 1)
                return CounterResult.NotCounter;

            long need = target - current;
            if (need < 1) need = 1;
            if (need > MaxTotalEvents) need = MaxTotalEvents;

            var events = new EventUnlocker(_api, _titleId, _xuid, _eventsToken);

            try
            {
                await events.SendEventsAsync(achievementId, gameData, 1);
            }
            catch
            {
                return CounterResult.Failed;
            }

            if (need > 1)
            {
                try
                {
                    await events.SendEventsAsync(achievementId, gameData, (int)(need - 1));
                }
                catch
                {
                }
            }

            var ok = await WaitAchievedAsync(achievementId, target, token);
            return ok ? CounterResult.Achieved : CounterResult.Pending;
        }

        private async Task<(long current, long target, bool achieved)?> ReadProgressAsync(string achievementId, CancellationToken token)
        {
            try
            {
                await XboxRateLimiter.Achievements.WaitAsync(token);
                var resp = await _api.GetAchievementsForTitleAsync(_xuid, _titleId);
                var ach = resp?.achievements?.FirstOrDefault(a => a.id == achievementId);
                if (ach == null)
                    return null;

                bool achieved = ach.progressState == StringConstants.Achieved;

                var req = ach.progression?.requirements?
                    .FirstOrDefault(r => long.TryParse(r.target, out var t) && t > 1);
                if (req == null)
                    return achieved ? (0L, 1L, true) : ((long, long, bool)?)null;

                long.TryParse(req.target, out var target);
                long.TryParse(req.current, out var current);
                return (current, target, achieved);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> WaitAchievedAsync(string achievementId, long target, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + VerifyTimeout;
            int attempt = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (token.IsCancellationRequested)
                    return false;

                var seconds = VerifyBackoff[Math.Min(attempt, VerifyBackoff.Length - 1)];
                attempt++;
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
                catch (OperationCanceledException) { return false; }

                var state = await ReadProgressAsync(achievementId, token);
                if (state == null)
                    continue;
                if (state.Value.achieved || state.Value.current >= target)
                    return true;
            }
            return false;
        }
    }
}
