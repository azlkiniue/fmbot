using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;
using Dapper;
using Discord.Commands;
using FMBot.Bot.Extensions;
using FMBot.Bot.Models;
using FMBot.Domain.Models;
using FMBot.Persistence.Domain.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FMBot.Bot.Services
{
    public class TimeService
    {
        private readonly IMemoryCache _cache;
        private readonly BotSettings _botSettings;

        public TimeService(IMemoryCache cache, IOptions<BotSettings> botSettings)
        {
            this._cache = cache;
            this._botSettings = botSettings.Value;
        }

        public async Task<TimeSpan> GetPlayTimeForPlays(IEnumerable<UserPlay> plays)
        {
            await CacheAllTrackLengths();

            var totalMs = plays.Sum(userPlay => GetTrackLengthForTrack(userPlay.ArtistName, userPlay.TrackName));

            return TimeSpan.FromMilliseconds(totalMs);
        }

        public async Task<TimeSpan> GetPlayTimeForTrackWithPlaycount(string artistName, string trackName, long playcount)
        {
            await CacheAllTrackLengths();

            var length = GetTrackLengthForTrack(artistName, trackName);

            return TimeSpan.FromMilliseconds(length * playcount);
        }

        private long GetTrackLengthForTrack(string artistName, string trackName)
        {
            var trackLength = (long?)this._cache.Get(CacheKeyForTrack(trackName.ToLower(), artistName.ToLower()));

            if (trackLength.HasValue)
            {
                return trackLength.Value;
            }

            var avgArtistTrackLength = (long?)this._cache.Get(CacheKeyForArtist(artistName.ToLower()));

            return avgArtistTrackLength ?? 210000;
        }


        private async Task CacheAllTrackLengths()
        {
            const string cacheKey = "track-lengths-cached";
            var cacheTime = TimeSpan.FromMinutes(10);

            if (this._cache.TryGetValue(cacheKey, out _))
            {
                return;
            }

            const string sql = "SELECT LOWER(artist_name) as artist_name, LOWER(name) as track_name, duration_ms " +
                               "FROM public.tracks where duration_ms is not null;";

            DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
            await connection.OpenAsync();

            var trackLengths = (await connection.QueryAsync<TrackLengthDto>(sql)).ToList();

            foreach (var length in trackLengths)
            {
                this._cache.Set(CacheKeyForTrack(length.TrackName, length.ArtistName), length.DurationMs, cacheTime);
            }

            foreach (var artistLength in trackLengths.GroupBy(g => g.ArtistName))
            {
                this._cache.Set(CacheKeyForArtist(artistLength.Key), (long)artistLength.Average(a => a.DurationMs), cacheTime);
            }

            this._cache.Set(cacheKey, true, cacheTime);
        }

        private static string CacheKeyForTrack(string trackName, string artistName)
        {
            return $"track-length-{trackName}-{artistName}";
        }
        private static string CacheKeyForArtist(string artistName)
        {
            return $"artist-length-avg-{artistName}";
        }

        public async Task<List<WhoKnowsObjectWithUser>> UserPlaysToGuildLeaderboard(ICommandContext context, List<UserPlay> userPlays, ICollection<GuildUser> guildUsers)
        {
            var whoKnowsAlbumList = new List<WhoKnowsObjectWithUser>();

            var userPlaysPerUser = userPlays
                .GroupBy(g => g.UserId)
                .ToList();

            for (var i = 0; i < userPlaysPerUser.Count(); i++)
            {
                var user = userPlaysPerUser[i];

                var timeListened = await GetPlayTimeForPlays(user);

                var guildUser = guildUsers.FirstOrDefault(f => f.UserId == user.Key);

                if (guildUser != null)
                {
                    var userName = guildUser.UserName ?? guildUser.User.UserNameLastFM;

                    if (i <= 10)
                    {
                        var discordUser = await context.Guild.GetUserAsync(guildUser.User.DiscordUserId);
                        if (discordUser != null)
                        {
                            userName = discordUser.Nickname ?? discordUser.Username;
                        }
                    }

                    whoKnowsAlbumList.Add(new WhoKnowsObjectWithUser
                    {
                        DiscordName = userName,
                        Playcount = (int)timeListened.TotalMinutes,
                        LastFMUsername = guildUser.User.UserNameLastFM,
                        UserId = user.Key,
                        WhoKnowsWhitelisted = guildUser.WhoKnowsWhitelisted,
                        Name = StringExtensions.GetListeningTimeString(timeListened)
                    });
                }
            }

            return whoKnowsAlbumList
                .OrderByDescending(o => o.Playcount)
                .ToList();
        }
    }
}
