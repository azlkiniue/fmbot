using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FMBot.Domain;
using FMBot.Domain.Models;
using FMBot.Persistence.Domain.Models;
using FMBot.Persistence.EntityFrameWork;
using IF.Lastfm.Core.Api;
using IF.Lastfm.Core.Api.Enums;
using IF.Lastfm.Core.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PostgreSQLCopyHelper;
using Serilog;

namespace FMBot.LastFM.Repositories
{
    public class IndexRepository
    {
        private readonly LastfmClient _lastFMClient;

        private readonly IMemoryCache _cache;

        private readonly string _key;
        private readonly string _secret;

        private readonly IDbContextFactory<FMBotDbContext> _contextFactory;

        private readonly string _connectionString;

        private readonly LastFmRepository _lastFmRepository;

        public IndexRepository(
            IConfiguration configuration,
            LastFmRepository lastFmRepository,
            IDbContextFactory<FMBotDbContext> contextFactory,
            IMemoryCache cache)
        {
            this._lastFmRepository = lastFmRepository;
            this._contextFactory = contextFactory;
            this._cache = cache;
            this._key = configuration.GetSection("LastFm:Key").Value;
            this._secret = configuration.GetSection("LastFm:Secret").Value;
            this._connectionString = configuration.GetSection("Database:ConnectionString").Value;
            this._lastFMClient = new LastfmClient(this._key, this._secret);
        }

        public async Task IndexUser(IndexUserQueueItem queueItem)
        {
            var concurrencyCacheKey = $"index-started-{queueItem.UserId}";
            this._cache.Set(concurrencyCacheKey, true);

            Thread.Sleep(queueItem.TimeoutMs);

            await using var db = this._contextFactory.CreateDbContext();
            var user = await db.Users.FindAsync(queueItem.UserId);


            Log.Information($"Starting index for {user.UserNameLastFM}");
            var now = DateTime.UtcNow;

            await using var connection = new NpgsqlConnection(this._connectionString);
            await connection.OpenAsync();

            var userInfo = await this._lastFmRepository.GetLfmUserInfoAsync(user.UserNameLastFM, user.SessionKeyLastFm);
            if (userInfo?.Registered?.Text != null)
            {
                await SetUserSignUpTime(user.UserId, userInfo.Registered.Text, connection);
            }

            await SetUserPlaycount(user, connection);

            var plays = await GetPlaysForUserFromLastFm(user);
            await InsertPlaysIntoDatabase(plays, user.UserId, connection);

            var artists = await GetArtistsForUserFromLastFm(user);
            await InsertArtistsIntoDatabase(artists, user.UserId, connection);

            var albums = await GetAlbumsForUserFromLastFm(user);
            await InsertAlbumsIntoDatabase(albums, user.UserId, connection);

            var tracks = await GetTracksForUserFromLastFm(user);
            await InsertTracksIntoDatabase(tracks, user.UserId, connection);

            var latestScrobbleDate = await GetLatestScrobbleDate(user);

            await SetUserIndexTime(user.UserId, now, latestScrobbleDate, connection);

            Statistics.IndexedUsers.Inc();
            this._cache.Remove(concurrencyCacheKey);
        }

        private async Task<IReadOnlyList<UserArtist>> GetArtistsForUserFromLastFm(User user)
        {
            Log.Information($"Getting artists for user {user.UserNameLastFM}");

            var topArtists = new List<LastArtist>();

            var indexLimit = UserHasHigherIndexLimit(user) ? 200 : 4;

            for (var i = 1; i < indexLimit + 1; i++)
            {
                var artistResult = await this._lastFMClient.User.GetTopArtists(user.UserNameLastFM,
                    LastStatsTimeSpan.Overall, i, 1000);
                Statistics.LastfmApiCalls.Inc();

                topArtists.AddRange(artistResult);

                if (artistResult.Count() < 1000)
                {
                    break;
                }
            }

            if (topArtists.Count == 0)
            {
                return new List<UserArtist>();
            }

            return topArtists.Select(a => new UserArtist
            {
                Name = a.Name,
                Playcount = a.PlayCount.Value,
                UserId = user.UserId
            }).ToList();
        }

        private async Task<IReadOnlyList<UserPlay>> GetPlaysForUserFromLastFm(User user)
        {
            Log.Information($"Getting plays for user {user.UserNameLastFM}");

            var recentPlays = await this._lastFMClient.User.GetRecentScrobbles(
                user.UserNameLastFM,
                count: 1000,
                @from: DateTime.UtcNow.AddDays(-14));

            if (!recentPlays.Success || recentPlays.Content.Count == 0)
            {
                return new List<UserPlay>();
            }

            return recentPlays
                .Where(w => w.TimePlayed.HasValue && w.TimePlayed.Value.DateTime > DateTime.UtcNow.AddDays(-Constants.DaysToStorePlays))
                .Select(t => new UserPlay
                {
                    TrackName = t.Name,
                    AlbumName = t.AlbumName,
                    ArtistName = t.ArtistName,
                    TimePlayed = t.TimePlayed.Value.DateTime,
                    UserId = user.UserId
                }).ToList();
        }

        private async Task<IReadOnlyList<UserAlbum>> GetAlbumsForUserFromLastFm(User user)
        {
            Log.Information($"Getting albums for user {user.UserNameLastFM}");

            var topAlbums = new List<LastAlbum>();

            var indexLimit = UserHasHigherIndexLimit(user) ? 200 : 5;

            for (var i = 1; i < indexLimit + 1; i++)
            {
                var albumResult = await this._lastFMClient.User.GetTopAlbums(user.UserNameLastFM,
                    LastStatsTimeSpan.Overall, i, 1000);
                Statistics.LastfmApiCalls.Inc();

                topAlbums.AddRange(albumResult);

                if (albumResult.Count() < 1000)
                {
                    break;
                }
            }

            if (topAlbums.Count == 0)
            {
                return new List<UserAlbum>();
            }

            return topAlbums.Select(a => new UserAlbum
            {
                Name = a.Name,
                ArtistName = a.ArtistName,
                Playcount = a.PlayCount.Value,
                UserId = user.UserId
            }).ToList();
        }

        private async Task<IReadOnlyList<UserTrack>> GetTracksForUserFromLastFm(User user)
        {
            Log.Information($"Getting tracks for user {user.UserNameLastFM}");

            var indexLimit = UserHasHigherIndexLimit(user) ? 200 : 6;

            var trackResult = await this._lastFmRepository.GetTopTracksAsync(user.UserNameLastFM, "overall", 1000, indexLimit);

            if (!trackResult.Success || trackResult.Content.TopTracks.Count == 0)
            {
                return new List<UserTrack>();
            }

            return trackResult.Content.TopTracks.Select(a => new UserTrack
            {
                Name = a.TrackName,
                ArtistName = a.ArtistName,
                Playcount = Convert.ToInt32(a.UserPlaycount),
                UserId = user.UserId
            }).ToList();
        }

        private static async Task InsertPlaysIntoDatabase(IReadOnlyList<UserPlay> userPlays, int userId,
            NpgsqlConnection connection)
        {
            Log.Information($"Inserting {userPlays.Count} plays for user {userId}");

            await using var deletePlays = new NpgsqlCommand("DELETE FROM public.user_plays " +
                                                               "WHERE user_id = @userId", connection);

            deletePlays.Parameters.AddWithValue("userId", userId);

            await deletePlays.ExecuteNonQueryAsync();

            var copyHelper = new PostgreSQLCopyHelper<UserPlay>("public", "user_plays")
                .MapText("track_name", x => x.TrackName)
                .MapText("album_name", x => x.AlbumName)
                .MapText("artist_name", x => x.ArtistName)
                .MapTimeStampTz("time_played", x => DateTime.SpecifyKind(x.TimePlayed, DateTimeKind.Utc))
                .MapInteger("user_id", x => x.UserId);

            await copyHelper.SaveAllAsync(connection, userPlays);
        }

        private static async Task InsertArtistsIntoDatabase(IReadOnlyList<UserArtist> artists, int userId,
            NpgsqlConnection connection)
        {
            Log.Information($"Inserting {artists.Count} artists for user {userId}");

            var copyHelper = new PostgreSQLCopyHelper<UserArtist>("public", "user_artists")
                .MapText("name", x => x.Name)
                .MapInteger("user_id", x => x.UserId)
                .MapInteger("playcount", x => x.Playcount);

            await using var deleteCurrentArtists = new NpgsqlCommand($"DELETE FROM public.user_artists WHERE user_id = {userId};", connection);
            await deleteCurrentArtists.ExecuteNonQueryAsync();

            await copyHelper.SaveAllAsync(connection, artists);
        }

        private static async Task InsertAlbumsIntoDatabase(IReadOnlyList<UserAlbum> albums, int userId,
            NpgsqlConnection connection)
        {
            Log.Information($"Inserting {albums.Count} albums for user {userId}");

            var copyHelper = new PostgreSQLCopyHelper<UserAlbum>("public", "user_albums")
                .MapText("name", x => x.Name)
                .MapText("artist_name", x => x.ArtistName)
                .MapInteger("user_id", x => x.UserId)
                .MapInteger("playcount", x => x.Playcount);

            await using var deleteCurrentAlbums = new NpgsqlCommand($"DELETE FROM public.user_albums WHERE user_id = {userId};", connection);
            await deleteCurrentAlbums.ExecuteNonQueryAsync();

            await copyHelper.SaveAllAsync(connection, albums);
        }

        private static async Task InsertTracksIntoDatabase(IReadOnlyList<UserTrack> artists, int userId,
            NpgsqlConnection connection)
        {
            Log.Information($"Inserting {artists.Count} tracks for user {userId}");

            var copyHelper = new PostgreSQLCopyHelper<UserTrack>("public", "user_tracks")
                .MapText("name", x => x.Name)
                .MapText("artist_name", x => x.ArtistName)
                .MapInteger("user_id", x => x.UserId)
                .MapInteger("playcount", x => x.Playcount);

            await using var deleteCurrentTracks = new NpgsqlCommand($"DELETE FROM public.user_tracks WHERE user_id = {userId};", connection);
            await deleteCurrentTracks.ExecuteNonQueryAsync();

            await copyHelper.SaveAllAsync(connection, artists);

        }

        private async Task SetUserIndexTime(int userId, DateTime now, DateTime lastScrobble, NpgsqlConnection connection)
        {
            Log.Information($"Setting user index time for user {userId}");

            await using var setIndexTime = new NpgsqlCommand($"UPDATE public.users SET last_indexed='{now:u}', last_updated='{now:u}', last_scrobble_update = '{lastScrobble:u}' WHERE user_id = {userId};", connection);
            await setIndexTime.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task SetUserSignUpTime(int userId, long signUpDateTimeLong, NpgsqlConnection connection)
        {
            var signUpDateTime = DateTime.UnixEpoch.AddSeconds(signUpDateTimeLong).ToUniversalTime();

            Log.Information($"Setting user index signup time ({signUpDateTime}) for user {userId}");

            await using var setIndexTime = new NpgsqlCommand($"UPDATE public.users SET registered_last_fm='{signUpDateTime:u}' WHERE user_id = {userId};", connection);
            await setIndexTime.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task<DateTime> GetLatestScrobbleDate(User user)
        {
            var recentTracks = await this._lastFMClient.User.GetRecentScrobbles(user.UserNameLastFM, count: 1);
            Statistics.LastfmApiCalls.Inc();
            if (!recentTracks.Success || !recentTracks.Content.Any() || !recentTracks.Content.Any(a => a.TimePlayed.HasValue))
            {
                Log.Information("Recent track call to get latest scrobble date failed!");
                return DateTime.UtcNow;
            }

            return recentTracks.Content.First(f => f.TimePlayed.HasValue).TimePlayed.Value.DateTime;
        }

        private async Task SetUserPlaycount(User user, NpgsqlConnection connection)
        {
            var recentTracks = await this._lastFmRepository.GetRecentTracksAsync(
                user.UserNameLastFM,
                count: 1,
                useCache: false,
                user.SessionKeyLastFm);

            if (recentTracks.Success)
            {
                await using var setPlaycount = new NpgsqlCommand($"UPDATE public.users SET total_playcount = {recentTracks.Content.TotalAmount} WHERE user_id = {user.UserId};", connection);

                await setPlaycount.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private bool UserHasHigherIndexLimit(User user)
        {
            switch (user.UserType)
            {
                case UserType.Supporter:
                    return true;
                case UserType.Contributor:
                    return true;
                case UserType.Admin:
                    return true;
                case UserType.Owner:
                    return true;
                case UserType.User:
                    return false;
                default:
                    return false;
            }
        }
    }
}
